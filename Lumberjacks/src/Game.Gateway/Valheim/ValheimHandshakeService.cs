using System.Collections.Concurrent;

namespace Game.Gateway.Valheim;

/// <summary>What the enrollment roster says about a joining SteamID64.</summary>
public enum ValheimRosterVerdict
{
    /// <summary>No enrollment has ever existed for this SteamID.</summary>
    NotEnrolled,
    /// <summary>Enrolled once, but every enrollment for it is revoked or superseded.</summary>
    Revoked,
    Active,
}

/// <summary>
/// Valheim connection-status codes, mirrored from the decompiled <c>ZNet.ConnectionStatus</c>
/// enum (assembly_valheim.dll 0.221.12, ZNet.decompiled.cs:23-38). The handshake gate emits
/// these as the "Error" RPC argument. See fieldlab/NETCODE-HANDSHAKE-CONTRACT.md.
/// </summary>
public enum ValheimConnectionStatus
{
    None = 0,
    Connecting = 1,
    Connected = 2,
    ErrorVersion = 3,
    ErrorDisconnected = 4,
    ErrorConnectFailed = 5,
    ErrorPassword = 6,
    ErrorAlreadyConnected = 7,
    ErrorBanned = 8,
    ErrorFull = 9,
    ErrorPlatformExcluded = 10,
    ErrorCrossplayPrivilege = 11,
    ErrorKicked = 12,
}

/// <summary>
/// The emulated dedicated-server context a Lumberjacks-fronted peer answers a handshake
/// against — the logical inputs to Funnel 5's gate. Defaults mirror the am4 ComfyEra16
/// Steam-only server (no password, 0/10 players, no ban/whitelist). The mod hook (P6 step 5)
/// supplies the real values at connect time; the loopback shim overrides per battery case.
/// </summary>
public sealed record ValheimHandshakeServerContext
{
    public int NetworkVersion { get; init; } = ValheimHandshakeService.NetworkVersion;

    /// <summary>Comparison target for the password gate — equals the server's stored
    /// <c>m_serverPassword</c> (an opaque hash string). Empty ⇒ needPassword=false.</summary>
    public string Password { get; init; } = string.Empty;

    public string Salt { get; init; } = string.Empty;
    public int MaxPlayers { get; init; } = ValheimHandshakeService.MaxPlayers;
    public int CurrentPlayers { get; init; }
    public bool RequireSteamTicket { get; init; } = true;
    public IReadOnlyList<string> BannedHosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PermittedHosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<long> ConnectedUids { get; init; } = Array.Empty<long>();

    /// <summary>
    /// Lumberjacks roster gate: reject a joining SteamID64 that holds no active enrollment.
    ///
    /// The key is <see cref="ValheimPeerInfoSubmission.HostName"/>, NOT <c>Uid</c>. Vanilla reads
    /// the host from the SOCKET (<c>peer.m_socket.GetHostName()</c>, ZNet.decompiled.cs:833) and
    /// then verifies the Steam session ticket against that same socket identity
    /// (<c>VerifySessionTicket(ticket, zSteamSocket.GetPeerID())</c>, :882) — so it is
    /// server-derived and Steam-authenticated, unlike <c>Uid</c>, which is a client-supplied
    /// <c>ZDOMan.GetSessionID()</c> and not a SteamID at all (plan §5.3). Live capture confirms
    /// the wire value is a bare SteamID64: <c>host=76561198088711642</c>
    /// (fieldlab/evidence/i5-handshake-live/am4-server-log-decisions.txt).
    ///
    /// Valid only while crossplay is off, which is a standing assumption (I6): the PlayFab branch
    /// parses the same host as a PlatformUserID (:891), so it would not be a SteamID there.
    ///
    /// Defaults to OFF. Turning it on makes enrollment load-bearing for joining, and a frozen mod
    /// treats a reject as a reject — only endpoint faults fail open — so a roster miss locks the
    /// volunteer out of their own server. It is opt-in per window until an operator is watching.
    /// </summary>
    public bool StrictRosterEnabled { get; init; }

    /// <summary>
    /// The mod release this window will admit, e.g. "m1-clean-20260717-r1".
    ///
    /// **Defaults to the value baked into this assembly at build**
    /// (<see cref="ValheimReleaseIdentity.ExpectedModRelease"/>), which is the point: the expected
    /// value travels with the image and cannot drift from the artifact that shipped. It is never
    /// read live from the bundle, which is untracked and unreachable from a container (plan risk 9).
    ///
    /// An earlier revision made this settable-only, with no default, so the sole way to populate it
    /// was a runtime <c>POST /config</c> — an operator's opinion, which is the second source of
    /// truth risk 9 explicitly rejected, only worse for being per-window and un-reviewable. The
    /// property survives as an **override for tests**, which need to name a release without
    /// rebuilding; production should not set it.
    ///
    /// Null disables the gate, which is what an uncut local build ("dev") gets.
    /// </summary>
    public string? ExpectedModReleaseId { get; init; } = ValheimReleaseIdentity.ExpectedModRelease;

    /// <summary>
    /// Lumberjacks release-compatibility gate. Defaults to OFF, and the sequencing is the whole
    /// reason: every mod deployed today predates <see cref="ValheimPeerInfoSubmission.ModReleaseId"/>
    /// and sends nothing, so switching this on before the stage-3 cut has landed everywhere would
    /// reject every real volunteer for being honest about their version. Ships off, flips after —
    /// the <see cref="StrictRosterEnabled"/> pattern.
    ///
    /// This is a COMPATIBILITY gate, not authentication: a volunteer can edit the constant in their
    /// own mod and claim anything, and nothing here stops that. Its job is to stop the Gateway
    /// handing a *strict* verdict to a mod too old to enforce one — a stale mod fails OPEN on a
    /// reject, so an authority that believes it is rejecting while the mod waves players through is
    /// worse than no gate at all.
    /// </summary>
    public bool StrictReleaseEnabled { get; init; }

    /// <summary>
    /// Lumberjacks seat gate: concurrent seats this window admits. The volunteer pilot is one seat
    /// (VOLUNTEER-ENDPOINT.md: "Admit only one client while P7 still uses the shared p7-primary-v1
    /// queue"), so 1 is the default — including for a window that materializes on first contact
    /// without an operator ever configuring it. 0 disables the gate.
    ///
    /// This is NOT the native <see cref="MaxPlayers"/> check: that one emulates vanilla's
    /// <c>GetNrOfPlayers() >= 10</c> against an operator-supplied <see cref="CurrentPlayers"/> that
    /// nothing keeps current. The seat gate counts what the Gateway itself admitted.
    /// </summary>
    public int SeatCapacity { get; init; } = 1;

    /// <summary>
    /// How long a seat survives with no sign of life from its holder — see
    /// <see cref="ValheimWindowActivityService"/> for why liveness, not a plain timer, drives this.
    ///
    /// The lease only ever bites a DIFFERENT player, because the seat is keyed on the holder's
    /// stable `host_name` — the volunteer reconnecting is recognised and refreshes their own seat
    /// rather than waiting it out. (An earlier revision keyed the seat on `uid` and justified 60s
    /// by "relaunching Valheim takes longer than that anyway". Both halves were wrong: `uid` is
    /// rebuilt on every reconnect, not every process — `ZDOMan.m_sessionID` is readonly and built
    /// in `ZNet.Awake()`, ZNet.decompiled.cs:264 — so quitting to the menu and returning in 20s
    /// would have been told the server was full.)
    ///
    /// So 60s now trades only against a stranger: how long after a holder vanishes before someone
    /// else may take the seat. Short enough that a genuinely departed volunteer frees it promptly;
    /// long enough to ride out a consumer hiccup. The residual cost is a holder whose consumer
    /// stalls a full minute could lose their seat to a second player — and today there is one.
    /// </summary>
    public int SeatLeaseSeconds { get; init; } = 60;

    /// <summary>Per-recipient readiness lease used by M4a consumer activity.</summary>
    public int RecipientLeaseSeconds { get; init; } = 60;

    // Server-PeerInfo reply fields (the emulated world).
    public long ServerUid { get; init; } = 1;
    public string VersionString { get; init; } = "0.221.12";
    public string WorldName { get; init; } = "ComfyEra16";
    public int Seed { get; init; }
    public string SeedName { get; init; } = string.Empty;
    public long WorldUid { get; init; }
    public int WorldGenVersion { get; init; }
    public double NetTime { get; init; }
}

public sealed record ValheimHandshakeConfigRequest
{
    public string? WindowId { get; init; }
    public ValheimHandshakeServerContext? Context { get; init; }
}

/// <summary>Response to the client's ServerHandshake — the ClientHandshake args.</summary>
public sealed record ValheimHandshakeBeginResult(bool NeedPassword, string Salt, int NetworkVersion);

/// <summary>
/// The decoded client PeerInfo, as the mod hook would hand it over after reading the
/// ZPackage. Logical fields only — the byte layout stays in the mod.
/// </summary>
public sealed record ValheimPeerInfoSubmission
{
    public string? WindowId { get; init; }
    public string? ConnectionId { get; init; }
    public long Uid { get; init; }
    public string? Version { get; init; }
    public int NetVersion { get; init; }
    public double[]? RefPos { get; init; }
    public string? PlayerName { get; init; }
    public string? HostName { get; init; }
    public string? PasswordHash { get; init; }
    /// <summary>The mod's Steamworks VerifySessionTicket result (gate check C). The real
    /// crypto verify can only run in-game, so it is supplied as a boolean.</summary>
    public bool TicketValid { get; init; } = true;

    /// <summary>The release the *mod* was cut as — the build answering the handshake, not the
    /// client joining it (<see cref="Version"/> and <see cref="NetVersion"/> are the client's).
    /// Null from any mod predating the field, which is the signal that matters: a stale mod
    /// cannot claim to be current, because it does not know to claim anything.</summary>
    public string? ModReleaseId { get; init; }

    /// <summary>The mod's own plugin version, for the operator log. Not gated on: the release id
    /// is the identity a cut names, and gating two things that must agree invites them not to.</summary>
    public string? ModVersion { get; init; }
}

public sealed record ValheimServerPeerInfo(
    long Uid, string Version, int NetVersion, string WorldName, int Seed,
    string SeedName, long WorldUid, int WorldGenVersion, double NetTime);

/// <summary>
/// Result of the logical handshake gate. <see cref="EntersSteadyState"/> means the gate
/// accepted and WOULD drive Valheim's AddPeer transition — it is a headless decision, NOT an
/// observation of a real ZDOMan.AddPeer / in-world peer. The live proof is the P6 step 6-7
/// gate (Derek connects in-game, ≥30s in-world).
/// </summary>
public sealed record ValheimHandshakeGateResult(
    bool Accept,
    bool EntersSteadyState,
    int? ErrorCode,
    string? ErrorName,
    string? FailedCheck,
    ValheimServerPeerInfo? ServerPeerInfo);

public sealed record ValheimHandshakeExchangeRecord(
    string ConnectionId,
    bool Accept,
    bool EnteredSteadyState,
    int? ErrorCode,
    string? ErrorName,
    string? FailedCheck,
    DateTime Utc);

public sealed record ValheimHandshakeWindowStatus(
    string WindowId,
    int NetworkVersion,
    bool NeedPassword,
    int MaxPlayers,
    int CurrentPlayers,
    bool RequireSteamTicket,
    int BannedHosts,
    int PermittedHosts,
    long Begins,
    long Accepted,
    long Rejected,
    long SteadyStateReached,
    IReadOnlyDictionary<string, long> ByCode,
    IReadOnlyList<ValheimHandshakeExchangeRecord> Exchanges,
    DateTime? FirstUtc,
    DateTime? LastUtc);

/// <summary>
/// Logical handshake responder for the I5 / P6 rung: given the two handshake decision points
/// (ServerHandshake ⇒ ClientHandshake args; PeerInfo ⇒ accept-or-reject), it replicates
/// Funnel 5's ordered gate and error-code contract from a running Valheim dedicated server,
/// WITHOUT parsing raw ZPackage bytes (the mod owns that, as in I3/I4). Windowed and
/// in-memory, matching ValheimZdoRedirectService / ValheimZdoInjectionService. Contract:
/// fieldlab/NETCODE-HANDSHAKE-CONTRACT.md (two decompile sources agree).
/// </summary>
public sealed class ValheimHandshakeService
{
    /// <summary>Literal network-version gate value — <c>Version.m_networkVersion = 36u</c>.</summary>
    public const int NetworkVersion = 36;

    /// <summary>Server-full threshold — the literal <c>>= 10</c> in RPC_PeerInfo:912 (NOT the
    /// unused c_NetworkVersionMaxPlayerCount=35 const, a catalogued red herring).</summary>
    public const int MaxPlayers = 10;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly ValheimWindowActivityService? _activity;
    private readonly Func<DateTime> _nowUtc;
    private readonly Func<string?, ValheimRosterVerdict>? _roster;
    private readonly Func<string?, string?>? _recipientResolver;

    /// <param name="activity">Consumer liveness for the seat gate. Null ⇒ seats expire on their
    /// grant alone, which is the pre-stage-2 behaviour and is what unit tests get by default.</param>
    /// <param name="nowUtc">Injected so seat-lease expiry is testable without sleeping.</param>
    /// <param name="roster">Roster lookup by SteamID64. A delegate rather than a reference to
    /// SteamEnrollmentService so the gate stays testable without a store on disk. Null ⇒ the roster
    /// gate cannot answer, and <see cref="ValheimHandshakeServerContext.StrictRosterEnabled"/> is
    /// then refused at Configure rather than silently admitting everyone.</param>
    public ValheimHandshakeService(
        ValheimWindowActivityService? activity = null,
        Func<DateTime>? nowUtc = null,
        Func<string?, ValheimRosterVerdict>? roster = null,
        Func<string?, string?>? recipientResolver = null)
    {
        _activity = activity;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _roster = roster;
        _recipientResolver = recipientResolver;
    }

    public (bool Ok, string Error) Configure(string windowId, ValheimHandshakeServerContext context)
    {
        var error = ValidateToken(windowId, "window_id") ?? ValidateContext(context);
        // Fail closed on misconfiguration: a strict window with no roster to ask would quietly
        // admit everyone while the operator believed admission was gated.
        if (error is null && context.StrictRosterEnabled && _roster is null)
            error = "strict_roster_enabled requires a roster source; none is wired";
        if (error is not null)
            return (false, error);
        _windows.GetOrAdd(windowId, static _ => new Window()).Configure(context);
        return (true, string.Empty);
    }

    public (bool Ok, string Error, ValheimHandshakeBeginResult? Result) Begin(
        string windowId, string connectionId)
    {
        var error = ValidateToken(windowId, "window_id") ?? ValidateToken(connectionId, "connection_id");
        if (error is not null)
            return (false, error, null);
        var window = _windows.GetOrAdd(windowId, static _ => new Window());
        return (true, string.Empty, window.Begin(connectionId));
    }

    public (bool Ok, string Error, ValheimHandshakeGateResult? Result) SubmitPeerInfo(
        string windowId, ValheimPeerInfoSubmission submission)
    {
        var error = ValidateToken(windowId, "window_id") ?? ValidateSubmission(submission);
        if (error is not null)
            return (false, error, null);
        var window = _windows.GetOrAdd(windowId, static _ => new Window());
        var recipient = _recipientResolver?.Invoke(submission.HostName) ?? ValheimRecipient.Legacy;
        return (true, string.Empty, window.SubmitPeerInfo(
            submission, _nowUtc(), _activity?.LastActivityUtc(windowId, recipient), _roster));
    }

    public ValheimHandshakeWindowStatus GetStatus(string windowId) =>
        _windows.TryGetValue(windowId, out var window)
            ? window.Status(windowId)
            : Window.Empty(windowId);

    public IReadOnlyList<ValheimHandshakeWindowStatus> GetAllStatuses() =>
        _windows.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value.Status(kv.Key)).ToList();

    public bool Reset(string windowId)
    {
        // Liveness goes with the window: a surviving mark would hold a seat in a window that was
        // just reset to empty.
        _activity?.Clear(windowId);
        return _windows.TryRemove(windowId, out _);
    }

    public int ResetAll()
    {
        var count = _windows.Count;
        _windows.Clear();
        _activity?.ClearAll();
        return count;
    }

    // --- validation -------------------------------------------------------------------

    public static string? ValidateContext(ValheimHandshakeServerContext? context)
    {
        if (context is null)
            return "context is required";
        if (context.NetworkVersion is < 0 or > ushort.MaxValue)
            return "network_version out of range";
        if (context.MaxPlayers is < 1 or > 1000)
            return "max_players must be 1..1000";
        if (context.CurrentPlayers is < 0 or > 100_000)
            return "current_players out of range";
        // Refused rather than clamped: the seat gate's liveness is window-scoped, so it can tell
        // that SOMEONE is consuming but not WHICH holder. That is exactly enough for one seat and
        // incoherent for more — an N-seat window would let a single live consumer vouch for N
        // departed players. Honouring the number would be a silent correctness bug; N seats need
        // per-holder liveness, which needs the stage-3 identity (plan §5.4).
        if (context.SeatCapacity is < 0 or > 1)
            return "seat_capacity must be 0 (disabled) or 1; N-seat windows need per-holder liveness";
        if (context.SeatLeaseSeconds is < 1 or > 86_400)
            return "seat_lease_seconds must be 1..86400";
        if (context.RecipientLeaseSeconds is < 1 or > 86_400)
            return "recipient_lease_seconds must be 1..86400";
        return null;
    }

    public static string? ValidateSubmission(ValheimPeerInfoSubmission? s)
    {
        if (s is null)
            return "submission is required";
        var tokenError = ValidateToken(s.ConnectionId, "connection_id");
        if (tokenError is not null)
            return tokenError;
        // The client version string is always written before the network-version uint
        // (SendPeerInfo:787). The mod applies the 0.214.301 conditional-read shim when decoding
        // (a pre-0.214.301 client yields net_version=0, which then fails gate check A) and hands
        // the resulting net_version here; a missing version string is a malformed PeerInfo.
        if (string.IsNullOrWhiteSpace(s.Version) || s.Version.Length > 64)
            return "version string is required (1-64 characters)";
        if (string.IsNullOrWhiteSpace(s.PlayerName) || s.PlayerName.Length > 128)
            return "player_name must be 1-128 characters";
        if (string.IsNullOrWhiteSpace(s.HostName) || s.HostName.Length > 256)
            return "host_name must be 1-256 characters";
        if (s.RefPos is null || s.RefPos.Length != 3 || s.RefPos.Any(v => !double.IsFinite(v)))
            return "ref_pos must contain three finite numbers";
        return null;
    }

    private static string? ValidateToken(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
            return $"{name} must use 1-128 letters, digits, '.', '_' or '-'";
        return null;
    }

    private sealed class Window
    {
        // Bound the exchange trace so a runaway sender can't grow it without limit; counters
        // keep accumulating past the cap.
        private const int MaxTrackedExchanges = 10_000;

        private readonly object _gate = new();
        private readonly List<ValheimHandshakeExchangeRecord> _exchanges = new();
        private readonly Dictionary<int, long> _byCode = new();
        private readonly HashSet<long> _connectedUids = new();

        private ValheimHandshakeServerContext _context = new();
        private long _begins;
        private long _accepted;
        private long _rejected;
        private long _steadyState;
        private DateTime? _firstUtc;
        private DateTime? _lastUtc;

        /// <summary>
        /// Who holds a seat, keyed on the socket's Steam identity (`host_name`), and when it was
        /// last granted or refreshed. Bounded by SeatCapacity, so it cannot grow the way
        /// _connectedUids does.
        ///
        /// Keyed on host_name and NOT on uid: `ZDOMan.m_sessionID` is a readonly field built in
        /// `ZNet.Awake()` (ZNet.decompiled.cs:264), and ZNet is destroyed when you leave a world —
        /// so every reconnect carries a brand-new uid, even without restarting Valheim. A
        /// uid-keyed seat therefore cannot recognise its own holder coming back, and would tell a
        /// volunteer who quit to the menu and returned 20s later that the server is full for the
        /// rest of the lease. host_name is stable across reconnects and is the same identity
        /// vanilla ticket-verifies (:882).
        /// </summary>
        private readonly Dictionary<string, DateTime> _seats = new(StringComparer.Ordinal);

        public void Configure(ValheimHandshakeServerContext context)
        {
            lock (_gate)
            {
                _context = context;
                _connectedUids.Clear();
                foreach (var uid in context.ConnectedUids)
                    _connectedUids.Add(uid);
                // Reconfiguring re-states the world; a seat held under the old context would be a
                // reservation for a server that no longer exists.
                _seats.Clear();
            }
        }

        public ValheimHandshakeBeginResult Begin(string connectionId)
        {
            lock (_gate)
            {
                _begins++;
                Touch();
                var needPassword = !string.IsNullOrEmpty(_context.Password);
                return new ValheimHandshakeBeginResult(needPassword, _context.Salt, _context.NetworkVersion);
            }
        }

        public ValheimHandshakeGateResult SubmitPeerInfo(
            ValheimPeerInfoSubmission s, DateTime nowUtc, DateTime? lastActivityUtc,
            Func<string?, ValheimRosterVerdict>? roster)
        {
            lock (_gate)
            {
                var result = Evaluate(s, nowUtc, lastActivityUtc, roster);
                Touch();

                if (result.Accept)
                {
                    _accepted++;
                    if (result.EntersSteadyState)
                    {
                        _steadyState++;
                        _connectedUids.Add(s.Uid); // now IsConnected(uid) for the next attempt
                        TakeSeat(s.HostName, nowUtc);
                    }
                }
                else
                {
                    _rejected++;
                    if (result.ErrorCode is int code)
                        _byCode[code] = _byCode.GetValueOrDefault(code) + 1;
                }

                if (_exchanges.Count < MaxTrackedExchanges)
                {
                    _exchanges.Add(new ValheimHandshakeExchangeRecord(
                        s.ConnectionId!, result.Accept, result.EntersSteadyState,
                        result.ErrorCode, result.ErrorName, result.FailedCheck, DateTime.UtcNow));
                }

                return result;
            }
        }

        /// <summary>The ordered gate — the checks run in the exact decompile order; the first
        /// failure returns its code. Mirrors RPC_PeerInfo:835-926. The Lumberjacks seat gate (H)
        /// runs only after all six native checks, so a client vanilla would have rejected still
        /// gets vanilla's own code and label rather than a Lumberjacks one.</summary>
        private ValheimHandshakeGateResult Evaluate(
            ValheimPeerInfoSubmission s, DateTime nowUtc, DateTime? lastActivityUtc,
            Func<string?, ValheimRosterVerdict>? roster)
        {
            // A — network version (RPC_PeerInfo:835)
            if (s.NetVersion != _context.NetworkVersion)
                return Reject(ValheimConnectionStatus.ErrorVersion, "version");

            // B — blacklist / whitelist (IsAllowed, :871 / :2512)
            if (!IsAllowed(s.HostName ?? string.Empty, s.PlayerName ?? string.Empty))
                return Reject(ValheimConnectionStatus.ErrorBanned, "blacklist");

            // C — Steam session ticket (:878-888; verify result supplied by the mod)
            if (_context.RequireSteamTicket && !s.TicketValid)
                return Reject(ValheimConnectionStatus.ErrorBanned, "ticket");

            // D — PlayFab/crossplay (:889-911) omitted: Steam-only per I6.

            // E — server full (GetNrOfPlayers() >= 10, :912)
            if (_context.CurrentPlayers >= _context.MaxPlayers)
                return Reject(ValheimConnectionStatus.ErrorFull, "full");

            // F — wrong password (m_serverPassword != passwordHash, :918)
            if (!string.Equals(_context.Password ?? string.Empty, s.PasswordHash ?? string.Empty,
                    StringComparison.Ordinal))
                return Reject(ValheimConnectionStatus.ErrorPassword, "password");

            // G — duplicate (IsConnected(uid), :924)
            if (_connectedUids.Contains(s.Uid))
                return Reject(ValheimConnectionStatus.ErrorAlreadyConnected, "duplicate");

            // J — Lumberjacks release compatibility. Not a native check. Runs first among the
            // Lumberjacks gates because it asks whether this conversation can be trusted at all,
            // which precedes whether the joiner is allowed (I) or has room (H) — but still after
            // the six native checks, so a client vanilla would have rejected keeps vanilla's label.
            //
            // Unlike I and H this is not about the joiner: it is about the mod answering. A skewed
            // mod rejects everyone, which is severe, and exactly why the flag ships off and flips
            // only once the cut has landed.
            //
            // ErrorVersion, matching gate A: "your side and my side disagree about the build" is
            // precisely what code 3 means to a player, and it is the only honest one available.
            //
            // A NULL id is the stale case and MUST reject when this is on — a mod predating the
            // field sends nothing, which is exactly the mod that will fail open on the strict
            // verdicts this gate exists to protect. Absence is the signal, not an exemption.
            if (_context.StrictReleaseEnabled
                && !string.IsNullOrEmpty(_context.ExpectedModReleaseId)
                && !string.Equals(s.ModReleaseId, _context.ExpectedModReleaseId, StringComparison.Ordinal))
                return Reject(ValheimConnectionStatus.ErrorVersion, "release_incompatible");

            // I — Lumberjacks roster. Runs before the seat gate: whether you are allowed at all
            // precedes whether there is room, and an unenrolled join must not consume a seat.
            // Keyed on HostName (the socket's SteamID64, ZNet.decompiled.cs:833), never on Uid.
            // Surfaces as ErrorBanned because it is the nearest native meaning ("you are not
            // permitted here") and vanilla already uses code 8 for its own blacklist; the specific
            // reason rides failed_check to the operator's log.
            if (_context.StrictRosterEnabled && roster is not null)
            {
                switch (roster(s.HostName))
                {
                    case ValheimRosterVerdict.NotEnrolled:
                        return Reject(ValheimConnectionStatus.ErrorBanned, "not_enrolled");
                    case ValheimRosterVerdict.Revoked:
                        return Reject(ValheimConnectionStatus.ErrorBanned, "enrollment_revoked");
                }
            }

            // H — Lumberjacks seat reservation. NOT a native check: it has no decompile line and
            // runs last so the native verdict always wins first. Surfaces as ErrorFull because that
            // is the only native code whose meaning matches ("the server has no room for you") —
            // the player sees vanilla's full-server screen, and the reason rides failed_check into
            // the server log for the operator (plan §6: only the int reaches the client).
            if (SeatUnavailableFor(s.HostName, nowUtc, lastActivityUtc))
                return Reject(ValheimConnectionStatus.ErrorFull, "capacity_reserved");

            // All pass ⇒ server replies PeerInfo and AddPeer(zdoMan)/AddPeer(routedRpc) (:975-976).
            var reply = new ValheimServerPeerInfo(
                _context.ServerUid, _context.VersionString, _context.NetworkVersion,
                _context.WorldName, _context.Seed, _context.SeedName, _context.WorldUid,
                _context.WorldGenVersion, _context.NetTime);
            return new ValheimHandshakeGateResult(true, true, null, null, null, reply);
        }

        /// <summary>
        /// True when every seat is spoken for by someone other than <paramref name="uid"/>.
        ///
        /// Expiry is passive — evaluated here, on the request that cares — so there is no sweeper
        /// thread and no lock beyond the one SubmitPeerInfo already holds. A seat counts as live
        /// while EITHER its own grant OR the window's consumer traffic is inside the lease: the
        /// grant covers the gap between the verdict and the consumer's first poll, and the consumer
        /// traffic covers the rest of the session. Window-scoped liveness cannot say WHICH holder
        /// is alive, which is exactly good enough at one seat and is why SeatCapacity > 1 is
        /// honoured for counting but not really meaningful yet.
        /// </summary>
        private bool SeatUnavailableFor(string? holderId, DateTime nowUtc, DateTime? lastActivityUtc)
        {
            if (_context.SeatCapacity <= 0)
                return false;

            var seatLease = TimeSpan.FromSeconds(Math.Max(1, _context.SeatLeaseSeconds));
            var recipientLease = TimeSpan.FromSeconds(Math.Max(1, _context.RecipientLeaseSeconds));
            var activityIsLive = lastActivityUtc is DateTime seen && nowUtc - seen < recipientLease;

            foreach (var (holder, grantedUtc) in _seats)
            {
                // The seat is already ours: this is the volunteer reconnecting, which gate G cannot
                // recognise because their uid is new every session. Refresh rather than compete —
                // otherwise quitting to the menu costs them their own server for a full lease.
                if (string.Equals(holder, holderId, StringComparison.Ordinal))
                    return false;
                if (activityIsLive || nowUtc - grantedUtc < seatLease)
                    return true; // SeatCapacity is validated to 0..1, so one live holder is enough
            }
            return false;
        }

        private void TakeSeat(string? holderId, DateTime nowUtc)
        {
            if (_context.SeatCapacity <= 0 || string.IsNullOrWhiteSpace(holderId))
                return;
            // Reaching here means SeatUnavailableFor found no live holder, so any surviving entry
            // has lapsed by definition and clearing is safe. Doing it this way keeps ONE liveness
            // rule in the class: an earlier revision re-derived staleness here from grant age alone
            // while the gate counted grant age OR activity, so a live holder could be purged by the
            // very join their own liveness should have blocked.
            _seats.Clear();
            _seats[holderId] = nowUtc;
        }

        private bool IsAllowed(string host, string name)
        {
            if (_context.BannedHosts.Contains(host, StringComparer.Ordinal)
                || _context.BannedHosts.Contains(name, StringComparer.Ordinal))
                return false;
            if (_context.PermittedHosts.Count > 0
                && !_context.PermittedHosts.Contains(host, StringComparer.Ordinal))
                return false;
            return true;
        }

        private static ValheimHandshakeGateResult Reject(ValheimConnectionStatus status, string check) =>
            new(false, false, (int)status, status.ToString(), check, null);

        private void Touch()
        {
            var now = DateTime.UtcNow;
            _firstUtc ??= now;
            _lastUtc = now;
        }

        public ValheimHandshakeWindowStatus Status(string windowId)
        {
            lock (_gate)
            {
                return new ValheimHandshakeWindowStatus(
                    windowId,
                    _context.NetworkVersion,
                    !string.IsNullOrEmpty(_context.Password),
                    _context.MaxPlayers,
                    _context.CurrentPlayers,
                    _context.RequireSteamTicket,
                    _context.BannedHosts.Count,
                    _context.PermittedHosts.Count,
                    _begins,
                    _accepted,
                    _rejected,
                    _steadyState,
                    _byCode.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    new List<ValheimHandshakeExchangeRecord>(_exchanges),
                    _firstUtc,
                    _lastUtc);
            }
        }

        public static ValheimHandshakeWindowStatus Empty(string windowId) =>
            new(windowId, NetworkVersion, false, MaxPlayers, 0, true, 0, 0,
                0, 0, 0, 0, new Dictionary<string, long>(),
                new List<ValheimHandshakeExchangeRecord>(), null, null);
    }
}
