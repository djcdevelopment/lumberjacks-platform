using System.Security.Cryptography;
using System.Text;
using Game.Gateway.Valheim;

namespace Game.Gateway.WebSocket;

public sealed record NativeClientAdmissionDecision(
    bool Allowed,
    int StatusCode,
    string? Error,
    string? PlayerId);

/// <summary>Pure admission policy for the invite-only first-party client.</summary>
public static class NativeClientAdmission
{
    public const string ReleaseHeader = "X-Lumberjacks-Native-Release";

    public static NativeClientAdmissionDecision Evaluate(
        ValheimPrincipal? principal,
        string? suppliedRelease,
        string requiredRelease,
        int activeNativeSessions,
        int maxNativeSessions,
        string worldId)
    {
        if (principal is null ||
            (principal.Enrollment is null &&
             !string.Equals(principal.Kind, "private-plane", StringComparison.Ordinal)))
        {
            return Deny(StatusCodes.Status403Forbidden, "native_enrollment_required");
        }

        if (string.IsNullOrWhiteSpace(suppliedRelease) ||
            !CryptographicEquals(suppliedRelease, requiredRelease))
        {
            return Deny(StatusCodes.Status409Conflict, "native_release_mismatch");
        }

        if (activeNativeSessions >= maxNativeSessions)
        {
            return Deny(StatusCodes.Status503ServiceUnavailable, "native_capacity_reached");
        }

        var playerId = principal.Enrollment is { } enrollment
            ? PlayerIdFor(worldId, enrollment.EnrollmentId)
            : "p-dev-" + Guid.NewGuid().ToString("N")[..20];
        return new NativeClientAdmissionDecision(true, StatusCodes.Status101SwitchingProtocols, null, playerId);
    }

    public static string PlayerIdFor(string worldId, string enrollmentId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{worldId}:{enrollmentId}"));
        return "p-" + Convert.ToHexString(digest).ToLowerInvariant()[..24];
    }

    private static NativeClientAdmissionDecision Deny(int statusCode, string error) =>
        new(false, statusCode, error, null);

    private static bool CryptographicEquals(string actual, string expected)
    {
        var left = Encoding.UTF8.GetBytes(actual);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length &&
            CryptographicOperations.FixedTimeEquals(left, right);
    }
}
