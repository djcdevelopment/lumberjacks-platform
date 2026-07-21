using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Game.ServiceDefaults;

public static class LumberjacksTelemetry
{
    // In-process tallies alongside the OTel counters, so the Gateway can expose
    // live-metrics endpoints without a Cloud Monitoring round-trip.
    private static readonly ConcurrentDictionary<string, long> DeliveryTally = new();
    private static readonly ConcurrentDictionary<string, long> TransitionTally = new();
    private static readonly ConcurrentDictionary<string, long> UdpPacketTally = new();
    public const string ActivitySourceName = "Lumberjacks.Gameplay";
    public const string MeterName = "Lumberjacks.Gameplay";

    private static readonly ActivitySource Activities = new(ActivitySourceName);
    private static readonly Meter Metrics = new(MeterName);
    private static readonly Counter<long> Messages = Metrics.CreateCounter<long>(
        "lumberjacks.messages", description: "Gameplay messages processed by transport and type.");
    private static readonly UpDownCounter<long> ActiveSessions = Metrics.CreateUpDownCounter<long>(
        "lumberjacks.sessions.active", description: "Currently attached gameplay sessions.");
    private static readonly Counter<long> SessionTransitions = Metrics.CreateCounter<long>(
        "lumberjacks.sessions.transitions", description: "Gameplay session lifecycle transitions.");
    private static readonly Counter<long> UdpPackets = Metrics.CreateCounter<long>(
        "lumberjacks.udp.packets", unit: "{packet}", description: "UDP packet outcomes.");
    private static readonly Counter<long> Delivery = Metrics.CreateCounter<long>(
        "lumberjacks.delivery", unit: "{message}", description: "Gameplay delivery path outcomes.");

    public static Activity? StartMessageActivity(string messageType, string transport)
    {
        if (messageType is "player_input" or "player_move")
            return null;

        var activity = Activities.StartActivity("game.message", ActivityKind.Server);
        activity?.SetTag("game.message.type", messageType);
        activity?.SetTag("network.transport", transport);
        return activity;
    }

    public static void RecordMessage(string messageType, string transport) =>
        Messages.Add(1,
            new KeyValuePair<string, object?>("message.type", messageType),
            new KeyValuePair<string, object?>("network.transport", transport));

    public static void SessionCreated(bool resumed)
    {
        ActiveSessions.Add(1);
        var transition = resumed ? "resumed" : "created";
        SessionTransitions.Add(1, new KeyValuePair<string, object?>("transition", transition));
        TransitionTally.AddOrUpdate(transition, 1, (_, v) => v + 1);
    }

    public static void SessionDetached()
    {
        ActiveSessions.Add(-1);
        SessionTransitions.Add(1, new KeyValuePair<string, object?>("transition", "detached"));
        TransitionTally.AddOrUpdate("detached", 1, (_, v) => v + 1);
    }

    public static void RecordUdpPacket(string outcome)
    {
        UdpPackets.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        UdpPacketTally.AddOrUpdate(outcome, 1, (_, v) => v + 1);
    }

    public static void RecordDelivery(string path)
    {
        Delivery.Add(1, new KeyValuePair<string, object?>("path", path));
        DeliveryTally.AddOrUpdate(path, 1, (_, v) => v + 1);
    }

    /// <summary>Point-in-time copy of delivery-path counts, keyed by path label.</summary>
    public static IReadOnlyDictionary<string, long> SnapshotDelivery() =>
        new Dictionary<string, long>(DeliveryTally);

    /// <summary>Point-in-time copy of session transition counts (created/resumed/detached).</summary>
    public static IReadOnlyDictionary<string, long> SnapshotTransitions() =>
        new Dictionary<string, long>(TransitionTally);

    /// <summary>
    /// Point-in-time copy of UDP packet-outcome counts, keyed by outcome
    /// (received/invalid/unknown_session/send_error). These are outcomes over the packets the
    /// server actually received — NOT network packet loss, which is unobservable server-side.
    /// </summary>
    public static IReadOnlyDictionary<string, long> SnapshotUdpPackets() =>
        new Dictionary<string, long>(UdpPacketTally);
}
