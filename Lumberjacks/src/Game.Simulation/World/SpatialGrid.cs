using System.Collections.Concurrent;
using Game.Contracts.Entities;

namespace Game.Simulation.World;

/// <summary>
/// Grid-based spatial hash for O(1) insertion and fast radius queries.
/// Subdivides a region into fixed-size cells on the XZ plane (Y is ignored for spatial queries).
/// Thread-safe for concurrent reads/writes.
/// </summary>
public class SpatialGrid
{
    private readonly double _cellSize;
    private readonly ConcurrentDictionary<long, HashSet<string>> _cells = new();
    private readonly ConcurrentDictionary<string, (int CellX, int CellZ, Vec3 Position)> _entities = new();
    private readonly object _lock = new();

    public SpatialGrid(double cellSize = 50.0)
    {
        _cellSize = cellSize;
    }

    /// <summary>Number of tracked entities.</summary>
    public int Count => _entities.Count;

    /// <summary>Insert or update an entity's position in the grid.</summary>
    public void Update(string entityId, Vec3 position)
    {
        var newCellX = (int)Math.Floor(position.X / _cellSize);
        var newCellZ = (int)Math.Floor(position.Z / _cellSize);

        lock (_lock)
        {
            if (_entities.TryGetValue(entityId, out var old))
            {
                if (old.CellX == newCellX && old.CellZ == newCellZ)
                {
                    // Same cell — just update position
                    _entities[entityId] = (newCellX, newCellZ, position);
                    return;
                }

                // Remove from old cell
                var oldKey = CellKey(old.CellX, old.CellZ);
                if (_cells.TryGetValue(oldKey, out var oldSet))
                {
                    oldSet.Remove(entityId);
                    if (oldSet.Count == 0)
                        _cells.TryRemove(oldKey, out _);
                }
            }

            // Add to new cell
            var newKey = CellKey(newCellX, newCellZ);
            var newSet = _cells.GetOrAdd(newKey, _ => new HashSet<string>());
            newSet.Add(entityId);

            _entities[entityId] = (newCellX, newCellZ, position);
        }
    }

    /// <summary>Remove an entity from the grid.</summary>
    public bool Remove(string entityId)
    {
        lock (_lock)
        {
            if (!_entities.TryRemove(entityId, out var entry))
                return false;

            var key = CellKey(entry.CellX, entry.CellZ);
            if (_cells.TryGetValue(key, out var set))
            {
                set.Remove(entityId);
                if (set.Count == 0)
                    _cells.TryRemove(key, out _);
            }
            return true;
        }
    }

    /// <summary>Get the last known position of an entity.</summary>
    public Vec3? GetPosition(string entityId)
    {
        return _entities.TryGetValue(entityId, out var entry) ? entry.Position : null;
    }

    /// <summary>
    /// Query all entity IDs within a given radius of a center point (XZ plane distance).
    /// Returns results including the center entity itself if present.
    /// </summary>
    public List<string> QueryRadius(Vec3 center, double radius)
    {
        var results = new List<string>();
        var radiusSq = radius * radius;

        // Determine which cells to check
        var minCellX = (int)Math.Floor((center.X - radius) / _cellSize);
        var maxCellX = (int)Math.Floor((center.X + radius) / _cellSize);
        var minCellZ = (int)Math.Floor((center.Z - radius) / _cellSize);
        var maxCellZ = (int)Math.Floor((center.Z + radius) / _cellSize);

        lock (_lock)
        {
            for (var cx = minCellX; cx <= maxCellX; cx++)
            {
                for (var cz = minCellZ; cz <= maxCellZ; cz++)
                {
                    var key = CellKey(cx, cz);
                    if (!_cells.TryGetValue(key, out var set))
                        continue;

                    foreach (var entityId in set)
                    {
                        if (!_entities.TryGetValue(entityId, out var entry))
                            continue;

                        var dx = entry.Position.X - center.X;
                        var dz = entry.Position.Z - center.Z;
                        if (dx * dx + dz * dz <= radiusSq)
                            results.Add(entityId);
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Compute squared XZ-plane distance between two tracked entities.
    /// Returns null if either entity is not in the grid.
    /// </summary>
    public double? DistanceSq(string entityA, string entityB)
    {
        if (!_entities.TryGetValue(entityA, out var a) || !_entities.TryGetValue(entityB, out var b))
            return null;
        var dx = a.Position.X - b.Position.X;
        var dz = a.Position.Z - b.Position.Z;
        return dx * dx + dz * dz;
    }

    private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;
}
