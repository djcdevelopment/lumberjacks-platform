namespace Game.Gateway.Valheim;

/// <summary>Bounded latest-value store for the mod's sanitized runtime heartbeat.</summary>
public sealed class ValheimTelemetryHeartbeatService
{
    private readonly object _gate = new();
    private ValheimTelemetryHeartbeat? _latest;
    private DateTimeOffset? _lastSeen;

    public void Record(ValheimTelemetryHeartbeat heartbeat)
    {
        lock (_gate)
        {
            _latest = heartbeat;
            _lastSeen = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Returns the mod version reported by the running Valheim instance. Deployment
    /// telemetry uses this ahead of its cold-start environment fallback so updating
    /// the plugin never requires a Gateway restart (and therefore cannot discard an
    /// in-flight authoritative queue merely to refresh a dashboard label).
    /// </summary>
    public string? LatestModVersion()
    {
        lock (_gate)
        {
            return string.IsNullOrWhiteSpace(_latest?.ModVersion) ? null : _latest.ModVersion;
        }
    }

    public object Snapshot(ValheimHandshakeService? handshakes = null)
    {
        lock (_gate)
        {
            var stale = _lastSeen is null || DateTimeOffset.UtcNow - _lastSeen > TimeSpan.FromSeconds(15);
            var players = handshakes is null || string.IsNullOrWhiteSpace(_latest?.EnrollmentManifestId)
                ? Array.Empty<string>()
                : handshakes.GetAcceptedPlayerNames(_latest.EnrollmentManifestId);
            return new
            {
                stability = "unstable",
                stale,
                last_seen = _lastSeen,
                heartbeat = _latest is null ? null : new
                {
                    cutover_mode = _latest.CutoverMode,
                    enrollment_manifest_id = _latest.EnrollmentManifestId,
                    mod_version = _latest.ModVersion,
                    instance_id = _latest.InstanceId,
                    server_role = _latest.ServerRole,
                    server_state = _latest.ServerState,
                    peer_count = _latest.PeerCount,
                    players,
                    handshake_accepted = _latest.HandshakeAccepted,
                    handshake_rejected = _latest.HandshakeRejected,
                    redirect_suppressed = _latest.RedirectSuppressed,
                    redirect_received = _latest.RedirectReceived,
                    redirect_missing = _latest.RedirectMissing,
                    redirect_duplicates = _latest.RedirectDuplicates,
                    injection_applied = _latest.InjectionApplied,
                    injection_rendered = _latest.InjectionRendered,
                    injection_rejected = _latest.InjectionRejected,
                    coverage_total = _latest.CoverageTotal,
                    coverage_lumberjacks = _latest.CoverageLumberjacks,
                    coverage_native_only = _latest.CoverageNativeOnly,
                    native_fallbacks = _latest.NativeFallbacks,
                    zdo_probe_running = _latest.ZdoProbeRunning,
                    zdo_probe_recv_rows = _latest.ZdoProbeRecvRows,
                    zdo_probe_send_rows = _latest.ZdoProbeSendRows,
                    zdo_probe_recv_calls = _latest.ZdoProbeRecvCalls,
                    zdo_probe_create_sync_calls = _latest.ZdoProbeCreateSyncCalls,
                    zdo_authoritative_enabled = _latest.ZdoAuthoritativeEnabled,
                    zdo_authoritative_applied = _latest.ZdoAuthoritativeApplied,
                    zdo_authoritative_rejected = _latest.ZdoAuthoritativeRejected,
                    zdo_authoritative_duplicates = _latest.ZdoAuthoritativeDuplicates,
                    zdo_authoritative_retried = _latest.ZdoAuthoritativeRetried,
                    zdo_authoritative_pending = _latest.ZdoAuthoritativePending,
                    motion_state = _latest.MotionState,
                    motion_websocket_connected = _latest.MotionWebsocketConnected,
                    motion_udp_ready = _latest.MotionUdpReady,
                    motion_apply_enabled = _latest.MotionApplyEnabled,
                    motion_sent_udp = _latest.MotionSentUdp,
                    motion_sent_websocket = _latest.MotionSentWebsocket,
                    motion_received_udp = _latest.MotionReceivedUdp,
                    motion_received_websocket = _latest.MotionReceivedWebsocket,
                    motion_applied = _latest.MotionApplied,
                    motion_stale_fallbacks = _latest.MotionStaleFallbacks,
                    motion_unknown_zdos = _latest.MotionUnknownZdos,
                    motion_remote_entities = _latest.MotionRemoteEntities,
                    motion_last_error = _latest.MotionLastError,
                    sample_timestamp_utc = _latest.TimestampUtc,
                },
            };
        }
    }

    public object CutoverSnapshot(ValheimZdoRedirectService redirects, ValheimZdoConsumerTelemetryService consumers)
    {
        lock (_gate)
        {
            var stale = _lastSeen is null || DateTimeOffset.UtcNow - _lastSeen > TimeSpan.FromSeconds(15);
            var mode = _latest?.CutoverMode;
            var coverageTotal = _latest?.CoverageTotal;
            var coverageLumberjacks = _latest?.CoverageLumberjacks;
            var nativeOnly = _latest?.CoverageNativeOnly;
            var coveragePercent = coverageTotal is > 0 && coverageLumberjacks.HasValue
                ? Math.Round(100d * coverageLumberjacks.Value / coverageTotal.Value, 2)
                : (double?)null;
            var windowId = _latest?.EnrollmentManifestId ?? string.Empty;
            var redirect = redirects.GetStatus(windowId);
            var consumer = consumers.GetWindowStatus(windowId);
            var authoritativeComplete = IsAuthoritativeComplete(windowId, redirects, consumers);
            var consumerActive = consumer.ActiveConsumers > 0;
            var consumerDraining = consumerActive && (redirect.Pending > 0 || consumer.Pending > 0);

            return new
            {
                state = stale ? "stale" : (mode ?? "unknown"),
                stale,
                heartbeat_stale = stale,
                consumer_active = consumerActive,
                consumer_draining = consumerDraining,
                mode,
                enrollment_manifest_id = _latest?.EnrollmentManifestId,
                coverage_total = coverageTotal,
                coverage_lumberjacks = coverageLumberjacks,
                coverage_native_only = nativeOnly,
                coverage_percent = coveragePercent,
                native_fallbacks = _latest?.NativeFallbacks,
                zdo_probe_running = _latest?.ZdoProbeRunning,
                zdo_probe_recv_rows = _latest?.ZdoProbeRecvRows,
                zdo_probe_send_rows = _latest?.ZdoProbeSendRows,
                zdo_authoritative_enabled = _latest?.ZdoAuthoritativeEnabled,
                zdo_authoritative_applied = _latest?.ZdoAuthoritativeApplied,
                zdo_authoritative_rejected = _latest?.ZdoAuthoritativeRejected,
                zdo_authoritative_duplicates = _latest?.ZdoAuthoritativeDuplicates,
                zdo_authoritative_retried = _latest?.ZdoAuthoritativeRetried,
                zdo_authoritative_pending = _latest?.ZdoAuthoritativePending,
                authoritative_window = new
                {
                    window_id = windowId,
                    durable_queue = redirects.PersistenceEnabled,
                    persistence_healthy = redirects.PersistenceHealthy,
                    wal_bytes = redirects.WalBytes,
                    receipts = redirect.Receipts,
                    acknowledged = redirect.Acknowledged,
                    pending = redirect.Pending,
                    active_consumers = consumer.ActiveConsumers,
                    applied = consumer.Applied,
                    superseded = consumer.Superseded,
                    consumer_acknowledged = consumer.Acknowledged,
                    rejected = consumer.Rejected,
                    duplicates = consumer.Duplicates,
                    retried = consumer.Retried,
                    consumer_pending = consumer.Pending,
                    complete = authoritativeComplete,
                    last_seen = consumer.LastSeen,
                },
                last_seen = _lastSeen,
                mod_version = _latest?.ModVersion,
                instance_id = _latest?.InstanceId,
            };
        }
    }

    public bool IsAuthoritativeComplete(string windowId, ValheimZdoRedirectService redirects,
        ValheimZdoConsumerTelemetryService consumers)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return false;
        var redirect = redirects.GetStatus(windowId);
        var consumer = consumers.GetWindowStatus(windowId);
        return redirect.DistinctSeq > 0 &&
            redirect.MissingSeq == 0 &&
            !redirect.SeqTrackingSaturated &&
            (!redirects.PersistenceEnabled || redirects.PersistenceHealthy) &&
            redirect.Pending == 0 &&
            redirect.Acknowledged >= redirect.DistinctSeq &&
            // Multi-consumer: N concurrent players each drain their own (window, recipient)
            // partition, so completeness is "at least one active consumer and every partition
            // caught up" — NOT exactly one. redirect.Pending / consumer.Pending are already
            // aggregated across all recipient partitions (AggregateStatuses / GetWindowStatus),
            // so an undrained peer partition keeps this false until that peer's consumer catches
            // up. Was `== 1`, the single-volunteer gate that broke the dashboard on the 2nd cutover.
            consumer.ActiveConsumers >= 1 &&
            consumer.Rejected == 0 &&
            consumer.Pending == 0;
    }

    /// <summary>
    /// Records liveness, then answers whether the beat is admissible. The order is the
    /// point and is why this is one method rather than two calls at the endpoint: a
    /// rejected heartbeat is still proof the server is alive and reporting, so it must
    /// refresh <c>_lastSeen</c>. Skipping <see cref="Record"/> on rejection let the 15s
    /// staleness clock run out under sustained load — the coverage figures that prove the
    /// cutover is working froze at their last admitted values and the dashboard headline
    /// flipped to "stale", while the redirect and consumer counters beside them kept
    /// updating live off their own services. <see cref="CutoverSnapshot"/> recomputes
    /// <see cref="IsAuthoritativeComplete"/> per request, so recording a rejected beat
    /// cannot make the dashboard claim completeness; it reads "fresh but draining", which
    /// is the truth. Same reasoning as the <c>PeerCount == 0</c> carve-out below.
    /// </summary>
    public bool RecordAndAdmit(ValheimTelemetryHeartbeat heartbeat,
        ValheimZdoRedirectService redirects, ValheimZdoConsumerTelemetryService consumers)
    {
        Record(heartbeat);
        return CanAcceptPrimaryHeartbeat(heartbeat, redirects, consumers);
    }

    public bool CanAcceptPrimaryHeartbeat(ValheimTelemetryHeartbeat heartbeat,
        ValheimZdoRedirectService redirects, ValheimZdoConsumerTelemetryService consumers)
    {
        if (heartbeat.CutoverMode != "lumberjacks-primary") return true;
        if (string.IsNullOrWhiteSpace(heartbeat.EnrollmentManifestId)) return false;

        // A primary server remains healthy and armed when it is empty. Requiring a
        // positive post-start traffic sample or a fresh consumer in that state
        // rejects every heartbeat after a restart and makes the dashboard stale
        // until a player joins. Connected peers still require positive coverage
        // and the full apply/ack gate on every accepted primary heartbeat.
        if (heartbeat.PeerCount == 0) return true;
        return heartbeat.CoverageTotal is > 0 && heartbeat.CoverageNativeOnly is 0 &&
            IsAuthoritativeComplete(heartbeat.EnrollmentManifestId, redirects, consumers);
    }

    public object EnrollmentSnapshot(string manifestId)
    {
        lock (_gate)
        {
            var stale = _lastSeen is null || DateTimeOffset.UtcNow - _lastSeen > TimeSpan.FromSeconds(15);
            var mode = _latest?.CutoverMode ?? "native";
            return new
            {
                manifest_id = manifestId,
                state = stale ? "stale" : "advertised",
                mode,
                server_instance_id = _latest?.InstanceId,
                mod_version = _latest?.ModVersion,
                required_transport = "lumberjacks-progressive",
                native_fallback_allowed = true,
                coverage_gate = new
                {
                    total = _latest?.CoverageTotal,
                    lumberjacks = _latest?.CoverageLumberjacks,
                    native_only = _latest?.CoverageNativeOnly,
                    complete = _latest?.CoverageTotal is > 0 && _latest.CoverageNativeOnly == 0,
                },
                last_seen = _lastSeen,
            };
        }
    }
}

public sealed record ValheimTelemetryHeartbeat
{
    public string? CutoverMode { get; init; }
    public string? EnrollmentManifestId { get; init; }
    public string? InstanceId { get; init; }
    public string? ModVersion { get; init; }
    public string? TimestampUtc { get; init; }
    public string? ServerRole { get; init; }
    public string? ServerState { get; init; }
    public int? PeerCount { get; init; }
    public long? HandshakeAccepted { get; init; }
    public long? HandshakeRejected { get; init; }
    public long? RedirectSuppressed { get; init; }
    public long? RedirectReceived { get; init; }
    public long? RedirectMissing { get; init; }
    public long? RedirectDuplicates { get; init; }
    public long? InjectionApplied { get; init; }
    public long? InjectionRendered { get; init; }
    public long? InjectionRejected { get; init; }
    public long? CoverageTotal { get; init; }
    public long? CoverageLumberjacks { get; init; }
    public long? CoverageNativeOnly { get; init; }
    public long? NativeFallbacks { get; init; }
    public bool? ZdoProbeRunning { get; init; }
    public long? ZdoProbeRecvRows { get; init; }
    public long? ZdoProbeSendRows { get; init; }
    public bool? ZdoAuthoritativeEnabled { get; init; }
    public long? ZdoAuthoritativeApplied { get; init; }
    public long? ZdoAuthoritativeRejected { get; init; }
    public long? ZdoAuthoritativeDuplicates { get; init; }
    public long? ZdoAuthoritativeRetried { get; init; }
    public long? ZdoAuthoritativePending { get; init; }
    public long? ZdoProbeRecvCalls { get; init; }
    public long? ZdoProbeCreateSyncCalls { get; init; }
    public string? MotionState { get; init; }
    public bool? MotionWebsocketConnected { get; init; }
    public bool? MotionUdpReady { get; init; }
    public bool? MotionApplyEnabled { get; init; }
    public long? MotionSentUdp { get; init; }
    public long? MotionSentWebsocket { get; init; }
    public long? MotionReceivedUdp { get; init; }
    public long? MotionReceivedWebsocket { get; init; }
    public long? MotionApplied { get; init; }
    public long? MotionStaleFallbacks { get; init; }
    public long? MotionUnknownZdos { get; init; }
    public int? MotionRemoteEntities { get; init; }
    public string? MotionLastError { get; init; }
}
