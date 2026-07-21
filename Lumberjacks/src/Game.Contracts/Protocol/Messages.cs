using Game.Contracts.Entities;

namespace Game.Contracts.Protocol;

// Client → Server messages
public record JoinRegionMessage(string RegionId, string? GuildId = null);
public record LeaveRegionMessage(string RegionId);
public record PlayerMoveMessage(Vec3 Position, Vec3 Velocity);
public record PlaceStructureMessage(string StructureType, Vec3 Position, double Rotation);
public record InteractMessage(string Action, string? ItemId = null, string? ContainerId = null, string? ItemType = null, int Quantity = 1);

// Server → Client messages
public record SessionStartedMessage(string SessionId, string PlayerId, string WorldId, string ResumeToken, bool Resumed = false);
public record WorldSnapshotMessage(string RegionId, List<Dictionary<string, object>> Entities, long Tick);
public record EntityUpdateMessage(string EntityId, string EntityType, Dictionary<string, object> Data, long Tick);
public record EntityRemovedMessage(string EntityId, long Tick);
public record EventEmittedMessage(string EventType, Dictionary<string, object>? Data = null);
public record ErrorMessage(string Code, string Message);
