using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Game.Simulation.World;

/// <summary>
/// Selects how <see cref="InterestManager"/> filters entity updates per observer.
/// Phase 1 of the replication-policy experiment rig: these policies are for
/// A/B measurement against the existing off-host harness, not (yet) the
/// optimized send path — see docs on the 400-bot broadcast-phase knee.
/// </summary>
public enum ReplicationPolicy
{
    /// <summary>Existing near/mid/far behavior — the default, byte-for-byte unchanged.</summary>
    Tiered,

    /// <summary>No interest filtering — every observer gets every changed entity every tick.</summary>
    Full,

    /// <summary>Hard cutoff at NearRadius: inside → every tick, outside → dropped.</summary>
    Radius,
}

/// <summary>
/// Replication policy configuration, read once at startup from raw <see cref="IConfiguration"/>
/// (matching the <c>Udp:Port</c> / <c>Udp__Port</c> pattern used by <c>UdpTransport</c>).
///
/// Env vars: Replication__Policy, Replication__NearRadius, Replication__MidRadius,
/// Replication__MidTickInterval, Replication__SendWorkers, Replication__BroadcastDeadlineMs,
/// Replication__AdaptiveDegrade (send-loop rework, phase 2 — see TickBroadcaster),
/// Replication__UdpSockets (phase 3a — see UdpTransport),
/// Replication__SubscriptionEvents, Replication__SubscriptionSampleTicks
/// (interest_subscription_changed evidence feed — see InterestSubscriptionTracker).
/// </summary>
public sealed record ReplicationOptions
{
    public const double DefaultNearRadius = 100.0;
    public const double DefaultMidRadius = 300.0;
    public const int DefaultMidTickInterval = 4;
    public const int DefaultSendWorkers = 1;
    public const int DefaultBroadcastDeadlineMs = 0;
    public const bool DefaultAdaptiveDegrade = false;
    public const int DefaultUdpSockets = 1;
    public const bool DefaultSubscriptionEvents = false;
    public const int DefaultSubscriptionSampleTicks = 20;

    public ReplicationPolicy Policy { get; init; } = ReplicationPolicy.Tiered;
    public double NearRadius { get; init; } = DefaultNearRadius;
    public double MidRadius { get; init; } = DefaultMidRadius;
    public int MidTickInterval { get; init; } = DefaultMidTickInterval;

    /// <summary>
    /// Broadcast send-phase parallelism. 1 (default) = today's serial <c>foreach</c>, exactly.
    /// 0 = auto (<see cref="Game.Simulation.Tick.SendFanOut.ResolveWorkerCount"/>). N>1 = split each region's
    /// session snapshot into N contiguous chunks and fan them out as concurrent tasks.
    /// </summary>
    public int SendWorkers { get; init; } = DefaultSendWorkers;

    /// <summary>
    /// Per-broadcast deadline in milliseconds. 0 (default) = off — no CancellationTokenSource,
    /// no behavior change. &gt;0 = sessions whose send hasn't completed by the deadline are
    /// aborted so the tick can end (see <see cref="Game.Simulation.Tick.BroadcastDeadline"/>).
    /// </summary>
    public int BroadcastDeadlineMs { get; init; } = DefaultBroadcastDeadlineMs;

    /// <summary>
    /// ADR-0011 "reduce frequency before dropping". False (default) = off. True = when the
    /// previous tick's broadcast wall time exceeded budget, this tick suppresses mid-band
    /// updates (tiered) or every-other-session (radius/full) — see <see cref="Game.Simulation.Tick.AdaptiveDegrade"/>.
    /// </summary>
    public bool AdaptiveDegrade { get; init; } = DefaultAdaptiveDegrade;

    /// <summary>
    /// Phase-3a experiment knob: how many UDP send sockets <see cref="Game.Gateway.WebSocket.UdpTransport"/>
    /// uses for outbound datagram-lane sends. 1 (default) = today's exact behavior — the single
    /// bound socket that receives also sends every reply. 0 = auto — resolve to the effective
    /// <see cref="SendWorkers"/> count (see <see cref="Game.Simulation.Tick.SendFanOut.ResolveUdpSocketCount"/>)
    /// so each worker chunk gets its own socket. N&gt;1 = exactly N sockets. Tests the hypothesis
    /// that a single shared <c>UdpClient</c> (synchronous <c>Send</c>, kernel-serialized) is why
    /// parallel send workers showed zero overlap (Follow-up E). Extra sockets bind to ephemeral
    /// ports — see UdpTransport for the NAT caveat that makes this experiment-only, not a
    /// production default.
    /// </summary>
    public int UdpSockets { get; init; } = DefaultUdpSockets;

    /// <summary>
    /// Emit the canonical <c>interest_subscription_changed</c> event when a player enters or leaves
    /// an observer's interest radius (see <see cref="Game.Simulation.World.InterestSubscriptionTracker"/>).
    /// False (default) = off — zero cost, the diff/emit pass never runs. True = a sampled,
    /// off-tick-thread diagnostic feed for replication-policy experiments (Goal 6); it does not
    /// change what is broadcast. No-op for the Full policy (no interest filtering to observe).
    /// </summary>
    public bool SubscriptionEvents { get; init; } = DefaultSubscriptionEvents;

    /// <summary>
    /// When <see cref="SubscriptionEvents"/> is on, take a subscription sample every Nth tick
    /// (default 20 → ~1 Hz at a 20 Hz tick). Larger = coarser/cheaper. &lt;=1 samples every tick
    /// (heaviest). Bounds event volume: a full snapshot is diffed at most once per this many ticks.
    /// </summary>
    public int SubscriptionSampleTicks { get; init; } = DefaultSubscriptionSampleTicks;

    /// <summary>Lowercase policy name, for logging and metrics tagging.</summary>
    public string PolicyName => Policy switch
    {
        ReplicationPolicy.Full => "full",
        ReplicationPolicy.Radius => "radius",
        _ => "tiered",
    };

    /// <summary>
    /// Builds options from raw configuration, tolerating operator typos. Every key is parsed
    /// with explicit TryParse-and-fall-back-to-default semantics rather than the raw type-binder
    /// (<see cref="ConfigurationBinder.GetValue{T}(IConfiguration, string, T)"/>), because the
    /// binder throws an unhandled <see cref="FormatException"/> on any unparseable value — a
    /// plausible-but-wrong operator input like <c>AdaptiveDegrade=off</c> or
    /// <c>SendWorkers=lots</c> hard-crashes the gateway on startup (exit 139) instead of degrading
    /// to the documented default. See docs/benchmark-host-capacity-2026-07-12.md
    /// ("Robustness bug found during the rerun").
    /// </summary>
    /// <param name="config">Configuration source (env vars / appsettings).</param>
    /// <param name="onWarning">
    /// Optional sink for a human-readable warning whenever a key is present but unparseable and
    /// the default is substituted. Left null in tests; the gateway wires it to its logger.
    /// </param>
    public static ReplicationOptions FromConfiguration(IConfiguration config, Action<string>? onWarning = null)
    {
        var policy = (config["Replication:Policy"] ?? "tiered").Trim().ToLowerInvariant() switch
        {
            "full" => ReplicationPolicy.Full,
            "radius" => ReplicationPolicy.Radius,
            "tiered" => ReplicationPolicy.Tiered,
            _ => WarnAndDefault("Replication:Policy", config["Replication:Policy"], ReplicationPolicy.Tiered, onWarning),
        };

        return new ReplicationOptions
        {
            Policy = policy,
            NearRadius = ParseDouble(config, "Replication:NearRadius", DefaultNearRadius, onWarning),
            MidRadius = ParseDouble(config, "Replication:MidRadius", DefaultMidRadius, onWarning),
            MidTickInterval = ParseInt(config, "Replication:MidTickInterval", DefaultMidTickInterval, onWarning),
            SendWorkers = ParseInt(config, "Replication:SendWorkers", DefaultSendWorkers, onWarning),
            BroadcastDeadlineMs = ParseInt(config, "Replication:BroadcastDeadlineMs", DefaultBroadcastDeadlineMs, onWarning),
            AdaptiveDegrade = ParseBool(config, "Replication:AdaptiveDegrade", DefaultAdaptiveDegrade, onWarning),
            UdpSockets = ParseInt(config, "Replication:UdpSockets", DefaultUdpSockets, onWarning),
            SubscriptionEvents = ParseBool(config, "Replication:SubscriptionEvents", DefaultSubscriptionEvents, onWarning),
            SubscriptionSampleTicks = ParseInt(config, "Replication:SubscriptionSampleTicks", DefaultSubscriptionSampleTicks, onWarning),
        };
    }

    // ── Tolerant parsers ─────────────────────────────────────────────────────────────────
    // A missing/blank key silently takes the default (not configured is not an error). A key
    // that is present but unparseable warns and takes the default — never throws.

    private static int ParseInt(IConfiguration config, string key, int @default, Action<string>? onWarning)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw))
            return @default;
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : WarnAndDefault(key, raw, @default, onWarning);
    }

    private static double ParseDouble(IConfiguration config, string key, double @default, Action<string>? onWarning)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw))
            return @default;
        return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : WarnAndDefault(key, raw, @default, onWarning);
    }

    private static bool ParseBool(IConfiguration config, string key, bool @default, Action<string>? onWarning)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw))
            return @default;
        return bool.TryParse(raw.Trim(), out var value)
            ? value
            : WarnAndDefault(key, raw, @default, onWarning);
    }

    private static T WarnAndDefault<T>(string key, string? raw, T @default, Action<string>? onWarning)
    {
        onWarning?.Invoke(
            $"Config '{key}' has unrecognized value '{raw}' — falling back to default '{@default}'. " +
            $"Expected a {DescribeExpected(@default)}.");
        return @default;
    }

    private static string DescribeExpected<T>(T @default) => @default switch
    {
        bool => "boolean (true/false)",
        int => "whole number",
        double => "number",
        ReplicationPolicy => "policy name (tiered/full/radius)",
        _ => "valid value",
    };
}
