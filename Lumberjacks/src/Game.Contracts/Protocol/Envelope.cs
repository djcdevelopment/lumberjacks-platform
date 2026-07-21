using System.Text.Json;

namespace Game.Contracts.Protocol;

public record Envelope
{
    public int Version { get; init; } = EnvelopeFactory.ProtocolVersion;
    public required string Type { get; init; }
    public long Seq { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public JsonElement Payload { get; init; }
}

public static class EnvelopeFactory
{
    public const int ProtocolVersion = 1;
    private static long _seqCounter;

    public static Envelope Create(string type, object payload)
    {
        var json = JsonSerializer.SerializeToElement(payload, JsonOptions.Default);
        return new Envelope
        {
            Version = ProtocolVersion,
            Type = type,
            Seq = Interlocked.Increment(ref _seqCounter),
            Timestamp = DateTimeOffset.UtcNow,
            Payload = json,
        };
    }

    public static Envelope Parse(string raw)
    {
        return JsonSerializer.Deserialize<Envelope>(raw, JsonOptions.Default)
            ?? throw new JsonException("Failed to deserialize envelope");
    }

    public static string Serialize(Envelope envelope)
    {
        return JsonSerializer.Serialize(envelope, JsonOptions.Default);
    }
}
