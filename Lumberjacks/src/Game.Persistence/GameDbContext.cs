using Game.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Game.Persistence;

public class GameDbContext : DbContext
{
    public GameDbContext(DbContextOptions<GameDbContext> options) : base(options) { }

    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<PlayerProgressEntity> PlayerProgress => Set<PlayerProgressEntity>();
    public DbSet<GuildProgressEntity> GuildProgress => Set<GuildProgressEntity>();
    public DbSet<StructureEntity> Structures => Set<StructureEntity>();
    public DbSet<WorldItemEntity> WorldItems => Set<WorldItemEntity>();
    public DbSet<PlayerInventoryEntity> PlayerInventories => Set<PlayerInventoryEntity>();
    public DbSet<ContainerEntity> Containers => Set<ContainerEntity>();
    public DbSet<ContainerItemEntity> ContainerItems => Set<ContainerItemEntity>();
    public DbSet<ChallengeEntity> Challenges => Set<ChallengeEntity>();
    public DbSet<ChallengeProgressEntity> ChallengeProgress => Set<ChallengeProgressEntity>();
    public DbSet<RegionEntity> Regions => Set<RegionEntity>();
    public DbSet<NaturalResourceEntity> NaturalResources => Set<NaturalResourceEntity>();
    public DbSet<RegionProfileEntity> RegionProfiles => Set<RegionProfileEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Map to existing tables created by the TS services
        modelBuilder.Entity<EventEntity>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EventId).HasColumnName("event_id")
                .HasConversion(v => Guid.Parse(v), v => v.ToString())
                .HasColumnType("uuid");
            e.Property(x => x.EventType).HasColumnName("event_type");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.WorldId).HasColumnName("world_id");
            e.Property(x => x.RegionId).HasColumnName("region_id");
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.GuildId).HasColumnName("guild_id");
            e.Property(x => x.SourceService).HasColumnName("source_service");
            e.Property(x => x.SchemaVersion).HasColumnName("schema_version");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.EventType);
            e.HasIndex(x => x.ActorId);
            e.HasIndex(x => x.OccurredAt);
        });

        modelBuilder.Entity<PlayerProgressEntity>(e =>
        {
            e.ToTable("player_progress");
            e.HasKey(x => x.PlayerId);
            e.Property(x => x.PlayerId).HasColumnName("player_id");
            e.Property(x => x.Rank).HasColumnName("rank");
            e.Property(x => x.Points).HasColumnName("points");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<GuildProgressEntity>(e =>
        {
            e.ToTable("guild_progress");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.GuildId).HasColumnName("guild_id");
            e.Property(x => x.Points).HasColumnName("points");
            e.Property(x => x.ChallengesCompleted).HasColumnName("challenges_completed");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<StructureEntity>(e =>
        {
            e.ToTable("structures");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.PositionX).HasColumnName("position_x");
            e.Property(x => x.PositionY).HasColumnName("position_y");
            e.Property(x => x.PositionZ).HasColumnName("position_z");
            e.Property(x => x.Rotation).HasColumnName("rotation");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.RegionId).HasColumnName("region_id");
            e.Property(x => x.PlacedAt).HasColumnName("placed_at");
            e.Property(x => x.Tags).HasColumnName("tags").HasColumnType("jsonb");
            e.HasIndex(x => x.RegionId);
            e.HasIndex(x => x.OwnerId);
        });

        modelBuilder.Entity<WorldItemEntity>(e =>
        {
            e.ToTable("world_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ItemType).HasColumnName("item_type");
            e.Property(x => x.PositionX).HasColumnName("position_x");
            e.Property(x => x.PositionY).HasColumnName("position_y");
            e.Property(x => x.PositionZ).HasColumnName("position_z");
            e.Property(x => x.RegionId).HasColumnName("region_id");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.SpawnedAt).HasColumnName("spawned_at");
            e.HasIndex(x => x.RegionId);
        });

        modelBuilder.Entity<PlayerInventoryEntity>(e =>
        {
            e.ToTable("player_inventories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.PlayerId).HasColumnName("player_id");
            e.Property(x => x.ItemType).HasColumnName("item_type");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.AcquiredAt).HasColumnName("acquired_at");
            e.HasIndex(x => x.PlayerId);
        });

        modelBuilder.Entity<ContainerEntity>(e =>
        {
            e.ToTable("containers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.StructureId).HasColumnName("structure_id");
            e.Property(x => x.RegionId).HasColumnName("region_id");
        });

        modelBuilder.Entity<ContainerItemEntity>(e =>
        {
            e.ToTable("container_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ContainerId).HasColumnName("container_id");
            e.Property(x => x.ItemType).HasColumnName("item_type");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.StoredAt).HasColumnName("stored_at");
            e.HasIndex(x => x.ContainerId);
        });

        modelBuilder.Entity<ChallengeEntity>(e =>
        {
            e.ToTable("challenges");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.TriggerEvent).HasColumnName("trigger_event");
            e.Property(x => x.TriggerFilters).HasColumnName("trigger_filters").HasColumnType("jsonb");
            e.Property(x => x.ProgressMode).HasColumnName("progress_mode");
            e.Property(x => x.Target).HasColumnName("target");
            e.Property(x => x.WindowStart).HasColumnName("window_start");
            e.Property(x => x.WindowEnd).HasColumnName("window_end");
            e.Property(x => x.Rewards).HasColumnName("rewards").HasColumnType("jsonb");
            e.Property(x => x.Active).HasColumnName("active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.TriggerEvent);
            e.HasIndex(x => x.Active);
        });

        modelBuilder.Entity<RegionEntity>(e =>
        {
            e.ToTable("regions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.BoundsMinX).HasColumnName("bounds_min_x");
            e.Property(x => x.BoundsMinY).HasColumnName("bounds_min_y");
            e.Property(x => x.BoundsMinZ).HasColumnName("bounds_min_z");
            e.Property(x => x.BoundsMaxX).HasColumnName("bounds_max_x");
            e.Property(x => x.BoundsMaxY).HasColumnName("bounds_max_y");
            e.Property(x => x.BoundsMaxZ).HasColumnName("bounds_max_z");
            e.Property(x => x.Active).HasColumnName("active");
            e.Property(x => x.TickRate).HasColumnName("tick_rate");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<ChallengeProgressEntity>(e =>
        {
            e.ToTable("challenge_progress");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ChallengeId).HasColumnName("challenge_id");
            e.Property(x => x.GuildId).HasColumnName("guild_id");
            e.Property(x => x.CurrentValue).HasColumnName("current_value");
            e.Property(x => x.Completed).HasColumnName("completed");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.ChallengeId, x.GuildId }).IsUnique();
        });

        modelBuilder.Entity<NaturalResourceEntity>(e =>
        {
            e.ToTable("natural_resources");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.PositionX).HasColumnName("position_x");
            e.Property(x => x.PositionY).HasColumnName("position_y");
            e.Property(x => x.PositionZ).HasColumnName("position_z");
            e.Property(x => x.RegionId).HasColumnName("region_id");
            e.Property(x => x.Health).HasColumnName("health");
            e.Property(x => x.StumpHealth).HasColumnName("stump_health");
            e.Property(x => x.RegrowthProgress).HasColumnName("regrowth_progress");
            e.Property(x => x.LeanX).HasColumnName("lean_x");
            e.Property(x => x.LeanZ).HasColumnName("lean_z");
            e.Property(x => x.GrowthHistory).HasColumnName("growth_history").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.LastUpdatedAt).HasColumnName("last_updated_at");
            e.HasIndex(x => x.RegionId);
        });

        modelBuilder.Entity<RegionProfileEntity>(e =>
        {
            e.ToTable("region_profiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RegionId).HasColumnName("region_id");
            e.Property(x => x.AltitudeGrid).HasColumnName("altitude_grid").HasColumnType("jsonb");
            e.Property(x => x.HumidityGrid).HasColumnName("humidity_grid").HasColumnType("jsonb");
            e.Property(x => x.GridWidth).HasColumnName("grid_width");
            e.Property(x => x.GridHeight).HasColumnName("grid_height");
            e.Property(x => x.TradeWindX).HasColumnName("trade_wind_x");
            e.Property(x => x.TradeWindZ).HasColumnName("trade_wind_z");
            e.Property(x => x.GeologicHistory).HasColumnName("geologic_history").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.RegionId);
        });
    }
}
