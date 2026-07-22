using System.IO.Compression;
using System.Text;
using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// Covers the personalized mod-pack generation (self-service onboarding, Increment 1): the base
/// config's other sections survive, the [Lumberjacks] block is fully populated including the consumer
/// flag volunteers used to add by hand, and the zip is copied through byte-for-byte except that one
/// config entry.
/// </summary>
public sealed class ModPackBuilderTests
{
    private const string BaseCfg =
        "[General]\nisModEnabled = true\n\n" +
        "[Gameplay]\ngameplayEventProducerEnabled = true\nquestEvaluatorEnabled = true\n\n" +
        "[Lumberjacks]\n## placeholder — paste your block below\nlumberjacksGatewayUrl = http://placeholder\n";

    // --- PersonalizeConfig (pure) ---------------------------------------------------------------

    [Fact]
    public void PersonalizeConfig_InjectsFullBlock_AndPreservesOtherSections()
    {
        var cfg = ModPackBuilder.PersonalizeConfig(
            BaseCfg, "https://gw.example:42317", "p7-primary-v1", "enroll-123", "ACCESS-KEY-XYZ");

        // Other sections survive untouched.
        Assert.Contains("[General]\nisModEnabled = true", cfg);
        Assert.Contains("gameplayEventProducerEnabled = true", cfg);
        Assert.Contains("questEvaluatorEnabled = true", cfg);

        // The [Lumberjacks] block is fully populated (all four keys + the consumer flag).
        Assert.Contains("lumberjacksGatewayUrl = https://gw.example:42317", cfg);
        Assert.Contains("lumberjacksAuthoritativeWindowId = p7-primary-v1", cfg);
        Assert.Contains("lumberjacksEnrollmentId = enroll-123", cfg);
        Assert.Contains("lumberjacksClientAccessKey = ACCESS-KEY-XYZ", cfg);
        Assert.Contains("zdoAuthoritativeConsumerEnabled = true", cfg);

        // The placeholder gateway URL is gone; there is exactly one [Lumberjacks] header.
        Assert.DoesNotContain("http://placeholder", cfg);
        Assert.Equal(1, CountOccurrences(cfg, "[Lumberjacks]"));
        Assert.Equal(1, CountOccurrences(cfg, "lumberjacksClientAccessKey ="));
    }

    [Fact]
    public void PersonalizeConfig_AppendsBlock_WhenBaseHasNoLumberjacksSection()
    {
        var cfg = ModPackBuilder.PersonalizeConfig(
            "[General]\nisModEnabled = true\n", "gw", "win", "id", "key");
        Assert.Contains("[General]\nisModEnabled = true", cfg);
        Assert.Contains("[Lumberjacks]", cfg);
        Assert.Contains("lumberjacksClientAccessKey = key", cfg);
        Assert.Contains("zdoAuthoritativeConsumerEnabled = true", cfg);
    }

    [Fact]
    public void PersonalizeConfig_PreservesASectionThatFollowsLumberjacks()
    {
        var baseCfg = "[General]\na = 1\n\n[Lumberjacks]\nlumberjacksGatewayUrl = x\n\n[HUD]\nshowHudOnStart = true\n";
        var cfg = ModPackBuilder.PersonalizeConfig(baseCfg, "gw", "win", "id", "key");
        Assert.Contains("[General]\na = 1", cfg);
        Assert.Contains("[HUD]\nshowHudOnStart = true", cfg);
        Assert.Contains("lumberjacksClientAccessKey = key", cfg);
        Assert.Equal(1, CountOccurrences(cfg, "[Lumberjacks]"));
    }

    // --- BuildPersonalizedPack (zip) ------------------------------------------------------------

    [Fact]
    public void BuildPersonalizedPack_ReplacesOnlyTheConfigEntry_AndKeepsTheRest()
    {
        var template = BuildTemplateZip(new()
        {
            ["Valheim\\BepInEx\\config\\djcdevelopment.valheim.comfynetworksense.cfg"] = BaseCfg,
            ["Valheim\\winhttp.dll"] = "DUMMY-BINARY",
            ["README.txt"] = "install guide",
        });

        var pack = ModPackBuilder.BuildPersonalizedPack(
            template, "https://gw:42317", "p7-primary-v1", "enroll-9", "KEY-9");

        var entries = ReadZip(pack);
        // The personalized config carries the credential.
        var cfg = entries["Valheim\\BepInEx\\config\\djcdevelopment.valheim.comfynetworksense.cfg"];
        Assert.Contains("lumberjacksEnrollmentId = enroll-9", cfg);
        Assert.Contains("lumberjacksClientAccessKey = KEY-9", cfg);
        Assert.Contains("zdoAuthoritativeConsumerEnabled = true", cfg);
        Assert.DoesNotContain("http://placeholder", cfg);
        // Everything else is byte-for-byte.
        Assert.Equal("DUMMY-BINARY", entries["Valheim\\winhttp.dll"]);
        Assert.Equal("install guide", entries["README.txt"]);
    }

    [Fact]
    public void BuildPersonalizedPack_Throws_WhenTemplateHasNoConfigEntry()
    {
        var template = BuildTemplateZip(new() { ["Valheim\\winhttp.dll"] = "DUMMY" });
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModPackBuilder.BuildPersonalizedPack(template, "gw", "win", "id", "key"));
        Assert.Contains(ModPackBuilder.ConfigEntrySuffix, ex.Message);
    }

    // Opt-in real-template smoke: runs only when MODPACK_TEMPLATE_SMOKE points at an actual
    // Comfy-P7-Alpha-Mods.zip (backslash paths, directory entries, binary DLLs). Skipped in CI.
    [Fact]
    public void BuildPersonalizedPack_AgainstRealTemplate_WhenProvided()
    {
        var path = Environment.GetEnvironmentVariable("MODPACK_TEMPLATE_SMOKE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var pack = ModPackBuilder.BuildPersonalizedPack(
            File.ReadAllBytes(path), "https://comfy-p7.duckdns.org", "p7-primary-v1", "e-real", "K-real");

        using var ms = new MemoryStream(pack);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        // The real binaries and structure survive.
        Assert.Contains(archive.Entries, e => e.FullName.Replace('\\', '/').EndsWith("plugins/ComfyNetworkSense.dll"));
        Assert.Contains(archive.Entries, e => e.FullName.Replace('\\', '/').EndsWith("winhttp.dll"));
        // The config entry is personalized.
        var cfgEntry = archive.Entries.Single(e => e.FullName.Replace('\\', '/').EndsWith(ModPackBuilder.ConfigEntrySuffix));
        using var reader = new StreamReader(cfgEntry.Open(), Encoding.UTF8);
        var cfg = reader.ReadToEnd();
        Assert.Contains("lumberjacksClientAccessKey = K-real", cfg);
        Assert.Contains("zdoAuthoritativeConsumerEnabled = true", cfg);
    }

    // --- helpers --------------------------------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static byte[] BuildTemplateZip(Dictionary<string, string> entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private static Dictionary<string, string> ReadZip(byte[] zip)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var ms = new MemoryStream(zip);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            result[entry.FullName] = reader.ReadToEnd();
        }
        return result;
    }
}
