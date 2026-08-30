using Game.Contracts.Entities;
using Game.Contracts.Protocol;
using Game.Simulation.World;
using System.Globalization;

namespace Game.Simulation.Tick;

/// <summary>
/// Executes one simulation step: drains input queue, applies physics,
/// updates world state. Called by TickLoop on every tick.
/// </summary>
public class SimulationStep
{
    /// <summary>Max player movement speed in units per tick (at 20 Hz = 6 units/sec).</summary>
    public const double MaxSpeedPerTick = 0.3;

    /// <summary>Friction deceleration per tick when no input (units/tick²).</summary>
    public const double FrictionPerTick = 0.08;

    public const byte AxeActionFlag = 0x04;
    public const long AxeCooldownTicks = 10;
    public const double AxeRange = 2.5;
    public const double AxeDamage = 12.5;

    /// <summary>
    /// Process one tick: apply queued inputs → compute physics → return list of changed entities.
    /// </summary>
    public static SimulationStepResult Execute(
        WorldState world,
        InputQueue inputQueue,
        long tick)
    {
        var changedPlayers = new HashSet<string>();
        var changedResources = new HashSet<string>();
        var resourceMutations = new List<NaturalResourceMutation>();

        // 1. Drain inputs for this tick
        var inputs = inputQueue.DrainForTick(tick);

        // 2. Apply inputs to players
        foreach (var (playerId, queuedInput) in inputs)
        {
            if (!world.Players.TryGetValue(playerId, out var player))
                continue;
            if (!player.Connected)
                continue;

            var input = queuedInput.Input;
            
            // === INTERACTION LOGIC (Axe Geometry) ===
            // PlayerInput carries button state, not commands.  Only the rising edge can strike;
            // the server cooldown is a second boundary against fast or malicious clients.
            var previousFlags = world.LastActionFlags.GetValueOrDefault(playerId);
            world.LastActionFlags[playerId] = input.ActionFlags;
            var axePressed =
                (input.ActionFlags & AxeActionFlag) != 0 &&
                (previousFlags & AxeActionFlag) == 0;

            if (axePressed &&
                player.EquippedItemType == "axe" &&
                tick >= world.NextAxeStrikeTick.GetValueOrDefault(playerId))
            {
                var target = world.SpatialGrid.QueryRadius(player.Position, AxeRange)
                    .Select(id => world.NaturalResources.TryGetValue(id, out var resource)
                        ? (Id: id, Resource: resource, DistanceSq: XzDistanceSq(player.Position, resource.Position))
                        : (Id: id, Resource: (NaturalResource?)null, DistanceSq: double.PositiveInfinity))
                    .Where(candidate => candidate.Resource is { Health: > 0 } &&
                        candidate.DistanceSq <= AxeRange * AxeRange)
                    .OrderBy(candidate => candidate.DistanceSq)
                    .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (target.Resource is { } resource)
                {
                    var dx = resource.Position.X - player.Position.X;
                    var dz = resource.Position.Z - player.Position.Z;
                    var length = Math.Sqrt(dx * dx + dz * dz);
                    if (length <= double.Epsilon)
                    {
                        var headingRadians = player.Heading * Math.PI / 180.0;
                        dx = Math.Sin(headingRadians);
                        dz = Math.Cos(headingRadians);
                        length = 1;
                    }

                    var history = new Dictionary<string, string>(resource.GrowthHistory, StringComparer.Ordinal);
                    var strikeCount = history.TryGetValue("strike_count", out var storedCount) &&
                        int.TryParse(storedCount, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount)
                            ? parsedCount + 1
                            : 1;
                    history["strike_count"] = strikeCount.ToString(CultureInfo.InvariantCulture);

                    var updatedResource = resource with
                    {
                        Health = Math.Max(0, resource.Health - AxeDamage),
                        LeanX = resource.LeanX + dx / length,
                        LeanZ = resource.LeanZ + dz / length,
                        GrowthHistory = history,
                        LastUpdatedAt = DateTimeOffset.UtcNow,
                    };

                    var felled = resource.Health > 0 && updatedResource.Health <= 0;
                    if (felled)
                    {
                        world.RegionProfiles.TryGetValue(player.RegionId, out var profile);
                        var windX = profile?.TradeWindX ?? 0;
                        var windZ = profile?.TradeWindZ ?? 0;
                        var windLength = Math.Sqrt(windX * windX + windZ * windZ);
                        if (windLength > double.Epsilon)
                        {
                            windX = windX / windLength * 0.35;
                            windZ = windZ / windLength * 0.35;
                        }

                        var finalFallAngle = Math.Atan2(
                            updatedResource.LeanX + windX,
                            updatedResource.LeanZ + windZ);
                        history["fall_heading"] = (finalFallAngle * 180.0 / Math.PI)
                            .ToString("F1", CultureInfo.InvariantCulture);
                    }

                    world.NaturalResources[target.Id] = updatedResource;
                    world.NextAxeStrikeTick[playerId] = tick + AxeCooldownTicks;
                    changedResources.Add(target.Id);
                    resourceMutations.Add(new NaturalResourceMutation(
                        target.Id, playerId, updatedResource, felled, strikeCount, tick));
                }
            }

            // === MOVEMENT PHYSICS ===
            var speed = Math.Clamp(input.SpeedPercent, (byte)0, (byte)100) / 100.0 * MaxSpeedPerTick;

            // Convert direction byte (0-255) to radians
            var headingDeg = speed > 0
                ? input.Direction / 255.0 * 360.0
                : player.Heading;
            var headingRad = headingDeg * Math.PI / 180.0;

            // Compute velocity from direction + speed
            var vx = Math.Sin(headingRad) * speed;
            var vz = Math.Cos(headingRad) * speed;
            var vy = 0.0; // Gravity/jumping can be added later

            var velocity = new Vec3(vx, vy, vz);

            // Compute new position
            var newPos = new Vec3(
                player.Position.X + vx,
                player.Position.Y + vy,
                player.Position.Z + vz);

            // Bounds clamping
            if (world.Regions.TryGetValue(player.RegionId, out var region))
            {
                newPos = new Vec3(
                    Math.Clamp(newPos.X, region.BoundsMin.X, region.BoundsMax.X),
                    Math.Clamp(newPos.Y, region.BoundsMin.Y, region.BoundsMax.Y),
                    Math.Clamp(newPos.Z, region.BoundsMin.Z, region.BoundsMax.Z));
            }

            world.Players[playerId] = player with
            {
                Position = newPos,
                Velocity = velocity,
                Heading = headingDeg,
                LastInputSeq = input.InputSeq,
                LastActivityAt = DateTimeOffset.UtcNow,
            };
            world.SpatialGrid.Update(playerId, newPos);

            changedPlayers.Add(playerId);
        }

        // 3. Apply friction to players who had NO input this tick
        foreach (var (playerId, player) in world.Players)
        {
            if (changedPlayers.Contains(playerId)) continue; // already updated
            if (!player.Connected) continue;
            if (player.Velocity.X == 0 && player.Velocity.Y == 0 && player.Velocity.Z == 0) continue;

            // Apply friction: decelerate toward zero
            var vel = player.Velocity;
            var speed = Math.Sqrt(vel.X * vel.X + vel.Y * vel.Y + vel.Z * vel.Z);

            if (speed <= FrictionPerTick)
            {
                // Fully stopped
                world.Players[playerId] = player with { Velocity = new Vec3(0, 0, 0) };
                changedPlayers.Add(playerId);
            }
            else
            {
                // Reduce speed by friction
                var scale = (speed - FrictionPerTick) / speed;
                var newVel = new Vec3(vel.X * scale, vel.Y * scale, vel.Z * scale);
                var newPos = new Vec3(
                    player.Position.X + newVel.X,
                    player.Position.Y + newVel.Y,
                    player.Position.Z + newVel.Z);

                // Bounds clamping
                if (world.Regions.TryGetValue(player.RegionId, out var region))
                {
                    newPos = new Vec3(
                        Math.Clamp(newPos.X, region.BoundsMin.X, region.BoundsMax.X),
                        Math.Clamp(newPos.Y, region.BoundsMin.Y, region.BoundsMax.Y),
                        Math.Clamp(newPos.Z, region.BoundsMin.Z, region.BoundsMax.Z));
                }

                world.Players[playerId] = player with
                {
                    Position = newPos,
                    Velocity = newVel,
                };
                world.SpatialGrid.Update(playerId, newPos);
                changedPlayers.Add(playerId);
            }
        }

        return new SimulationStepResult(changedPlayers, changedResources, resourceMutations);
    }

    private static double XzDistanceSq(Vec3 left, Vec3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return dx * dx + dz * dz;
    }
}

public sealed record NaturalResourceMutation(
    string ResourceId,
    string ActorId,
    NaturalResource Resource,
    bool Felled,
    int StrikeCount,
    long Tick);

public sealed record SimulationStepResult(
    HashSet<string> PlayerIds,
    HashSet<string> ResourceIds,
    IReadOnlyList<NaturalResourceMutation> ResourceMutations);
