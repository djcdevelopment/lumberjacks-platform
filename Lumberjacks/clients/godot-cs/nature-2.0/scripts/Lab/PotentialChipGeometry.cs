#nullable enable

using System;
using System.Collections.Generic;

namespace CommunitySurvival.Lab;

/// <summary>
/// Derives candidate chip volumes from retained cut history. A candidate is
/// geometry only; fracture and remaining support decide whether it releases.
/// </summary>
public static class PotentialChipSolver
{
    public static AxePotentialChip[] FindCandidates(WoodCutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return FindCandidates(state.CutMarks, state.CellSizeMeters);
    }

    public static AxePotentialChip[] FindCandidates(
        IReadOnlyList<AxeCutMark> marks,
        float minimumFeatureMeters = WoodCutState.DefaultCellSizeMeters)
    {
        ArgumentNullException.ThrowIfNull(marks);
        if (!float.IsFinite(minimumFeatureMeters) || minimumFeatureMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(minimumFeatureMeters));

        var candidates = new List<AxePotentialChip>();
        for (var newerIndex = 1; newerIndex < marks.Count; newerIndex++)
        {
            var newer = marks[newerIndex];
            for (var olderIndex = 0; olderIndex < newerIndex; olderIndex++)
            {
                var older = marks[olderIndex];
                if (!AreOpposing(older, newer, minimumFeatureMeters)) continue;

                var overlapStart = MathF.Max(older.MinimumFaceMeters, newer.MinimumFaceMeters);
                var overlapEnd = MathF.Min(older.MaximumFaceMeters, newer.MaximumFaceMeters);
                var overlapWidth = overlapEnd - overlapStart;
                if (overlapWidth < minimumFeatureMeters) continue;
                if (!older.TryFindDeepestIntersection(newer, out var intersection)) continue;
                if (intersection.Point.DepthMeters < minimumFeatureMeters) continue;

                var boundary = BuildBoundary(older, newer, intersection);
                var area = PolygonArea(boundary);
                if (area < minimumFeatureMeters * minimumFeatureMeters) continue;

                candidates.Add(new AxePotentialChip(
                    olderIndex,
                    newerIndex,
                    intersection.Point,
                    overlapStart,
                    overlapEnd,
                    area,
                    area * overlapWidth,
                    boundary));
            }
        }

        candidates.Sort((left, right) => right.VolumeCubicMeters.CompareTo(left.VolumeCubicMeters));
        return candidates.ToArray();
    }

    private static bool AreOpposing(
        AxeCutMark first,
        AxeCutMark second,
        float minimumFeatureMeters) =>
        MathF.Abs(first.VerticalTravelMeters) >= minimumFeatureMeters &&
        MathF.Abs(second.VerticalTravelMeters) >= minimumFeatureMeters &&
        MathF.Sign(first.VerticalTravelMeters) != MathF.Sign(second.VerticalTravelMeters);

    private static AxeCutMarkPoint[] BuildBoundary(
        AxeCutMark first,
        AxeCutMark second,
        AxeCutIntersection intersection)
    {
        var boundary = new List<AxeCutMarkPoint>(
            intersection.FirstSegmentStartIndex + intersection.SecondSegmentStartIndex + 4);
        for (var index = 0; index <= intersection.FirstSegmentStartIndex; index++)
            boundary.Add(first.Points[index]);
        boundary.Add(intersection.Point);
        for (var index = intersection.SecondSegmentStartIndex; index >= 0; index--)
            boundary.Add(second.Points[index]);
        return boundary.ToArray();
    }

    private static float PolygonArea(IReadOnlyList<AxeCutMarkPoint> polygon)
    {
        var twiceArea = 0f;
        for (var index = 0; index < polygon.Count; index++)
        {
            var next = (index + 1) % polygon.Count;
            twiceArea +=
                polygon[index].DepthMeters * polygon[next].VerticalMeters -
                polygon[next].DepthMeters * polygon[index].VerticalMeters;
        }
        return MathF.Abs(twiceArea) * 0.5f;
    }
}

public sealed record AxePotentialChip(
    int FirstCutIndex,
    int SecondCutIndex,
    AxeCutMarkPoint DeepestIntersection,
    float MinimumFaceMeters,
    float MaximumFaceMeters,
    float CrossSectionAreaSquareMeters,
    float VolumeCubicMeters,
    AxeCutMarkPoint[] Boundary)
{
    public float FaceWidthMeters => MaximumFaceMeters - MinimumFaceMeters;
}
