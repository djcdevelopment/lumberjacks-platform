using System.IO.Hashing;
using System.Text;
using Game.Simulation.World;

namespace Game.Simulation.Tick;

/// <summary>
/// Computes a deterministic hash of the simulation world state.
/// Used for desync detection: server sends hash with every authoritative update,
/// clients can compare against their local predicted state.
///
/// Uses CRC32 for speed (this is a checksum, not cryptographic).
/// </summary>
public static class StateHasher
{
    /// <summary>
    /// Compute a CRC32 hash of all player positions and velocities in the world.
    /// Deterministic: same world state → same hash, regardless of dictionary iteration order.
    /// </summary>
    public static uint ComputeHash(WorldState world)
    {
        var crc = new Crc32();

        // Sort players by ID for deterministic ordering
        var sortedPlayers = world.Players
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var (id, player) in sortedPlayers)
        {
            // Hash player ID
            crc.Append(Encoding.UTF8.GetBytes(id));

            // Hash position (convert doubles to fixed-point int64 to avoid float nondeterminism)
            AppendDouble(crc, player.Position.X);
            AppendDouble(crc, player.Position.Y);
            AppendDouble(crc, player.Position.Z);

            // Hash velocity
            AppendDouble(crc, player.Velocity.X);
            AppendDouble(crc, player.Velocity.Y);
            AppendDouble(crc, player.Velocity.Z);
        }

        // Sort resources by ID for deterministic ordering
        var sortedResources = world.NaturalResources
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var (id, resource) in sortedResources)
        {
            // Hash resource ID
            crc.Append(Encoding.UTF8.GetBytes(id));

            // Hash health and position
            AppendDouble(crc, resource.Health);
            AppendDouble(crc, resource.StumpHealth);
            AppendDouble(crc, resource.Position.X);
            AppendDouble(crc, resource.Position.Y);
            AppendDouble(crc, resource.Position.Z);

            // Hash Axe Geometry state
            AppendDouble(crc, resource.LeanX);
            AppendDouble(crc, resource.LeanZ);

            // Hash all modifiers (GrowthHistory)
            foreach (var (key, val) in resource.GrowthHistory.OrderBy(kv => kv.Key))
            {
                crc.Append(Encoding.UTF8.GetBytes(key));
                crc.Append(Encoding.UTF8.GetBytes(val));
            }
        }

        // Include tick number for chain integrity
        Span<byte> tickBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(tickBytes, world.CurrentTick);
        crc.Append(tickBytes);

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Append a double as its raw 8-byte IEEE 754 representation.
    /// </summary>
    private static void AppendDouble(Crc32 crc, double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(bytes, value);
        crc.Append(bytes);
    }
}
