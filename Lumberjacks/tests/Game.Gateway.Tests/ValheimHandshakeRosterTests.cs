using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// The M1 roster gate. It exists at all because the joining SteamID64 IS on the wire, as
/// `host_name` — vanilla reads it from the SOCKET (`peer.m_socket.GetHostName()`,
/// ZNet.decompiled.cs:833) and ticket-verifies that same identity at :882, so it is server-derived
/// and Steam-authenticated. `uid` is a client-supplied session id and is NOT a SteamID (plan §5.3);
/// an earlier revision of the plan concluded from that alone that no Steam identity reached the
/// Gateway, and deferred this gate to stage 3. It was wrong, and the live capture
/// (`host=76561198088711642`) is what settles it.
///
/// The gate defaults OFF: a frozen mod treats a reject as a reject, so a roster miss locks the sole
/// volunteer out of their own server.
/// </summary>
public sealed class ValheimHandshakeRosterTests
{
    private const string Window = "i5-roster";
    private const string EnrolledSteamId = "76561198088711642"; // the real OMEN account, i5 capture
    private const string StrangerSteamId = "76561190000000001";

    private static Func<string?, ValheimRosterVerdict> Roster(
        params (string SteamId, ValheimRosterVerdict Verdict)[] entries) =>
        host => entries.FirstOrDefault(e => e.SteamId == host) is { SteamId: not null } hit
            ? hit.Verdict
            : ValheimRosterVerdict.NotEnrolled;

    [Fact]
    public void Disabled_ByDefault_SoAnUnenrolledJoinStillGetsIn()
    {
        // The safety property that matters while nobody is watching: shipping this code must not
        // change who can join until an operator opts the window in.
        var service = new ValheimHandshakeService(
            roster: Roster((EnrolledSteamId, ValheimRosterVerdict.Active)));

        var gate = service.SubmitPeerInfo(Window, Submission(StrangerSteamId)).Result!;
        Assert.True(gate.Accept);
    }

    [Fact]
    public void Enrolled_IsAdmitted_WhenStrict()
    {
        var service = Strict(Roster((EnrolledSteamId, ValheimRosterVerdict.Active)));
        var gate = service.SubmitPeerInfo(Window, Submission(EnrolledSteamId)).Result!;

        Assert.True(gate.Accept);
        Assert.True(gate.EntersSteadyState);
    }

    [Fact]
    public void Uninvited_IsRejected_NotEnrolled()
    {
        var service = Strict(Roster((EnrolledSteamId, ValheimRosterVerdict.Active)));
        var gate = service.SubmitPeerInfo(Window, Submission(StrangerSteamId)).Result!;

        Assert.False(gate.Accept);
        Assert.Equal((int)ValheimConnectionStatus.ErrorBanned, gate.ErrorCode);
        Assert.Equal("not_enrolled", gate.FailedCheck);
    }

    [Fact]
    public void Revoked_IsRejected_WithItsOwnReason_NotNotEnrolled()
    {
        // Different operator stories: "never invited" and "invited, then revoked" must not collapse
        // into one label, or the audit cannot tell a stranger from a removed volunteer.
        var service = Strict(Roster((EnrolledSteamId, ValheimRosterVerdict.Revoked)));
        var gate = service.SubmitPeerInfo(Window, Submission(EnrolledSteamId)).Result!;

        Assert.False(gate.Accept);
        Assert.Equal((int)ValheimConnectionStatus.ErrorBanned, gate.ErrorCode);
        Assert.Equal("enrollment_revoked", gate.FailedCheck);
    }

    [Fact]
    public void RosterIsKeyedOnHostName_NotUid()
    {
        // The whole correction in one test. Uid is a ZDOMan session id that has nothing to do with
        // Steam; if the gate ever keyed on it, an enrolled player would be rejected as a stranger.
        var service = Strict(Roster((EnrolledSteamId, ValheimRosterVerdict.Active)));

        var enrolledHostButArbitraryUid = Submission(EnrolledSteamId) with { Uid = 1_167_002_880 };
        Assert.True(service.SubmitPeerInfo(Window, enrolledHostButArbitraryUid).Result!.Accept);
    }

    [Fact]
    public void NativeGatesStillWinFirst_RosterNeverShadowsAVanillaVerdict()
    {
        var service = Strict(Roster((EnrolledSteamId, ValheimRosterVerdict.Active)));

        // An unenrolled client on the wrong protocol is vanilla's to reject, with vanilla's code.
        var wrongVersion = Submission(StrangerSteamId) with { NetVersion = 35 };
        var gate = service.SubmitPeerInfo(Window, wrongVersion).Result!;

        Assert.Equal((int)ValheimConnectionStatus.ErrorVersion, gate.ErrorCode);
        Assert.Equal("version", gate.FailedCheck);
    }

    [Fact]
    public void RosterRunsBeforeTheSeatGate_SoAStrangerCannotConsumeASeat()
    {
        var service = Strict(Roster((EnrolledSteamId, ValheimRosterVerdict.Active)));

        var stranger = service.SubmitPeerInfo(Window, Submission(StrangerSteamId)).Result!;
        Assert.Equal("not_enrolled", stranger.FailedCheck);

        // If the stranger had taken the seat, the enrolled volunteer would now be told the server
        // is full — a rejected join must leave no trace.
        var enrolled = service.SubmitPeerInfo(Window, Submission(EnrolledSteamId)).Result!;
        Assert.True(enrolled.Accept);
    }

    [Fact]
    public void StrictWithNoRosterSource_IsRefusedAtConfigure_RatherThanAdmittingEveryone()
    {
        var service = new ValheimHandshakeService(); // no roster wired
        var configured = service.Configure(Window, new ValheimHandshakeServerContext
        {
            StrictRosterEnabled = true,
        });

        Assert.False(configured.Ok);
        Assert.Contains("roster", configured.Error);
    }

    private static ValheimHandshakeService Strict(Func<string?, ValheimRosterVerdict> roster)
    {
        var service = new ValheimHandshakeService(roster: roster);
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext
        {
            StrictRosterEnabled = true,
        }).Ok);
        return service;
    }

    /// <summary>HostName carries the bare SteamID64 exactly as the live capture shows it — no
    /// prefix, no decoration (am4-server-log-decisions.txt: <c>host=76561198088711642</c>).</summary>
    private static ValheimPeerInfoSubmission Submission(string steamId) => new()
    {
        WindowId = Window,
        ConnectionId = "conn-" + steamId,
        Uid = 5_497_853_135_698,
        Version = "0.221.12",
        NetVersion = 36,
        RefPos = new double[] { 9376, 105, 544 },
        PlayerName = "floooooobcakes",
        HostName = steamId,
        PasswordHash = string.Empty,
        TicketValid = true,
    };
}
