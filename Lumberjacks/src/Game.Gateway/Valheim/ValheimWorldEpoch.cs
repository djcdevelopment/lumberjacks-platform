using System.Globalization;

namespace Game.Gateway.Valheim;

/// <summary>
/// Contract identity for stores that contain server-session-scoped Valheim ZDO ids.
/// </summary>
public static class ValheimWorldEpoch
{
    private const string WorldPrefix = "world-";
    private const string SessionPrefix = "session-";

    public static string StableWorld(long worldUid) =>
        WorldPrefix + unchecked((ulong)worldUid).ToString("x16", CultureInfo.InvariantCulture);

    public static string ServerSession(long sessionId) =>
        SessionPrefix + unchecked((ulong)sessionId).ToString("x16", CultureInfo.InvariantCulture);

    public static string Compose(long worldUid, long sessionId) =>
        StableWorld(worldUid) + "-" + ServerSession(sessionId);

    public static bool IsConsistent(
        string worldEpoch,
        string stableWorldEpoch,
        string serverSessionEpoch,
        long worldUid) =>
        string.Equals(stableWorldEpoch, StableWorld(worldUid), StringComparison.Ordinal) &&
        IsHexComponent(serverSessionEpoch, SessionPrefix) &&
        string.Equals(
            worldEpoch,
            stableWorldEpoch + "-" + serverSessionEpoch,
            StringComparison.Ordinal);

    private static bool IsHexComponent(string value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + 16 ||
            !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (var index = prefix.Length; index < value.Length; index++)
        {
            var c = value[index];
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        }
        return true;
    }
}
