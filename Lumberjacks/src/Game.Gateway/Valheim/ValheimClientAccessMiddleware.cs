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

    public ValheimClientAccessMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context, SteamEnrollmentService enrollments)
    {
        if (!ValheimAccessPolicy.IsGated(context))
        {
            await _next(context);
            return;
        }

        var principal = Resolve(context, enrollments);
        context.Items[ValheimPrincipal.ItemKey] = principal;

        var required = ValheimAccessPolicy.RequiredFor(context);
        if (!principal.Has(required))
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

    ValheimPrincipal Resolve(HttpContext context, SteamEnrollmentService enrollments)
    {
        if (IsPrivateOrLoopback(context.Connection.RemoteIpAddress))
        {
            return new ValheimPrincipal("private-plane",
                ValheimCapability.Admin | ValheimCapability.Producer |
                ValheimCapability.Consumer | ValheimCapability.Telemetry);
        }

        var supplied = context.Request.Headers["X-Lumberjacks-Client-Key"].ToString();
        var enrollmentId = context.Request.Headers["X-Lumberjacks-Enrollment-Id"].ToString();

        if (!string.IsNullOrWhiteSpace(enrollmentId) &&
            enrollments.Verify(enrollmentId, supplied, out var view, out _))
        {
            return new ValheimPrincipal("enrollment",
                ValheimCapability.Consumer | ValheimCapability.Telemetry, view);
        }

        var sharedKey = _configuration["LUMBERJACKS_CLIENT_ACCESS_KEY"];
        if (!string.IsNullOrWhiteSpace(sharedKey) && CryptographicEquals(supplied, sharedKey))
        {
            return new ValheimPrincipal("shared-client-key",
                ValheimCapability.Consumer | ValheimCapability.Telemetry);
        }

        return ValheimPrincipal.Anonymous;
    }

    static bool IsPrivateOrLoopback(System.Net.IPAddress? address)
    {
        if (address is null || System.Net.IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10 ||
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
