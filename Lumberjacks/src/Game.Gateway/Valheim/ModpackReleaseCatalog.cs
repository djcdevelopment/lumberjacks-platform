using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game.Gateway.Valheim;

/// <summary>
/// Runtime release pointer for the alpha modpack. The Gateway reads it for every download and
/// manifest request, so publishing a new package is an artifact operation rather than an image
/// deployment. A malformed pointer fails closed; the legacy template remains a temporary fallback
/// for an installation that has not published its first pointer yet.
/// </summary>
public static class ModpackReleaseCatalog
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryGetCurrent(out RuntimeModpackRelease release, out string? error)
    {
        var pointerPath = Environment.GetEnvironmentVariable("LUMBERJACKS_MODPACK_MANIFEST")
            ?? "/var/lib/lumberjacks/modpack/current.json";
        if (File.Exists(pointerPath))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<RuntimeModpackRelease>(File.ReadAllText(pointerPath), Json);
                if (parsed is null || parsed.schema_version != 1 || string.IsNullOrWhiteSpace(parsed.release) ||
                    string.IsNullOrWhiteSpace(parsed.mod_release) || string.IsNullOrWhiteSpace(parsed.package_file) ||
                    string.IsNullOrWhiteSpace(parsed.package_sha256))
                {
                    release = default!;
                    error = "modpack runtime manifest is invalid";
                    return false;
                }

                var root = Path.GetDirectoryName(Path.GetFullPath(pointerPath))!;
                var packagePath = Path.GetFullPath(Path.Combine(root, parsed.package_file));
                if (!packagePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(packagePath))
                {
                    release = default!;
                    error = "modpack package referenced by runtime manifest is missing";
                    return false;
                }

                var actualHash = Sha256File(packagePath);
                if (!string.Equals(actualHash, parsed.package_sha256, StringComparison.OrdinalIgnoreCase))
                {
                    release = default!;
                    error = "modpack package hash does not match runtime manifest";
                    return false;
                }

                release = parsed with { package_file = packagePath, package_sha256 = actualHash, package_size_bytes = new FileInfo(packagePath).Length };
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                release = default!;
                error = "modpack runtime manifest could not be read";
                return false;
            }
        }

        var templatePath = Environment.GetEnvironmentVariable("LUMBERJACKS_MODPACK_TEMPLATE");
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            release = default!;
            error = "mod pack template not configured (LUMBERJACKS_MODPACK_TEMPLATE)";
            return false;
        }

        release = new RuntimeModpackRelease(
            1,
            Environment.GetEnvironmentVariable("LUMBERJACKS_VERSION") ?? "unknown",
            ValheimReleaseIdentity.ExpectedModRelease ?? "unknown",
            "comfy_p7_alpha_modpack",
            templatePath,
            Sha256File(templatePath),
            new FileInfo(templatePath).Length,
            DateTime.UtcNow,
            true,
            "legacy_template_fallback");
        error = null;
        return true;
    }

    public static object ToPublicManifest(RuntimeModpackRelease release, string publicBaseUrl) => new
    {
        schema_version = release.schema_version,
        release = release.release,
        mod_release = release.mod_release,
        created_utc = release.created_utc,
        requires_valheim_restart = release.requires_valheim_restart,
        package = new
        {
            kind = release.package_kind,
            sha256 = release.package_sha256,
            size_bytes = release.package_size_bytes,
        },
        downloads = new
        {
            package = publicBaseUrl + "/api/v0/valheim/modpack/package",
            first_install = publicBaseUrl + "/join",
            latest_update = publicBaseUrl + "/join/update",
        },
        install_policy = new
        {
            ordinary_update_rotates_credential = false,
            update_preserves_config = true,
            recovery_rotates_credential = true,
        },
    };

    static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed record RuntimeModpackRelease(
    int schema_version,
    string release,
    string mod_release,
    string package_kind,
    string package_file,
    string package_sha256,
    long package_size_bytes,
    DateTime created_utc,
    bool requires_valheim_restart,
    string? notes);
