using Game.Gateway.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class QuestPickerViewEndpointsTests : IDisposable
{
    private const string PathVariable = "LUMBERJACKS_QUESTPICKER_HTML";
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "lumberjacks-questpicker-" + Guid.NewGuid().ToString("N"));

    public QuestPickerViewEndpointsTests() => Directory.CreateDirectory(Path.Combine(_contentRoot, "Community"));

    private string BakedPath => Path.Combine(_contentRoot, "Community", "quest-picker.html");
    private string Load() => QuestPickerViewEndpoints.LoadHtml(_contentRoot, NullLogger.Instance);

    [Fact]
    public void LoadHtml_ServesARepublishedAssetWithoutARestart()
    {
        File.WriteAllText(BakedPath, "<!DOCTYPE html><p>sample one</p>");
        Assert.Contains("sample one", Load());

        File.WriteAllText(BakedPath, "<!DOCTYPE html><p>sample two, refreshed</p>");
        Assert.Contains("sample two, refreshed", Load());
    }

    [Fact]
    public void LoadHtml_StripsAByteOrderMarkFromPageContent()
    {
        File.WriteAllBytes(BakedPath, [0xEF, 0xBB, 0xBF, .. "<!DOCTYPE html>"u8]);
        Assert.StartsWith("<!DOCTYPE html>", Load(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadHtml_DegradesToTheFallbackPageWhenTheAssetIsAbsent() =>
        Assert.Contains("Quest Picker unavailable", Load());

    [Fact]
    public void ResolvePath_PrefersAMountedAssetOnlyOnceItIsPresent()
    {
        var mounted = Path.Combine(_contentRoot, "mounted.html");
        Environment.SetEnvironmentVariable(PathVariable, mounted);
        Assert.Equal(BakedPath, QuestPickerViewEndpoints.ResolvePath(_contentRoot));

        File.WriteAllText(mounted, "<!DOCTYPE html><p>mounted</p>");
        Assert.Equal(mounted, QuestPickerViewEndpoints.ResolvePath(_contentRoot));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathVariable, null);
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
    }
}
