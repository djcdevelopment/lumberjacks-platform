using System.Security.Cryptography;
using System.Text.Json;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Serves the packaged tool zips advertised by the Community Workbench catalog, from a mounted
/// artifact directory rather than from the repository tree. The discipline is the one
/// <see cref="Game.Gateway.Valheim.ModpackReleaseCatalog"/> already uses for the alpha modpack:
/// a pointer file names the artifacts, the resolved path must stay inside the mounted directory,
/// and the file's SHA-256 is recomputed and compared before a single byte is streamed. A hash
/// that does not match the pointer is a 503, never a download.
///
/// It fails closed by design. Publishing downloads is a deliberate mount, so an instance with no
/// downloads directory answers 404 in plain text rather than falling back to the content root —
/// the built-in Community folder holds a generated HTML page, and must never become a file
/// server for whatever else happens to sit beside it in the image.
///
/// The pointer file is <c>tools.json</c> in the downloads directory:
/// <code>
/// {
///   "schema_version": 1,
///   "downloads": [
///     {
///       "id": "quest-picker",
///       "file": "quest-picker-20260729.zip",
///       "sha256": "9f2c...64 lowercase hex chars...b1",
///       "size_bytes": 184320
///     }
///   ]
/// }
/// </code>
/// <c>id</c> matches the tool id in docs/workbench/workbench.json, and that catalog's
/// <c>access.sha256</c> must equal the digest here — the page tells a visitor which bytes to
/// expect, and this endpoint refuses to serve any others.
/// </summary>
public static class WorkbenchDownloadEndpoints
{
    private const string PointerFile = "tools.json";
    private const string DirectoryVariable = "LUMBERJACKS_WORKBENCH_DOWNLOADS_DIR";
    private const string ShaHeader = "X-Download-Sha256";

    private const string NotPublished = "downloads not published on this instance";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app)
    {
        var logger = app.Logger;

        app.MapGet("/workbench/downloads/{id}", (HttpContext context, string id) =>
        {
            // Reject anything that is not a catalog-shaped id before it reaches the filesystem.
            // The containment check below is the real guard; this one keeps traversal attempts
            // out of the logs as ordinary 404s.
            if (!IsCatalogId(id))
                return Results.NotFound();

            var root = ResolveDownloadsDirectory();
            if (root is null)
                // Every argument is named because the 4-parameter Results.Text overload declares
                // no defaults; the 3-parameter one has no statusCode to set.
                return Results.Text(
                    NotPublished,
                    "text/plain; charset=utf-8",
                    contentEncoding: null,
                    statusCode: StatusCodes.Status404NotFound);

            if (!TryReadPointer(root, logger, out var entries, out var pointerError))
                return Results.Problem(pointerError, statusCode: StatusCodes.Status503ServiceUnavailable);

            var entry = entries.FirstOrDefault(item => string.Equals(item.id, id, StringComparison.Ordinal));
            if (entry is null || string.IsNullOrWhiteSpace(entry.file) || string.IsNullOrWhiteSpace(entry.sha256))
                return Results.NotFound();

            // Containment: a pointer entry is operator-authored, but an entry of "../../secrets"
            // must still resolve to nothing servable. Compare the fully-resolved path against the
            // fully-resolved root with a trailing separator, so "/downloads-evil" cannot pass as a
            // prefix match of "/downloads".
            var packagePath = Path.GetFullPath(Path.Combine(root, entry.file));
            if (!packagePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Workbench download {Id} points outside the downloads directory and was refused.", id);
                return Results.NotFound();
            }

            if (!File.Exists(packagePath))
                return Results.NotFound();

            string actualHash;
            long actualLength;
            try
            {
                actualHash = Sha256File(packagePath);
                actualLength = new FileInfo(packagePath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not read workbench download {Id} at {Path}.", id, packagePath);
                return Results.Problem(
                    $"workbench download '{id}' could not be read",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // The published page advertises a digest. Serving bytes that do not match it would
            // quietly turn a verifiable download into an unverifiable one, so this is a refusal
            // with an honest reason rather than a best-effort stream.
            if (!string.Equals(actualHash, entry.sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(
                    "Workbench download {Id} hash mismatch: pointer says {Expected}, file is {Actual}.",
                    id, entry.sha256, actualHash);
                return Results.Problem(
                    $"workbench download '{id}' does not match the SHA-256 recorded in {PointerFile}; it will not be served",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (entry.size_bytes > 0 && entry.size_bytes != actualLength)
            {
                logger.LogError(
                    "Workbench download {Id} size mismatch: pointer says {Expected}, file is {Actual}.",
                    id, entry.size_bytes, actualLength);
                return Results.Problem(
                    $"workbench download '{id}' does not match the size recorded in {PointerFile}; it will not be served",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            context.Response.Headers[ShaHeader] = actualHash;
            var fileName = $"{id}-{actualHash[..12]}.zip";
            return Results.File(File.OpenRead(packagePath), "application/zip", fileName, enableRangeProcessing: true);
        });
    }

    /// <summary>
    /// The mounted downloads directory, or null when this instance publishes none.
    ///
    /// Order: the explicit variable, then the directory holding a relocated workbench page — a
    /// deployment that mounts a freshly rendered catalog naturally puts the zips it advertises
    /// beside it. When neither is set there is deliberately no third fallback: the content root
    /// is where the image's own files live, and it is never a download source.
    /// </summary>
    private static string? ResolveDownloadsDirectory()
    {
        if (Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } configured &&
            Directory.Exists(configured))
        {
            return Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar);
        }

        if (Environment.GetEnvironmentVariable("LUMBERJACKS_WORKBENCH_HTML") is { Length: > 0 } mounted &&
            File.Exists(mounted) &&
            Path.GetDirectoryName(Path.GetFullPath(mounted)) is { Length: > 0 } mountedDirectory)
        {
            return mountedDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }

        return null;
    }

    private static bool TryReadPointer(
        string root,
        ILogger logger,
        out IReadOnlyList<DownloadEntry> entries,
        out string? error)
    {
        entries = Array.Empty<DownloadEntry>();
        var pointerPath = Path.Combine(root, PointerFile);
        if (!File.Exists(pointerPath))
        {
            error = $"workbench download pointer {PointerFile} is missing";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<DownloadPointer>(File.ReadAllText(pointerPath), Json);
            if (parsed is null || parsed.schema_version != 1 || parsed.downloads is null)
            {
                error = $"workbench download pointer {PointerFile} is invalid";
                return false;
            }

            entries = parsed.downloads;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the workbench download pointer at {Path}.", pointerPath);
            error = $"workbench download pointer {PointerFile} could not be read";
            return false;
        }
    }

    /// <summary>Catalog ids are lowercase slugs; anything else cannot name a published artifact.</summary>
    private static bool IsCatalogId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        foreach (var character in id)
        {
            var allowed = character is >= 'a' and <= 'z' || character is >= '0' and <= '9' || character == '-';
            if (!allowed) return false;
        }
        return id[0] != '-';
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record DownloadPointer(int schema_version, List<DownloadEntry>? downloads);

    private sealed record DownloadEntry(string id, string file, string sha256, long size_bytes);
}
