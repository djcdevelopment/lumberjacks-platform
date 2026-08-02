using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimShipControlServiceTests
{
    static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-02T12:00:00Z");

    [Fact]
    public void RemoteRequest_OwnerGrant_InputAndRelease_FormHeldHelmLease()
    {
        var service = new ValheimShipControlService();

        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42, BitConverter.GetBytes(9L), Now)
            .Accepted);
        Assert.True(service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1], Now)
            .Accepted);
        Assert.True(service.Validate(
            "Forward", 101, 202, 202, 42, [], Now)
            .Accepted);
        Assert.True(service.Validate(
            "Rudder", 101, 202, 202, 42, BitConverter.GetBytes(0.5f), Now)
            .Accepted);
        Assert.True(service.Validate(
            "ReleaseControl", 101, 202, 202, 42, BitConverter.GetBytes(9L), Now)
            .Accepted);

        Assert.False(service.Validate(
            "Forward", 101, 202, 202, 42, [], Now).Accepted);
    }

    [Fact]
    public void PassengerCannotInjectInputOrReleaseAnotherController()
    {
        var service = new ValheimShipControlService();
        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42, BitConverter.GetBytes(9L), Now)
            .Accepted);
        Assert.True(service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1], Now)
            .Accepted);

        Assert.Equal("ship_control_input_not_holder", service.Validate(
            "Forward", 303, 202, 202, 42, [], Now).Reason);
        Assert.Equal("ship_control_release_not_holder", service.Validate(
            "ReleaseControl", 303, 202, 202, 42, BitConverter.GetBytes(9L), Now)
            .Reason);
    }

    [Fact]
    public void GrantMustComeFromPendingOwnerAndDisconnectReclaimsHelm()
    {
        var service = new ValheimShipControlService();
        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42, BitConverter.GetBytes(9L), Now)
            .Accepted);
        Assert.Equal("ship_control_response_not_pending", service.Validate(
            "RequestRespons", 303, 101, 202, 42, [1], Now).Reason);
        Assert.True(service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1], Now).Accepted);

        Assert.Equal(1, service.ReclaimByPeer(101));
        Assert.False(service.Validate(
            "Forward", 101, 202, 202, 42, [], Now).Accepted);
    }

    [Fact]
    public void PendingRequestExpiresAndSelfOwnedRequestIsRejected()
    {
        var service = new ValheimShipControlService();
        Assert.False(service.Validate(
            "RequestControl", 101, 101, 101, 42, BitConverter.GetBytes(9L), Now)
            .Accepted);
        Assert.True(service.Validate(
            "RequestControl", 101, 202, 202, 42, BitConverter.GetBytes(9L), Now)
            .Accepted);

        Assert.Equal("ship_control_response_not_pending", service.Validate(
            "RequestRespons", 202, 101, 202, 42, [1],
            Now.AddSeconds(16)).Reason);
    }
}
