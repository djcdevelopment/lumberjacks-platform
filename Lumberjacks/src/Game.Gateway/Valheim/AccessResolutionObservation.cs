using System.Net;

namespace Game.Gateway.Valheim;

public sealed record AccessResolutionObservation(
    string PrincipalKind,
    string? PrincipalReference,
    string ResolutionBasis,
    string SocketPeerClass,
    string? ForwardedPeerClaimClass,
    ValheimCapability GrantedCapabilities);

internal static class PeerClass
{
    public static string Classify(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address)) return "loopback_or_missing";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4 && (bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168))) return "private";
        return "public";
    }

    public static string? ForwardedClass(HttpRequest request)
    {
        var value = request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return IPAddress.TryParse(first, out var address) ? Classify(address) : "invalid";
    }
}
