using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// The M1 release-compatibility gate. It is a COMPATIBILITY gate and not authentication: a
/// volunteer can edit the constant in their own mod and claim anything, and nothing here stops
/// that. Its job is to stop the Gateway handing a *strict* verdict to a mod too old to enforce one
/// — a stale mod fails OPEN on a reject, so an authority that believes it is rejecting while the
/// mod waves players through is worse than no gate at all (plan risk 9).
///
/// Which is why <see cref="NullReleaseId_IsTheStaleMod_AndMustReject"/> is the load-bearing test:
/// absence is the signal. A mod predating the field cannot claim to be current, because it does
/// not know to claim anything — and it is exactly the mod this gate exists to catch.
/// </summary>
public sealed class ValheimHandshakeReleaseTests
{
    private const string Window = "i5-release";
    private const string Expected = "m1-clean-20260717-r1";
    private const string SteamId = "76561198088711642";

    [Fact]
    public void Disabled_ByDefault_SoTodaysFrozenModStillJoins()
    {
        // The sequencing property, and the reason this ships off. EVERY mod deployed right now
        // predates mod_release_id and sends null. Switching this on before the stage-3 cut has
        // landed everywhere would reject every real volunteer for being honest about their version.
        var service = new ValheimHandshakeService();
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext
        {
            ExpectedModReleaseId = Expected,
        }).Ok);

        var gate = service.SubmitPeerInfo(Window, Submission(null)).Result!;
        Assert.True(gate.Accept);
    }

    [Fact]
    public void MatchingRelease_IsAdmitted()
    {
        var gate = Strict().SubmitPeerInfo(Window, Submission(Expected)).Result!;

        Assert.True(gate.Accept);
        Assert.True(gate.EntersSteadyState);
    }

    [Fact]
    public void SkewedRelease_IsRejected_AsErrorVersion()
    {
        var gate = Strict().SubmitPeerInfo(Window, Submission("m0-clean-20260716-r2")).Result!;

        Assert.False(gate.Accept);
        // ErrorVersion, matching native gate A: "your side and my side disagree about the build" is
        // what code 3 already means to a player, and it is the only honest code available.
        Assert.Equal((int)ValheimConnectionStatus.ErrorVersion, gate.ErrorCode);
        Assert.Equal("release_incompatible", gate.FailedCheck);
    }

    [Fact]
    public void NullReleaseId_IsTheStaleMod_AndMustReject()
    {
        // THE ONE THAT MATTERS. A mod predating the field sends nothing, and that mod is precisely
        // the one that will fail OPEN on the strict verdicts this gate protects. Treating null as
        // "no opinion, let them in" would exempt the only case worth catching and quietly make the
        // gate decorative.
        var gate = Strict().SubmitPeerInfo(Window, Submission(null)).Result!;

        Assert.False(gate.Accept);
        Assert.Equal((int)ValheimConnectionStatus.ErrorVersion, gate.ErrorCode);
        Assert.Equal("release_incompatible", gate.FailedCheck);
    }

    [Fact]
    public void NoExpectedRelease_DisablesTheGate_RatherThanRejectingEveryone()
    {
        // An uncut local build has nothing to compare against. Failing closed on an unset
        // expectation would make every dev Gateway reject every join, which teaches people to turn
        // the flag off and leave it off.
        var service = new ValheimHandshakeService();
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext
        {
            StrictReleaseEnabled = true,
            ExpectedModReleaseId = null,
        }).Ok);

        Assert.True(service.SubmitPeerInfo(Window, Submission(null)).Result!.Accept);
    }

    [Fact]
    public void NativeGatesStillWinFirst_ReleaseNeverShadowsAVanillaVerdict()
    {
        // A client on the wrong protocol is vanilla's to reject with vanilla's label, even though
        // both rejects surface as the same int. The operator's log must say which gate fired.
        var wrongProtocol = Submission(null) with { NetVersion = 35 };
        var gate = Strict().SubmitPeerInfo(Window, wrongProtocol).Result!;

        Assert.Equal((int)ValheimConnectionStatus.ErrorVersion, gate.ErrorCode);
        Assert.Equal("version", gate.FailedCheck);
    }

    [Fact]
    public void ReleaseRunsBeforeTheSeatGate_SoASkewedModCannotConsumeASeat()
    {
        var service = Strict();

        var skewed = service.SubmitPeerInfo(Window, Submission("stale")).Result!;
        Assert.Equal("release_incompatible", skewed.FailedCheck);

        // Had the skewed join taken the seat, the next good one would be told the server is full.
        // A rejected join must leave no trace.
        var good = service.SubmitPeerInfo(Window, Submission(Expected)).Result!;
        Assert.True(good.Accept);
    }

    [Fact]
    public void ComparisonIsOrdinal_SoACaseSkewIsRealSkew()
    {
        // Release ids name build artifacts, not prose. If "M1-Clean" and "m1-clean" are treated as
        // one, the gate is asserting something it did not check.
        var gate = Strict().SubmitPeerInfo(Window, Submission(Expected.ToUpperInvariant())).Result!;

        Assert.False(gate.Accept);
        Assert.Equal("release_incompatible", gate.FailedCheck);
    }

    [Fact]
    public void ExpectedRelease_ComesFromTheBuild_NotFromAnOperator()
    {
        // The correction to risk 9's first implementation, pinned. A context nobody configured must
        // already know which release it admits, because the value is compiled in - if this ever
        // needs a POST /config to be populated, the expected release is an operator's opinion again
        // and the gate attests only that someone typed a matching string.
        var fromBuild = ValheimReleaseIdentity.ExpectedModRelease;
        var context = new ValheimHandshakeServerContext();

        Assert.Equal(fromBuild, context.ExpectedModReleaseId);
    }

    [Fact]
    public void UncutBuild_ReadsAsNoExpectation_SoADevGatewayDoesNotRejectEveryone()
    {
        // These tests run against an uncut build (no -p:LumberjacksExpectedModRelease), so the
        // baked value is "dev" and must surface as null. A dev Gateway that refused every join
        // would teach people to switch the flag off and leave it off, which costs more than the
        // gate is worth. Asserting the *build's* value, not a literal, so this keeps meaning
        // whatever a release cut bakes in.
        Assert.Null(ValheimReleaseIdentity.ExpectedModRelease);
    }

    private static ValheimHandshakeService Strict()
    {
        var service = new ValheimHandshakeService();
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext
        {
            StrictReleaseEnabled = true,
            ExpectedModReleaseId = Expected,
        }).Ok);
        return service;
    }

    /// <summary>mod_release_id describes the BUILD ANSWERING, not the client joining — Version and
    /// NetVersion below are the joiner's and are deliberately left valid, so a failure here can only
    /// be the release gate.</summary>
    private static ValheimPeerInfoSubmission Submission(string? modReleaseId) => new()
    {
        WindowId = Window,
        ConnectionId = "conn-" + SteamId,
        Uid = 5_497_853_135_698,
        Version = "0.221.12",
        NetVersion = 36,
        RefPos = new double[] { 9376, 105, 544 },
        PlayerName = "floooooobcakes",
        HostName = SteamId,
        PasswordHash = string.Empty,
        TicketValid = true,
        ModReleaseId = modReleaseId,
        ModVersion = modReleaseId is null ? null : "0.5.31",
    };
}
