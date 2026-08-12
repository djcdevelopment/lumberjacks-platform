using System.Text.Json;
using Comfy.Quest.Studio;

namespace Lumberjacks.Companion;

/// <summary>
/// Companion's implementation of Quest.Studio's IQuestStudioHost seam. Wires the carved-out
/// Comfy.Quest.Studio package back to Companion's own WorkbenchStore (state dir),
/// ValheimLocator (Valheim discovery), WorkbenchService (browser-mutation auth), and the
/// global Json.Options — the exact surface finding F4 identified as Quest Studio's real
/// dependency footprint. Companion stays the host per docs/quest-studio-runtime-boundary.md.
/// </summary>
sealed class CompanionQuestStudioHost : IQuestStudioHost
{
    readonly WorkbenchStore _store;
    readonly ValheimLocator _locator;
    readonly WorkbenchService _workbench;

    public CompanionQuestStudioHost(WorkbenchStore store, ValheimLocator locator, WorkbenchService workbench)
    {
        _store = store;
        _locator = locator;
        _workbench = workbench;
    }

    public string StateDirectory => _store.RootDirectory;
    public string? FindValheim() => _locator.Find();
    public bool Authorize(HttpRequest request) => _workbench.BrowserMutationAllowed(request);
    public JsonSerializerOptions Json => global::Json.Options;
}
