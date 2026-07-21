using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Game.Gateway.Valheim;

/// <summary>
/// File-backed enrollment store for the volunteer pilot (M1 schema v3).
///
/// Invite, bootstrap, and access tokens are 256-bit random values, so a single
/// unsalted SHA-256 at rest is cryptographically sufficient (no KDF stretching is
/// needed for high-entropy secrets) and cheap enough to verify on every
/// authenticated request. Raw secrets exist only in the issuance response; they are
/// never persisted and never returned again. A v1 plaintext store found on disk is
/// migrated in place on first load.
///
/// Redeeming an invite issues a single-use <see cref="Bootstrap"/>, not the access
/// token. The access token is minted when the installer consumes the bootstrap
/// (<see cref="TryConsumeBootstrap"/>), so the reusable credential never reaches the
/// browser, and — because it is minted on consumption rather than parked in the store
/// waiting for one — it never exists at rest in any form. A bootstrap is worthless
/// once used, so the value the volunteer copies out of a browser cannot be replayed.
/// </summary>
public sealed class SteamEnrollmentService
{
    readonly object _gate = new();
    readonly string _path;
    readonly string _auditPath;
    Dictionary<string, Invite> _invites = new(StringComparer.Ordinal);
    Dictionary<string, Enrollment> _enrollments = new(StringComparer.Ordinal);
    Dictionary<string, Bootstrap> _bootstraps = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> _persistedLastUsed = new(StringComparer.Ordinal);
    static readonly TimeSpan LastUsedPersistInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Defaults to the invite's own 24h lifetime. The single-use rule is what makes a
    /// bootstrap safe to hand to a browser; the window only bounds how long a copied
    /// code stays live in history or a clipboard. Too short is its own hazard: the
    /// enrollment already exists and one-active-per-SteamID refuses a second, so a
    /// volunteer whose bootstrap expired before they installed needs an admin to
    /// revoke and re-invite. Configurable because expiry is otherwise untestable —
    /// this service reads the clock directly and has no injection point.
    /// </summary>
    public const double DefaultBootstrapTtlHours = 24;
    readonly TimeSpan _bootstrapLifetime;

    /// <summary>
    /// Throttles for the self-serve bootstrap re-issue (Steam re-sign-in). The cooldown
    /// bounds how often one enrollment can mint codes — Steam authentication already
    /// gates who can ask, so this only stops a scripted session grinding store writes
    /// and audit lines. The cap bounds the lifetime chain; hitting it means something
    /// other than installer fumbling and is worth an operator's eyes. Both configurable
    /// for the same reason as the TTL: this service reads the clock directly.
    /// </summary>
    public const double DefaultReissueCooldownMinutes = 15;
    public const int DefaultReissueMaxBootstraps = 10;
    readonly TimeSpan _reissueCooldown;
    readonly int _reissueMaxBootstraps;

    public SteamEnrollmentService(IConfiguration configuration)
    {
        _path = configuration["LUMBERJACKS_ENROLLMENT_PATH"] ?? "/var/lib/lumberjacks/enrollment/invites.json";
        _auditPath = Path.Combine(Path.GetDirectoryName(_path) ?? ".", "enrollment-audit.jsonl");
        _bootstrapLifetime = TimeSpan.FromHours(
            double.TryParse(configuration["LUMBERJACKS_BOOTSTRAP_TTL_HOURS"], out var hours)
                ? hours
                : DefaultBootstrapTtlHours);
        _reissueCooldown = TimeSpan.FromMinutes(
            double.TryParse(configuration["LUMBERJACKS_REISSUE_COOLDOWN_MINUTES"], out var cooldown)
                ? cooldown
                : DefaultReissueCooldownMinutes);
        _reissueMaxBootstraps =
            int.TryParse(configuration["LUMBERJACKS_REISSUE_MAX_BOOTSTRAPS"], out var cap)
                ? cap
                : DefaultReissueMaxBootstraps;
        Load();
    }

    public InviteReceipt CreateInvite(TimeSpan lifetime)
    {
        var token = NewToken();
        var invite = new Invite(Hash(token), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(lifetime));
        lock (_gate)
        {
            _invites[invite.TokenHash] = invite;
            Save();
        }
        Audit("invite_created", new { expires_utc = invite.ExpiresUtc });
        return new InviteReceipt(token, invite.ExpiresUtc);
    }

    public bool TryRedeem(string token, string steamId, out BootstrapIssued issued, out string reason)
    {
        issued = null!;
        reason = RedeemLocked(token, steamId, out issued);
        if (reason != "ok")
        {
            Audit("redeem_rejected", new { reason });
            return false;
        }
        Audit("enrollment_redeemed", new { enrollment_id = issued.Enrollment.EnrollmentId, steam_id = steamId });
        return true;
    }

    string RedeemLocked(string token, string steamId, out BootstrapIssued issued)
    {
        issued = null!;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(steamId)) return "invite_invalid";

        lock (_gate)
        {
            if (!_invites.TryGetValue(Hash(token), out var invite)) return "invite_invalid";
            if (invite.Used) return "invite_consumed";
            if (invite.ExpiresUtc <= DateTimeOffset.UtcNow) return "invite_expired";
            // One active enrollment per SteamID; an admin replaces it by revoking
            // the existing record first (explicit, audited).
            if (_enrollments.Values.Any(item =>
                item.Status == EnrollmentStatus.Active &&
                string.Equals(item.SteamId, steamId, StringComparison.Ordinal)))
            {
                return "steamid_already_enrolled";
            }

            // No access token is minted here. The enrollment starts with an empty
            // TokenHash — it exists and holds the SteamID's seat, but carries no
            // credential until the installer consumes the bootstrap. Verify refuses
            // an empty hash outright, so a pending enrollment authenticates nothing.
            var now = DateTimeOffset.UtcNow;
            var enrollment = new Enrollment(
                EnrollmentId: Guid.NewGuid().ToString("N"),
                SteamId: steamId,
                RecipientId: Guid.NewGuid().ToString("N"),
                TokenHash: string.Empty,
                Status: EnrollmentStatus.Active,
                EnrolledUtc: now,
                LastUsedUtc: null,
                RevokedUtc: null,
                RevokedReason: null,
                QueueWindowId: Environment.GetEnvironmentVariable("LUMBERJACKS_AUTHORITATIVE_WINDOW_ID") ?? "p7-primary-v1",
                BootstrapIssueCount: 1);
            var bootstrapToken = NewToken();
            var bootstrap = new Bootstrap(
                TokenHash: Hash(bootstrapToken),
                EnrollmentId: enrollment.EnrollmentId,
                CreatedUtc: now,
                ExpiresUtc: now.Add(_bootstrapLifetime));
            _invites[invite.TokenHash] = invite with { Used = true, EnrollmentId = enrollment.EnrollmentId };
            _enrollments[enrollment.EnrollmentId] = enrollment;
            _bootstraps[bootstrap.TokenHash] = bootstrap;
            Save();
            issued = new BootstrapIssued(View(enrollment), bootstrapToken, bootstrap.ExpiresUtc);
        }
        return "ok";
    }

    /// <summary>
    /// Exchanges a single-use bootstrap for the enrollment's access token, minting the
    /// token at consumption. Replay fails: the bootstrap is marked used inside the same
    /// lock that reads it, so two racing installers cannot both mint a credential, and
    /// the second sees bootstrap_consumed.
    /// </summary>
    public bool TryConsumeBootstrap(string bootstrapToken, out EnrollmentIssued issued, out string reason)
    {
        issued = null!;
        if (string.IsNullOrWhiteSpace(bootstrapToken))
        {
            reason = "bootstrap_invalid";
            Audit("bootstrap_rejected", new { reason });
            return false;
        }

        string enrollmentId;
        lock (_gate)
        {
            if (!_bootstraps.TryGetValue(Hash(bootstrapToken), out var bootstrap)) reason = "bootstrap_invalid";
            else if (bootstrap.Used) reason = "bootstrap_consumed";
            else if (bootstrap.ExpiresUtc <= DateTimeOffset.UtcNow) reason = "bootstrap_expired";
            else if (!_enrollments.TryGetValue(bootstrap.EnrollmentId, out var enrollment)) reason = "enrollment_unknown";
            else if (enrollment.Status != EnrollmentStatus.Active) reason = "enrollment_revoked";
            else
            {
                var accessToken = NewToken();
                var credentialed = enrollment with { TokenHash = Hash(accessToken) };
                _enrollments[credentialed.EnrollmentId] = credentialed;
                _bootstraps[bootstrap.TokenHash] = bootstrap with { Used = true, ConsumedUtc = DateTimeOffset.UtcNow };
                Save();
                issued = new EnrollmentIssued(View(credentialed), accessToken);
                reason = "ok";
            }
            enrollmentId = issued?.Enrollment.EnrollmentId ?? string.Empty;
        }

        if (reason != "ok")
        {
            Audit("bootstrap_rejected", new { reason });
            return false;
        }
        Audit("bootstrap_consumed", new { enrollment_id = enrollmentId });
        return true;
    }

    /// <summary>
    /// Self-serve bootstrap re-issue for a volunteer whose code expired (or was lost)
    /// before they installed. The caller must have Steam-authenticated the SteamID —
    /// this is the same identity root that created the enrollment, so no copyable
    /// artifact (least of all the old code) authorizes a re-issue.
    ///
    /// Pending-only by design: once the bootstrap chain has been consumed and a
    /// credential exists (TokenHash set), re-issue refuses. Recovering a lost
    /// credential stays an admin revoke + re-invite.
    ///
    /// The prior unused bootstrap is deleted, not flagged: a deleted record answers
    /// bootstrap_invalid under every past and future binary, so a rollback cannot
    /// resurrect a superseded code the way an ignored flag would. At most one live
    /// bootstrap exists per enrollment at any time.
    /// </summary>
    public bool TryReissueBootstrap(string steamId, out BootstrapIssued issued, out string reason)
    {
        issued = null!;
        reason = ReissueLocked(steamId, out issued);
        if (reason != "ok")
        {
            Audit("reissue_rejected", new { steam_id = steamId, reason });
            return false;
        }
        Audit("bootstrap_reissued", new
        {
            enrollment_id = issued.Enrollment.EnrollmentId,
            steam_id = steamId,
            issue_count = issued.Enrollment.BootstrapIssueCount,
        });
        return true;
    }

    string ReissueLocked(string steamId, out BootstrapIssued issued)
    {
        issued = null!;
        if (string.IsNullOrWhiteSpace(steamId)) return "not_enrolled";
        lock (_gate)
        {
            var matches = _enrollments.Values
                .Where(item => string.Equals(item.SteamId, steamId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0) return "not_enrolled";
            var active = matches.FirstOrDefault(item => item.Status == EnrollmentStatus.Active);
            if (active is null) return "enrollment_revoked";
            if (!string.IsNullOrEmpty(active.TokenHash)) return "already_installed";
            // Exhaustion is terminal, cooldown is retryable; check the terminal one
            // first so the volunteer is not told to wait for a re-issue that can
            // never come.
            if (active.BootstrapIssueCount >= _reissueMaxBootstraps) return "reissue_exhausted";
            var pending = _bootstraps
                .Where(pair => string.Equals(pair.Value.EnrollmentId, active.EnrollmentId, StringComparison.Ordinal) && !pair.Value.Used)
                .ToList();
            var now = DateTimeOffset.UtcNow;
            // The newest pending code anchors the cooldown, whether it came from the
            // original redeem or a previous re-issue.
            var lastIssuedUtc = pending.Count == 0
                ? DateTimeOffset.MinValue
                : pending.Max(pair => pair.Value.CreatedUtc);
            if (now - lastIssuedUtc < _reissueCooldown) return "reissue_cooldown";
            foreach (var pair in pending) _bootstraps.Remove(pair.Key);
            var token = NewToken();
            var bootstrap = new Bootstrap(
                TokenHash: Hash(token),
                EnrollmentId: active.EnrollmentId,
                CreatedUtc: now,
                ExpiresUtc: now.Add(_bootstrapLifetime));
            var updated = active with { BootstrapIssueCount = active.BootstrapIssueCount + 1 };
            _bootstraps[bootstrap.TokenHash] = bootstrap;
            _enrollments[updated.EnrollmentId] = updated;
            Save();
            issued = new BootstrapIssued(View(updated), token, bootstrap.ExpiresUtc);
        }
        return "ok";
    }

    public bool Verify(string enrollmentId, string accessToken, out EnrollmentView view, out string reason)
    {
        view = null!;
        if (string.IsNullOrWhiteSpace(enrollmentId) || string.IsNullOrWhiteSpace(accessToken))
        {
            reason = "credentials_required";
            return false;
        }
        lock (_gate)
        {
            if (!_enrollments.TryGetValue(enrollmentId, out var enrollment))
            {
                reason = "enrollment_unknown";
                return false;
            }
            // An enrollment whose bootstrap has not been consumed carries no
            // credential. FixedTimeEquals would already refuse it on length, but the
            // guard is explicit so that a future change to the hash encoding cannot
            // turn "no credential" into a comparison that might succeed.
            if (string.IsNullOrEmpty(enrollment.TokenHash))
            {
                reason = "bootstrap_pending";
                return false;
            }
            if (!FixedTimeEquals(enrollment.TokenHash, Hash(accessToken)))
            {
                reason = "token_invalid";
                return false;
            }
            if (enrollment.Status != EnrollmentStatus.Active)
            {
                reason = "enrollment_revoked";
                return false;
            }
            var now = DateTimeOffset.UtcNow;
            enrollment = enrollment with { LastUsedUtc = now };
            _enrollments[enrollmentId] = enrollment;
            // Persisting on every verified request would thrash the store at the
            // consumer poll rate; the on-disk last-used value trails by ≤60 s.
            if (!_persistedLastUsed.TryGetValue(enrollmentId, out var persisted) ||
                now - persisted >= LastUsedPersistInterval)
            {
                _persistedLastUsed[enrollmentId] = now;
                Save();
            }
            view = View(enrollment);
        }
        reason = "ok";
        return true;
    }

    /// <summary>Compatibility shim for callers that only need a boolean.</summary>
    public bool IsCredentialValid(string enrollmentId, string accessToken) =>
        Verify(enrollmentId, accessToken, out _, out _);

    public IReadOnlyList<EnrollmentView> List()
    {
        lock (_gate) return _enrollments.Values.Select(View).OrderBy(item => item.EnrolledUtc).ToList();
    }

    /// <summary>
    /// Roster answer for the admission gate, keyed on the joining SteamID64.
    ///
    /// Separates "never invited" from "was invited, then revoked or replaced" because they are
    /// different operator stories and the plan's §4 matrix names them separately
    /// (not_enrolled vs enrollment_revoked). At most one Active can exist per SteamID —
    /// RedeemLocked refuses to create a second and MigrateV1 collapses v1 duplicates — so the
    /// first Active match is the answer, not an arbitrary one.
    /// </summary>
    public ValheimRosterVerdict CheckSteamId(string? steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
            return ValheimRosterVerdict.NotEnrolled;
        lock (_gate)
        {
            var matches = _enrollments.Values
                .Where(item => string.Equals(item.SteamId, steamId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
                return ValheimRosterVerdict.NotEnrolled;
            return matches.Any(item => item.Status == EnrollmentStatus.Active)
                ? ValheimRosterVerdict.Active
                : ValheimRosterVerdict.Revoked;
        }
    }

    /// <summary>Returns the active opaque delivery recipient for a SteamID, if enrolled.</summary>
    public string? GetRecipientId(string? steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return null;
        lock (_gate)
        {
            return _enrollments.Values
                .FirstOrDefault(item => string.Equals(item.SteamId, steamId, StringComparison.Ordinal)
                    && item.Status == EnrollmentStatus.Active)?.RecipientId;
        }
    }

    public EnrollmentView? Get(string enrollmentId)
    {
        lock (_gate) return _enrollments.TryGetValue(enrollmentId, out var item) ? View(item) : null;
    }

    public bool Revoke(string enrollmentId, string reason)
    {
        lock (_gate)
        {
            if (!_enrollments.TryGetValue(enrollmentId, out var enrollment) ||
                enrollment.Status != EnrollmentStatus.Active) return false;
            _enrollments[enrollmentId] = enrollment with
            {
                Status = EnrollmentStatus.Revoked,
                RevokedUtc = DateTimeOffset.UtcNow,
                RevokedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason,
            };
            Save();
        }
        Audit("enrollment_revoked", new { enrollment_id = enrollmentId, reason });
        return true;
    }

    static EnrollmentView View(Enrollment enrollment) => new(
        enrollment.EnrollmentId,
        enrollment.SteamId,
        enrollment.RecipientId,
        enrollment.Status.ToString().ToLowerInvariant(),
        enrollment.EnrolledUtc,
        enrollment.LastUsedUtc,
        enrollment.QueueWindowId,
        enrollment.BootstrapIssueCount);

    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var b = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    void Audit(string eventName, object detail)
    {
        try
        {
            var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, @event = eventName, detail });
            lock (_gate) File.AppendAllText(_auditPath, line + "\n");
        }
        catch
        {
            // The audit trail must never take the control plane down.
        }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var text = File.ReadAllText(_path);
            using var probe = JsonDocument.Parse(text);
            if (probe.RootElement.TryGetProperty("schema_version", out _))
            {
                var store = JsonSerializer.Deserialize<StoreFile>(text, SerializerOptions);
                if (store is not null)
                {
                    _invites = store.Invites ?? _invites;
                    _enrollments = store.Enrollments ?? _enrollments;
                    // v2 has no bootstraps property; it deserializes to null and the
                    // next Save rewrites the file as v3. Existing v2 enrollments keep
                    // their access token hash and keep verifying, so a store written
                    // before this change needs no migration pass of its own.
                    _bootstraps = store.Bootstraps ?? _bootstraps;
                }
                return;
            }
            MigrateV1(text);
        }
        catch
        {
            // An unreadable store yields an empty roster: fail closed (nobody
            // verifies) rather than failing open.
        }
    }

    /// <summary>v1 stored raw invite tokens as keys and raw access tokens inline.</summary>
    void MigrateV1(string text)
    {
        var v1 = JsonSerializer.Deserialize<Dictionary<string, V1Invite>>(text);
        if (v1 is null) return;
        foreach (var (rawToken, invite) in v1)
        {
            string? enrollmentId = null;
            if (invite.Enrollment is not null)
            {
                enrollmentId = invite.Enrollment.ManifestId;
                _enrollments[enrollmentId] = new Enrollment(
                    EnrollmentId: enrollmentId,
                    SteamId: invite.Enrollment.SteamId,
                    RecipientId: Guid.NewGuid().ToString("N"),
                    TokenHash: Hash(invite.Enrollment.AccessToken),
                    Status: EnrollmentStatus.Active,
                    EnrolledUtc: invite.Enrollment.EnrolledUtc,
                    LastUsedUtc: null,
                    RevokedUtc: null,
                    RevokedReason: null,
                    QueueWindowId: invite.Enrollment.QueueWindowId);
            }
            _invites[Hash(rawToken)] = new Invite(Hash(rawToken), invite.CreatedUtc, invite.ExpiresUtc, invite.Used, enrollmentId);
        }
        var collapsed = CollapseDuplicateSteamIds();
        Save();
        // Audited only once the collapse is on disk, matching Revoke.
        foreach (var (enrollment, survivorId) in collapsed)
        {
            Audit("enrollment_revoked", new
            {
                enrollment_id = enrollment.EnrollmentId,
                steam_id = enrollment.SteamId,
                reason = SupersededByMigration,
                superseded_by = survivorId,
            });
        }
        Audit("store_migrated_v1", new
        {
            invites = _invites.Count,
            enrollments = _enrollments.Count,
            collapsed = collapsed.Count,
        });
    }

    /// <summary>
    /// v1 had no one-active-enrollment-per-SteamID rule, so its store can hold
    /// several redeemed invites for the same player. Migrating them all as active
    /// would seed a v2 roster that violates the invariant <see cref="RedeemLocked"/>
    /// enforces on every redeem. The newest enrollment wins: it is the one the
    /// player most recently proved they wanted, and it matches what an admin doing
    /// the replacement by hand would have done.
    /// </summary>
    List<(Enrollment Superseded, string SurvivorId)> CollapseDuplicateSteamIds()
    {
        var collapsed = new List<(Enrollment, string)>();
        // Materialized before the loop mutates _enrollments.
        var duplicates = _enrollments.Values
            .Where(item => item.Status == EnrollmentStatus.Active)
            .GroupBy(item => item.SteamId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();
        foreach (var group in duplicates)
        {
            // Ties on EnrolledUtc break on id so the survivor never depends on
            // the order the v1 dictionary happened to deserialize in.
            var ordered = group
                .OrderByDescending(item => item.EnrolledUtc)
                .ThenByDescending(item => item.EnrollmentId, StringComparer.Ordinal)
                .ToList();
            var survivor = ordered[0];
            foreach (var enrollment in ordered.Skip(1))
            {
                _enrollments[enrollment.EnrollmentId] = enrollment with
                {
                    Status = EnrollmentStatus.Revoked,
                    RevokedUtc = DateTimeOffset.UtcNow,
                    RevokedReason = SupersededByMigration,
                };
                collapsed.Add((enrollment, survivor.EnrollmentId));
            }
        }
        return collapsed;
    }

    public const string SupersededByMigration = "superseded_by_migration";

    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var store = new StoreFile(3, _invites, _enrollments, _bootstraps);
        File.WriteAllText(_path, JsonSerializer.Serialize(store, SerializerOptions));
    }

    public enum EnrollmentStatus { Active, Revoked }

    sealed record StoreFile(
        [property: System.Text.Json.Serialization.JsonPropertyName("schema_version")] int SchemaVersion,
        Dictionary<string, Invite> Invites,
        Dictionary<string, Enrollment> Enrollments,
        Dictionary<string, Bootstrap>? Bootstraps = null);

    public sealed record Invite(string TokenHash, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, bool Used = false, string? EnrollmentId = null);
    public sealed record InviteReceipt(string Token, DateTimeOffset ExpiresUtc);

    /// <summary>
    /// Single-use handoff from the browser to the installer. Holds only a hash and the
    /// enrollment it credentials — never a secret, because the access token it yields
    /// does not exist until this is consumed.
    /// </summary>
    public sealed record Bootstrap(
        string TokenHash,
        string EnrollmentId,
        DateTimeOffset CreatedUtc,
        DateTimeOffset ExpiresUtc,
        bool Used = false,
        DateTimeOffset? ConsumedUtc = null);

    /// <summary>What the browser gets: a single-use code, no reusable credential.</summary>
    public sealed record BootstrapIssued(EnrollmentView Enrollment, string BootstrapToken, DateTimeOffset ExpiresUtc);

    /// <summary>What the installer gets, exactly once: the access token itself.</summary>
    public sealed record EnrollmentIssued(EnrollmentView Enrollment, string AccessToken);

    public sealed record Enrollment(
        string EnrollmentId,
        string SteamId,
        string RecipientId,
        string TokenHash,
        EnrollmentStatus Status,
        DateTimeOffset EnrolledUtc,
        DateTimeOffset? LastUsedUtc,
        DateTimeOffset? RevokedUtc,
        string? RevokedReason,
        string QueueWindowId,
        // Lifetime count of bootstraps minted for this enrollment (initial redeem = 1).
        // Additive on schema v3: pre-existing records deserialize as 0, which under-counts
        // by one and grants one spare re-issue — an abuse bound, not a security invariant.
        int BootstrapIssueCount = 0);

    /// <summary>Secret-free projection served to admins and to the enrollee itself.</summary>
    public sealed record EnrollmentView(
        string EnrollmentId,
        string SteamId,
        string RecipientId,
        string Status,
        DateTimeOffset EnrolledUtc,
        DateTimeOffset? LastUsedUtc,
        string QueueWindowId,
        int BootstrapIssueCount = 0);

    sealed record V1Invite(string Token, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, bool Used = false, V1Enrollment? Enrollment = null);
    sealed record V1Enrollment(
        string SteamId,
        string ManifestId,
        DateTimeOffset EnrolledUtc,
        string AccessToken = "",
        string QueueWindowId = "p7-primary-v1");
}
