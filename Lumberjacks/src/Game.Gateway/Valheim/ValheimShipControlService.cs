namespace Game.Gateway.Valheim;

/// <summary>
/// Authenticated, held helm authority for a Ship ZDO. This is deliberately not
/// the one-shot pickup lease: a successful owner response opens a continuous
/// control stream which remains bound to the requesting peer until release or
/// disconnect.
/// </summary>
public sealed class ValheimShipControlService
{
    readonly object _gate = new();
    readonly Dictionary<ShipKey, HelmState> _states = new();

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
            return new(false, "ship_control_identity_invalid");

        var key = new ShipKey(zdoUserId, zdoId);
        lock (_gate)
        {
            PrunePending(now);
            _states.TryGetValue(key, out var state);
            switch (method)
            {
                case "RequestControl":
                    if (senderPeerId == targetPeerId)
                        return new(false, "ship_control_request_not_remote");
                    if (state is { Held: true } &&
                        state.ControllerPeerId != senderPeerId)
                        return new(false, "ship_control_already_held");
                    _states[key] = new HelmState(
                        senderPeerId,
                        targetPeerId,
                        Held: false,
                        UpdatedAt: now);
                    return new(true, "ship_control_pending");

                case "RequestRespons":
                    if (parameters.Length != 1 || parameters[0] > 1)
                        return new(false, "ship_control_response_invalid");
                    if (state is null || state.Held ||
                        state.OwnerPeerId != senderPeerId ||
                        state.ControllerPeerId != targetPeerId)
                        return new(false, "ship_control_response_not_pending");
                    if (parameters[0] == 0)
                    {
                        _states.Remove(key);
                        return new(true, "ship_control_denied");
                    }
                    _states[key] = state with { Held = true, UpdatedAt = now };
                    return new(true, "ship_control_granted");

                case "ReleaseControl":
                    if (!HeldBy(state, senderPeerId, targetPeerId))
                        return new(false, "ship_control_release_not_holder");
                    _states.Remove(key);
                    return new(true, "ship_control_released");

                case "Stop":
                case "Forward":
                case "Backward":
                case "Rudder":
                    if (!HeldBy(state, senderPeerId, targetPeerId))
                        return new(false, "ship_control_input_not_holder");
                    _states[key] = state! with { UpdatedAt = now };
                    return new(true, "ship_control_input_accepted");

                default:
                    return new(false, "ship_control_method_invalid");
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
                    pair.Value.ControllerPeerId == peerId ||
                    pair.Value.OwnerPeerId == peerId)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in matches) _states.Remove(key);
            return matches.Length;
        }
    }

    static bool HeldBy(HelmState? state, long controller, long owner) =>
        state is { Held: true } &&
        state.ControllerPeerId == controller &&
        state.OwnerPeerId == owner;

    void PrunePending(DateTimeOffset now)
    {
        var expired = _states
            .Where(pair =>
                !pair.Value.Held && now - pair.Value.UpdatedAt >
                    TimeSpan.FromSeconds(15))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired) _states.Remove(key);
    }

    public sealed record Validation(bool Accepted, string Reason);
    readonly record struct ShipKey(long UserId, uint Id);
    sealed record HelmState(
        long ControllerPeerId,
        long OwnerPeerId,
        bool Held,
        DateTimeOffset UpdatedAt);
}
