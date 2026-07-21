using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Xunit;

namespace Game.Contracts.Tests;

public class MessageTypeMappingTests
{
    [Theory]
    [InlineData(MessageType.JoinRegion, MessageTypeId.JoinRegion)]
    [InlineData(MessageType.LeaveRegion, MessageTypeId.LeaveRegion)]
    [InlineData(MessageType.PlayerMove, MessageTypeId.PlayerMove)]
    [InlineData(MessageType.PlaceStructure, MessageTypeId.PlaceStructure)]
    [InlineData(MessageType.Interact, MessageTypeId.Interact)]
    [InlineData(MessageType.SessionStarted, MessageTypeId.SessionStarted)]
    [InlineData(MessageType.WorldSnapshot, MessageTypeId.WorldSnapshot)]
    [InlineData(MessageType.EntityUpdate, MessageTypeId.EntityUpdate)]
    [InlineData(MessageType.EntityRemoved, MessageTypeId.EntityRemoved)]
    [InlineData(MessageType.PriorityManifest, MessageTypeId.PriorityManifest)]
    [InlineData(MessageType.EventEmitted, MessageTypeId.EventEmitted)]
    [InlineData(MessageType.Error, MessageTypeId.Error)]
    public void String_to_id_and_back_roundtrip(string name, MessageTypeId expectedId)
    {
        var id = MessageTypeMapping.ToId(name);
        Assert.Equal(expectedId, id);

        var restored = MessageTypeMapping.ToName(id);
        Assert.Equal(name, restored);
    }

    [Fact]
    public void Unknown_string_type_throws()
    {
        Assert.Throws<ArgumentException>(() => MessageTypeMapping.ToId("nonexistent"));
    }

    [Fact]
    public void Unknown_id_throws()
    {
        Assert.Throws<ArgumentException>(() => MessageTypeMapping.ToName((MessageTypeId)99));
    }

    [Fact]
    public void TryGetId_returns_false_for_unknown()
    {
        Assert.False(MessageTypeMapping.TryGetId("nope", out _));
    }

    [Fact]
    public void TryGetName_returns_false_for_unknown()
    {
        Assert.False(MessageTypeMapping.TryGetName((MessageTypeId)99, out _));
    }

    [Fact]
    public void All_message_type_ids_fit_in_6_bits()
    {
        // BinaryEnvelope allocates 6 bits for type (max 63)
        foreach (MessageTypeId id in Enum.GetValues<MessageTypeId>())
        {
            Assert.True((byte)id <= 63, $"MessageTypeId.{id} = {(byte)id} exceeds 6-bit max (63)");
        }
    }
}
