using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimRecipientLeaseTests
{
    private static readonly DateTime T0 = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LeaseIsScopedAndExpiresWithoutSleeping()
    {
        var activity = new ValheimWindowActivityService();
        activity.Touch("w", "a", T0);
        Assert.True(activity.IsLive("w", "a", T0.AddSeconds(59), 60));
        Assert.False(activity.IsLive("w", "a", T0.AddSeconds(60), 60));
        Assert.False(activity.IsLive("w", "b", T0.AddSeconds(1), 60));
    }

    [Fact]
    public void RecipientReconnectRefreshesOnlyItsOwnLeaseAndTakeoverFollowsExpiry()
    {
        var activity = new ValheimWindowActivityService();
        activity.Touch("w", "a", T0);
        activity.Touch("w", "a", T0.AddSeconds(30));
        Assert.True(activity.IsLive("w", "a", T0.AddSeconds(89), 60));
        Assert.False(activity.IsLive("w", "b", T0.AddSeconds(89), 60));
        Assert.False(activity.IsLive("w", "a", T0.AddSeconds(91), 60));
        Assert.False(activity.IsLive("w", "b", T0.AddSeconds(91), 60));
    }

    [Fact]
    public void ContextValidatesRecipientLeaseConfiguration()
    {
        var error = ValheimHandshakeService.ValidateContext(new ValheimHandshakeServerContext
        {
            RecipientLeaseSeconds = 0,
        });
        Assert.Contains("recipient_lease_seconds", error);
    }
}
