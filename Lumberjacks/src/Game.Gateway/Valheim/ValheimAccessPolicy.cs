namespace Game.Gateway.Valheim;

/// <summary>Least-privilege capabilities for the Valheim control plane (M1).</summary>
[Flags]
public enum ValheimCapability
{
    None = 0,
    Consumer = 1,
    Telemetry = 2,
    Producer = 4,
    Admin = 8,
}

/// <summary>
/// The resolved caller identity for a gated request. Admin implies every other
/// capability; nothing implies Admin.
/// </summary>
public sealed record ValheimPrincipal(
    string Kind,
    ValheimCapability Capabilities,
    SteamEnrollmentService.EnrollmentView? Enrollment = null)
{
    public const string ItemKey = "lumberjacks.principal";

    public bool Has(ValheimCapability required) =>
        (Capabilities & ValheimCapability.Admin) != 0 || (Capabilities & required) == required;

    public static ValheimPrincipal Anonymous { get; } = new("anonymous", ValheimCapability.None);

    public static ValheimPrincipal? From(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as ValheimPrincipal : null;
}

/// <summary>
/// Route → required-capability table for the gated Valheim surfaces. The grants
/// mirror exactly what the frozen 0.5.31 mod calls: the client (public network)
/// polls/acks/heartbeats as consumer+telemetry; the server mod (private Docker
/// network) produces receipts and handshake verdict requests and performs window
/// resets, all of which ride the private-plane implicit grant. Anything unlisted
/// under a gated prefix is admin-only — fail closed.
/// </summary>
public static class ValheimAccessPolicy
{
    static readonly (string Method, string Prefix, ValheimCapability Required)[] Rules =
    {
        ("POST", "/valheim/zdo-redirect/receipts", ValheimCapability.Producer),
        ("GET", "/valheim/zdo-redirect/pending/", ValheimCapability.Consumer),
        ("POST", "/valheim/zdo-redirect/ack/", ValheimCapability.Consumer),
        ("POST", "/valheim/zdo-redirect/consumer", ValheimCapability.Telemetry),
        ("GET", "/valheim/zdo-injection/next/", ValheimCapability.Consumer),
        ("POST", "/valheim/zdo-injection/ack", ValheimCapability.Consumer),
        ("POST", "/valheim/handshake/config", ValheimCapability.Producer),
        ("POST", "/valheim/handshake/begin", ValheimCapability.Producer),
        ("POST", "/valheim/handshake/peerinfo", ValheimCapability.Producer),
        ("POST", "/valheim/telemetry/heartbeat", ValheimCapability.Telemetry),
        ("GET", "/api/v0/valheim/enrollment/me", ValheimCapability.Consumer),
        // Per-enrollment telemetry snapshot; self-scoped inside the endpoint.
        ("GET", "/api/v0/valheim/enrollment/", ValheimCapability.Consumer),
        ("POST", "/api/v0/enrollment/invites", ValheimCapability.Admin),
        ("GET", "/api/v0/enrollment", ValheimCapability.Admin),
        ("POST", "/api/v0/enrollment/", ValheimCapability.Admin),
    };

    /// <summary>Paths (plus WebSocket upgrades) that require a resolved principal.</summary>
    public static bool IsGated(HttpContext context) =>
        context.WebSockets.IsWebSocketRequest ||
        context.Request.Path.StartsWithSegments("/valheim") ||
        context.Request.Path.StartsWithSegments("/api/v0/valheim/enrollment") ||
        context.Request.Path.StartsWithSegments("/api/v0/enrollment");

    public static ValheimCapability RequiredFor(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest) return ValheimCapability.Consumer;
        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var rule in Rules)
        {
            if (!string.Equals(context.Request.Method, rule.Method, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Prefix.EndsWith('/')
                ? path.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase)
                : string.Equals(path, rule.Prefix, StringComparison.OrdinalIgnoreCase))
                return rule.Required;
        }
        return ValheimCapability.Admin;
    }
}
