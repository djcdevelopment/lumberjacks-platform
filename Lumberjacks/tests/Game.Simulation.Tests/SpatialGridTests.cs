using Game.Contracts.Entities;
using Game.Simulation.World;
using Xunit;

namespace Game.Simulation.Tests;

public class SpatialGridTests
{
    [Fact]
    public void InsertAndQueryFindsEntity()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(10, 0, 10));

        var results = grid.QueryRadius(new Vec3(10, 0, 10), radius: 5);
        Assert.Single(results);
        Assert.Equal("a", results[0]);
    }

    [Fact]
    public void QueryExcludesDistantEntities()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("near", new Vec3(10, 0, 10));
        grid.Update("far", new Vec3(500, 0, 500));

        var results = grid.QueryRadius(new Vec3(0, 0, 0), radius: 50);
        Assert.Single(results);
        Assert.Equal("near", results[0]);
    }

    [Fact]
    public void RemoveEntityExcludesFromQuery()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(10, 0, 10));
        grid.Remove("a");

        var results = grid.QueryRadius(new Vec3(10, 0, 10), radius: 50);
        Assert.Empty(results);
    }

    [Fact]
    public void UpdateMovesEntityBetweenCells()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(10, 0, 10)); // cell (0,0)

        // Move to a different cell
        grid.Update("a", new Vec3(200, 0, 200)); // cell (4,4)

        // Should not be found near original position
        var nearOrigin = grid.QueryRadius(new Vec3(10, 0, 10), radius: 20);
        Assert.Empty(nearOrigin);

        // Should be found near new position
        var nearNew = grid.QueryRadius(new Vec3(200, 0, 200), radius: 20);
        Assert.Single(nearNew);
    }

    [Fact]
    public void UpdateWithinSameCellPreservesEntity()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(10, 0, 10));
        grid.Update("a", new Vec3(15, 0, 15)); // same cell

        Assert.Equal(1, grid.Count);
        var results = grid.QueryRadius(new Vec3(15, 0, 15), radius: 5);
        Assert.Single(results);
    }

    [Fact]
    public void DistanceSqReturnCorrectValue()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(0, 0, 0));
        grid.Update("b", new Vec3(3, 0, 4)); // XZ distance = 5, sq = 25

        var distSq = grid.DistanceSq("a", "b");
        Assert.NotNull(distSq);
        Assert.Equal(25.0, distSq.Value, precision: 5);
    }

    [Fact]
    public void DistanceSqReturnsNullForMissingEntity()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(0, 0, 0));

        Assert.Null(grid.DistanceSq("a", "missing"));
    }

    [Fact]
    public void QueryIgnoresYAxis()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(10, 999, 10)); // far away in Y, close in XZ

        var results = grid.QueryRadius(new Vec3(10, 0, 10), radius: 5);
        Assert.Single(results); // Y is ignored for spatial queries
    }

    [Fact]
    public void QuerySpanningMultipleCells()
    {
        var grid = new SpatialGrid(cellSize: 50);
        // Entities in different cells but within radius
        grid.Update("a", new Vec3(48, 0, 0));  // cell (0,0)
        grid.Update("b", new Vec3(52, 0, 0));  // cell (1,0)

        var results = grid.QueryRadius(new Vec3(50, 0, 0), radius: 10);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void CountTracksEntities()
    {
        var grid = new SpatialGrid(cellSize: 50);
        Assert.Equal(0, grid.Count);

        grid.Update("a", new Vec3(0, 0, 0));
        grid.Update("b", new Vec3(100, 0, 100));
        Assert.Equal(2, grid.Count);

        grid.Remove("a");
        Assert.Equal(1, grid.Count);
    }

    [Fact]
    public void GetPositionReturnsLastKnownPosition()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(10, 20, 30));

        var pos = grid.GetPosition("a");
        Assert.NotNull(pos);
        Assert.Equal(10, pos.Value.X);
        Assert.Equal(20, pos.Value.Y);
        Assert.Equal(30, pos.Value.Z);
    }

    [Fact]
    public void GetPositionReturnsNullForMissing()
    {
        var grid = new SpatialGrid(cellSize: 50);
        Assert.Null(grid.GetPosition("missing"));
    }

    [Fact]
    public void NegativeCoordinatesWork()
    {
        var grid = new SpatialGrid(cellSize: 50);
        grid.Update("a", new Vec3(-100, 0, -100));

        var results = grid.QueryRadius(new Vec3(-100, 0, -100), radius: 10);
        Assert.Single(results);
    }
}
