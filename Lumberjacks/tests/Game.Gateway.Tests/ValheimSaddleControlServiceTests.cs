using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimSaddleControlServiceTests
{
    static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-02T20:00:00Z");

    [Fact]
    public void RemoteRequest_OwnerGrant_ControlsAndRoutedReleaseAreAuthenticated()
    {
        var service = new ValheimSaddleControlService();

        Assert.Equal("saddle_control_pending", service.Validate(
            "RequestControl", 101, 202, 202, 42,
            BitConverter.GetBytes(101L), Now).Reason);
        Assert.Equal("saddle_control_granted", service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1], Now).Reason);
        Assert.Equal("saddle_control_input_accepted", service.Validate(
            "Controls", 101, 202, 202, 42,
            Controls(0f, 0f, 1f, 2, 0.5f), Now).Reason);
        Assert.Equal("saddle_control_released", service.Validate(
            "ReleaseControl", 101, 101, 202, 42,
            BitConverter.GetBytes(101L), Now).Reason);

        Assert.False(service.Validate(
            "Controls", 101, 101, 202, 42,
            Controls(0f, 0f, 1f, 2, 0.5f), Now).Accepted);
    }

    [Fact]
    public void Identity_ResponseAndControlBoundsFailClosed()
    {
        var service = new ValheimSaddleControlService();
        Assert.Equal("saddle_control_request_identity_invalid", service.Validate(
            "RequestControl", 101, 202, 202, 42,
            BitConverter.GetBytes(999L), Now).Reason);
        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42,
            BitConverter.GetBytes(101L), Now).Accepted);
        Assert.Equal("saddle_control_response_not_pending", service.Validate(
            "RequestRespons", 303, 101, 202, 42, [1], Now).Reason);
        Assert.True(service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1], Now).Accepted);

        Assert.Equal("saddle_control_input_not_rider", service.Validate(
            "Controls", 303, 202, 202, 42,
            Controls(0f, 0f, 1f, 2, 0.5f), Now).Reason);
        Assert.Equal("saddle_control_input_invalid", service.Validate(
            "Controls", 101, 202, 202, 42,
            Controls(0f, 0f, 1f, 5, 0.5f), Now).Reason);
        Assert.Equal("saddle_control_input_invalid", service.Validate(
            "Controls", 101, 202, 202, 42,
            Controls(float.NaN, 0f, 0f, 2, 0.5f), Now).Reason);
        Assert.Equal("saddle_control_input_invalid", service.Validate(
            "Controls", 101, 202, 202, 42,
            Controls(1f, 0f, 1f, 2, 0.5f), Now).Reason);
    }

    [Fact]
    public void CurrentOwnerCanGrantNextRiderAfterLocalReleaseWasSelfDispatched()
    {
        var service = new ValheimSaddleControlService();
        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42,
            BitConverter.GetBytes(101L), Now).Accepted);
        Assert.True(service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1], Now).Accepted);

        // Vanilla keeps ownership with rider 101 after a local dismount. The
        // next request targeting 101 is evidence of that owner transition;
        // only 101 can grant or deny the pending request.
        Assert.True(service.Validate(
            "RequestControl", 303, 101, 202, 42,
            BitConverter.GetBytes(303L), Now).Accepted);
        Assert.True(service.Validate(
            "RequestRespons", 101, 303, 202, 42, [1], Now).Accepted);
        Assert.True(service.Validate(
            "Controls", 303, 101, 202, 42,
            Controls(0f, 0f, 1f, 1, 0.25f), Now).Accepted);
    }

    [Fact]
    public void PendingContentionExpiryAndDisconnectReclaimAreBounded()
    {
        var service = new ValheimSaddleControlService();
        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42,
            BitConverter.GetBytes(101L), Now).Accepted);
        Assert.Equal("saddle_control_request_contended", service.Validate(
            "RequestControl", 303, 202, 202, 42,
            BitConverter.GetBytes(303L), Now).Reason);
        Assert.Equal("saddle_control_response_not_pending", service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1],
            Now.AddSeconds(16)).Reason);

        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42,
            BitConverter.GetBytes(101L), Now.AddSeconds(17)).Accepted);
        Assert.True(service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1],
            Now.AddSeconds(17)).Accepted);
        Assert.Equal(1, service.ReclaimByPeer(101));
        Assert.False(service.Validate(
            "Controls", 101, 202, 202, 42,
            Controls(0f, 0f, 1f, 2, 0.5f), Now.AddSeconds(18)).Accepted);
    }

    [Fact]
    public void SaddleRemovalRequiresFinitePointAndNoActiveRider()
    {
        var service = new ValheimSaddleControlService();
        Assert.Equal("saddle_remove_point_invalid", service.Validate(
            "RemoveSaddle", 101, 202, 202, 42,
            Controls(float.NaN, 0f, 0f, 0, 0f)[..12], Now).Reason);
        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42,
            BitConverter.GetBytes(101L), Now).Accepted);
        Assert.True(service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1], Now).Accepted);
        Assert.Equal("saddle_remove_control_active_or_owner_stale", service.Validate(
            "RemoveSaddle", 303, 101, 202, 42,
            Point(1f, 2f, 3f), Now).Reason);
    }

    static byte[] Controls(
        float x,
        float y,
        float z,
        int speed,
        float skill)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
        writer.Write(speed);
        writer.Write(skill);
        writer.Flush();
        return stream.ToArray();
    }

    static byte[] Point(float x, float y, float z)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
        writer.Flush();
        return stream.ToArray();
    }
}
