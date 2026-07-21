using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// Who a consumer gets filed under. The property that matters is that the caller cannot choose it:
/// a recipient you can select is not an identity, which is why the mod no longer names itself at
/// all (comfy 0e120f4) and why a name it does send is discarded rather than believed.
///
/// These tests exist because this rule shipped uncovered. It lived inside the /consumer lambda,
/// and every test in this project is service-level — there is no WebApplicationFactory harness, and
/// building one means Postgres. Extracting the rule was cheaper than standing up a web server, and
/// the rule never needed one.
/// </summary>
public sealed class ValheimConsumerHeartbeatPolicyTests
{
    private const string Derived = "rcpt_7f3a9c21";
    private const string Forged = "i-picked-this";

    [Fact]
    public void EnrolledCaller_IsFiledUnderTheServersRecipient_NotItsOwnClaim()
    {
        // THE ONE THAT MATTERS. The caller names itself something; the server files it under what
        // the credential says. If this ever returns Forged, the recipient is client-selected and
        // the whole derivation is decorative.
        var resolved = ValheimConsumerHeartbeatPolicy.Resolve(Heartbeat(Forged), Derived);

        Assert.Null(resolved.Error);
        Assert.Equal(Derived, resolved.Recorded!.ConsumerId);
    }

    [Fact]
    public void EnrolledCaller_NeedNotNameItself_WhichIsWhatTheStage3ModDoes()
    {
        // The mod no longer mints a consumer id, so this is the normal path after the cut. Before
        // the endpoint stopped requiring the field, this shape 400'd.
        var resolved = ValheimConsumerHeartbeatPolicy.Resolve(Heartbeat(null), Derived);

        Assert.Null(resolved.Error);
        Assert.Equal(Derived, resolved.Recorded!.ConsumerId);
    }

    [Fact]
    public void UnenrolledCaller_MustNameItself_BecauseNothingElseCan()
    {
        // The legacy shared-key path has no server-side identity to derive from. Its own label is
        // the only one available, so its absence is unrecoverable rather than merely untrusted.
        var resolved = ValheimConsumerHeartbeatPolicy.Resolve(Heartbeat(null), null);

        Assert.Null(resolved.Recorded);
        Assert.Contains("consumer_id is required", resolved.Error);
    }

    [Fact]
    public void UnenrolledCallerWithALabel_IsTakenAtItsWord_ThereBeingNothingBetter()
    {
        var resolved = ValheimConsumerHeartbeatPolicy.Resolve(Heartbeat(Forged), null);

        Assert.Null(resolved.Error);
        Assert.Equal(Forged, resolved.Recorded!.ConsumerId);
    }

    [Theory]
    [InlineData(null, "0.5.31", "2026-07-17T00:00:00Z")]
    [InlineData("w", null, "2026-07-17T00:00:00Z")]
    [InlineData("w", "0.5.31", null)]
    public void AHeartbeatMustIdentifyItsWindowBuildAndTime(string? window, string? mod, string? stamp)
    {
        var resolved = ValheimConsumerHeartbeatPolicy.Resolve(
            new ValheimZdoConsumerHeartbeat
            {
                WindowId = window,
                ModVersion = mod,
                TimestampUtc = stamp,
                ConsumerId = Forged,
            },
            Derived);

        Assert.Null(resolved.Recorded);
        Assert.Contains("required", resolved.Error);
    }

    [Fact]
    public void RequiredFieldsAreCheckedBeforeIdentity_SoAMalformedBeatCannotBeRecordedAtAll()
    {
        // A heartbeat missing its window has nowhere to be filed, enrollment or not. Deriving a
        // recipient for it would record a beat against a window that does not exist.
        var resolved = ValheimConsumerHeartbeatPolicy.Resolve(
            Heartbeat(Forged) with { WindowId = "  " }, Derived);

        Assert.Null(resolved.Recorded);
        Assert.Contains("window_id", resolved.Error);
    }

    private static ValheimZdoConsumerHeartbeat Heartbeat(string? consumerId) => new()
    {
        WindowId = "p7-primary-v1",
        ConsumerId = consumerId,
        ModVersion = "0.5.31",
        TimestampUtc = "2026-07-17T00:00:00Z",
    };
}
