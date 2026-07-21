using Game.Contracts.Events;

namespace Game.ServiceDefaults;

/// <summary>
/// In-process, bounded, newest-first ring of public-safe gameplay events — the honest source
/// for the Public Telemetry API v0 <c>GET /api/v0/telemetry/events</c> feed (G4). Mirrors the
/// <see cref="LumberjacksTelemetry"/> static-tally pattern exactly: producers call
/// <see cref="Capture"/> at the in-process emission seam (BEFORE the fire-and-forget HTTP POST
/// to the out-of-process EventLog/Postgres), the endpoint reads a point-in-time
/// <see cref="Snapshot"/>. No DB is ever read — the DB-less invariant of the v0 surface holds.
///
/// Privacy is enforced by construction, in two layers:
/// <list type="number">
/// <item>A captured event carries ONLY non-identifying fields — an opaque process-local sequence
/// id, the event type, a UTC timestamp, an optional region id, an optional NON-identifying
/// <c>detail</c> (a category/label — never a player name, id, or free text), and a provenance
/// label. There is no field that can hold actor identity or position.</item>
/// <item>The <see cref="AllowedTypes"/> allow-list is enforced INSIDE <see cref="Capture"/>: any
/// event type outside the public world/build/progression/operational subset (e.g. the
/// identity/social <c>player_connected</c>) is silently dropped, so a future miswiring at a call
/// site cannot leak an excluded type into the public feed.</item>
/// </list>
///
/// The endpoint additionally applies a configurable exposure delay (serves only events older than
/// N seconds) so the unauthenticated feed is delayed, not live — that filtering lives at the
/// endpoint, not here; this ring always captures live.
/// </summary>
public static class GameplayEventFeed
{
    /// <summary>Bounded ring capacity. Oldest events age out once this is exceeded.</summary>
    public const int Capacity = 200;

    /// <summary>Default provenance for directly-server-derived events (all live producers today).</summary>
    public const string DefaultProvenance = "observed";

    /// <summary>
    /// The public-safe event subset (world/build/progression/operational). Identity/social/connection
    /// events are deliberately excluded — they are too correlatable even anonymized and are covered by
    /// the /sessions aggregates instead. Only these types may ever enter the feed.
    /// </summary>
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        EventType.StructurePlaced,
        EventType.StructureRemoved,
        EventType.ItemPickedUp,
        EventType.ItemStored,
        EventType.ContainerOpened,
        EventType.RoadSegmentMaintained,
        EventType.SettlementSignatureUpdated,
        EventType.PlayerDiscoveredLandmark,
        EventType.PlayerFollowedRoadSegment,
        EventType.PlayerEnteredRegion,
        EventType.ChallengeStarted,
        EventType.ChallengeCompleted,
        EventType.GuildObjectiveProgressed,
        EventType.GuildObjectiveCompleted,
        EventType.RewardGranted,
        EventType.RegionActivated,
        EventType.RegionDeactivated,
        EventType.InterestSubscriptionChanged,
    };

    private static readonly object Gate = new();
    private static readonly Queue<CapturedEvent> Ring = new(Capacity);
    private static long _sequence;   // process-local monotonic; formats the opaque event_id
    private static long _dropped;    // events aged out of the ring since process start (honesty counter)

    /// <summary>True if <paramref name="eventType"/> is in the public-safe subset the feed accepts.</summary>
    public static bool IsPublicEventType(string eventType) => AllowedTypes.Contains(eventType);

    /// <summary>
    /// Capture a public-safe event at the emission seam. Non-allow-listed types are silently ignored
    /// (defense-in-depth). Uses the current UTC time as <c>occurred_at</c>.
    /// </summary>
    /// <param name="eventType">A canonical <see cref="EventType"/> constant. Must be in the allow-list.</param>
    /// <param name="regionId">Region the event occurred in, where cheaply available; else null. No lookups.</param>
    /// <param name="detail">Optional NON-identifying category/label (structure category, item type, tier label). NEVER a player name/id or free text.</param>
    /// <param name="provenance">observed | reconstructed | verified | community_awarded. Defaults to <see cref="DefaultProvenance"/>.</param>
    public static void Capture(string eventType, string? regionId, string? detail, string? provenance = null) =>
        Capture(eventType, regionId, detail, provenance, DateTimeOffset.UtcNow);

    /// <summary>
    /// Overload accepting an explicit <paramref name="occurredAt"/> — lets tests backdate captures to
    /// exercise the endpoint's exposure-delay filter deterministically.
    /// </summary>
    public static void Capture(string eventType, string? regionId, string? detail, string? provenance, DateTimeOffset occurredAt)
    {
        // Layer 2 privacy: excluded/unknown types never enter the feed, whatever the call site does.
        if (!AllowedTypes.Contains(eventType))
            return;

        lock (Gate)
        {
            var seq = ++_sequence;
            var captured = new CapturedEvent(
                EventId: $"evt-{seq:D6}",
                EventType: eventType,
                OccurredAt: occurredAt,
                RegionId: regionId,
                Detail: detail,
                Provenance: string.IsNullOrWhiteSpace(provenance) ? DefaultProvenance : provenance);

            if (Ring.Count >= Capacity)
            {
                Ring.Dequeue();
                _dropped++;
            }
            Ring.Enqueue(captured);
        }
    }

    /// <summary>
    /// Point-in-time copy of the ring, newest-first, plus the capacity and the running
    /// dropped-since-start counter. Never reads the DB.
    /// </summary>
    public static FeedSnapshot Snapshot()
    {
        lock (Gate)
        {
            // Queue enumerates oldest→newest; reverse for the newest-first contract.
            var events = new CapturedEvent[Ring.Count];
            var i = events.Length - 1;
            foreach (var e in Ring)
                events[i--] = e;

            return new FeedSnapshot(events, Capacity, _dropped);
        }
    }

    /// <summary>Test/ops helper: clear the ring and reset the sequence + dropped counters.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Ring.Clear();
            _sequence = 0;
            _dropped = 0;
        }
    }
}

/// <summary>One public-safe event in the feed. Carries no actor identity, name, or position by design.</summary>
public sealed record CapturedEvent(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    string? RegionId,
    string? Detail,
    string Provenance);

/// <summary>Newest-first snapshot of the feed plus its capacity and dropped-since-start counter.</summary>
public sealed record FeedSnapshot(
    IReadOnlyList<CapturedEvent> Events,
    int Capacity,
    long DroppedSinceStart);
