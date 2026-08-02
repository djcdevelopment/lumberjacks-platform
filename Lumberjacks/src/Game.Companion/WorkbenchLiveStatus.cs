using System.Text.Json;

namespace Lumberjacks.Companion;

sealed record WorkbenchLiveGateway(bool Reachable, string Origin, string? Version, string? Environment);

sealed record WorkbenchLiveServer(
    bool TelemetryAvailable,
    bool Stale,
    bool Online,
    string State,
    int PeerCount,
    IReadOnlyList<string> Players,
    string? LastSeen,
    string? ModVersion,
    string? InstanceId);

sealed record WorkbenchLiveCutover(
    bool TelemetryAvailable,
    bool Stale,
    string Mode,
    bool ConsumerActive,
    int ActiveConsumers,
    int Pending,
    int Acknowledged,
    int Applied,
    bool Complete,
    bool? JournalPersistenceHealthy,
    int JournalDurableObjects,
    string? ActiveWorldEpoch,
    int EpochInvalidations);

sealed record WorkbenchLiveMotion(
    bool TelemetryAvailable,
    string State,
    bool? WebSocketConnected,
    bool? UdpReady,
    bool? ApplyEnabled,
    int Received,
    int Relayed,
    int Applied,
    string? LastError);

sealed record WorkbenchLiveActivity(
    string State,
    string Summary,
    bool Executing,
    bool NeedsHuman,
    string? JobId,
    string? CapabilityId,
    string? Title,
    string? Target,
    string? ReasonCode,
    DateTimeOffset? UpdatedUtc);

sealed record WorkbenchLiveSnapshot(
    int SchemaVersion,
    string EventType,
    DateTimeOffset GeneratedUtc,
    string Level,
    string Headline,
    string NextAction,
    WorkbenchLiveGateway Gateway,
    WorkbenchLiveServer Server,
    WorkbenchLiveCutover Cutover,
    WorkbenchLiveMotion Motion,
    WorkbenchLiveActivity Activity);

static class WorkbenchLiveStatus
{
    static readonly string[] ExecutingStates =
        ["queued", "leased", "running", "waiting_dependency", "cancelling"];

    public static WorkbenchLiveSnapshot Build(
        JsonElement? deployment,
        JsonElement? valheim,
        JsonElement? cutover,
        JsonElement? motion,
        JsonElement? journal,
        WorkbenchJob? activeJob,
        string gatewayOrigin,
        DateTimeOffset? generatedUtc = null)
    {
        var heartbeat = Child(valheim, "heartbeat") ?? valheim;
        var window = Child(cutover, "authoritative_window");
        var serverState = Text(heartbeat, "server_state") ?? Text(valheim, "server_state") ?? "unknown";
        var serverStale = Flag(valheim, "stale") ?? true;
        var serverOnline = !serverStale && serverState is "ready" or "running" or "online" or "active";
        var peers = Number(heartbeat, "peer_count") ?? Number(valheim, "peer_count") ?? Number(valheim, "peers") ?? 0;
        var players = PlayerNames(heartbeat ?? valheim);

        var gateway = new WorkbenchLiveGateway(
            deployment.HasValue,
            gatewayOrigin,
            Text(deployment, "lumberjacks_version"),
            Text(deployment, "environment"));

        var server = new WorkbenchLiveServer(
            valheim.HasValue,
            serverStale,
            serverOnline,
            serverState,
            peers,
            players,
            Text(valheim, "last_seen"),
            Text(heartbeat, "mod_version") ?? Text(valheim, "mod_version"),
            Text(heartbeat, "instance_id") ?? Text(valheim, "instance_id"));

        var cutoverStatus = new WorkbenchLiveCutover(
            cutover.HasValue,
            Flag(cutover, "stale") ?? true,
            Text(cutover, "mode") ?? Text(cutover, "state") ?? "unknown",
            Flag(cutover, "consumer_active") ?? false,
            Number(window, "active_consumers") ?? 0,
            Number(window, "pending") ?? Number(window, "consumer_pending") ?? 0,
            Number(window, "consumer_acknowledged") ?? Number(window, "acknowledged") ?? 0,
            Number(window, "applied") ?? 0,
            Flag(window, "complete") ?? false,
            Flag(journal, "persistence_healthy"),
            Number(journal, "durable_objects") ?? 0,
            Text(journal, "active_world_epoch"),
            Number(journal, "epoch_invalidations") ?? 0);

        var relayed = (Number(motion, "relayed_udp") ?? 0) + (Number(motion, "relayed_websocket") ?? 0);
        var received = Number(motion, "received")
            ?? ((Number(heartbeat, "motion_received_udp") ?? 0) + (Number(heartbeat, "motion_received_websocket") ?? 0));
        var motionStatus = new WorkbenchLiveMotion(
            motion.HasValue,
            Text(heartbeat, "motion_state") ?? "unknown",
            Flag(heartbeat, "motion_websocket_connected"),
            Flag(heartbeat, "motion_udp_ready"),
            Flag(heartbeat, "motion_apply_enabled"),
            received,
            relayed,
            Number(heartbeat, "motion_applied") ?? 0,
            EmptyToNull(Text(heartbeat, "motion_last_error")));

        var activity = Activity(activeJob);
        var (level, headline, nextAction) = Explain(
            gateway, server, cutoverStatus, motionStatus, activity);
        return new WorkbenchLiveSnapshot(
            1,
            "workbench.live_status",
            generatedUtc ?? DateTimeOffset.UtcNow,
            level,
            headline,
            nextAction,
            gateway,
            server,
            cutoverStatus,
            motionStatus,
            activity);
    }

    static WorkbenchLiveActivity Activity(WorkbenchJob? job)
    {
        if (job is null)
        {
            return new WorkbenchLiveActivity(
                "idle", "No Workbench job is running.", false, false,
                null, null, null, null, null, null);
        }

        var executing = ExecutingStates.Contains(job.State, StringComparer.Ordinal);
        var needsHuman = job.State == "waiting_human";
        var summary = needsHuman
            ? $"{job.Title} finished its machine work and is waiting for operator review."
            : executing
                ? $"{job.Title} is {job.State.Replace('_', ' ')}."
                : $"{job.Title} is {job.State.Replace('_', ' ')}.";
        return new WorkbenchLiveActivity(
            job.State,
            summary,
            executing,
            needsHuman,
            job.JobId,
            job.CapabilityId,
            job.Title,
            job.Target,
            job.ReasonCode,
            job.UpdatedUtc);
    }

    static (string Level, string Headline, string NextAction) Explain(
        WorkbenchLiveGateway gateway,
        WorkbenchLiveServer server,
        WorkbenchLiveCutover cutover,
        WorkbenchLiveMotion motion,
        WorkbenchLiveActivity activity)
    {
        if (!gateway.Reachable)
        {
            return ("bad", "Gateway telemetry is unavailable.",
                $"Check the configured Gateway origin ({gateway.Origin}) before trusting live game status.");
        }

        if (!server.TelemetryAvailable)
        {
            return ("bad", "The Gateway is reachable, but no Valheim heartbeat is available.",
                "Restore the server heartbeat before starting another gameplay or cutover run.");
        }

        if (server.Stale)
        {
            return ("bad", "The last Valheim heartbeat is stale.",
                "Check the AM4 server and mod telemetry path; do not interpret old player or motion counters.");
        }

        if (cutover.JournalPersistenceHealthy == false)
        {
            return ("bad", "The Valheim zone bank reports unhealthy persistence.",
                "Inspect the Gateway journal status before joining a client or trusting restart replay.");
        }

        if (activity.Executing)
        {
            return ("working", activity.Summary,
                "Follow the active job phase and open its events if progress stops.");
        }

        var who = server.Players.Count == 0 ? null : string.Join(", ", server.Players);
        if (!server.Online)
        {
            return ("bad", $"Valheim reports server state '{server.State}'.",
                "Bring the server to ready before launching clients or starting a cutover job.");
        }

        if (server.PeerCount == 0)
        {
            return ("ready", "AM4 is up and ready; nobody is connected.",
                activity.NeedsHuman
                    ? $"Review the completed '{activity.Title}' evidence; no machine action is still running."
                    : "No action is required. Join clients only for the next intentional test window.");
        }

        var peerText = server.PeerCount == 1 ? "1 peer" : $"{server.PeerCount} peers";
        var playerText = who is null ? peerText : $"{peerText}: {who}";
        if (!motion.TelemetryAvailable)
        {
            return ("bad", $"AM4 is up with {playerText}, but motion telemetry is unavailable.",
                "Open the live trace before interpreting visible movement.");
        }

        if (motion.Received == 0)
        {
            return ("wait", $"AM4 is up with {playerText}; Lumberjacks motion is idle.",
                "If players are moving, inspect the motion route before treating the run as Lumberjacks-authoritative.");
        }

        return ("ready", $"AM4 is up with {playerText}; Lumberjacks motion frames are arriving.",
            "Watch the intended scenario and use the receipt for the final verdict.");
    }

    static JsonElement? Child(JsonElement? root, string name)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(name, out var child) || child.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return child;
    }

    static string? Text(JsonElement? root, string name)
    {
        var value = Child(root, name);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    static int? Number(JsonElement? root, string name)
    {
        var value = Child(root, name);
        if (value?.ValueKind != JsonValueKind.Number) return null;
        if (value.Value.TryGetInt32(out var number)) return number;
        return value.Value.TryGetInt64(out var wide)
            ? (int)Math.Clamp(wide, int.MinValue, int.MaxValue)
            : null;
    }

    static bool? Flag(JsonElement? root, string name)
    {
        var value = Child(root, name);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    static IReadOnlyList<string> PlayerNames(JsonElement? root)
    {
        var players = Child(root, "players");
        if (players?.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var names = new List<string>();
        foreach (var player in players.Value.EnumerateArray())
        {
            string? name = player.ValueKind switch
            {
                JsonValueKind.String => player.GetString(),
                JsonValueKind.Object => FirstText(player, "name", "player_name", "character_name", "steam_name"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static string? FirstText(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
