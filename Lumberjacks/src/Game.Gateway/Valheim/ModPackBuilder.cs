using System.IO.Compression;
using System.Text;

namespace Game.Gateway.Valheim;

/// <summary>
/// Builds a volunteer's personalized ComfyNetworkSense mod pack: takes the shared base package
/// template (BepInEx + the mod DLLs + a base config) and rewrites the single ComfyNetworkSense
/// <c>.cfg</c> entry to carry that volunteer's <c>[Lumberjacks]</c> credential block, so the download
/// is drop-in — extract into Valheim and play, no hand-editing. Everything else in the template is
/// copied through byte-for-byte.
///
/// The credential lands inside the download by design (ADR-tracked tradeoff for volunteer smoothness);
/// it is minted at bootstrap consumption and never parked, and this builder only ever writes it into
/// the streamed bytes — it persists nothing.
/// </summary>
public static class ModPackBuilder
{
    /// <summary>The mod's BepInEx config entry, matched by suffix so either path separator works.</summary>
    public const string ConfigEntrySuffix = "djcdevelopment.valheim.comfynetworksense.cfg";
    public const string UpdateInstallerEntry = "Install-LumberjacksMod.ps1";

    static bool IsConfigEntry(string fullName) =>
        fullName.Replace('\\', '/').EndsWith(ConfigEntrySuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Streams a copy of <paramref name="templateZip"/> with the ComfyNetworkSense config entry
    /// replaced by a personalized one. Throws if the template has no such entry (a misconfigured
    /// template must fail loudly, not ship a credential-less pack).
    /// </summary>
    public static byte[] BuildPersonalizedPack(
        byte[] templateZip, string gatewayUrl, string windowId, string enrollmentId, string accessKey)
    {
        using var output = new MemoryStream();
        using (var outArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var inStream = new MemoryStream(templateZip, writable: false))
        using (var inArchive = new ZipArchive(inStream, ZipArchiveMode.Read))
        {
            var personalized = false;
            foreach (var entry in inArchive.Entries)
            {
                if (IsConfigEntry(entry.FullName))
                {
                    string baseCfg;
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        baseCfg = reader.ReadToEnd();
                    var cfg = PersonalizeConfig(baseCfg, gatewayUrl, windowId, enrollmentId, accessKey);
                    var outEntry = outArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    using var outStream = outEntry.Open();
                    var bytes = new UTF8Encoding(false).GetBytes(cfg);
                    outStream.Write(bytes, 0, bytes.Length);
                    personalized = true;
                }
                else
                {
                    var outEntry = outArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    using var outStream = outEntry.Open();
                    using var src = entry.Open();
                    src.CopyTo(outStream);
                }
            }

            if (!personalized)
                throw new InvalidOperationException(
                    "mod pack template is missing the ComfyNetworkSense config entry (" + ConfigEntrySuffix + ")");
        }

        return output.ToArray();
    }

    /// <summary>
    /// Builds the ordinary update pack for already-installed clients. It intentionally omits the
    /// ComfyNetworkSense config entry because the Gateway stores only a credential hash after install;
    /// overwriting that file would erase the one copy of the raw access key the client owns.
    /// </summary>
    public static byte[] BuildConfigPreservingUpdatePack(byte[] templateZip)
    {
        using var output = new MemoryStream();
        using (var outArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var inStream = new MemoryStream(templateZip, writable: false))
        using (var inArchive = new ZipArchive(inStream, ZipArchiveMode.Read))
        {
            foreach (var entry in inArchive.Entries)
            {
                if (IsConfigEntry(entry.FullName))
                    continue;

                var outEntry = outArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var outStream = outEntry.Open();
                using var src = entry.Open();
                src.CopyTo(outStream);
            }

            var installer = outArchive.CreateEntry(UpdateInstallerEntry, CompressionLevel.Optimal);
            using var installerStream = installer.Open();
            var bytes = new UTF8Encoding(false).GetBytes(UpdateInstallerScript);
            installerStream.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Produces the personalized <c>.cfg</c> text: preserves everything in the base config except the
    /// <c>[Lumberjacks]</c> section, which is replaced with a fully-populated block (the four
    /// credential keys plus <c>zdoAuthoritativeConsumerEnabled = true</c>, the line volunteers used to
    /// add by hand). Pure and Unity-free so it is unit-testable without a zip.
    /// </summary>
    public static string PersonalizeConfig(
        string baseCfg, string gatewayUrl, string windowId, string enrollmentId, string accessKey)
    {
        var block =
            "[Lumberjacks]\n" +
            "lumberjacksGatewayUrl = " + gatewayUrl + "\n" +
            "lumberjacksAuthoritativeWindowId = " + windowId + "\n" +
            "lumberjacksEnrollmentId = " + enrollmentId + "\n" +
            "lumberjacksClientAccessKey = " + accessKey + "\n" +
            "zdoAuthoritativeConsumerEnabled = true\n";

        var start = baseCfg.IndexOf("[Lumberjacks]", StringComparison.Ordinal);
        if (start < 0)
            return baseCfg.TrimEnd() + "\n\n" + block;

        var head = baseCfg.Substring(0, start).TrimEnd();
        var afterHeader = start + "[Lumberjacks]".Length;
        // Preserve any sections that follow [Lumberjacks] (there are none today, but don't assume it).
        var next = baseCfg.IndexOf("\n[", afterHeader, StringComparison.Ordinal);
        var tail = next >= 0 ? baseCfg.Substring(next + 1).TrimEnd() : string.Empty;

        var result = (head.Length > 0 ? head + "\n\n" : string.Empty) + block;
        if (tail.Length > 0)
            result += "\n" + tail + "\n";
        return result;
    }

    const string UpdateInstallerScript =
        "param([string]$ValheimPath)\n" +
        "$ErrorActionPreference = 'Stop'\n" +
        "if ([string]::IsNullOrWhiteSpace($ValheimPath)) {\n" +
        "  $ValheimPath = Join-Path ${env:ProgramFiles(x86)} 'Steam\\steamapps\\common\\Valheim'\n" +
        "}\n" +
        "$source = Join-Path $PSScriptRoot 'Valheim'\n" +
        "if (!(Test-Path -LiteralPath $source)) { throw \"Missing Valheim folder beside installer.\" }\n" +
        "if (!(Test-Path -LiteralPath $ValheimPath)) { throw \"Valheim path not found: $ValheimPath\" }\n" +
        "$config = Join-Path $ValheimPath 'BepInEx\\config\\djcdevelopment.valheim.comfynetworksense.cfg'\n" +
        "$backup = $null\n" +
        "if (Test-Path -LiteralPath $config) {\n" +
        "  $backup = Join-Path ([IO.Path]::GetTempPath()) ('lumberjacks-config-' + [guid]::NewGuid().ToString('N') + '.cfg')\n" +
        "  Copy-Item -LiteralPath $config -Destination $backup -Force\n" +
        "}\n" +
        "Copy-Item -Path (Join-Path $source '*') -Destination $ValheimPath -Recurse -Force\n" +
        "if ($backup -and (Test-Path -LiteralPath $backup)) {\n" +
        "  New-Item -ItemType Directory -Path (Split-Path -Parent $config) -Force | Out-Null\n" +
        "  Copy-Item -LiteralPath $backup -Destination $config -Force\n" +
        "  Remove-Item -LiteralPath $backup -Force\n" +
        "}\n" +
        "Write-Host 'Lumberjacks mod files updated. Existing ComfyNetworkSense config preserved.'\n";
}
