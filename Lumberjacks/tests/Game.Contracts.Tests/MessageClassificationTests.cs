using Game.Contracts.Protocol;
using Xunit;

namespace Game.Contracts.Tests;

public class MessageClassificationTests
{
    [Theory]
    [InlineData(MessageType.JoinRegion, DeliveryLane.Reliable)]
    [InlineData(MessageType.LeaveRegion, DeliveryLane.Reliable)]
    [InlineData(MessageType.SessionStarted, DeliveryLane.Reliable)]
    [InlineData(MessageType.Error, DeliveryLane.Reliable)]
    [InlineData(MessageType.PriorityManifest, DeliveryLane.Reliable)]
    [InlineData(MessageType.PlayerMove, DeliveryLane.Datagram)]
    [InlineData(MessageType.EntityUpdate, DeliveryLane.Datagram)]
    public void Known_messages_have_correct_lane(string messageType, DeliveryLane expected)
    {
        Assert.Equal(expected, MessageClassification.GetLane(messageType));
    }

    [Fact]
    public void Unknown_message_defaults_to_reliable()
    {
        Assert.Equal(DeliveryLane.Reliable, MessageClassification.GetLane("unknown_type"));
    }

    [Fact]
    public void All_event_types_are_present()
    {
        // 30 canonical event types from docs/events.md
        Assert.Equal(30, Game.Contracts.Events.EventType.All.Count);
    }
}
