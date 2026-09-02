using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunitySurvival.Forest;

/// <summary>
/// Editable, deterministic visual-only storm input. This is deliberately not a
/// network contract: the R&D lab can evolve without turning tuning knobs into a
/// compatibility promise.
/// </summary>
public sealed record ForestStormScenario
{
    public const string SchemaId = "lumberjacks.forest-storm/v1";

    [JsonPropertyName("schema")]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyName("seed")]
    public uint Seed { get; init; } = 0xC0FFEEu;

    [JsonPropertyName("duration_seconds")]
    public float DurationSeconds { get; init; } = 80f;

    [JsonPropertyName("center_x")]
    public float CenterX { get; init; }

    [JsonPropertyName("center_z")]
    public float CenterZ { get; init; }

    [JsonPropertyName("radius_meters")]
    public float RadiusMeters { get; init; } = 240f;

    [JsonPropertyName("wind_direction_degrees")]
    public float WindDirectionDegrees { get; init; } = 35f;

    [JsonPropertyName("peak_wind_mps")]
    public float PeakWindMetersPerSecond { get; init; } = 27f;

    [JsonPropertyName("moisture")]
    public float Moisture { get; init; } = 0.78f;

    [JsonPropertyName("temperature_c")]
    public float TemperatureCelsius { get; init; } = 9f;

    [JsonPropertyName("turbulence")]
    public float Turbulence { get; init; } = 0.72f;

    [JsonPropertyName("intensity_curve")]
    public float[] IntensityCurve { get; init; } = [0f, 0.04f, 0.42f, 1f, 0.88f, 0.52f, 0.26f, 0f];

    public static ForestStormScenario Load(string path)
    {
        return Parse(File.ReadAllText(path));
    }

    public static ForestStormScenario Parse(string json)
    {
        var scenario = JsonSerializer.Deserialize<ForestStormScenario>(json, JsonOptions())
            ?? throw new InvalidDataException("The forest storm scenario is empty.");
        scenario.Validate();
        return scenario;
    }

    public string ToCanonicalJson() => JsonSerializer.Serialize(this, JsonOptions());

    public string ContentHash()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public float EvaluateIntensity(float elapsedSeconds)
    {
        Validate();
        var wrapped = elapsedSeconds % DurationSeconds;
        if (wrapped < 0f) wrapped += DurationSeconds;
        var position = wrapped / DurationSeconds * (IntensityCurve.Length - 1);
        var low = Math.Min((int)MathF.Floor(position), IntensityCurve.Length - 1);
        var high = Math.Min(low + 1, IntensityCurve.Length - 1);
        var blend = position - low;
        return IntensityCurve[low] + (IntensityCurve[high] - IntensityCurve[low]) * blend;
    }

    public void Validate()
    {
        if (!string.Equals(Schema, SchemaId, StringComparison.Ordinal))
            throw new InvalidDataException($"Expected scenario schema {SchemaId}.");
        RequireFinite(DurationSeconds, nameof(DurationSeconds));
        RequireFinite(CenterX, nameof(CenterX));
        RequireFinite(CenterZ, nameof(CenterZ));
        RequireFinite(RadiusMeters, nameof(RadiusMeters));
        RequireFinite(WindDirectionDegrees, nameof(WindDirectionDegrees));
        RequireFinite(PeakWindMetersPerSecond, nameof(PeakWindMetersPerSecond));
        RequireFinite(Moisture, nameof(Moisture));
        RequireFinite(TemperatureCelsius, nameof(TemperatureCelsius));
        RequireFinite(Turbulence, nameof(Turbulence));

        if (DurationSeconds is < 1f or > 3600f) throw new InvalidDataException("duration_seconds must be in [1, 3600].");
        if (RadiusMeters is <= 0f or > 5000f) throw new InvalidDataException("radius_meters must be in (0, 5000].");
        if (WindDirectionDegrees is < 0f or >= 360f) throw new InvalidDataException("wind_direction_degrees must be in [0, 360).");
        if (PeakWindMetersPerSecond is < 0f or > 80f) throw new InvalidDataException("peak_wind_mps must be in [0, 80].");
        if (Moisture is < 0f or > 1f) throw new InvalidDataException("moisture must be in [0, 1].");
        if (TemperatureCelsius is < -60f or > 60f) throw new InvalidDataException("temperature_c must be in [-60, 60].");
        if (Turbulence is < 0f or > 1f) throw new InvalidDataException("turbulence must be in [0, 1].");
        if (IntensityCurve is not { Length: 8 }) throw new InvalidDataException("intensity_curve must contain exactly eight samples.");
        foreach (var sample in IntensityCurve)
        {
            RequireFinite(sample, "intensity_curve sample");
            if (sample is < 0f or > 1f) throw new InvalidDataException("intensity_curve samples must be in [0, 1].");
        }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
    };

    private static void RequireFinite(float value, string name)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException($"{name} must be finite.");
    }
}
