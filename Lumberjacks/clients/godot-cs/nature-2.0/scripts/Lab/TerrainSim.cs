using System;

namespace CommunitySurvival.Lab;

/// <summary>
/// Standalone terrain generation + erosion simulation.
/// No Godot dependency — pure math. Can run on server or in batch.
/// </summary>
public class TerrainSim
{
    public int Size { get; }
    public float[] Heightmap;
    public float[] OriginalHeight;
    public float[] Moisture;
    public float[] Flow;
    public int[] FlowDir;

    // Generation params
    public int Seed = 42;
    public int Octaves = 6;
    public float Frequency = 2.5f;
    public float SeaLevel = 0.3f;

    // Erosion params
    public float ErosionRate = 0.3f;
    public float DepositionRate = 0.3f;
    public float Evaporation = 0.01f;
    public float Inertia = 0.05f;
    public float SedimentCapacity = 4f;
    public int DropletLifetime = 50;

    // Climate
    public float WindAngle = 45f;
    public float WindStrength = 1f;

    public int TotalErosionIterations;

    public TerrainSim(int size)
    {
        Size = size;
        Heightmap = new float[size * size];
        OriginalHeight = new float[size * size];
        Moisture = new float[size * size];
        Flow = new float[size * size];
        FlowDir = new int[size * size];
    }

    public void Generate()
    {
        var rng = new Random(Seed);
        var offsets = new float[Octaves * 2];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = (float)(rng.NextDouble() * 1000);

        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                float nx = (float)x / Size, nz = (float)z / Size;
                float h = 0, amp = 1, freq = Frequency, totalAmp = 0;
                for (int o = 0; o < Octaves; o++)
                {
                    float sx = nx * freq + offsets[o * 2];
                    float sz = nz * freq + offsets[o * 2 + 1];
                    float n = (float)(Math.Sin(sx * 6.28 + Math.Cos(sz * 4.17)) *
                                     Math.Cos(sz * 6.28 + Math.Sin(sx * 3.71)) * 0.5 + 0.5);
                    h += n * amp;
                    totalAmp += amp;
                    amp *= 0.5f;
                    freq *= 2.1f;
                }
                h /= totalAmp;

                float dx = nx - 0.5f, dz = nz - 0.5f;
                float dist = (float)Math.Sqrt(dx * dx + dz * dz) * 2f;
                h *= Math.Max(0, 1f - dist * dist);

                Heightmap[z * Size + x] = h;
            }

        Array.Copy(Heightmap, OriginalHeight, Heightmap.Length);
        TotalErosionIterations = 0;
    }

    public void Erode(int iterations)
    {
        var rng = new Random(TotalErosionIterations + Seed * 1000);
        for (int i = 0; i < iterations; i++)
            SimulateDroplet(rng);
        TotalErosionIterations += iterations;
    }

    private void SimulateDroplet(Random rng)
    {
        float posX = (float)(rng.NextDouble() * (Size - 2) + 1);
        float posZ = (float)(rng.NextDouble() * (Size - 2) + 1);
        float dirX = 0, dirZ = 0, speed = 1, water = 1, sediment = 0;

        for (int step = 0; step < DropletLifetime; step++)
        {
            int ix = (int)posX, iz = (int)posZ;
            if (ix < 1 || ix >= Size - 2 || iz < 1 || iz >= Size - 2) break;

            float fx = posX - ix, fz = posZ - iz;
            float h00 = Heightmap[iz * Size + ix];
            float h10 = Heightmap[iz * Size + ix + 1];
            float h01 = Heightmap[(iz + 1) * Size + ix];
            float h11 = Heightmap[(iz + 1) * Size + ix + 1];

            float gradX = (h10 - h00) * (1 - fz) + (h11 - h01) * fz;
            float gradZ = (h01 - h00) * (1 - fx) + (h11 - h10) * fx;
            float height = h00 * (1 - fx) * (1 - fz) + h10 * fx * (1 - fz) + h01 * (1 - fx) * fz + h11 * fx * fz;

            dirX = dirX * Inertia - gradX * (1 - Inertia);
            dirZ = dirZ * Inertia - gradZ * (1 - Inertia);
            float len = (float)Math.Sqrt(dirX * dirX + dirZ * dirZ);
            if (len < 0.0001f) break;
            dirX /= len; dirZ /= len;

            float newX = posX + dirX, newZ = posZ + dirZ;
            int nix = (int)newX, niz = (int)newZ;
            if (nix < 0 || nix >= Size - 1 || niz < 0 || niz >= Size - 1) break;

            float nfx = newX - nix, nfz = newZ - niz;
            float newH = Heightmap[niz * Size + nix] * (1 - nfx) * (1 - nfz)
                       + Heightmap[niz * Size + nix + 1] * nfx * (1 - nfz)
                       + Heightmap[(niz + 1) * Size + nix] * (1 - nfx) * nfz
                       + Heightmap[(niz + 1) * Size + nix + 1] * nfx * nfz;

            float hDiff = newH - height;
            float capacity = Math.Max(-hDiff * speed * water * SedimentCapacity, 0.01f);

            if (sediment > capacity || hDiff > 0)
            {
                float dep = hDiff > 0 ? Math.Min(hDiff, sediment) : (sediment - capacity) * DepositionRate;
                sediment -= dep;
                Heightmap[iz * Size + ix] += dep * (1 - fx) * (1 - fz);
                Heightmap[iz * Size + ix + 1] += dep * fx * (1 - fz);
                Heightmap[(iz + 1) * Size + ix] += dep * (1 - fx) * fz;
                Heightmap[(iz + 1) * Size + ix + 1] += dep * fx * fz;
            }
            else
            {
                float ero = Math.Min((capacity - sediment) * ErosionRate, -hDiff);
                sediment += ero;
                Heightmap[iz * Size + ix] -= ero * (1 - fx) * (1 - fz);
                Heightmap[iz * Size + ix + 1] -= ero * fx * (1 - fz);
                Heightmap[(iz + 1) * Size + ix] -= ero * (1 - fx) * fz;
                Heightmap[(iz + 1) * Size + ix + 1] -= ero * fx * fz;
            }

            speed = (float)Math.Sqrt(Math.Max(speed * speed - hDiff, 0.01));
            water *= (1 - Evaporation);
            posX = newX; posZ = newZ;
        }
    }

    public void ComputeFlow()
    {
        Array.Clear(Flow, 0, Flow.Length);
        Array.Fill(FlowDir, -1);
        int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dz = { -1, -1, -1, 0, 0, 1, 1, 1 };

        for (int z = 1; z < Size - 1; z++)
            for (int x = 1; x < Size - 1; x++)
            {
                int idx = z * Size + x;
                float h = Heightmap[idx], steepest = 0;
                int best = -1;
                for (int d = 0; d < 8; d++)
                {
                    float drop = h - Heightmap[(z + dz[d]) * Size + (x + dx[d])];
                    if (drop > steepest) { steepest = drop; best = d; }
                }
                FlowDir[idx] = best;
            }

        var order = new int[Size * Size];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => Heightmap[b].CompareTo(Heightmap[a]));

        for (int i = 0; i < order.Length; i++)
        {
            int idx = order[i];
            Flow[idx] += 1;
            int dir = FlowDir[idx];
            if (dir < 0) continue;
            int x = idx % Size, z = idx / Size;
            int nx = x + dx[dir], nz = z + dz[dir];
            if (nx >= 0 && nx < Size && nz >= 0 && nz < Size)
                Flow[nz * Size + nx] += Flow[idx];
        }
    }

    public void ComputeMoisture()
    {
        float windRad = WindAngle * (float)Math.PI / 180f;
        float wx = (float)Math.Cos(windRad), wz = (float)Math.Sin(windRad);

        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int idx = z * Size + x;
                float h = Heightmap[idx];
                float baseMoist = h < SeaLevel ? 1f : Math.Max(0.1f, 1f - (h - SeaLevel) * 2f);
                float slopeX = 0, slopeZ = 0;
                if (x > 0 && x < Size - 1) slopeX = Heightmap[idx + 1] - Heightmap[idx - 1];
                if (z > 0 && z < Size - 1) slopeZ = Heightmap[idx + Size] - Heightmap[idx - Size];
                float windward = (slopeX * wx + slopeZ * wz) * WindStrength;
                Moisture[idx] = Math.Clamp(baseMoist + windward * 3f, 0f, 1f);
            }
    }

    // ——— Metrics ———

    public TerrainMetrics ComputeMetrics(float riverThreshold = 80f)
    {
        var m = new TerrainMetrics();
        float sumOrig = 0, sumFinal = 0, sumSq = 0;
        int landCells = 0, riverCells = 0;
        float maxH = 0, sumSlope = 0, sumSlopeSq = 0;
        int slopeCount = 0;

        for (int i = 0; i < Heightmap.Length; i++)
        {
            float h = Heightmap[i];
            sumOrig += OriginalHeight[i];
            sumFinal += h;
            sumSq += h * h;
            if (h > SeaLevel) landCells++;
            if (Flow[i] > riverThreshold && h > SeaLevel) riverCells++;
            if (h > maxH) maxH = h;
        }

        // Slope variance
        for (int z = 1; z < Size - 1; z++)
            for (int x = 1; x < Size - 1; x++)
            {
                int idx = z * Size + x;
                float sx = Heightmap[idx + 1] - Heightmap[idx - 1];
                float sz = Heightmap[idx + Size] - Heightmap[idx - Size];
                float slope = (float)Math.Sqrt(sx * sx + sz * sz);
                sumSlope += slope;
                sumSlopeSq += slope * slope;
                slopeCount++;
            }

        float n = Heightmap.Length;
        float mean = sumFinal / n;
        m.HeightRetention = sumOrig > 0 ? sumFinal / sumOrig : 1;
        m.ElevationVariance = (float)Math.Sqrt(sumSq / n - mean * mean);
        m.MaxRidgeHeight = maxH - SeaLevel;
        m.RiverDensity = landCells > 0 ? (float)riverCells / landCells : 0;
        m.SlopeVariance = slopeCount > 0 ? (float)Math.Sqrt(sumSlopeSq / slopeCount - (sumSlope / slopeCount) * (sumSlope / slopeCount)) : 0;
        m.LandFraction = (float)landCells / n;

        return m;
    }
}

public struct TerrainMetrics
{
    public float HeightRetention;   // mean(final) / mean(original)
    public float ElevationVariance; // std of heightmap
    public float MaxRidgeHeight;    // tallest peak above sea level
    public float RiverDensity;      // river cells / land cells
    public float SlopeVariance;     // std of slope
    public float LandFraction;      // land cells / total cells

    public string ToCsv() =>
        $"{HeightRetention:F4},{ElevationVariance:F4},{MaxRidgeHeight:F4},{RiverDensity:F4},{SlopeVariance:F4},{LandFraction:F4}";

    public static string CsvHeader =>
        "height_retention,elevation_variance,max_ridge_height,river_density,slope_variance,land_fraction";
}
