using System.Text.Json;
using Game.Contracts.Protocol;
using Xunit;

namespace Game.Contracts.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Create_produces_valid_envelope()
    {
        var envelope = EnvelopeFactory.Create(MessageType.SessionStarted, new { session_id = "s1" });

        Assert.Equal(EnvelopeFactory.ProtocolVersion, envelope.Version);
        Assert.Equal(MessageType.SessionStarted, envelope.Type);
        Assert.True(envelope.Seq > 0);
        Assert.True(envelope.Timestamp <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Serialize_and_parse_round_trip()
    {
        var original = EnvelopeFactory.Create(MessageType.PlayerMove,
            new PlayerMoveMessage(new Contracts.Entities.Vec3(1, 2, 3), new Contracts.Entities.Vec3(0, 0, 1)));

        var json = EnvelopeFactory.Serialize(original);
        var parsed = EnvelopeFactory.Parse(json);

        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.Type, parsed.Type);
        Assert.Equal(original.Seq, parsed.Seq);
    }

    [Fact]
    public void Serialize_uses_snake_case()
    {
        var envelope = EnvelopeFactory.Create(MessageType.Error, new ErrorMessage("TEST", "test error"));
        var json = EnvelopeFactory.Serialize(envelope);

        Assert.DoesNotContain("\"Version\"", json);
        Assert.DoesNotContain("\"Type\"", json);
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"type\"", json);
    }

    [Fact]
    public void Parse_throws_on_invalid_json()
    {
        Assert.Throws<JsonException>(() => EnvelopeFactory.Parse("not json"));
    }

    [Fact]
    public void Seq_increments_across_calls()
    {
        var e1 = EnvelopeFactory.Create(MessageType.Error, new { });
        var e2 = EnvelopeFactory.Create(MessageType.Error, new { });

        Assert.True(e2.Seq > e1.Seq);
    }
}
