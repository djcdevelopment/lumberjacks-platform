using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// The M1 stage-2 seat gate: the one admission rule buildable Gateway-only, because a seat needs a
/// count rather than a name (plan §4). Every case here drives a fixed clock, so lease expiry is
/// asserted rather than slept on.
///
/// The gate's whole difficulty is that the Gateway is never told a player left (plan §5.4), so
/// these tests pin both directions of the tradeoff: a live consumer must hold its seat
/// indefinitely, and a holder that goes silent must release it inside the lease.
/// </summary>
public sealed class ValheimHandshakeSeatTests
{
    private const string Window = "i5-seat";
    private static readonly DateTime T0 = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);

    // Seats key on host_name (the socket's Steam identity), not uid — see the _seats comment.
    private const string HolderSteamId = "76561198088711642";
    private const string RivalSteamId = "76561190000000001";

    [Fact]
    public void SecondPlayer_IsRejected_WhileFirstHoldsTheSeat()
    {
        var now = T0;
        var service = new ValheimHandshakeService(nowUtc: () => now);

        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        now = T0.AddSeconds(5);
        var rival = service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!;

        Assert.False(rival.Accept);
        Assert.False(rival.EntersSteadyState);
        // ErrorFull is the only native code meaning "no room" — the player sees vanilla's
        // full-server screen and the operator gets the reason in the log (plan §6).
        Assert.Equal((int)ValheimConnectionStatus.ErrorFull, rival.ErrorCode);
        Assert.Equal("capacity_reserved", rival.FailedCheck);
    }

    [Fact]
    public void SameSession_IsRejectedByTheDuplicateGate_NotTheSeatGate()
    {
        var now = T0;
        var service = new ValheimHandshakeService(nowUtc: () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        // Same uid AND same host: gate G owns "you are already connected" and fires first, so the
        // reason must be G's and not a capacity answer, which would be a lie to the operator.
        now = T0.AddSeconds(5);
        var again = service.SubmitPeerInfo(Window, Submission("c2", HolderSteamId)).Result!;

        Assert.Equal((int)ValheimConnectionStatus.ErrorAlreadyConnected, again.ErrorCode);
        Assert.Equal("duplicate", again.FailedCheck);
    }

    [Fact]
    public void SeatCapacityAboveOne_IsRefused_RatherThanSilentlyMiscounted()
    {
        var service = new ValheimHandshakeService();

        // Window-scoped liveness cannot attribute a poll to a holder, so at N>1 a single live
        // consumer would vouch for N-1 departed players. Refusing the config beats honouring a
        // number the model cannot enforce.
        var configured = service.Configure(Window, new ValheimHandshakeServerContext
        {
            SeatCapacity = 2,
        });

        Assert.False(configured.Ok);
        Assert.Contains("seat_capacity", configured.Error);
    }

    [Fact]
    public void ImplausibleLease_IsRefused()
    {
        var service = new ValheimHandshakeService();
        Assert.False(service.Configure(Window, new ValheimHandshakeServerContext
        {
            SeatLeaseSeconds = 0,
        }).Ok);
    }

    [Fact]
    public void SeatFrees_OnceTheLeaseLapsesWithNoSignOfLife()
    {
        var now = T0;
        var service = new ValheimHandshakeService(nowUtc: () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        // A ghost accept looks exactly like this: granted, then never heard from again — vanilla
        // overturned it on the ticket check and the Gateway was never told (plan §5.5).
        now = T0.AddSeconds(61);
        var rival = service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!;

        Assert.True(rival.Accept);
        Assert.True(rival.EntersSteadyState);
    }

    [Fact]
    public void SeatHolds_PastTheGrant_WhileTheConsumerKeepsPolling()
    {
        var now = T0;
        var activity = new ValheimWindowActivityService();
        var service = new ValheimHandshakeService(activity, () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        // Well past the grant, but the holder is demonstrably still there: without this the seat
        // would expire mid-session and a rival would be admitted onto the shared queue.
        now = T0.AddSeconds(600);
        activity.Touch(Window, now.AddSeconds(-5));

        var rival = service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!;
        Assert.False(rival.Accept);
        Assert.Equal("capacity_reserved", rival.FailedCheck);
    }

    [Fact]
    public void SeatFrees_WhenTheHolderGoesSilent_SoAnotherPlayerMayTakeIt()
    {
        var now = T0;
        var activity = new ValheimWindowActivityService();
        var service = new ValheimHandshakeService(activity, () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        now = T0.AddSeconds(300);
        activity.Touch(Window, now.AddSeconds(-5)); // playing happily...

        now = T0.AddSeconds(600); // ...then crashed: last poll is now 305s old.

        // A DIFFERENT player may now take the abandoned seat — liveness is the only thing that can
        // release it, since nothing ever tells the Gateway the holder left (plan §5.4).
        var stranger = service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!;
        Assert.True(stranger.Accept);
    }

    [Fact]
    public void Volunteer_ReconnectingWithAFreshUid_KeepsTheirOwnSeat()
    {
        var now = T0;
        var activity = new ValheimWindowActivityService();
        var service = new ValheimHandshakeService(activity, () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        now = T0.AddSeconds(10);
        activity.Touch(Window, now); // their consumer was polling right up until they quit

        // They quit to the menu and rejoin 20s later. ZNet is rebuilt, so Uid is brand new
        // (ZDOMan.m_sessionID is readonly, built in ZNet.Awake(), ZNet.decompiled.cs:264) and gate G
        // cannot recognise them. Keyed on uid this returned "server is full" for the rest of the
        // lease — locking the sole volunteer out of their own server. host_name is stable, so the
        // seat is theirs and simply refreshes.
        now = T0.AddSeconds(30);
        var reconnect = Submission("c2", HolderSteamId) with { Uid = 9_999_999_999 };
        var gate = service.SubmitPeerInfo(Window, reconnect).Result!;

        Assert.True(gate.Accept);
        Assert.True(gate.EntersSteadyState);
    }

    [Fact]
    public void ActivityOnAnotherWindow_DoesNotHoldThisWindowsSeat()
    {
        var now = T0;
        var activity = new ValheimWindowActivityService();
        var service = new ValheimHandshakeService(activity, () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        now = T0.AddSeconds(61);
        activity.Touch("some-other-window", now); // liveness must be window-scoped, not global

        Assert.True(service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!.Accept);
    }

    [Fact]
    public void SeatCapacityZero_DisablesTheGate()
    {
        var now = T0;
        var service = new ValheimHandshakeService(nowUtc: () => now);
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext
        {
            SeatCapacity = 0,
        }).Ok);

        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!.Accept);
    }

    [Fact]
    public void Reconfiguring_ReleasesHeldSeats()
    {
        var now = T0;
        var service = new ValheimHandshakeService(nowUtc: () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        // A new context is a new world; a seat reserved against the old one is meaningless.
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext()).Ok);

        Assert.True(service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!.Accept);
    }

    [Fact]
    public void Reset_ClearsLiveness_SoAStaleMarkCannotHoldASeatInAFreshWindow()
    {
        var now = T0;
        var activity = new ValheimWindowActivityService();
        var service = new ValheimHandshakeService(activity, () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);
        activity.Touch(Window, now);

        Assert.True(service.Reset(Window));
        Assert.Null(activity.LastActivityUtc(Window));

        Assert.True(service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!.Accept);
    }

    [Fact]
    public void CustomLease_IsHonoured()
    {
        var now = T0;
        var service = new ValheimHandshakeService(nowUtc: () => now);
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext
        {
            SeatLeaseSeconds = 600,
        }).Ok);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        now = T0.AddSeconds(61); // past the default lease, inside the configured one
        Assert.False(service.SubmitPeerInfo(Window, Submission("c2", RivalSteamId)).Result!.Accept);

        now = T0.AddSeconds(601);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c3", RivalSteamId)).Result!.Accept);
    }

    [Fact]
    public void NativeGatesStillWinFirst_SeatGateNeverShadowsAVanillaVerdict()
    {
        var now = T0;
        var service = new ValheimHandshakeService(nowUtc: () => now);
        Assert.True(service.SubmitPeerInfo(Window, Submission("c1", HolderSteamId)).Result!.Accept);

        // A rival who would fail vanilla's version check must get ErrorVersion, not capacity —
        // the seat gate runs last precisely so native emulation stays exact.
        var wrongVersion = Submission("c2", RivalSteamId) with { NetVersion = 35 };
        var result = service.SubmitPeerInfo(Window, wrongVersion).Result!;

        Assert.Equal((int)ValheimConnectionStatus.ErrorVersion, result.ErrorCode);
        Assert.Equal("version", result.FailedCheck);
    }

    /// <summary>
    /// Uid is DERIVED from the SteamId rather than pinned, because gate G keys on uid and fires
    /// before the seat gate: two real players differ in both, and giving them a shared uid would
    /// have them rejected as duplicates without the seat gate ever running. A player reconnecting
    /// overrides Uid explicitly (see Volunteer_ReconnectingWithAFreshUid_KeepsTheirOwnSeat) — that
    /// is the case the seat's host_name key exists for.
    /// </summary>
    private static ValheimPeerInfoSubmission Submission(string connectionId, string steamId) => new()
    {
        WindowId = Window,
        ConnectionId = connectionId,
        Uid = long.Parse(steamId[^9..]),
        Version = "0.221.12",
        NetVersion = 36,
        RefPos = new double[] { 9376, 105, 544 },
        PlayerName = "floooooobcakes",
        HostName = steamId,
        PasswordHash = string.Empty,
        TicketValid = true,
    };
}
