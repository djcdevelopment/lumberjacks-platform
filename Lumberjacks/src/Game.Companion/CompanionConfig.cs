namespace Lumberjacks.Companion;

/// <summary>Reads the installed client credential locally; it never serializes or returns the key.</summary>
sealed record ModpackCredentials(string enrollment_id, string client_access_key);

static class CompanionConfig
{
    public static bool TryReadCredentials(string? configPath, out ModpackCredentials? credentials)
    {
        credentials = null;
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return false;
        var values = File.ReadLines(configPath)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("lumberjacksEnrollmentId", out var enrollmentId) || string.IsNullOrWhiteSpace(enrollmentId) ||
            !values.TryGetValue("lumberjacksClientAccessKey", out var key) || string.IsNullOrWhiteSpace(key)) return false;
        credentials = new ModpackCredentials(enrollmentId, key);
        return true;
    }
}
