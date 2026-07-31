using System.Globalization;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Game.Simulation.World;

namespace Game.Gateway.WebSocket;

/// <summary>
/// Authoritative bridge between the reliable native CharacterID lifecycle and the lossy
/// Valheim motion lane. A reliable descriptor opens one observer/subject edge; a reliable
/// tombstone closes it. Motion is relayed only while that edge is open.
/// </summary>
public sealed class ValheimPlayerLifecycle
{
    private readonly record struct InterestEdge(string Observer, string Subject);
    private readonly record struct DescriptorDelivery(
        GameSession Observer,
        GameSession Subject,
        ValheimCharacterAuthority Authority,
        ValheimMotionPosition Position);
    private readonly record struct TombstoneDelivery(
        GameSession Observer,
        string SubjectLogicalPeerId,
        ValheimCharacterAuthority Authority,
        string Reason);

    private readonly object _gate = new();
    private readonly SessionManager _sessions;
    private readonly ILogger<ValheimPlayerLifecycle> _logger;
    private readonly Dictionary<InterestEdge, ValheimCharacterAuthority> _edges = new();
    private readonly double _enterRadiusSquared;
    private readonly double _leaveRadiusSquared;

    public ValheimPlayerLifecycle(
        SessionManager sessions,
        IConfiguration configuration,
        ILogger<ValheimPlayerLifecycle> logger)
    {
        _sessions = sessions;
        _logger = logger;

        var replication = ReplicationOptions.FromConfiguration(configuration);
        var enterRadius = ReadPositiveDouble(
            configuration["Valheim:PlayerInterestRadius"],
            replication.MidRadius);
        var hysteresis = ReadPositiveDouble(
            configuration["Valheim:PlayerInterestHysteresis"],
            Math.Max(10.0, enterRadius * 0.1));
        _enterRadiusSquared = enterRadius * enterRadius;
        _leaveRadiusSquared = (enterRadius + hysteresis) * (enterRadius + hysteresis);

        _logger.LogInformation(
            "Valheim Player AoI lifecycle configured enterRadius={EnterRadius} leaveRadius={LeaveRadius}",
            enterRadius,
            enterRadius + hysteresis);
    }

    public async Task CharacterRegisteredAsync(
        GameSession subject,
        ValheimCharacterRegistration registration)
    {
        if (!registration.Changed) return;

        List<TombstoneDelivery> tombstones = new();
        lock (_gate)
        {
            foreach (var pair in _edges.ToArray())
            {
                if (pair.Key.Subject == subject.ValheimLogicalPeerId)
                {
                    var observer = _sessions.FindByValheimLogicalPeer(pair.Key.Observer);
                    if (observer != null)
                        tombstones.Add(new TombstoneDelivery(
                            observer,
                            subject.ValheimLogicalPeerId,
                            pair.Value,
                            "character_generation_changed"));
                    _edges.Remove(pair.Key);
                }
                else if (pair.Key.Observer == subject.ValheimLogicalPeerId)
                {
                    var observed = _sessions.FindByValheimLogicalPeer(pair.Key.Subject);
                    if (observed != null)
                        tombstones.Add(new TombstoneDelivery(
                            subject,
                            pair.Key.Subject,
                            pair.Value,
                            "observer_generation_changed"));
                    _edges.Remove(pair.Key);
                }
            }
        }

        await DeliverAsync(Array.Empty<DescriptorDelivery>(), tombstones);
        _logger.LogInformation(
            "Valheim character generation authorized logicalPeer={LogicalPeer} zdo={ZdoUser}:{ZdoId} generation={Generation}",
            subject.ValheimLogicalPeerId,
            registration.Current.ZdoUserId,
            registration.Current.ZdoId,
            registration.Current.Generation);
    }

    public async Task<IReadOnlyList<GameSession>> ReconcileMotionAsync(GameSession source)
    {
        var descriptors = new List<DescriptorDelivery>();
        var tombstones = new List<TombstoneDelivery>();
        var motionTargets = new List<GameSession>();
        var candidates = _sessions.GetAll();

        lock (_gate)
        {
            foreach (var target in candidates)
            {
                if (!IsPlayerCandidate(source, target)) continue;

                var targetSeesSource = ReconcileEdge(
                    observer: target,
                    subject: source,
                    descriptors,
                    tombstones);
                ReconcileEdge(
                    observer: source,
                    subject: target,
                    descriptors,
                    tombstones);

                if (targetSeesSource)
                    motionTargets.Add(target);
            }
        }

        await DeliverAsync(descriptors, tombstones);
        return motionTargets;
    }

    public async Task SessionDisconnectedAsync(GameSession session)
    {
        if (string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId)) return;

        var tombstones = new List<TombstoneDelivery>();
        lock (_gate)
        {
            foreach (var pair in _edges.ToArray())
            {
                if (pair.Key.Subject == session.ValheimLogicalPeerId)
                {
                    var observer = _sessions.FindByValheimLogicalPeer(pair.Key.Observer);
                    if (observer != null)
                        tombstones.Add(new TombstoneDelivery(
                            observer,
                            session.ValheimLogicalPeerId,
                            pair.Value,
                            "session_disconnected"));
                    _edges.Remove(pair.Key);
                }
                else if (pair.Key.Observer == session.ValheimLogicalPeerId)
                {
                    _edges.Remove(pair.Key);
                }
            }
        }

        await DeliverAsync(Array.Empty<DescriptorDelivery>(), tombstones);
    }

    private bool ReconcileEdge(
        GameSession observer,
        GameSession subject,
        List<DescriptorDelivery> descriptors,
        List<TombstoneDelivery> tombstones)
    {
        var edge = new InterestEdge(
            observer.ValheimLogicalPeerId,
            subject.ValheimLogicalPeerId);
        var hadEdge = _edges.TryGetValue(edge, out var previousAuthority);

        var observerPosition = observer.ValheimMotionPosition;
        var subjectPosition = subject.ValheimMotionPosition;
        var subjectAuthority = subject.ValheimCharacterAuthority;
        var sameRegion =
            observer.RegionId != null &&
            string.Equals(observer.RegionId, subject.RegionId, StringComparison.Ordinal);

        var withinRadius = false;
        if (sameRegion &&
            observerPosition is { } op &&
            subjectPosition is { } sp &&
            subjectAuthority is { })
        {
            var dx = op.X - sp.X;
            var dz = op.Z - sp.Z;
            var radiusSquared = hadEdge ? _leaveRadiusSquared : _enterRadiusSquared;
            withinRadius = dx * dx + dz * dz <= radiusSquared;
        }

        if (hadEdge &&
            (!withinRadius ||
             subjectAuthority is not { } current ||
             current.Generation != previousAuthority.Generation ||
             current.ZdoUserId != previousAuthority.ZdoUserId ||
             current.ZdoId != previousAuthority.ZdoId))
        {
            tombstones.Add(new TombstoneDelivery(
                observer,
                subject.ValheimLogicalPeerId,
                previousAuthority,
                withinRadius ? "character_generation_changed" : "interest_left"));
            _edges.Remove(edge);
            hadEdge = false;
        }

        if (!hadEdge &&
            withinRadius &&
            subjectAuthority is { } authority &&
            subjectPosition is { } position)
        {
            _edges[edge] = authority;
            descriptors.Add(new DescriptorDelivery(observer, subject, authority, position));
            return true;
        }

        return hadEdge;
    }

    private static bool IsPlayerCandidate(GameSession source, GameSession target) =>
        target.SessionId != source.SessionId &&
        string.Equals(source.ValheimRole, "client", StringComparison.Ordinal) &&
        string.Equals(target.ValheimRole, "client", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(source.ValheimLogicalPeerId) &&
        !string.IsNullOrWhiteSpace(target.ValheimLogicalPeerId) &&
        !string.Equals(
            source.ValheimLogicalPeerId,
            target.ValheimLogicalPeerId,
            StringComparison.Ordinal);

    private async Task DeliverAsync(
        IReadOnlyCollection<DescriptorDelivery> descriptors,
        IReadOnlyCollection<TombstoneDelivery> tombstones)
    {
        foreach (var tombstone in tombstones)
        {
            var queued = await tombstone.Observer.SendReliableAsync(
                MessageType.ValheimRemotePlayerTombstone,
                new
                {
                    logical_peer_id = tombstone.SubjectLogicalPeerId,
                    zdo_user_id = tombstone.Authority.ZdoUserId,
                    zdo_id = tombstone.Authority.ZdoId,
                    generation = tombstone.Authority.Generation,
                    reason = tombstone.Reason,
                },
                CancellationToken.None);
            if (!queued.Queued)
                _logger.LogWarning(
                    "Valheim Player tombstone queue failed observer={Observer} subject={Subject} reason={Reason}",
                    tombstone.Observer.ValheimLogicalPeerId,
                    tombstone.SubjectLogicalPeerId,
                    queued.Reason);
        }

        foreach (var descriptor in descriptors)
        {
            var queued = await descriptor.Observer.SendReliableAsync(
                MessageType.ValheimRemotePlayerDescriptor,
                new
                {
                    logical_peer_id = descriptor.Subject.ValheimLogicalPeerId,
                    peer_uid = descriptor.Subject.ValheimPeerUid,
                    character = descriptor.Subject.ValheimCharacter,
                    zdo_user_id = descriptor.Authority.ZdoUserId,
                    zdo_id = descriptor.Authority.ZdoId,
                    generation = descriptor.Authority.Generation,
                    x = descriptor.Position.X,
                    y = descriptor.Position.Y,
                    z = descriptor.Position.Z,
                },
                CancellationToken.None);
            if (!queued.Queued)
                _logger.LogWarning(
                    "Valheim Player descriptor queue failed observer={Observer} subject={Subject} reason={Reason}",
                    descriptor.Observer.ValheimLogicalPeerId,
                    descriptor.Subject.ValheimLogicalPeerId,
                    queued.Reason);
        }
    }

    private static double ReadPositiveDouble(string? raw, double fallback) =>
        double.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value) &&
        value > 0
            ? value
            : fallback;
}
