namespace Lumberjacks.Companion;

/// <summary>Reads the installed client credential locally; it never serializes or returns the key.</summary>
sealed record ModpackCredentials(string enrollment_id, string client_access_key);

static class CompanionConfig
{
    public static bool TryReadCredentials(string? configPath, out ModpackCredentials? credentials)
    {
        credentials = null;
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return false;
        // Valheim/BepInEx config files can contain repeated sections after a plugin
        // regenerates them. Read only the two credentials this surface owns, and let
        // the last occurrence win, instead of treating unrelated repeated gameplay
        // settings as a malformed credential file.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(configPath))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;
            var keyName = parts[0].Trim();
            if (!keyName.Equals("lumberjacksEnrollmentId", StringComparison.OrdinalIgnoreCase) &&
                !keyName.Equals("lumberjacksClientAccessKey", StringComparison.OrdinalIgnoreCase)) continue;
            values[keyName] = parts[1].Trim();
        }
        if (!values.TryGetValue("lumberjacksEnrollmentId", out var enrollmentId) || string.IsNullOrWhiteSpace(enrollmentId) ||
            !values.TryGetValue("lumberjacksClientAccessKey", out var key) || string.IsNullOrWhiteSpace(key)) return false;
        credentials = new ModpackCredentials(enrollmentId, key);
        return true;
    }
}
