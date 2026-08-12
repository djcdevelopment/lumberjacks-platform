using System.Text.Json;
using Comfy.Quest.Studio;
using Microsoft.AspNetCore.Http;

namespace Game.Companion.Tests;

/// <summary>
/// A minimal IQuestStudioHost for unit-testing QuestStudioService/QuestPackPublisher directly,
/// without spinning up Companion's WorkbenchStore/CompanionStateStore/ValheimLocator/
/// WorkbenchService chain (and its env-var-driven wiring). Mirrors the same JsonSerializerOptions
/// Companion's real host (CompanionQuestStudioHost -> global Json.Options) uses.
/// </summary>
sealed class TestQuestStudioHost : IQuestStudioHost
{
    public required string StateDirectory { get; init; }
    public string? ValheimPath { get; init; }

    public string? FindValheim() => ValheimPath;
    public bool Authorize(HttpRequest request) => true;

    public JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
