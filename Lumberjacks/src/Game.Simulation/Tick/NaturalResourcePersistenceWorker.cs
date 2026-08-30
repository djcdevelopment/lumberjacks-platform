using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Contracts.Events;
using Game.Persistence;
using Game.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Tick;

/// <summary>
/// Moves natural-resource mutations off the 20 Hz simulation path. Mutations are
/// coalesced by resource and persisted frequently; a terminal felling and its
/// event are committed in the same database transaction.
/// </summary>
public sealed class NaturalResourcePersistenceWorker : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentDictionary<string, NaturalResourceMutation> _pending =
        new(StringComparer.Ordinal);
    private readonly IDbContextFactory<GameDbContext> _dbFactory;
    private readonly ILogger<NaturalResourcePersistenceWorker> _logger;
    private readonly string _worldId;
    private readonly string _clientRelease;
    private readonly string _gatewayRelease;

    public NaturalResourcePersistenceWorker(
        IDbContextFactory<GameDbContext> dbFactory,
        IConfiguration configuration,
        ILogger<NaturalResourcePersistenceWorker> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _worldId = configuration["World:Id"] ?? "world-default";
        _clientRelease = configuration["NativeClient:RequiredRelease"] ??
            configuration["LUMBERJACKS_NATIVE_CLIENT_RELEASE"] ??
            "unknown";
        _gatewayRelease = Environment.GetEnvironmentVariable("LUMBERJACKS_VERSION") ?? "unknown";
    }

    public void MarkDirty(NaturalResourceMutation mutation)
    {
        _pending.AddOrUpdate(
            mutation.ResourceId,
            mutation,
            (_, current) => mutation.Tick >= current.Tick ? mutation : current);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // StopAsync performs a final uncancelled drain below.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batch = TakePendingBatch();
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            foreach (var mutation in batch)
            {
                var resource = mutation.Resource;
                var entity = await db.NaturalResources.FindAsync([resource.Id], cancellationToken);
                if (entity is null)
                {
                    entity = new NaturalResourceEntity
                    {
                        Id = resource.Id,
                        Type = resource.Type,
                        RegionId = resource.RegionId,
                        CreatedAt = resource.CreatedAt,
                    };
                    db.NaturalResources.Add(entity);
                }

                entity.Type = resource.Type;
                entity.PositionX = resource.Position.X;
                entity.PositionY = resource.Position.Y;
                entity.PositionZ = resource.Position.Z;
                entity.RegionId = resource.RegionId;
                entity.Health = resource.Health;
                entity.StumpHealth = resource.StumpHealth;
                entity.RegrowthProgress = resource.RegrowthProgress;
                entity.LeanX = resource.LeanX;
                entity.LeanZ = resource.LeanZ;
                entity.GrowthHistory = JsonSerializer.Serialize(resource.GrowthHistory);
                entity.LastUpdatedAt = resource.LastUpdatedAt;

                if (mutation.Felled)
                {
                    var eventId = CreateFellingEventId(_worldId, resource.Id, resource.CreatedAt);
                    if (!await db.Events.AnyAsync(e => e.EventId == eventId, cancellationToken))
                    {
                        db.Events.Add(new EventEntity
                        {
                            EventId = eventId,
                            EventType = EventType.TreeFelled,
                            OccurredAt = resource.LastUpdatedAt,
                            WorldId = _worldId,
                            RegionId = resource.RegionId,
                            ActorId = mutation.ActorId,
                            SourceService = "lumberjacks-gateway",
                            SchemaVersion = 1,
                            Payload = JsonSerializer.Serialize(new
                            {
                                resource_id = resource.Id,
                                resource_type = resource.Type,
                                strike_count = mutation.StrikeCount,
                                final_heading = resource.GrowthHistory.GetValueOrDefault("fall_heading"),
                                server_tick = mutation.Tick,
                                client_release = _clientRelease,
                                gateway_release = _gatewayRelease,
                            }),
                        });
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var mutation in batch)
            {
                MarkDirty(mutation);
            }

            _logger.LogError(
                ex,
                "Failed to persist {ResourceCount} natural-resource mutations; queued for retry",
                batch.Count);
        }
    }

    public static string CreateFellingEventId(
        string worldId,
        string resourceId,
        DateTimeOffset resourceCreatedAt)
    {
        var identity = $"{worldId}:{resourceId}:{resourceCreatedAt:O}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16)).ToString();
    }

    private List<NaturalResourceMutation> TakePendingBatch()
    {
        var batch = new List<NaturalResourceMutation>(_pending.Count);
        var collection = (ICollection<KeyValuePair<string, NaturalResourceMutation>>)_pending;
        foreach (var pair in _pending)
        {
            // ICollection.Remove on ConcurrentDictionary is an atomic key-and-value
            // comparison, so a newer mutation cannot be accidentally removed here.
            if (collection.Remove(pair))
            {
                batch.Add(pair.Value);
            }
        }

        return batch;
    }
}
