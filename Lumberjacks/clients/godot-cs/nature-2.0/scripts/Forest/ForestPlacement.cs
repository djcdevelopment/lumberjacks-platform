using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using CommunitySurvival.Lab;

namespace CommunitySurvival.Forest;

public enum ForestArchetype : byte
{
    Pine = 0,
    Common = 1,
    Twisted = 2,
}

public readonly record struct ForestPlacement(
    float X,
    float Y,
    float Z,
    float Yaw,
    float Scale,
    float Phase,
    float Stiffness,
    float CrownMass,
    float Moisture,
    int ChunkX,
    int ChunkZ,
    ForestArchetype Archetype);

public sealed record ForestGeneration(
    int GridSize,
    float WorldSize,
    float HeightScale,
    float SeaLevel,
    float[] Heightmap,
    float[] Moisture,
    IReadOnlyList<ForestPlacement> Placements,
    string PlacementHash);

/// <summary>Pure CPU generation suitable for a worker thread and deterministic tests.</summary>
public static class ForestPlacementGenerator
{
    public const float ChunkSize = 64f;

    public static ForestGeneration Generate(ForestStormScenario scenario, int count,
        int gridSize = 128, float worldSize = 512f, float heightScale = 34f)
    {
        scenario.Validate();
        if (count is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(count));
        if (gridSize is < 16 or > 1024) throw new ArgumentOutOfRangeException(nameof(gridSize));

        var terrain = new TerrainSim(gridSize)
        {
            Seed = unchecked((int)scenario.Seed),
            Frequency = 2.8f,
            Octaves = 6,
            SeaLevel = 0.24f,
            WindAngle = scenario.WindDirectionDegrees,
            WindStrength = 1.2f,
        };
        terrain.Generate();
        terrain.ComputeMoisture();

        var placements = new List<ForestPlacement>(count);
        var random = new XorShift32(scenario.Seed == 0 ? 0x9E3779B9u : scenario.Seed);
        var half = worldSize * 0.5f;
        var attempts = 0;
        var maxAttempts = count * 20;
        while (placements.Count < count && attempts++ < maxAttempts)
        {
            var x = random.NextFloat(-half + 4f, half - 4f);
            var z = random.NextFloat(-half + 4f, half - 4f);
            var islandDistance = MathF.Sqrt(x * x + z * z) / half;
            var normalizedHeight = Sample(terrain.Heightmap, gridSize, x, z, worldSize);
            if (normalizedHeight <= terrain.SeaLevel + 0.018f || islandDistance > 0.94f) continue;

            var moisture = Sample(terrain.Moisture, gridSize, x, z, worldSize);
            var speciesRoll = random.Next01();
            ForestArchetype archetype;
            if (moisture > 0.68f) archetype = speciesRoll < 0.62f ? ForestArchetype.Pine : ForestArchetype.Common;
            else if (moisture < 0.38f) archetype = speciesRoll < 0.58f ? ForestArchetype.Twisted : ForestArchetype.Pine;
            else archetype = speciesRoll < 0.46f ? ForestArchetype.Common : speciesRoll < 0.78f ? ForestArchetype.Pine : ForestArchetype.Twisted;

            var traits = Traits(archetype);
            var scale = traits.Scale * random.NextFloat(0.78f, 1.22f);
            var stiffness = traits.Stiffness * random.NextFloat(0.88f, 1.12f);
            var crown = traits.CrownMass * random.NextFloat(0.86f, 1.14f);
            placements.Add(new ForestPlacement(
                x,
                normalizedHeight * heightScale,
                z,
                random.NextFloat(0f, MathF.PI * 2f),
                scale,
                random.Next01(),
                stiffness,
                crown,
                moisture,
                (int)MathF.Floor((x + half) / ChunkSize),
                (int)MathF.Floor((z + half) / ChunkSize),
                archetype));
        }

        if (placements.Count != count)
            throw new InvalidOperationException($"Could only place {placements.Count} of {count} trees.");

        return new ForestGeneration(gridSize, worldSize, heightScale, terrain.SeaLevel,
            terrain.Heightmap, terrain.Moisture, placements, Hash(placements));
    }

    public static float Sample(float[] values, int gridSize, float x, float z, float worldSize)
    {
        var gx = Math.Clamp((x / worldSize + 0.5f) * (gridSize - 1), 0f, gridSize - 1.001f);
        var gz = Math.Clamp((z / worldSize + 0.5f) * (gridSize - 1), 0f, gridSize - 1.001f);
        var ix = Math.Min((int)gx, gridSize - 2);
        var iz = Math.Min((int)gz, gridSize - 2);
        var fx = gx - ix;
        var fz = gz - iz;
        var top = Lerp(values[iz * gridSize + ix], values[iz * gridSize + ix + 1], fx);
        var bottom = Lerp(values[(iz + 1) * gridSize + ix], values[(iz + 1) * gridSize + ix + 1], fx);
        return Lerp(top, bottom, fz);
    }

    private static (float Scale, float Stiffness, float CrownMass) Traits(ForestArchetype archetype) => archetype switch
    {
        ForestArchetype.Pine => (0.98f, 0.86f, 0.72f),
        ForestArchetype.Common => (0.92f, 0.66f, 0.90f),
        ForestArchetype.Twisted => (0.48f, 1.14f, 1.18f),
        _ => throw new ArgumentOutOfRangeException(nameof(archetype)),
    };

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static string Hash(IReadOnlyList<ForestPlacement> placements)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[48];
        foreach (var p in placements)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes[0..4], p.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[4..8], p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[8..12], p.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[12..16], p.Yaw);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[16..20], p.Scale);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[20..24], p.Phase);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[24..28], p.Stiffness);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[28..32], p.CrownMass);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[32..36], p.Moisture);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[36..40], p.ChunkX);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[40..44], p.ChunkZ);
            bytes[44] = (byte)p.Archetype;
            bytes[45] = bytes[46] = bytes[47] = 0;
            incremental.AppendData(bytes);
        }
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    private struct XorShift32(uint state)
    {
        private uint _state = state;

        public float Next01()
        {
            var x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return (x >> 8) * (1f / 16777216f);
        }

        public float NextFloat(float min, float max) => min + (max - min) * Next01();
    }
}
