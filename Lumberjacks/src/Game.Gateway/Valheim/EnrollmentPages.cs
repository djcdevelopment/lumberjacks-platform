using System.Net;

namespace Game.Gateway.Valheim;

/// <summary>
/// Server-rendered HTML for the volunteer self-service enrollment flow — styled, single-file, no
/// wwwroot. One <see cref="Shell"/> wraps every page with shared CSS and a live server-status banner
/// that polls the public cutover snapshot, so a volunteer sees "join now" vs "come back" without any
/// operator round-trip.
/// </summary>
static class EnrollmentPages
{
    // The Valheim connect address shown when the server is up; overridable for non-P7 deployments.
    static string Connect() =>
        Environment.GetEnvironmentVariable("LUMBERJACKS_VALHEIM_CONNECT") ?? "8.231.129.249:2456";

    public static string InvitePage(string loginUrl) => Shell(
        "Lumberjacks invite",
        "<h1>Lumberjacks alpha</h1>" +
        "<p>You've been invited to the modded Valheim netcode test. Sign in with Steam to enrol — " +
        "you'll get a one-click download of a ready-to-play mod pack.</p>" +
        "<p><a class=\"btn\" href=\"" + WebUtility.HtmlEncode(loginUrl) + "\">Sign in with Steam</a></p>",
        withStatus: true);

    public static string ReissuePage(string loginUrl) => Shell(
        "Lumberjacks — fresh link",
        "<h1>Get a fresh download link</h1>" +
        "<p>Download didn't work, or expired before you installed? Sign in with Steam again for a fresh " +
        "one. Your enrolment stays as it is; the old link stops working.</p>" +
        "<p><a class=\"btn\" href=\"" + WebUtility.HtmlEncode(loginUrl) + "\">Sign in with Steam</a></p>",
        withStatus: true);

    public static string UpdatePage(string loginUrl) => Shell(
        "Lumberjacks - latest update",
        "<h1>Download the latest mod files</h1>" +
        "<p>Sign in with Steam and download the current alpha pack. Installed clients keep their " +
        "existing ComfyNetworkSense config and access key; recovery is a separate operator action.</p>" +
        CurrentModpackBox() +
        CompanionBootstrapBox() +
        ReleaseHistoryTable() +
        "<p><a class=\"btn\" href=\"" + WebUtility.HtmlEncode(loginUrl) + "\">Sign in with Steam</a></p>",
        withStatus: true);

    public static string DownloadPage(string steamId, string bootstrapToken, string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var action = WebUtility.HtmlEncode(trimmed + "/join/pack");
        var reissue = WebUtility.HtmlEncode(trimmed + "/join/reissue");
        var inner =
            "<h1>You're verified &#10003;</h1>" +
            "<p>SteamID <code>" + WebUtility.HtmlEncode(steamId) + "</code> is enrolled. One step left:</p>" +
            "<form method=\"post\" action=\"" + action + "\">" +
            "<input type=\"hidden\" name=\"token\" value=\"" + WebUtility.HtmlEncode(bootstrapToken) + "\">" +
            "<button class=\"btn\" type=\"submit\">Download my mod pack</button></form>" +
            "<h2>Then</h2><ol>" +
            "<li>Extract the <code>Valheim</code> folder from the zip into your Valheim install folder " +
            "(Steam &rarr; right-click Valheim &rarr; Manage &rarr; Browse local files), letting it merge.</li>" +
            "<li>Launch Valheim and join the server.</li></ol>" +
            "<h2>Your data &amp; privacy</h2>" +
            "<p>Capture is <strong>off by default</strong> and opt-in. See " +
            "<a href=\"/data-and-trust\">Data &amp; trust</a> for exactly what's collected, where it " +
            "goes, and how to turn it off.</p>" +
            "<h2>If something breaks</h2>" +
            "<p>Tell us the <strong>server time</strong> it happened, the <strong>quest name</strong> " +
            "(if any), and attach your client log at <code>Valheim\\BepInEx\\LogOutput.log</code>. " +
            "This is best-effort alpha — we sweep feedback weekly.</p>" +
            "<p class=\"warn\">This download is personal — it contains your access key. Don't share it.</p>" +
            "<p class=\"small\">The download works once. If it didn't start, " +
            "<a href=\"" + reissue + "\">get a fresh link</a>.</p>";
        return Shell("Lumberjacks — finish setup", inner, withStatus: true);
    }

    static string Shell(string title, string inner, bool withStatus)
    {
        var banner = withStatus
            ? "<div id=\"status\" class=\"status wait\">Checking server status&hellip;</div>" +
              "<button class=\"recheck\" onclick=\"__recheck()\">Re-check</button>"
            : string.Empty;
        var script = withStatus ? StatusScript() : string.Empty;
        return
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<title>" + WebUtility.HtmlEncode(title) + "</title><style>" + Css + "</style></head>" +
            "<body><main class=\"card\">" + banner + inner + "</main>" + script + "</body></html>";
    }

    static string ReleaseHistoryTable()
    {
        var current = Environment.GetEnvironmentVariable("LUMBERJACKS_VERSION") ?? "unknown";
        var rows = new (string WhenUtc, string Release, string Mod, string Notes)[]
        {
            ("2026-07-23 15:32Z", "m17-motionstate-20260723-r1", "0.5.35", "Observe-first motion state and WebSocket/UDP readiness are exposed in the client heartbeat; package preserves the existing config and admitted mod identity."),
            ("2026-07-23 09:21Z", "m17-updatefilename-20260723-r1", "0.5.35", "Post-Steam update zip filename includes Gateway release, admitted mod release, and package hash; callback sends no-store."),
            ("2026-07-23 09:11Z", "m16-updatehistory-20260723-r1", "0.5.35", "This pre-signin page adds release history and no-store cache headers; admits m15 mod."),
            ("2026-07-23 08:47Z", "m15-hudrecover-20260723-r1", "0.5.35", "Recovery tab remains visible even when an older config disabled the strip."),
            ("2026-07-23 08:22Z", "m14-hudtoggle-20260723-r1", "0.5.34", "Transport strip starts collapsed; side NET SHOW/HIDE tab added."),
        };

        var html =
            "<section class=\"release-box\"><h2>Recent alpha releases</h2>" +
            "<p class=\"small\">This page is served by Gateway release <code>" + WebUtility.HtmlEncode(current) +
            "</code>. If the table does not change after a refresh, the browser or proxy is serving stale HTML.</p>" +
            "<table class=\"releases\"><thead><tr><th>UTC</th><th>Release</th><th>Mod</th><th>Why it changed</th></tr></thead><tbody>";
        foreach (var row in rows)
        {
            var active = string.Equals(row.Release, current, StringComparison.Ordinal) ? " class=\"current\"" : string.Empty;
            html += "<tr" + active + "><td>" + WebUtility.HtmlEncode(row.WhenUtc) + "</td><td><code>" +
                WebUtility.HtmlEncode(row.Release) + "</code></td><td>" + WebUtility.HtmlEncode(row.Mod) + "</td><td>" +
                WebUtility.HtmlEncode(row.Notes) + "</td></tr>";
        }
        return html + "</tbody></table></section>";
    }

    static string CompanionBootstrapBox()
    {
        var available = CompanionBootstrapCatalog.TryGetCurrent(out var release, out var error);
        var html = "<section class=\"release-box\"><h2>Companion local dashboard</h2>";
        if (!available)
        {
            return html + "<p class=\"small\">Companion bootstrap is not published yet: " +
                WebUtility.HtmlEncode(error ?? "unknown") + ".</p></section>";
        }

        return html +
            "<p>New machine? Download the public Companion bootstrap first. It contains no Steam " +
            "credential or Valheim access key; it starts the local <code>127.0.0.1:8080</code> dashboard, " +
            "then the dashboard pulls authenticated mod updates using your installed config.</p>" +
            "<p><a class=\"btn\" href=\"/api/v0/companion/bootstrap/package\">Download Companion bootstrap</a> " +
            "<a href=\"/api/v0/companion/bootstrap/manifest\">manifest</a></p>" +
            "<p class=\"small\">Current bootstrap <code>" + WebUtility.HtmlEncode(release.release) +
            "</code> · sha256 <code>" + WebUtility.HtmlEncode(release.package_sha256[..12]) +
            "…</code> · entrypoint <code>" + WebUtility.HtmlEncode(release.entrypoint) +
            "</code>.</p></section>";
    }

    static string CurrentModpackBox()
    {
        var available = ModpackReleaseCatalog.TryGetCurrent(out var release, out var error);
        var html = "<section class=\"release-box\"><h2>Current downloadable mod pack</h2>";
        if (!available)
        {
            return html + "<p class=\"small\">Modpack release is not published yet: " +
                WebUtility.HtmlEncode(error ?? "unknown") + ".</p></section>";
        }

        return html +
            "<p>This is the package the Steam sign-in button will download right now. It is read from " +
            "the runtime manifest, not from the historical table below.</p>" +
            "<p><strong>Gateway release:</strong> <code>" + WebUtility.HtmlEncode(release.release) +
            "</code><br><strong>Admitted mod:</strong> <code>" + WebUtility.HtmlEncode(release.mod_release) +
            "</code><br><strong>Package:</strong> " + WebUtility.HtmlEncode(release.package_size_bytes.ToString()) +
            " bytes - sha256 <code>" + WebUtility.HtmlEncode(release.package_sha256[..12]) + "...</code></p>" +
            "<p><a href=\"/api/v0/companion/update/manifest\">mod manifest</a></p></section>";
    }

    // Polls the public, un-rate-limited cutover snapshot and flips the banner. `stale===false` means a
    // heartbeat arrived within the last 15s — the server is reporting and joinable; anything else
    // (stale, or fetch failure = VM down / network) is treated as offline.
    static string StatusScript()
    {
        var connect = WebUtility.HtmlEncode(Connect()).Replace("'", string.Empty);
        return
            "<script>(function(){var connect='" + connect + "';function check(){var el=document.getElementById('status');" +
            "if(!el)return;el.className='status wait';el.textContent='Checking server status\\u2026';" +
            "fetch('/api/v0/telemetry/cutover',{cache:'no-store'}).then(function(r){return r.ok?r.json():Promise.reject();})" +
            ".then(function(d){if(d&&d.stale===false){el.className='status ok';el.textContent='\\u2705 Server is up \\u2014 join '+connect+' in Valheim.';}" +
            "else{el.className='status wait';el.textContent='\\u23f3 Server is offline right now. Your spot is saved \\u2014 check back and this turns green.';}})" +
            ".catch(function(){el.className='status wait';el.textContent='\\u23f3 Server is offline right now. Your spot is saved \\u2014 check back and this turns green.';});}" +
            "window.__recheck=check;check();})();</script>";
    }

    const string Css =
        ":root{color-scheme:light dark}*{box-sizing:border-box}" +
        "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
        "font:16px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:#0f1115;color:#e8eaed;padding:24px}" +
        ".card{max-width:560px;width:100%;background:#181b22;border:1px solid #2a2f3a;border-radius:14px;" +
        "padding:26px 30px;box-shadow:0 10px 40px rgba(0,0,0,.35)}" +
        "h1{margin:.1em 0 .4em;font-size:1.5rem}h2{font-size:1rem;margin:1.4em 0 .4em;color:#aab2c0}" +
        "p{margin:.6em 0}code{background:#0f1115;border:1px solid #2a2f3a;border-radius:6px;padding:1px 6px;font-size:.9em}" +
        "table{width:100%;border-collapse:collapse;margin:.7em 0 1em;font-size:.82em}th,td{border-top:1px solid #2a2f3a;padding:7px 6px;text-align:left;vertical-align:top}" +
        "th{color:#aab2c0;font-weight:700}.release-box{margin:18px 0;padding:12px;border:1px solid #2a2f3a;border-radius:10px;background:#12151b}" +
        ".release-box h2{margin:0 0 .4em}.releases tr.current{background:#123a26}.releases tr.current td:first-child:before{content:'current ';color:#7ee2a8;font-weight:700}" +
        "a{color:#7aa2ff}ol{padding-left:1.2em}" +
        ".btn{display:inline-block;font-weight:600;cursor:pointer;background:#3b82f6;color:#fff;border:0;text-decoration:none;" +
        "border-radius:10px;padding:12px 20px;margin:6px 0}.btn:hover{background:#2f6fe0}" +
        "button.btn{font:inherit}.small{color:#8b93a3;font-size:.9em}.warn{color:#ffcf6b;font-weight:600}" +
        ".status{border-radius:10px;padding:10px 14px;margin:0 0 6px;font-weight:600;font-size:.95em}" +
        ".status.ok{background:#123a26;border:1px solid #1f6b41;color:#7ee2a8}" +
        ".status.wait{background:#3a2f12;border:1px solid #6b551f;color:#ffcf6b}" +
        ".recheck{background:transparent;border:1px solid #2a2f3a;color:#aab2c0;border-radius:8px;" +
        "padding:5px 12px;font:inherit;font-size:.85em;cursor:pointer;margin:0 0 18px}";
}
