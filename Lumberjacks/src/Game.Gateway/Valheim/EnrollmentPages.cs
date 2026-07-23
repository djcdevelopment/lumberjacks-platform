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
