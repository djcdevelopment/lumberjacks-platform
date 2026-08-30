using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimTelemetryHeartbeatAuthorizationTests
{
    const string Key = "telemetry-key-000000000000000000000000";

    [Fact]
    public void DirectPrivateServerNeedsNoSharedSecret_WhilePublicCallersRemainKeyed()
    {
        var privateServer = new ValheimPrincipal(
            "private-plane", ValheimCapability.Admin | ValheimCapability.Telemetry);
        var enrolledPublic = new ValheimPrincipal("enrollment", ValheimCapability.Telemetry);

        Assert.True(ValheimTelemetryHeartbeatAuthorization.Allows(privateServer, Key, null));
        Assert.True(ValheimTelemetryHeartbeatAuthorization.Allows(enrolledPublic, Key, Key));
        Assert.False(ValheimTelemetryHeartbeatAuthorization.Allows(enrolledPublic, Key, null));
        Assert.False(ValheimTelemetryHeartbeatAuthorization.Allows(enrolledPublic, Key, Key + "x"));
        Assert.True(ValheimTelemetryHeartbeatAuthorization.Allows(enrolledPublic, null, null));
    }
}
