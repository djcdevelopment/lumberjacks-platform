using Game.Contracts.Entities;
using Game.Contracts.Protocol;
using Game.Simulation.World;

namespace Game.Simulation.Tick;

/// <summary>
/// Executes one simulation step: drains input queue, applies physics,
/// updates world state. Called by TickLoop on every tick.
/// </summary>
public class SimulationStep
{
    /// <summary>Max player movement speed in units per tick (at 20Hz = 200 units/sec).</summary>
    public const double MaxSpeedPerTick = 10.0;

    /// <summary>Friction deceleration per tick when no input (units/tick²).</summary>
    public const double FrictionPerTick = 2.0;

    /// <summary>
    /// Process one tick: apply queued inputs → compute physics → return list of changed entities.
    /// </summary>
    public static (HashSet<string> PlayerIds, HashSet<string> ResourceIds) Execute(
        WorldState world,
        InputQueue inputQueue,
        long tick)
    {
        var changedPlayers = new HashSet<string>();
        var changedResources = new HashSet<string>();

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
            if ((input.ActionFlags & 0x04) != 0) // Bit 2: Interact
            {
                // Must have axe equipped (Nature 2.0 requirement)
                if (player.EquippedItemType == "axe")
                {
                    var nearby = world.SpatialGrid.QueryRadius(player.Position, 2.5);
                    foreach (var entityId in nearby)
                    {
                        if (world.NaturalResources.TryGetValue(entityId, out var resource))
                        {
                            // Calculate strike vector from input direction (0-255)
                            var swingRad = (input.Direction / 255.0) * 360.0 * Math.PI / 180.0;
                            var strikeX = Math.Sin(swingRad);
                            var strikeZ = Math.Cos(swingRad);

                            // Accumulate lean (Axe Geometry)
                            var updatedResource = resource with
                            {
                                Health = Math.Max(0, resource.Health - 5.0), // 20 hits to fell
                                LeanX = resource.LeanX + strikeX,
                                LeanZ = resource.LeanZ + strikeZ,
                                LastUpdatedAt = DateTimeOffset.UtcNow
                            };
                            
                            // If it just fell, finalize growth history with fall direction
                            if (resource.Health > 0 && updatedResource.Health <= 0)
                            {
                                // Final direction = strike mean + trade winds (Phase 0/3)
                                world.RegionProfiles.TryGetValue(player.RegionId, out var profile);
                                var windX = profile?.TradeWindX ?? 0;
                                var windZ = profile?.TradeWindZ ?? 0;
                                
                                var finalFallAngle = Math.Atan2(updatedResource.LeanX + windX, updatedResource.LeanZ + windZ);
                                updatedResource.GrowthHistory["fall_heading"] = (finalFallAngle * 180.0 / Math.PI).ToString("F1");
                                _ = world.NaturalResources.TryUpdate(entityId, updatedResource, resource);
                            }
                            else
                            {
                                world.NaturalResources[entityId] = updatedResource;
                            }
                            
                            changedResources.Add(entityId);
                            break; // only hit one tree per tick
                        }
                    }
                }
            }

            // === MOVEMENT PHYSICS ===
            var speed = Math.Clamp(input.SpeedPercent, (byte)0, (byte)100) / 100.0 * MaxSpeedPerTick;

            // Convert direction byte (0-255) to radians
            var headingDeg = input.Direction / 255.0 * 360.0;
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

        return (changedPlayers, changedResources);
    }
}
