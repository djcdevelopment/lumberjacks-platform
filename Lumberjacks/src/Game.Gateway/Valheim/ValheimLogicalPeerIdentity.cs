using System.Security.Cryptography;
using System.Text;

namespace Game.Gateway.Valheim;

/// <summary>
/// Derives the durable Valheim peer identity from authenticated, process-stable inputs.
/// WebSocket connection ids and resume tokens are transport incarnations; they must never
/// become ownership principals.
/// </summary>
public static class ValheimLogicalPeerIdentity
{
    public static string? Resolve(
        ValheimPrincipal? principal,
        string role,
        string? privatePlaneClaim,
        string? character,
        string serverInstanceId,
        string worldId,
        out string? error)
    {
        error = null;
        if (!SafeToken(serverInstanceId, 96) || !SafeToken(worldId, 96))
        {
            error = "logical_peer_world_scope_invalid";
            return null;
        }

        string material;
        if (string.Equals(role, "server", StringComparison.Ordinal))
        {
            if (principal?.Has(ValheimCapability.Producer) != true)
            {
                error = "logical_server_requires_producer";
                return null;
            }
            material = $"server|{serverInstanceId}|{worldId}";
        }
        else
        {
            var normalizedCharacter = (character ?? string.Empty).Trim().Normalize();
            if (normalizedCharacter.Length is < 1 or > 64 ||
                normalizedCharacter.Any(char.IsControl))
            {
                error = "logical_peer_character_invalid";
                return null;
            }

            if (principal?.Enrollment is { } enrollment)
            {
                material =
                    $"enrollment|{enrollment.RecipientId}|{serverInstanceId}|{worldId}|{normalizedCharacter.ToUpperInvariant()}";
            }
            else if (principal?.Kind is "private-plane" or "shared-client-key")
            {
                if (!SafeToken(privatePlaneClaim, 48))
                {
                    error = "logical_peer_private_claim_required";
                    return null;
                }
                material =
                    $"{principal.Kind}|{privatePlaneClaim!.Trim()}|{serverInstanceId}|{worldId}|{normalizedCharacter.ToUpperInvariant()}";
            }
            else
            {
                error = "logical_peer_enrollment_required";
                return null;
            }
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "lp_" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    static bool SafeToken(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
