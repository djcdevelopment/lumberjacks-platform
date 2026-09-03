#nullable enable

using System;
using System.Collections.Generic;

namespace CommunitySurvival.Lab;

/// <summary>
/// A deterministic side section through an upright trunk. Depth zero is the
/// bark surface and positive depth points toward the trunk core. The field is
/// deliberately material-only: Godot rendering is a projection of this state.
/// </summary>
public sealed class WoodCutState
{
    public const float DefaultTrunkDiameterMeters = 0.50f;
    public const float DefaultBandHeightMeters = 0.70f;
    public const float DefaultCellSizeMeters = 0.005f;

    private readonly WoodCellState[] _cells;
    private readonly AxeCutMark[] _cutMarks;

    private WoodCutState(
        float trunkDiameterMeters,
        float bandHeightMeters,
        float cellSizeMeters,
        int depthCells,
        int heightCells,
        WoodCellState[] cells,
        AxeCutMark[] cutMarks,
        int strikeCount)
    {
        TrunkDiameterMeters = trunkDiameterMeters;
        BandHeightMeters = bandHeightMeters;
        CellSizeMeters = cellSizeMeters;
        DepthCells = depthCells;
        HeightCells = heightCells;
        _cells = cells;
        _cutMarks = cutMarks;
        StrikeCount = strikeCount;
    }

    public float TrunkDiameterMeters { get; }
    public float BandHeightMeters { get; }
    public float CellSizeMeters { get; }
    public int DepthCells { get; }
    public int HeightCells { get; }
    public int StrikeCount { get; }
    public IReadOnlyList<AxeCutMark> CutMarks => _cutMarks;
    public int CutMarkCount => _cutMarks.Length;
    public float MinimumVerticalMeters => -BandHeightMeters * 0.5f;
    public int KerfCellCount => Count(WoodCellState.Kerf);
    public int ReleasedCellCount => Count(WoodCellState.Released);
    public float KerfAreaSquareMeters => KerfCellCount * CellSizeMeters * CellSizeMeters;

    public static WoodCutState Fresh(
        float trunkDiameterMeters = DefaultTrunkDiameterMeters,
        float bandHeightMeters = DefaultBandHeightMeters,
        float cellSizeMeters = DefaultCellSizeMeters)
    {
        if (!float.IsFinite(trunkDiameterMeters) || trunkDiameterMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(trunkDiameterMeters));
        if (!float.IsFinite(bandHeightMeters) || bandHeightMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(bandHeightMeters));
        if (!float.IsFinite(cellSizeMeters) || cellSizeMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSizeMeters));

        var depthCells = Math.Max(1, (int)MathF.Ceiling(trunkDiameterMeters / cellSizeMeters));
        var heightCells = Math.Max(1, (int)MathF.Ceiling(bandHeightMeters / cellSizeMeters));
        return new WoodCutState(
            trunkDiameterMeters,
            bandHeightMeters,
            cellSizeMeters,
            depthCells,
            heightCells,
            new WoodCellState[depthCells * heightCells],
            Array.Empty<AxeCutMark>(),
            0);
    }

    public WoodCellState Cell(int depthIndex, int heightIndex)
    {
        if ((uint)depthIndex >= (uint)DepthCells)
            throw new ArgumentOutOfRangeException(nameof(depthIndex));
        if ((uint)heightIndex >= (uint)HeightCells)
            throw new ArgumentOutOfRangeException(nameof(heightIndex));
        return _cells[heightIndex * DepthCells + depthIndex];
    }

    public WoodCellState CellAt(float depthMeters, float verticalMeters)
    {
        var depthIndex = (int)MathF.Floor(depthMeters / CellSizeMeters);
        var heightIndex = (int)MathF.Floor(
            (verticalMeters - MinimumVerticalMeters) / CellSizeMeters);
        if ((uint)depthIndex >= (uint)DepthCells || (uint)heightIndex >= (uint)HeightCells)
            return WoodCellState.Air;
        return _cells[heightIndex * DepthCells + depthIndex];
    }

    public float ResistanceMultiplierAt(float depthMeters, float verticalMeters) =>
        CellAt(depthMeters, verticalMeters) switch
        {
            WoodCellState.Intact => 1f,
            WoodCellState.Kerf => 0.12f,
            WoodCellState.Released => 0f,
            _ => 0f,
        };

    public WoodCutState ApplyKerf(
        IReadOnlyList<AxeBitePathPoint> path,
        float edgeThicknessMeters,
        float wedgeHalfAngleRadians,
        float cutWidthMeters = 0.10f,
        float faceCenterMeters = 0f)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
            throw new ArgumentException("A bite path must contain at least one point.", nameof(path));
        if (!float.IsFinite(edgeThicknessMeters) || edgeThicknessMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(edgeThicknessMeters));
        if (!float.IsFinite(wedgeHalfAngleRadians) || wedgeHalfAngleRadians < 0f ||
            wedgeHalfAngleRadians >= MathF.PI * 0.5f)
            throw new ArgumentOutOfRangeException(nameof(wedgeHalfAngleRadians));
        if (!float.IsFinite(cutWidthMeters) || cutWidthMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cutWidthMeters));
        if (!float.IsFinite(faceCenterMeters))
            throw new ArgumentOutOfRangeException(nameof(faceCenterMeters));

        var cells = (WoodCellState[])_cells.Clone();
        var cumulative = new float[path.Count];
        for (var index = 1; index < path.Count; index++)
        {
            var deltaDepth = path[index].DepthMeters - path[index - 1].DepthMeters;
            var deltaVertical = path[index].VerticalMeters - path[index - 1].VerticalMeters;
            cumulative[index] = cumulative[index - 1] +
                MathF.Sqrt(deltaDepth * deltaDepth + deltaVertical * deltaVertical);
        }

        var totalLength = cumulative[^1];
        var tangent = MathF.Tan(wedgeHalfAngleRadians);
        for (var pointIndex = 0; pointIndex < path.Count; pointIndex++)
        {
            var point = path[pointIndex];
            var remaining = totalLength - cumulative[pointIndex];
            var halfWidth = edgeThicknessMeters * 0.5f + remaining * tangent;
            RasterizeDisc(cells, point.DepthMeters, point.VerticalMeters, halfWidth);
        }

        var cutMarks = new AxeCutMark[_cutMarks.Length + 1];
        Array.Copy(_cutMarks, cutMarks, _cutMarks.Length);
        cutMarks[^1] = AxeCutMark.FromPath(path, cutWidthMeters, faceCenterMeters);
        return new WoodCutState(
            TrunkDiameterMeters,
            BandHeightMeters,
            CellSizeMeters,
            DepthCells,
            HeightCells,
            cells,
            cutMarks,
            StrikeCount + 1);
    }

    public ulong StableHash()
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var cell in _cells)
        {
            hash ^= (byte)cell;
            hash *= prime;
        }

        hash ^= (uint)StrikeCount;
        hash *= prime;
        foreach (var mark in _cutMarks)
        {
            hash ^= (uint)BitConverter.SingleToInt32Bits(mark.CutWidthMeters);
            hash *= prime;
            hash ^= (uint)BitConverter.SingleToInt32Bits(mark.FaceCenterMeters);
            hash *= prime;
            foreach (var point in mark.Points)
            {
                hash ^= (uint)BitConverter.SingleToInt32Bits(point.DepthMeters);
                hash *= prime;
                hash ^= (uint)BitConverter.SingleToInt32Bits(point.VerticalMeters);
                hash *= prime;
            }
        }
        return hash;
    }

    private void RasterizeDisc(
        WoodCellState[] cells,
        float depthMeters,
        float verticalMeters,
        float radiusMeters)
    {
        var minDepth = Math.Max(0, (int)MathF.Floor((depthMeters - radiusMeters) / CellSizeMeters));
        var maxDepth = Math.Min(
            DepthCells - 1,
            (int)MathF.Floor((depthMeters + radiusMeters) / CellSizeMeters));
        var minHeight = Math.Max(
            0,
            (int)MathF.Floor((verticalMeters - radiusMeters - MinimumVerticalMeters) / CellSizeMeters));
        var maxHeight = Math.Min(
            HeightCells - 1,
            (int)MathF.Floor((verticalMeters + radiusMeters - MinimumVerticalMeters) / CellSizeMeters));
        var radiusSquared = MathF.Max(radiusMeters * radiusMeters, CellSizeMeters * CellSizeMeters * 0.25f);

        for (var heightIndex = minHeight; heightIndex <= maxHeight; heightIndex++)
        {
            var centerVertical = MinimumVerticalMeters + (heightIndex + 0.5f) * CellSizeMeters;
            for (var depthIndex = minDepth; depthIndex <= maxDepth; depthIndex++)
            {
                var centerDepth = (depthIndex + 0.5f) * CellSizeMeters;
                var deltaDepth = centerDepth - depthMeters;
                var deltaVertical = centerVertical - verticalMeters;
                if (deltaDepth * deltaDepth + deltaVertical * deltaVertical > radiusSquared)
                    continue;

                var cellIndex = heightIndex * DepthCells + depthIndex;
                if (cells[cellIndex] == WoodCellState.Intact)
                    cells[cellIndex] = WoodCellState.Kerf;
            }
        }
    }

    private int Count(WoodCellState state)
    {
        var count = 0;
        foreach (var cell in _cells)
            if (cell == state) count++;
        return count;
    }
}

public sealed class AxeCutMark
{
    public AxeCutMark(
        AxeCutMarkPoint[] points,
        float cutWidthMeters = 0.10f,
        float faceCenterMeters = 0f)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Length < 2)
            throw new ArgumentException("A cut mark must contain at least two points.", nameof(points));
        if (!float.IsFinite(cutWidthMeters) || cutWidthMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cutWidthMeters));
        if (!float.IsFinite(faceCenterMeters))
            throw new ArgumentOutOfRangeException(nameof(faceCenterMeters));
        Points = (AxeCutMarkPoint[])points.Clone();
        CutWidthMeters = cutWidthMeters;
        FaceCenterMeters = faceCenterMeters;
    }

    public AxeCutMarkPoint[] Points { get; }
    public float CutWidthMeters { get; }
    public float FaceCenterMeters { get; }
    public float MinimumFaceMeters => FaceCenterMeters - CutWidthMeters * 0.5f;
    public float MaximumFaceMeters => FaceCenterMeters + CutWidthMeters * 0.5f;
    public float VerticalTravelMeters => Points[^1].VerticalMeters - Points[0].VerticalMeters;
    public float PenetrationMeters
    {
        get
        {
            var deepest = 0f;
            foreach (var point in Points)
                deepest = MathF.Max(deepest, point.DepthMeters);
            return deepest;
        }
    }

    public bool Intersects(AxeCutMark other, float toleranceMeters)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!float.IsFinite(toleranceMeters) || toleranceMeters < 0f)
            throw new ArgumentOutOfRangeException(nameof(toleranceMeters));

        for (var first = 1; first < Points.Length; first++)
        for (var second = 1; second < other.Points.Length; second++)
        {
            if (SegmentsIntersect(
                Points[first - 1], Points[first],
                other.Points[second - 1], other.Points[second],
                toleranceMeters))
                return true;
        }
        return false;
    }

    public bool TryFindDeepestIntersection(
        AxeCutMark other,
        out AxeCutIntersection intersection)
    {
        ArgumentNullException.ThrowIfNull(other);
        intersection = default;
        var found = false;

        for (var first = 1; first < Points.Length; first++)
        for (var second = 1; second < other.Points.Length; second++)
        {
            if (!TrySegmentIntersection(
                Points[first - 1], Points[first],
                other.Points[second - 1], other.Points[second],
                out var point,
                out var firstFraction,
                out var secondFraction))
                continue;
            if (found && point.DepthMeters <= intersection.Point.DepthMeters)
                continue;

            intersection = new AxeCutIntersection(
                point,
                first - 1,
                firstFraction,
                second - 1,
                secondFraction);
            found = true;
        }
        return found;
    }

    public static AxeCutMark FromPath(
        IReadOnlyList<AxeBitePathPoint> path,
        float cutWidthMeters = 0.10f,
        float faceCenterMeters = 0f)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count < 2)
            throw new ArgumentException("A cut path must contain at least two points.", nameof(path));
        var points = new AxeCutMarkPoint[path.Count];
        for (var index = 0; index < path.Count; index++)
            points[index] = new AxeCutMarkPoint(path[index].DepthMeters, path[index].VerticalMeters);
        return new AxeCutMark(points, cutWidthMeters, faceCenterMeters);
    }

    private static bool TrySegmentIntersection(
        AxeCutMarkPoint a,
        AxeCutMarkPoint b,
        AxeCutMarkPoint c,
        AxeCutMarkPoint d,
        out AxeCutMarkPoint point,
        out float firstFraction,
        out float secondFraction)
    {
        var firstDepth = b.DepthMeters - a.DepthMeters;
        var firstVertical = b.VerticalMeters - a.VerticalMeters;
        var secondDepth = d.DepthMeters - c.DepthMeters;
        var secondVertical = d.VerticalMeters - c.VerticalMeters;
        var denominator = firstDepth * secondVertical - firstVertical * secondDepth;
        if (MathF.Abs(denominator) < 0.0000001f)
        {
            point = default;
            firstFraction = 0f;
            secondFraction = 0f;
            return false;
        }

        var offsetDepth = c.DepthMeters - a.DepthMeters;
        var offsetVertical = c.VerticalMeters - a.VerticalMeters;
        firstFraction =
            (offsetDepth * secondVertical - offsetVertical * secondDepth) / denominator;
        secondFraction =
            (offsetDepth * firstVertical - offsetVertical * firstDepth) / denominator;
        if (firstFraction is < 0f or > 1f || secondFraction is < 0f or > 1f)
        {
            point = default;
            return false;
        }

        point = new AxeCutMarkPoint(
            a.DepthMeters + firstDepth * firstFraction,
            a.VerticalMeters + firstVertical * firstFraction);
        return true;
    }

    private static bool SegmentsIntersect(
        AxeCutMarkPoint a,
        AxeCutMarkPoint b,
        AxeCutMarkPoint c,
        AxeCutMarkPoint d,
        float tolerance)
    {
        if (DistanceToSegment(a, c, d) <= tolerance ||
            DistanceToSegment(b, c, d) <= tolerance ||
            DistanceToSegment(c, a, b) <= tolerance ||
            DistanceToSegment(d, a, b) <= tolerance)
            return true;

        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        return MathF.Sign(abC) != MathF.Sign(abD) && MathF.Sign(cdA) != MathF.Sign(cdB);
    }

    private static float Cross(AxeCutMarkPoint a, AxeCutMarkPoint b, AxeCutMarkPoint point) =>
        (b.DepthMeters - a.DepthMeters) * (point.VerticalMeters - a.VerticalMeters) -
        (b.VerticalMeters - a.VerticalMeters) * (point.DepthMeters - a.DepthMeters);

    private static float DistanceToSegment(
        AxeCutMarkPoint point,
        AxeCutMarkPoint start,
        AxeCutMarkPoint end)
    {
        var dx = end.DepthMeters - start.DepthMeters;
        var dy = end.VerticalMeters - start.VerticalMeters;
        var lengthSquared = dx * dx + dy * dy;
        var amount = lengthSquared > 0f
            ? Math.Clamp(
                ((point.DepthMeters - start.DepthMeters) * dx +
                 (point.VerticalMeters - start.VerticalMeters) * dy) / lengthSquared,
                0f,
                1f)
            : 0f;
        var nearestDepth = start.DepthMeters + dx * amount;
        var nearestVertical = start.VerticalMeters + dy * amount;
        var distanceDepth = point.DepthMeters - nearestDepth;
        var distanceVertical = point.VerticalMeters - nearestVertical;
        return MathF.Sqrt(distanceDepth * distanceDepth + distanceVertical * distanceVertical);
    }
}

public readonly record struct AxeCutMarkPoint(float DepthMeters, float VerticalMeters);

public readonly record struct AxeCutIntersection(
    AxeCutMarkPoint Point,
    int FirstSegmentStartIndex,
    float FirstSegmentFraction,
    int SecondSegmentStartIndex,
    float SecondSegmentFraction);

public static class GrowthRingPattern
{
    public static float[] ConcentricRadii(
        float trunkRadiusMeters,
        int ringCount = 14,
        float centerDensityExponent = 1.7f)
    {
        if (!float.IsFinite(trunkRadiusMeters) || trunkRadiusMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(trunkRadiusMeters));
        if (ringCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(ringCount));
        if (!float.IsFinite(centerDensityExponent) || centerDensityExponent <= 1f)
            throw new ArgumentOutOfRangeException(nameof(centerDensityExponent));

        var radii = new float[ringCount];
        for (var index = 1; index <= ringCount; index++)
            radii[index - 1] = trunkRadiusMeters * MathF.Pow((float)index / ringCount, centerDensityExponent);
        return radii;
    }

    public static int CountCrossed(float trunkRadiusMeters, float penetrationMeters, float[] radii)
    {
        ArgumentNullException.ThrowIfNull(radii);
        var innerRadius = MathF.Max(0f, trunkRadiusMeters - MathF.Max(0f, penetrationMeters));
        var crossed = 0;
        foreach (var radius in radii)
            if (radius >= innerRadius && radius <= trunkRadiusMeters) crossed++;
        return crossed;
    }
}

public enum WoodCellState : byte
{
    Intact = 0,
    Kerf = 1,
    Released = 2,
    Air = 3,
}
