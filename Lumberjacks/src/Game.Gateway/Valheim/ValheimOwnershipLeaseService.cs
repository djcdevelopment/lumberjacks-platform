namespace Game.Gateway.Valheim;

/// <summary>
/// Process-scoped ownership leases for the Valheim adapter. A Gateway restart
/// intentionally drops every lease, so mutations fail closed until the authoritative
/// server issues a new epoch.
/// </summary>
public sealed class ValheimOwnershipLeaseService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    public Lease Issue(
        string runId,
        string worldEpoch,
        long uidUser,
        uint uidId,
        string holderLogicalPeerId,
        long holderPeerUid,
        string actionId,
        int durationSeconds,
        DateTimeOffset now)
    {
        if (durationSeconds is < 1 or > 30)
            throw new InvalidDataException("ownership lease duration must be 1..30 seconds");
        var key = Key(worldEpoch, uidUser, uidId);
        lock (_gate)
        {
            var epoch = _leases.TryGetValue(key, out var previous)
                ? checked(previous.Epoch + 1)
                : 1;
            var lease = new Lease(
                runId, worldEpoch, uidUser, uidId, holderLogicalPeerId,
                holderPeerUid, actionId, epoch, now, now.AddSeconds(durationSeconds),
                "active", null);
            _leases[key] = lease;
            return lease;
        }
    }

    public Validation Validate(
        string runId,
        string worldEpoch,
        long uidUser,
        uint uidId,
        string holderLogicalPeerId,
        long epoch,
        DateTimeOffset now)
    {
        var key = Key(worldEpoch, uidUser, uidId);
        lock (_gate)
        {
            if (!_leases.TryGetValue(key, out var lease))
                return new(false, "lease_missing", null);
            if (!string.Equals(lease.RunId, runId, StringComparison.Ordinal))
                return new(false, "run_mismatch", lease);
            if (!string.Equals(
                    lease.HolderLogicalPeerId, holderLogicalPeerId,
                    StringComparison.Ordinal))
                return new(false, "holder_mismatch", lease);
            if (lease.Epoch != epoch)
                return new(false, "epoch_mismatch", lease);
            if (!string.Equals(lease.State, "active", StringComparison.Ordinal))
                return new(false, "lease_" + lease.State, lease);
            if (now >= lease.ExpiresUtc)
            {
                var expired = lease with { State = "expired" };
                _leases[key] = expired;
                return new(false, "lease_expired", expired);
            }
            return new(true, "accepted", lease);
        }
    }

    public Lease? FindByAction(string runId, string actionId)
    {
        lock (_gate)
        {
            return _leases.Values.SingleOrDefault(lease =>
                string.Equals(lease.RunId, runId, StringComparison.Ordinal) &&
                string.Equals(lease.ActionId, actionId, StringComparison.Ordinal));
        }
    }

    public Lease Complete(
        string runId,
        string worldEpoch,
        long uidUser,
        uint uidId,
        string holderLogicalPeerId,
        long epoch,
        DateTimeOffset now)
    {
        var validation = Validate(
            runId, worldEpoch, uidUser, uidId, holderLogicalPeerId, epoch, now);
        if (!validation.Accepted || validation.Lease is null)
            throw new InvalidDataException(
                "ownership result does not match active lease: " + validation.Reason);
        var key = Key(worldEpoch, uidUser, uidId);
        lock (_gate)
        {
            var completed = validation.Lease with
            {
                State = "completed",
                CompletedUtc = now,
            };
            _leases[key] = completed;
            return completed;
        }
    }

    public int ReclaimByLogicalPeer(string logicalPeerId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(logicalPeerId))
            return 0;
        lock (_gate)
        {
            var keys = _leases
                .Where(pair =>
                    string.Equals(
                        pair.Value.HolderLogicalPeerId,
                        logicalPeerId,
                        StringComparison.Ordinal) &&
                    string.Equals(pair.Value.State, "active", StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keys)
                _leases[key] = _leases[key] with
                {
                    State = "reclaimed",
                    CompletedUtc = now,
                };
            return keys.Length;
        }
    }

    static string Key(string worldEpoch, long uidUser, uint uidId) =>
        $"{worldEpoch}:{uidUser}:{uidId}";

    public sealed record Lease(
        string RunId,
        string WorldEpoch,
        long UidUser,
        uint UidId,
        string HolderLogicalPeerId,
        long HolderPeerUid,
        string ActionId,
        long Epoch,
        DateTimeOffset IssuedUtc,
        DateTimeOffset ExpiresUtc,
        string State,
        DateTimeOffset? CompletedUtc);

    public sealed record Validation(bool Accepted, string Reason, Lease? Lease);
}
