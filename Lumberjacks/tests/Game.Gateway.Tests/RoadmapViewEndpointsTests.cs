using Game.Gateway.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class RoadmapViewEndpointsTests : IDisposable
{
    const string PathVariable = "LUMBERJACKS_ROADMAP_HTML";

    readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "lumberjacks-roadmap-" + Guid.NewGuid().ToString("N"));

    public RoadmapViewEndpointsTests() => Directory.CreateDirectory(Path.Combine(_contentRoot, "Community"));

    string BakedPath => Path.Combine(_contentRoot, "Community", "roadmap.html");

    string Load() => RoadmapViewEndpoints.LoadHtml(_contentRoot, NullLogger.Instance);

    [Fact]
    public void LoadHtml_ServesARepublishedAssetWithoutARestart()
    {
        File.WriteAllText(BakedPath, "<!DOCTYPE html><p>M0 active</p>");
        Assert.Contains("M0 active", Load());

        File.WriteAllText(BakedPath, "<!DOCTYPE html><p>M1 active, and rather longer</p>");
        Assert.Contains("M1 active", Load());
    }

    [Fact]
    public void LoadHtml_NoticesARewriteOfIdenticalLength()
    {
        File.WriteAllText(BakedPath, "<!DOCTYPE html><p>aaaa</p>");
        Assert.Contains("aaaa", Load());

        // Same byte count, so only the timestamp arm of the cache key can catch this.
        File.WriteAllText(BakedPath, "<!DOCTYPE html><p>bbbb</p>");
        File.SetLastWriteTimeUtc(BakedPath, DateTime.UtcNow.AddSeconds(5));
        Assert.Contains("bbbb", Load());
    }

    [Fact]
    public void LoadHtml_StripsAByteOrderMarkFromPageContent()
    {
        File.WriteAllBytes(BakedPath, [0xEF, 0xBB, 0xBF, .. "<!DOCTYPE html>"u8]);
        Assert.StartsWith("<!DOCTYPE html>", Load(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadHtml_DegradesToTheFallbackPageWhenTheAssetIsAbsent()
    {
        Assert.Contains("Valheim roadmap unavailable", Load());
    }

    [Fact]
    public void ResolvePath_PrefersAMountedAssetOnlyOnceItIsPresent()
    {
        var mounted = Path.Combine(_contentRoot, "mounted.html");
        Environment.SetEnvironmentVariable(PathVariable, mounted);

        // An empty mount must cost freshness, not the page.
        Assert.Equal(BakedPath, RoadmapViewEndpoints.ResolvePath(_contentRoot));

        File.WriteAllText(mounted, "<!DOCTYPE html><p>mounted</p>");
        Assert.Equal(mounted, RoadmapViewEndpoints.ResolvePath(_contentRoot));
    }

    [Fact]
    public void ResolvePath_IgnoresAnUnsetOrEmptyOverride()
    {
        Environment.SetEnvironmentVariable(PathVariable, null);
        Assert.Equal(BakedPath, RoadmapViewEndpoints.ResolvePath(_contentRoot));

        Environment.SetEnvironmentVariable(PathVariable, string.Empty);
        Assert.Equal(BakedPath, RoadmapViewEndpoints.ResolvePath(_contentRoot));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathVariable, null);
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
    }
}
