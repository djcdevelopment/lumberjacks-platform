using Game.Gateway.Valheim;
using Game.Gateway.WebSocket;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class NativeClientAdmissionTests
{
    private static readonly SteamEnrollmentService.EnrollmentView Enrollment = new(
        "enrollment-a",
        "76561198000000000",
        "recipient-a",
        "active",
        DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
        null,
        "p7-primary-v1");

    [Fact]
    public void PublicNativeClientRequiresEnrollmentNotSharedKey()
    {
        var shared = new ValheimPrincipal(
            "shared-client-key",
            ValheimCapability.Consumer | ValheimCapability.Telemetry);

        var decision = NativeClientAdmission.Evaluate(
            shared, "0.1.0-alpha.1", "0.1.0-alpha.1", 0, 10, "world");

        Assert.False(decision.Allowed);
        Assert.Equal(403, decision.StatusCode);
        Assert.Equal("native_enrollment_required", decision.Error);
    }

    [Fact]
    public void ReleaseMustMatchExactly()
    {
        var principal = new ValheimPrincipal("enrollment", ValheimCapability.Consumer, Enrollment);

        var decision = NativeClientAdmission.Evaluate(
            principal, "0.1.0-alpha.0", "0.1.0-alpha.1", 0, 10, "world");

        Assert.False(decision.Allowed);
        Assert.Equal(409, decision.StatusCode);
        Assert.Equal("native_release_mismatch", decision.Error);
    }

    [Fact]
    public void NativeCapacityIsBoundedAtAdmission()
    {
        var principal = new ValheimPrincipal("enrollment", ValheimCapability.Consumer, Enrollment);

        var decision = NativeClientAdmission.Evaluate(
            principal, "release", "release", 10, 10, "world");

        Assert.False(decision.Allowed);
        Assert.Equal(503, decision.StatusCode);
        Assert.Equal("native_capacity_reached", decision.Error);
    }

    [Fact]
    public void PrivateRAndDUsesStableLoginWithoutEnrollmentOrReleaseGate()
    {
        var principal = new ValheimPrincipal(
            "private-plane",
            ValheimCapability.Admin | ValheimCapability.Consumer);

        var first = NativeClientAdmission.Evaluate(
            principal, null, "release-a", 0, 10, "world",
            privateRAndD: true,
            suppliedRAndDUser: "derek",
            suppliedRAndDPassword: "lumberjacks-rnd");
        var afterReleaseChange = NativeClientAdmission.Evaluate(
            principal, null, "release-b", 0, 10, "world",
            privateRAndD: true,
            suppliedRAndDUser: "derek",
            suppliedRAndDPassword: "lumberjacks-rnd");

        Assert.True(first.Allowed);
        Assert.Equal(first.PlayerId, afterReleaseChange.PlayerId);
    }

    [Fact]
    public void PrivateRAndDStillRequiresPrivatePlaneAndCorrectLogin()
    {
        var anonymous = NativeClientAdmission.Evaluate(
            ValheimPrincipal.Anonymous, null, "release", 0, 10, "world",
            privateRAndD: true,
            suppliedRAndDUser: "derek",
            suppliedRAndDPassword: "lumberjacks-rnd");
        var privatePlane = new ValheimPrincipal("private-plane", ValheimCapability.Consumer);
        var wrongPassword = NativeClientAdmission.Evaluate(
            privatePlane, null, "release", 0, 10, "world",
            privateRAndD: true,
            suppliedRAndDUser: "derek",
            suppliedRAndDPassword: "wrong");

        Assert.Equal("private_rnd_requires_private_plane", anonymous.Error);
        Assert.Equal("private_rnd_credentials_invalid", wrongPassword.Error);
    }

    [Fact]
    public void PlayerIdentityIsStableOpaqueAndWorldScoped()
    {
        var first = NativeClientAdmission.PlayerIdFor("world-a", "enrollment-a");
        var repeated = NativeClientAdmission.PlayerIdFor("world-a", "enrollment-a");
        var otherWorld = NativeClientAdmission.PlayerIdFor("world-b", "enrollment-a");

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, otherWorld);
        Assert.StartsWith("p-", first);
        Assert.DoesNotContain("enrollment", first);
        Assert.Equal(26, first.Length);
    }

    [Theory]
    [InlineData("https://play.example.test", "wss://play.example.test/game")]
    [InlineData("http://127.0.0.1:4000/old?q=1", "ws://127.0.0.1:4000/game")]
    public void FieldPassPinsNativeWebSocketPath(string input, string expected)
    {
        var access = NativeAccessDocument.Create(input, "enrollment", "secret", "release");

        Assert.Equal(NativeAccessDocument.SchemaId, access.Schema);
        Assert.Equal(expected, access.GatewayUrl);
        Assert.Equal("enrollment", access.EnrollmentId);
        Assert.Equal("secret", access.ClientKey);
        Assert.Equal("release", access.RequiredRelease);
    }
}
