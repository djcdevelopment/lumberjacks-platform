using System.Globalization;
using System.Text.Json;
using Game.Gateway.Valheim;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class SteamEnrollmentServiceTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "lumberjacks-enrollment-" + Guid.NewGuid().ToString("N"));

    string StorePath => Path.Combine(_directory, "invites.json");

    [Fact]
    public void Redeem_BindsSteamIdentityToQueueWindowAndIssuesOnlyABootstrap()
    {
        var service = CreateService();
        var invite = service.CreateInvite(TimeSpan.FromMinutes(5));

        const string syntheticSteamId = "76561198000000001";
        Assert.True(service.TryRedeem(invite.Token, syntheticSteamId, out var issued, out _));
        Assert.Equal(syntheticSteamId, issued.Enrollment.SteamId);
        Assert.Equal("p7-primary-v1", issued.Enrollment.QueueWindowId);
        Assert.NotEmpty(issued.Enrollment.EnrollmentId);
        Assert.NotEmpty(issued.Enrollment.RecipientId);
        Assert.NotEmpty(issued.BootstrapToken);

        // What the browser holds is not a credential. The enrollment exists and owns
        // the SteamID's seat, but authenticates nothing until the installer acts.
        Assert.False(service.Verify(issued.Enrollment.EnrollmentId, issued.BootstrapToken, out _, out var pending));
        Assert.Equal("bootstrap_pending", pending);

        Assert.True(service.TryConsumeBootstrap(issued.BootstrapToken, out var credentialed, out _));
        Assert.Equal(issued.Enrollment.EnrollmentId, credentialed.Enrollment.EnrollmentId);
        Assert.Equal(issued.Enrollment.RecipientId, credentialed.Enrollment.RecipientId);
        Assert.NotEmpty(credentialed.AccessToken);
        Assert.True(service.IsCredentialValid(credentialed.Enrollment.EnrollmentId, credentialed.AccessToken));
        Assert.False(service.IsCredentialValid(credentialed.Enrollment.EnrollmentId, "wrong"));
    }

    [Fact]
    public void Bootstrap_IsSingleUse()
    {
        var service = CreateService();
        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var issued, out _));
        Assert.True(service.TryConsumeBootstrap(issued.BootstrapToken, out var installed, out _));

        Assert.False(service.TryConsumeBootstrap(issued.BootstrapToken, out _, out var reason));
        Assert.Equal("bootstrap_consumed", reason);

        // Replay fails without disturbing what the one legitimate consume minted.
        Assert.True(service.IsCredentialValid(installed.Enrollment.EnrollmentId, installed.AccessToken));
    }

    [Fact]
    public void Bootstrap_MintsAFreshCredentialRatherThanReplayingOne()
    {
        var service = CreateService();
        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var first, out _));
        Assert.True(service.TryConsumeBootstrap(first.BootstrapToken, out var firstInstall, out _));

        // A revoke-and-re-invite cycle must not resurrect the previous credential.
        Assert.True(service.Revoke(firstInstall.Enrollment.EnrollmentId, "replaced in test"));
        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var second, out _));
        Assert.True(service.TryConsumeBootstrap(second.BootstrapToken, out var secondInstall, out _));

        Assert.NotEqual(firstInstall.AccessToken, secondInstall.AccessToken);
        Assert.False(service.IsCredentialValid(firstInstall.Enrollment.EnrollmentId, firstInstall.AccessToken));
        Assert.True(service.IsCredentialValid(secondInstall.Enrollment.EnrollmentId, secondInstall.AccessToken));
    }

    [Fact]
    public void Bootstrap_RejectsExpired()
    {
        var service = CreateService(bootstrapTtlHours: -1);
        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var issued, out _));

        Assert.False(service.TryConsumeBootstrap(issued.BootstrapToken, out _, out var reason));
        Assert.Equal("bootstrap_expired", reason);
        // Expiry leaves the enrollment credential-less rather than open.
        Assert.False(service.Verify(issued.Enrollment.EnrollmentId, issued.BootstrapToken, out _, out var pending));
        Assert.Equal("bootstrap_pending", pending);
    }

    [Fact]
    public void Bootstrap_RejectsUnknownTokenAndRevokedEnrollment()
    {
        var service = CreateService();
        Assert.False(service.TryConsumeBootstrap("never-issued", out _, out var unknown));
        Assert.Equal("bootstrap_invalid", unknown);

        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var issued, out _));
        Assert.True(service.Revoke(issued.Enrollment.EnrollmentId, "revoked before install"));

        // An operator who revokes between invite and install must not be overtaken by
        // a volunteer who still holds the code.
        Assert.False(service.TryConsumeBootstrap(issued.BootstrapToken, out _, out var revoked));
        Assert.Equal("enrollment_revoked", revoked);
    }

    [Fact]
    public void Reissue_MintsAFreshCodeAndKillsThePriorOne()
    {
        var service = CreateService(reissueCooldownMinutes: 0);
        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var original, out _));

        Assert.True(service.TryReissueBootstrap("76561198000000001", out var reissued, out _));
        Assert.NotEqual(original.BootstrapToken, reissued.BootstrapToken);
        // Same enrollment, same recipient: re-issue replaces the code, not the identity.
        Assert.Equal(original.Enrollment.EnrollmentId, reissued.Enrollment.EnrollmentId);
        Assert.Equal(original.Enrollment.RecipientId, reissued.Enrollment.RecipientId);
        Assert.Equal(2, reissued.Enrollment.BootstrapIssueCount);

        // The superseded code is gone from the store, so it answers as never-issued —
        // and stays dead even under a binary that predates re-issue.
        Assert.False(service.TryConsumeBootstrap(original.BootstrapToken, out _, out var dead));
        Assert.Equal("bootstrap_invalid", dead);
        Assert.DoesNotContain(reissued.BootstrapToken, File.ReadAllText(StorePath), StringComparison.Ordinal);

        Assert.True(service.TryConsumeBootstrap(reissued.BootstrapToken, out var installed, out _));
        Assert.True(service.IsCredentialValid(installed.Enrollment.EnrollmentId, installed.AccessToken));
    }

    [Fact]
    public void Reissue_RecoversAnExpiredBootstrapAcrossARestart()
    {
        // The stranded case this exists for: the code expired before the install.
        var expired = CreateService(bootstrapTtlHours: -1);
        Assert.True(expired.TryRedeem(
            expired.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var original, out _));
        Assert.False(expired.TryConsumeBootstrap(original.BootstrapToken, out _, out var reason));
        Assert.Equal("bootstrap_expired", reason);

        // A second service on the same store proves the chain survives a restart.
        var service = CreateService(reissueCooldownMinutes: 0);
        Assert.True(service.TryReissueBootstrap("76561198000000001", out var reissued, out _));
        Assert.True(service.TryConsumeBootstrap(reissued.BootstrapToken, out var installed, out _));
        Assert.True(service.IsCredentialValid(installed.Enrollment.EnrollmentId, installed.AccessToken));
    }

    [Fact]
    public void Reissue_RefusesOnceInstalled()
    {
        // Pending-only by design: once a credential exists, recovery is admin
        // revoke + re-invite, not self-serve.
        var service = CreateService(reissueCooldownMinutes: 0);
        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var issued, out _));
        Assert.True(service.TryConsumeBootstrap(issued.BootstrapToken, out _, out _));

        Assert.False(service.TryReissueBootstrap("76561198000000001", out _, out var reason));
        Assert.Equal("already_installed", reason);
    }

    [Fact]
    public void Reissue_RefusesUnknownAndRevoked()
    {
        var service = CreateService(reissueCooldownMinutes: 0);
        Assert.False(service.TryReissueBootstrap("76561190000000009", out _, out var unknown));
        Assert.Equal("not_enrolled", unknown);

        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var issued, out _));
        Assert.True(service.Revoke(issued.Enrollment.EnrollmentId, "revoked before install"));
        Assert.False(service.TryReissueBootstrap("76561198000000001", out _, out var revoked));
        Assert.Equal("enrollment_revoked", revoked);
    }

    [Fact]
    public void Reissue_CooldownBlocksAnImmediateRepeat()
    {
        // Default cooldown: the bootstrap minted by the redeem seconds ago anchors it.
        var service = CreateService();
        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out _, out _));

        Assert.False(service.TryReissueBootstrap("76561198000000001", out _, out var reason));
        Assert.Equal("reissue_cooldown", reason);
    }

    [Fact]
    public void Reissue_ChainCapExhausts()
    {
        var service = CreateService(reissueCooldownMinutes: 0, reissueMaxBootstraps: 3);
        Assert.True(service.TryRedeem(
            service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out _, out _));

        Assert.True(service.TryReissueBootstrap("76561198000000001", out _, out _));
        Assert.True(service.TryReissueBootstrap("76561198000000001", out var last, out _));
        Assert.Equal(3, last.Enrollment.BootstrapIssueCount);

        Assert.False(service.TryReissueBootstrap("76561198000000001", out _, out var reason));
        Assert.Equal("reissue_exhausted", reason);
        // Exhaustion refuses new codes; it does not damage the one still pending.
        Assert.True(service.TryConsumeBootstrap(last.BootstrapToken, out _, out _));
    }

    [Fact]
    public void Redeem_IsSingleUse()
    {
        var service = CreateService();
        var invite = service.CreateInvite(TimeSpan.FromMinutes(5));

        Assert.True(service.TryRedeem(invite.Token, "76561198000000001", out _, out _));
        Assert.False(service.TryRedeem(invite.Token, "76561198000000000", out _, out var reason));
        Assert.Equal("invite_consumed", reason);
    }

    [Fact]
    public void Redeem_RejectsExpiredInvite()
    {
        var service = CreateService();
        var invite = service.CreateInvite(TimeSpan.FromMilliseconds(-1));

        Assert.False(service.TryRedeem(invite.Token, "76561198000000001", out _, out var reason));
        Assert.Equal("invite_expired", reason);
    }

    [Fact]
    public void Redeem_EnforcesOneActiveEnrollmentPerSteamId()
    {
        var service = CreateService();
        const string steamId = "76561198000000001";
        Assert.True(service.TryRedeem(service.CreateInvite(TimeSpan.FromMinutes(5)).Token, steamId, out var first, out _));

        Assert.False(service.TryRedeem(service.CreateInvite(TimeSpan.FromMinutes(5)).Token, steamId, out _, out var reason));
        Assert.Equal("steamid_already_enrolled", reason);

        // Admin replacement is explicit: revoke, then a fresh invite redeems.
        Assert.True(service.Revoke(first.Enrollment.EnrollmentId, "replaced in test"));
        Assert.True(service.TryRedeem(service.CreateInvite(TimeSpan.FromMinutes(5)).Token, steamId, out _, out _));
    }

    [Fact]
    public void Revoke_InvalidatesCredentialWithReason()
    {
        var service = CreateService();
        Assert.True(service.TryRedeem(service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var issued, out _));
        Assert.True(service.TryConsumeBootstrap(issued.BootstrapToken, out var installed, out _));
        Assert.True(service.Revoke(installed.Enrollment.EnrollmentId, "test revocation"));

        Assert.False(service.Verify(installed.Enrollment.EnrollmentId, installed.AccessToken, out _, out var reason));
        Assert.Equal("enrollment_revoked", reason);
        Assert.False(service.Revoke(installed.Enrollment.EnrollmentId, "twice"));
    }

    [Fact]
    public void Store_NeverPersistsRawSecrets()
    {
        var service = CreateService();
        var invite = service.CreateInvite(TimeSpan.FromMinutes(5));
        Assert.True(service.TryRedeem(invite.Token, "76561198000000001", out var issued, out _));

        // Before the install: the store holds a pending bootstrap and no credential at
        // all, because the access token does not exist yet.
        var pendingText = File.ReadAllText(StorePath);
        Assert.DoesNotContain(invite.Token, pendingText, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.BootstrapToken, pendingText, StringComparison.Ordinal);

        Assert.True(service.TryConsumeBootstrap(issued.BootstrapToken, out var installed, out _));

        var storeText = File.ReadAllText(StorePath);
        Assert.DoesNotContain(invite.Token, storeText, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.BootstrapToken, storeText, StringComparison.Ordinal);
        Assert.DoesNotContain(installed.AccessToken, storeText, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_UpdatesLastUsed()
    {
        var service = CreateService();
        Assert.True(service.TryRedeem(service.CreateInvite(TimeSpan.FromMinutes(5)).Token, "76561198000000001", out var issued, out _));
        Assert.True(service.TryConsumeBootstrap(issued.BootstrapToken, out var installed, out _));
        Assert.Null(installed.Enrollment.LastUsedUtc);

        Assert.True(service.Verify(installed.Enrollment.EnrollmentId, installed.AccessToken, out var view, out _));
        Assert.NotNull(view.LastUsedUtc);
    }

    /// <summary>
    /// A v2 store predates bootstraps entirely: its enrollments already hold a token
    /// hash. Loading one under v3 must leave the frozen mod's credential working and
    /// must not strand it as bootstrap_pending.
    /// </summary>
    [Fact]
    public void Load_V2StoreKeepsWorkingAndUpgradesInPlace()
    {
        WriteV1StoreWithDuplicateSteamId();
        CreateService(); // migrates v1 -> v2-shaped store, then Save writes v3

        var reloaded = CreateService();
        Assert.True(reloaded.Verify("enrollment-1", "v1-raw-access-token-1", out _, out var reason));
        Assert.Equal("ok", reason);

        using var store = JsonDocument.Parse(File.ReadAllText(StorePath));
        Assert.Equal(3, store.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void Load_MigratesV1PlaintextStore()
    {
        Directory.CreateDirectory(_directory);
        const string rawInviteToken = "v1-raw-invite-token";
        const string rawAccessToken = "v1-raw-access-token";
        const string enrollmentId = "abcdef0123456789";
        var v1 = new Dictionary<string, object>
        {
            [rawInviteToken] = new
            {
                Token = rawInviteToken,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                Used = true,
                Enrollment = new
                {
                    SteamId = "76561198000000001",
                    ManifestId = enrollmentId,
                    EnrolledUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    AccessToken = rawAccessToken,
                    QueueWindowId = "p7-primary-v1",
                },
            },
        };
        File.WriteAllText(StorePath, JsonSerializer.Serialize(v1));

        var service = CreateService();

        // The frozen mod keeps presenting the same enrollment id + token pair.
        Assert.True(service.Verify(enrollmentId, rawAccessToken, out var view, out _));
        Assert.Equal("76561198000000001", view.SteamId);
        Assert.NotEmpty(view.RecipientId);

        var migratedText = File.ReadAllText(StorePath);
        Assert.DoesNotContain(rawAccessToken, migratedText, StringComparison.Ordinal);
        Assert.DoesNotContain(rawInviteToken, migratedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mirrors the shape the production P7 store is in: several redeemed v1
    /// invites for one player, deliberately not in EnrolledUtc order.
    /// </summary>
    const string DuplicateSteamId = "76561198088711642";

    void WriteV1StoreWithDuplicateSteamId()
    {
        Directory.CreateDirectory(_directory);
        var enrolled = new[]
        {
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(-10),
        };
        var v1 = new Dictionary<string, object>();
        for (var i = 0; i < enrolled.Length; i++)
        {
            v1["v1-raw-invite-token-" + i] = new
            {
                Token = "v1-raw-invite-token-" + i,
                CreatedUtc = enrolled[i],
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                Used = true,
                Enrollment = new
                {
                    SteamId = DuplicateSteamId,
                    ManifestId = "enrollment-" + i,
                    EnrolledUtc = enrolled[i],
                    AccessToken = "v1-raw-access-token-" + i,
                    QueueWindowId = "p7-primary-v1",
                },
            };
        }
        File.WriteAllText(StorePath, JsonSerializer.Serialize(v1));
    }

    [Fact]
    public void Load_CollapsesDuplicateSteamIdsToNewestActiveEnrollment()
    {
        WriteV1StoreWithDuplicateSteamId();
        const string steamId = DuplicateSteamId;

        var service = CreateService();

        // enrollment-1 is the newest (2 days ago), so it is the one that survives.
        var roster = service.List();
        Assert.Equal(3, roster.Count);
        var active = Assert.Single(roster, item => item.Status == "active");
        Assert.Equal("enrollment-1", active.EnrollmentId);
        Assert.Equal(steamId, active.SteamId);

        foreach (var superseded in new[] { "enrollment-0", "enrollment-2" })
        {
            Assert.Equal("revoked", service.Get(superseded)!.Status);
            // The revoked credential no longer verifies, reason included.
            var index = superseded["enrollment-".Length..];
            Assert.False(service.Verify(superseded, "v1-raw-access-token-" + index, out _, out var reason));
            Assert.Equal("enrollment_revoked", reason);
        }

        // The survivor's frozen-mod credential still works.
        Assert.True(service.Verify("enrollment-1", "v1-raw-access-token-1", out _, out _));

        var audit = File.ReadAllLines(Path.Combine(_directory, "enrollment-audit.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
        var collapses = audit
            .Where(entry => entry.GetProperty("event").GetString() == "enrollment_revoked")
            .Select(entry => entry.GetProperty("detail"))
            .ToList();
        Assert.Equal(2, collapses.Count);
        Assert.All(collapses, detail =>
        {
            Assert.Equal(SteamEnrollmentService.SupersededByMigration, detail.GetProperty("reason").GetString());
            Assert.Equal(steamId, detail.GetProperty("steam_id").GetString());
            Assert.Equal("enrollment-1", detail.GetProperty("superseded_by").GetString());
        });
        Assert.Equal(
            new[] { "enrollment-0", "enrollment-2" },
            collapses.Select(detail => detail.GetProperty("enrollment_id").GetString()).OrderBy(id => id, StringComparer.Ordinal));

        var migration = Assert.Single(audit, entry => entry.GetProperty("event").GetString() == "store_migrated_v1");
        Assert.Equal(2, migration.GetProperty("detail").GetProperty("collapsed").GetInt32());
    }

    [Fact]
    public void Load_CollapsedDuplicateSurvivesRoundTripAndHoldsTheInvariant()
    {
        WriteV1StoreWithDuplicateSteamId();
        CreateService();

        // Reload the migrated v2 store: the collapse must have been persisted, not
        // just applied in memory, and the surviving record still owns the SteamID.
        var reloaded = CreateService();
        var active = Assert.Single(reloaded.List(), item => item.Status == "active");
        Assert.Equal("enrollment-1", active.EnrollmentId);

        Assert.False(reloaded.TryRedeem(
            reloaded.CreateInvite(TimeSpan.FromMinutes(5)).Token, DuplicateSteamId, out _, out var reason));
        Assert.Equal("steamid_already_enrolled", reason);

        // And an admin revoking the survivor frees the SteamID exactly once.
        Assert.True(reloaded.Revoke("enrollment-1", "replaced in test"));
        Assert.True(reloaded.TryRedeem(
            reloaded.CreateInvite(TimeSpan.FromMinutes(5)).Token, DuplicateSteamId, out _, out _));
    }

    SteamEnrollmentService CreateService(
        double? bootstrapTtlHours = null,
        double? reissueCooldownMinutes = null,
        int? reissueMaxBootstraps = null)
    {
        Directory.CreateDirectory(_directory);
        var settings = new Dictionary<string, string?>
        {
            ["LUMBERJACKS_ENROLLMENT_PATH"] = StorePath,
        };
        if (bootstrapTtlHours is not null)
            settings["LUMBERJACKS_BOOTSTRAP_TTL_HOURS"] = bootstrapTtlHours.Value.ToString(CultureInfo.InvariantCulture);
        if (reissueCooldownMinutes is not null)
            settings["LUMBERJACKS_REISSUE_COOLDOWN_MINUTES"] = reissueCooldownMinutes.Value.ToString(CultureInfo.InvariantCulture);
        if (reissueMaxBootstraps is not null)
            settings["LUMBERJACKS_REISSUE_MAX_BOOTSTRAPS"] = reissueMaxBootstraps.Value.ToString(CultureInfo.InvariantCulture);
        return new SteamEnrollmentService(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
