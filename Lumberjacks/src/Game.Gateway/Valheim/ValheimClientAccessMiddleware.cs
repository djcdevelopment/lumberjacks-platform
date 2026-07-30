using System.Diagnostics;
using Game.Gateway.BoundaryEvents;
using Microsoft.Extensions.Options;

namespace Game.Gateway.Valheim;

/// <summary>
/// Capability gate for the Valheim control plane (M1 least-privilege split).
///
/// Callers on the loopback/private plane (the operator's IAP tunnel and the
/// server containers on the Docker network) receive the full capability set;
/// splitting that plane further arrives with public TLS in a later M1 stage.
/// Public callers get only what their credential grants:
///   - a valid enrollment credential ⇒ consumer + telemetry;
///   - the legacy shared client key  ⇒ consumer + telemetry (retire with the
///     stage-3 mod cut — before M1 it granted every gated surface);
///   - anything else                 ⇒ nothing.
/// The admin capability is never granted to a public caller here: until TLS
/// lands there must be no reusable admin credential on a plaintext public link.
/// Unmatched gated routes require admin, so new surfaces are private-only by
/// default — fail closed.
/// </summary>
public sealed class ValheimClientAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly IBoundaryEventSink _boundaryEvents;
    private readonly BoundaryEventSource _source;

    public ValheimClientAccessMiddleware(RequestDelegate next, IConfiguration configuration,
        IBoundaryEventSink? boundaryEvents = null, IOptions<BoundaryEventOptions>? options = null)
    {
        _next = next;
        _configuration = configuration;
        _boundaryEvents = boundaryEvents ?? NullBoundaryEventSink.Instance;
        var value = options?.Value;
        _source = new(value?.Service ?? "lumberjacks-gateway", value?.Instance ?? "unknown",
            value?.Release ?? "unknown", null);
    }

    public async Task InvokeAsync(HttpContext context, SteamEnrollmentService enrollments)
    {
        if (!ValheimAccessPolicy.IsGated(context))
        {
            await _next(context);
            return;
        }

        var principal = Resolve(context, enrollments, out var observation);
        context.Items[ValheimPrincipal.ItemKey] = principal;
        _boundaryEvents.TryWrite(BoundaryEventEnvelope.Create("identity.resolved", _source, new
        {
            principal_kind = observation.PrincipalKind,
            principal_reference = observation.PrincipalReference,
            resolution_basis = observation.ResolutionBasis,
            socket_peer_class = observation.SocketPeerClass,
            forwarded_peer_claim_class = observation.ForwardedPeerClaimClass,
            granted_capabilities = observation.GrantedCapabilities.ToString(),
        }, Activity.Current));

        var required = ValheimAccessPolicy.RequiredFor(context);
        var allowed = principal.Has(required);
        _boundaryEvents.TryWrite(BoundaryEventEnvelope.Create("authorization.decided", _source, new
        {
            policy = "valheim." + required.ToString().ToLowerInvariant() + ".required",
            required_capabilities = required.ToString(),
            granted_capabilities = principal.Capabilities.ToString(),
            result = allowed ? "allow" : "deny",
            reason = observation.ResolutionBasis,
        }, Activity.Current));
        if (!allowed)
        {
            context.Response.StatusCode = principal.Capabilities == ValheimCapability.None
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = principal.Capabilities == ValheimCapability.None
                    ? "credentials_required"
                    : "capability_denied",
                required = required.ToString().ToLowerInvariant(),
            });
            return;
        }

        await _next(context);
    }

    ValheimPrincipal Resolve(HttpContext context, SteamEnrollmentService enrollments,
        out AccessResolutionObservation observation)
    {
        // Preserve an explicit, valid enrollment through a trusted reverse proxy. Caddy's socket
        // peer is private, but resolving that fact first erases the enrolled recipient and makes
        // self-scoped WebSocket features (including Valheim motion) impossible over TLS.
        var supplied = context.Request.Headers["X-Lumberjacks-Client-Key"].ToString();
        var enrollmentId = context.Request.Headers["X-Lumberjacks-Enrollment-Id"].ToString();

        if (!string.IsNullOrWhiteSpace(enrollmentId) &&
            enrollments.Verify(enrollmentId, supplied, out var view, out _))
        {
            var principal = new ValheimPrincipal("enrollment",
                ValheimCapability.Consumer | ValheimCapability.Telemetry, view);
            observation = Observe(context, principal, view.EnrollmentId, "enrollment");
            return principal;
        }

        if (IsPrivateOrLoopback(context.Connection.RemoteIpAddress))
        {
            var principal = new ValheimPrincipal("private-plane",
                ValheimCapability.Admin | ValheimCapability.Producer |
                ValheimCapability.Consumer | ValheimCapability.Telemetry);
            observation = Observe(context, principal, null, "private_socket");
            return principal;
        }

        var sharedKey = _configuration["LUMBERJACKS_CLIENT_ACCESS_KEY"];
        if (!string.IsNullOrWhiteSpace(sharedKey) && CryptographicEquals(supplied, sharedKey))
        {
            var principal = new ValheimPrincipal("shared-client-key",
                ValheimCapability.Consumer | ValheimCapability.Telemetry);
            observation = Observe(context, principal, null, "shared_client_key");
            return principal;
        }

        observation = Observe(context, ValheimPrincipal.Anonymous, null, "anonymous");
        return ValheimPrincipal.Anonymous;
    }

    private static AccessResolutionObservation Observe(HttpContext context, ValheimPrincipal principal,
        string? reference, string basis) => new(principal.Kind, reference, basis,
        PeerClass.Classify(context.Connection.RemoteIpAddress), PeerClass.ForwardedClass(context.Request),
        principal.Capabilities);

    static bool IsPrivateOrLoopback(System.Net.IPAddress? address)
    {
        if (address is null || System.Net.IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10 ||
            // Tailscale assigns authenticated peers from RFC 6598. These addresses are not
            // Internet-routable; treating them as the private plane lets the AM4 development
            // clients exercise a local Gateway without copying production enrollment stores.
            (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    static bool CryptographicEquals(string actual, string expected)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(actual);
        var right = System.Text.Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }
}
