using System.Security.Cryptography;
using System.Text.Json;

namespace Game.Gateway.Valheim;

/// <summary>
/// Runtime release pointer for the generic Companion bootstrap. The package is deliberately
/// credential-free, so the Gateway can serve it publicly while still hash-verifying the mounted
/// artifact on every request.
/// </summary>
public static class CompanionBootstrapCatalog
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryGetCurrent(out RuntimeCompanionBootstrapRelease release, out string? error)
    {
        var pointerPath = Environment.GetEnvironmentVariable("LUMBERJACKS_COMPANION_BOOTSTRAP_MANIFEST")
            ?? "/var/lib/lumberjacks/modpack/companion-bootstrap/current.json";
        if (!File.Exists(pointerPath))
        {
            release = default!;
            error = "companion bootstrap runtime manifest is not published";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RuntimeCompanionBootstrapRelease>(File.ReadAllText(pointerPath), Json);
            if (parsed is null || parsed.schema_version != 1 || string.IsNullOrWhiteSpace(parsed.release) ||
                string.IsNullOrWhiteSpace(parsed.package_kind) || string.IsNullOrWhiteSpace(parsed.package_file) ||
                string.IsNullOrWhiteSpace(parsed.package_sha256) || string.IsNullOrWhiteSpace(parsed.entrypoint))
            {
                release = default!;
                error = "companion bootstrap runtime manifest is invalid";
                return false;
            }

            var root = Path.GetDirectoryName(Path.GetFullPath(pointerPath))!;
            var packagePath = Path.GetFullPath(Path.Combine(root, parsed.package_file));
            if (!packagePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(packagePath))
            {
                release = default!;
                error = "companion bootstrap package referenced by runtime manifest is missing";
                return false;
            }

            var actualHash = Sha256File(packagePath);
            if (!string.Equals(actualHash, parsed.package_sha256, StringComparison.OrdinalIgnoreCase))
            {
                release = default!;
                error = "companion bootstrap package hash does not match runtime manifest";
                return false;
            }

            release = parsed with
            {
                package_file = packagePath,
                package_sha256 = actualHash,
                package_size_bytes = new FileInfo(packagePath).Length,
            };
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            release = default!;
            error = "companion bootstrap runtime manifest could not be read";
            return false;
        }
    }

    public static object ToPublicManifest(RuntimeCompanionBootstrapRelease release, string publicBaseUrl) => new
    {
        schema_version = release.schema_version,
        release = release.release,
        created_utc = release.created_utc,
        package = new
        {
            kind = release.package_kind,
            sha256 = release.package_sha256,
            size_bytes = release.package_size_bytes,
            entrypoint = release.entrypoint,
        },
        downloads = new
        {
            package = publicBaseUrl + "/api/v0/companion/bootstrap/package",
            manifest = publicBaseUrl + "/api/v0/companion/bootstrap/manifest",
            latest_update = publicBaseUrl + "/join/update",
        },
        install_policy = new
        {
            contains_credential = false,
            requires_github_auth = false,
            requires_steam_signin = false,
            valheim_mod_updates_happen_inside_companion = true,
        },
        notes = release.notes,
    };

    static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed record RuntimeCompanionBootstrapRelease(
    int schema_version,
    string release,
    string package_kind,
    string package_file,
    string package_sha256,
    long package_size_bytes,
    DateTime created_utc,
    string entrypoint,
    string? notes);
