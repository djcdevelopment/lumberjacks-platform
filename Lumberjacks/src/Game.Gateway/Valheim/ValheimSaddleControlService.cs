namespace Game.Gateway.Valheim;

/// <summary>
/// Authenticated control state for Valheim's <c>Sadle</c> component. A saddle
/// grant is deliberately separate from a ship helm grant: the requester uses
/// its session/ZDO user identity and vanilla transfers the creature ZDO owner
/// to that rider as part of the grant.
/// </summary>
public sealed class ValheimSaddleControlService
{
    readonly object _gate = new();
    readonly Dictionary<MountKey, MountState> _states = new();

    public Validation Validate(
        string method,
        long senderPeerId,
        long targetPeerId,
        long zdoUserId,
        uint zdoId,
        byte[] parameters,
        DateTimeOffset now)
    {
        if (senderPeerId == 0 || targetPeerId == 0 ||
            zdoUserId == 0 || zdoId == 0)
            return new(false, "saddle_control_identity_invalid");

        var key = new MountKey(zdoUserId, zdoId);
        lock (_gate)
        {
            PrunePending(now);
            _states.TryGetValue(key, out var state);
            switch (method)
            {
                case "RequestControl":
                    if (senderPeerId == targetPeerId)
                        return new(false, "saddle_control_request_not_remote");
                    if (!ExactSessionIdentity(parameters, senderPeerId))
                        return new(false, "saddle_control_request_identity_invalid");
                    if (state is { PendingRequesterPeerId: not 0 } &&
                        (state.PendingRequesterPeerId != senderPeerId ||
                         state.PendingOwnerPeerId != targetPeerId))
                        return new(false, "saddle_control_request_contended");
                    if (state is not null && !ObservedOwner(state, targetPeerId))
                        return new(false, "saddle_control_request_stale_owner");

                    // A previous local rider release does not cross RouteRPC:
                    // the rider still owns the mount and ReleaseControl
                    // self-dispatches. Observing that peer as the next request
                    // target confirms the owner without trusting the requester
                    // to assert that the prior control is free. Only the owner
                    // may answer, and vanilla performs the HaveValidUser check.
                    _states[key] = new MountState(
                        OwnerPeerId: targetPeerId,
                        ControllerPeerId: state?.ControllerPeerId ?? 0,
                        TransferTargetPeerId: 0,
                        PendingRequesterPeerId: senderPeerId,
                        PendingOwnerPeerId: targetPeerId,
                        UpdatedAt: now);
                    return new(true, "saddle_control_pending");

                case "RequestRespons":
                    if (parameters.Length != 1 || parameters[0] > 1)
                        return new(false, "saddle_control_response_invalid");
                    if (state is null ||
                        state.PendingOwnerPeerId != senderPeerId ||
                        state.PendingRequesterPeerId != targetPeerId)
                        return new(false, "saddle_control_response_not_pending");
                    if (parameters[0] == 0)
                    {
                        _states[key] = state with
                        {
                            PendingRequesterPeerId = 0,
                            PendingOwnerPeerId = 0,
                            UpdatedAt = now,
                        };
                        return new(true, "saddle_control_denied");
                    }
                    _states[key] = state with
                    {
                        ControllerPeerId = targetPeerId,
                        TransferTargetPeerId = targetPeerId,
                        PendingRequesterPeerId = 0,
                        PendingOwnerPeerId = 0,
                        UpdatedAt = now,
                    };
                    return new(true, "saddle_control_granted");

                case "ReleaseControl":
                    if (!ExactSessionIdentity(parameters, senderPeerId) ||
                        state is null || state.ControllerPeerId != senderPeerId ||
                        !ObservedOwner(state, targetPeerId))
                        return new(false, "saddle_control_release_not_holder");
                    _states[key] = state with
                    {
                        OwnerPeerId = targetPeerId,
                        ControllerPeerId = 0,
                        TransferTargetPeerId = 0,
                        PendingRequesterPeerId = 0,
                        PendingOwnerPeerId = 0,
                        UpdatedAt = now,
                    };
                    return new(true, "saddle_control_released");

                case "Controls":
                    if (state is null || state.ControllerPeerId != senderPeerId ||
                        !ObservedOwner(state, targetPeerId))
                        return new(false, "saddle_control_input_not_rider");
                    if (!ValidControls(parameters))
                        return new(false, "saddle_control_input_invalid");
                    _states[key] = state with { UpdatedAt = now };
                    return new(true, "saddle_control_input_accepted");

                case "RemoveSaddle":
                    if (!ValidPoint(parameters))
                        return new(false, "saddle_remove_point_invalid");
                    if (state is { PendingRequesterPeerId: not 0 } ||
                        state is { ControllerPeerId: not 0 } ||
                        (state is not null && !ObservedOwner(state, targetPeerId)))
                        return new(false, "saddle_remove_control_active_or_owner_stale");
                    return new(true, "saddle_remove_authenticated");

                default:
                    return new(false, "saddle_control_method_invalid");
            }
        }
    }

    public int ReclaimByPeer(long peerId)
    {
        if (peerId == 0) return 0;
        lock (_gate)
        {
            var matches = _states
                .Where(pair =>
                    pair.Value.OwnerPeerId == peerId ||
                    pair.Value.ControllerPeerId == peerId ||
                    pair.Value.TransferTargetPeerId == peerId ||
                    pair.Value.PendingRequesterPeerId == peerId ||
                    pair.Value.PendingOwnerPeerId == peerId)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in matches) _states.Remove(key);
            return matches.Length;
        }
    }

    static bool ObservedOwner(MountState state, long peerId) =>
        state.OwnerPeerId == peerId || state.TransferTargetPeerId == peerId;

    static bool ExactSessionIdentity(byte[] parameters, long senderPeerId) =>
        parameters.Length == sizeof(long) &&
        BitConverter.ToInt64(parameters, 0) == senderPeerId;

    static bool ValidControls(byte[] parameters)
    {
        if (parameters.Length != 20) return false;
        var x = BitConverter.ToSingle(parameters, 0);
        var y = BitConverter.ToSingle(parameters, 4);
        var z = BitConverter.ToSingle(parameters, 8);
        var speed = BitConverter.ToInt32(parameters, 12);
        var skill = BitConverter.ToSingle(parameters, 16);
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(skill) ||
            speed is < 0 or > 4 || skill is < 0f or > 1f)
            return false;
        var magnitudeSquared = x * x + y * y + z * z;
        return magnitudeSquared <= 1.01f;
    }

    static bool ValidPoint(byte[] parameters) =>
        parameters.Length == 12 &&
        Finite(BitConverter.ToSingle(parameters, 0)) &&
        Finite(BitConverter.ToSingle(parameters, 4)) &&
        Finite(BitConverter.ToSingle(parameters, 8));

    static bool Finite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    void PrunePending(DateTimeOffset now)
    {
        foreach (var key in _states
                     .Where(pair =>
                         pair.Value.PendingRequesterPeerId != 0 &&
                         now - pair.Value.UpdatedAt > TimeSpan.FromSeconds(15))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            var state = _states[key];
            _states[key] = state with
            {
                PendingRequesterPeerId = 0,
                PendingOwnerPeerId = 0,
                UpdatedAt = now,
            };
        }
    }

    public sealed record Validation(bool Accepted, string Reason);
    readonly record struct MountKey(long UserId, uint Id);
    sealed record MountState(
        long OwnerPeerId,
        long ControllerPeerId,
        long TransferTargetPeerId,
        long PendingRequesterPeerId,
        long PendingOwnerPeerId,
        DateTimeOffset UpdatedAt);
}
