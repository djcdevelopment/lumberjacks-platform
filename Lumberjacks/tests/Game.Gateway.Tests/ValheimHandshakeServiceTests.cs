using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// Loopback shim harness (P6 step 3, no game): a scripted Valheim client hello driven
/// through the handshake responder in-process, asserting Funnel 5's happy path reaches the
/// AddPeer/steady-state transition and the full failure battery surfaces the exact ordered
/// ConnectionStatus codes. Contract: fieldlab/NETCODE-HANDSHAKE-CONTRACT.md.
/// </summary>
public sealed class ValheimHandshakeServiceTests
{
    private const string Window = "i5-test";

    [Fact]
    public void HappyPath_AcceptsAndEntersSteadyState()
    {
        var service = new ValheimHandshakeService();

        // ServerHandshake ⇒ ClientHandshake: no-password server, so needPassword=false.
        var begin = service.Begin(Window, "conn-1");
        Assert.True(begin.Ok);
        Assert.False(begin.Result!.NeedPassword);
        Assert.Equal(36, begin.Result.NetworkVersion);

        // PeerInfo ⇒ accept + server PeerInfo + the AddPeer transition.
        var submit = service.SubmitPeerInfo(Window, ValidSubmission("conn-1"));
        Assert.True(submit.Ok);
        var gate = submit.Result!;
        Assert.True(gate.Accept);
        Assert.True(gate.EntersSteadyState);
        Assert.Null(gate.ErrorCode);
        Assert.NotNull(gate.ServerPeerInfo);
        Assert.Equal(36, gate.ServerPeerInfo!.NetVersion);
        Assert.Equal("ComfyEra16", gate.ServerPeerInfo.WorldName);

        var status = service.GetStatus(Window);
        Assert.Equal(1, status.Begins);
        Assert.Equal(1, status.Accepted);
        Assert.Equal(0, status.Rejected);
        Assert.Equal(1, status.SteadyStateReached);
    }

    [Fact]
    public void PasswordServer_BeginReportsNeedPassword()
    {
        var service = new ValheimHandshakeService();
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext
        {
            Password = "expected-hash",
            Salt = "salt16bytes",
        }).Ok);

        var begin = service.Begin(Window, "conn-1");
        Assert.True(begin.Result!.NeedPassword);
        Assert.Equal("salt16bytes", begin.Result.Salt);

        // Right hash accepts; wrong hash rejects with ErrorPassword (6).
        Assert.True(service.SubmitPeerInfo(Window,
            ValidSubmission("conn-1") with { PasswordHash = "expected-hash" }).Result!.Accept);
        var wrong = service.SubmitPeerInfo(Window,
            ValidSubmission("conn-2") with { PasswordHash = "nope" }).Result!;
        Assert.False(wrong.Accept);
        Assert.Equal((int)ValheimConnectionStatus.ErrorPassword, wrong.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(FailureBattery))]
    public void FailureBattery_SurfacesExactCode(
        ValheimHandshakeServerContext context,
        ValheimPeerInfoSubmission submission,
        ValheimConnectionStatus expected,
        string expectedCheck)
    {
        var service = new ValheimHandshakeService();
        Assert.True(service.Configure(Window, context).Ok);

        var result = service.SubmitPeerInfo(Window, submission).Result!;

        Assert.False(result.Accept);
        Assert.False(result.EntersSteadyState);
        Assert.Equal((int)expected, result.ErrorCode);
        Assert.Equal(expected.ToString(), result.ErrorName);
        Assert.Equal(expectedCheck, result.FailedCheck);
    }

    public static IEnumerable<object[]> FailureBattery()
    {
        // wrong version → 3 ErrorVersion
        yield return [new ValheimHandshakeServerContext(),
            ValidSubmission("c") with { NetVersion = 35 },
            ValheimConnectionStatus.ErrorVersion, "version"];

        // pre-0.214.301 client: the mod's conditional-read shim yields net_version=0, which the
        // gate rejects as a version mismatch → 3 ErrorVersion.
        yield return [new ValheimHandshakeServerContext(),
            ValidSubmission("c") with { Version = "0.210.0", NetVersion = 0 },
            ValheimConnectionStatus.ErrorVersion, "version"];

        // blacklisted host → 8 ErrorBanned
        yield return [new ValheimHandshakeServerContext { BannedHosts = new[] { "steam_hacker" } },
            ValidSubmission("c") with { HostName = "steam_hacker" },
            ValheimConnectionStatus.ErrorBanned, "blacklist"];

        // not on non-empty whitelist → 8 ErrorBanned
        yield return [new ValheimHandshakeServerContext { PermittedHosts = new[] { "steam_admin" } },
            ValidSubmission("c") with { HostName = "steam_rando" },
            ValheimConnectionStatus.ErrorBanned, "blacklist"];

        // bad steam ticket → 8 ErrorBanned (same code as blacklist, distinct check label)
        yield return [new ValheimHandshakeServerContext(),
            ValidSubmission("c") with { TicketValid = false },
            ValheimConnectionStatus.ErrorBanned, "ticket"];

        // server full (>= 10) → 9 ErrorFull
        yield return [new ValheimHandshakeServerContext { CurrentPlayers = 10 },
            ValidSubmission("c"),
            ValheimConnectionStatus.ErrorFull, "full"];

        // wrong password → 6 ErrorPassword
        yield return [new ValheimHandshakeServerContext { Password = "expected-hash" },
            ValidSubmission("c") with { PasswordHash = "wrong" },
            ValheimConnectionStatus.ErrorPassword, "password"];

        // duplicate (uid already connected, seeded) → 7 ErrorAlreadyConnected
        yield return [new ValheimHandshakeServerContext { ConnectedUids = new long[] { 777 } },
            ValidSubmission("c") with { Uid = 777 },
            ValheimConnectionStatus.ErrorAlreadyConnected, "duplicate"];
    }

    [Fact]
    public void GateOrder_SurfacesEarliestFailure()
    {
        // A PeerInfo that fails version AND password must reject with version (3), not password (6):
        // the checks run in decompile order and the first one returns.
        var service = new ValheimHandshakeService();
        Assert.True(service.Configure(Window, new ValheimHandshakeServerContext
        {
            Password = "expected-hash",
        }).Ok);

        var result = service.SubmitPeerInfo(Window,
            ValidSubmission("c") with { NetVersion = 35, PasswordHash = "wrong" }).Result!;

        Assert.Equal((int)ValheimConnectionStatus.ErrorVersion, result.ErrorCode);
        Assert.Equal("version", result.FailedCheck);
    }

    [Fact]
    public void Duplicate_AcceptThenReconnectSameUid()
    {
        // First connect with a uid accepts and enters steady state; a second PeerInfo with the
        // same uid is now IsConnected(uid) → ErrorAlreadyConnected (7).
        var service = new ValheimHandshakeService();

        var first = service.SubmitPeerInfo(Window, ValidSubmission("conn-1") with { Uid = 555 }).Result!;
        Assert.True(first.Accept);

        var second = service.SubmitPeerInfo(Window, ValidSubmission("conn-2") with { Uid = 555 }).Result!;
        Assert.False(second.Accept);
        Assert.Equal((int)ValheimConnectionStatus.ErrorAlreadyConnected, second.ErrorCode);
    }

    [Fact]
    public void Status_TracksCountersAndByCode()
    {
        var service = new ValheimHandshakeService();
        service.Begin(Window, "conn-1");
        service.SubmitPeerInfo(Window, ValidSubmission("conn-1"));                     // accept
        service.SubmitPeerInfo(Window, ValidSubmission("c2") with { NetVersion = 1 }); // reject 3
        service.SubmitPeerInfo(Window, ValidSubmission("c3") with { TicketValid = false }); // reject 8

        var status = service.GetStatus(Window);
        Assert.Equal(1, status.Begins);
        Assert.Equal(1, status.Accepted);
        Assert.Equal(2, status.Rejected);
        Assert.Equal(1, status.SteadyStateReached);
        Assert.Equal(1, status.ByCode["3"]);
        Assert.Equal(1, status.ByCode["8"]);
        Assert.Equal(3, status.Exchanges.Count);
    }

    [Theory]
    [MemberData(nameof(MalformedSubmissions))]
    public void MalformedSubmissions_FailClosed(ValheimPeerInfoSubmission submission, string expected)
    {
        var result = new ValheimHandshakeService().SubmitPeerInfo(Window, submission);
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> MalformedSubmissions()
    {
        yield return [ValidSubmission("bad id!") , "connection_id"];
        yield return [ValidSubmission("c") with { Version = "" }, "version string"];
        yield return [ValidSubmission("c") with { PlayerName = "" }, "player_name"];
        yield return [ValidSubmission("c") with { HostName = "" }, "host_name"];
        yield return [ValidSubmission("c") with { RefPos = new[] { double.NaN, 0, 0 } }, "ref_pos"];
        yield return [ValidSubmission("c") with { RefPos = new double[] { 1, 2 } }, "ref_pos"];
    }

    private static ValheimPeerInfoSubmission ValidSubmission(string connectionId) => new()
    {
        WindowId = Window,
        ConnectionId = connectionId,
        Uid = 5_497_853_135_698,
        Version = "0.221.12",
        NetVersion = 36,
        RefPos = new double[] { 9376, 105, 544 },
        PlayerName = "floooooobcakes",
        HostName = "steam_76561198000000000",
        PasswordHash = string.Empty,
        TicketValid = true,
    };
}
