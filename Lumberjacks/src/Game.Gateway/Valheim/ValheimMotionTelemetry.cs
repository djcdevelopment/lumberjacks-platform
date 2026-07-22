namespace Game.Gateway.Valheim;

/// <summary>Small in-memory counters for the alpha Valheim movement relay.</summary>
public sealed class ValheimMotionTelemetry
{
    long _received;
    long _receivedUdp;
    long _receivedWebSocket;
    long _relayedUdp;
    long _relayedWebSocket;
    long _droppedInvalid;
    long _droppedUnauthorized;
    long _droppedStale;

    public void Received(string transport)
    {
        Interlocked.Increment(ref _received);
        if (string.Equals(transport, "udp", StringComparison.Ordinal))
            Interlocked.Increment(ref _receivedUdp);
        else if (string.Equals(transport, "websocket", StringComparison.Ordinal))
            Interlocked.Increment(ref _receivedWebSocket);
    }
    public void RelayedUdp() => Interlocked.Increment(ref _relayedUdp);
    public void RelayedWebSocket() => Interlocked.Increment(ref _relayedWebSocket);
    public void DroppedInvalid() => Interlocked.Increment(ref _droppedInvalid);
    public void DroppedUnauthorized() => Interlocked.Increment(ref _droppedUnauthorized);
    public void DroppedStale() => Interlocked.Increment(ref _droppedStale);

    public object Snapshot() => new
    {
        received = Interlocked.Read(ref _received),
        received_udp = Interlocked.Read(ref _receivedUdp),
        received_websocket = Interlocked.Read(ref _receivedWebSocket),
        relayed_udp = Interlocked.Read(ref _relayedUdp),
        relayed_websocket = Interlocked.Read(ref _relayedWebSocket),
        dropped_invalid = Interlocked.Read(ref _droppedInvalid),
        dropped_unauthorized = Interlocked.Read(ref _droppedUnauthorized),
        dropped_stale = Interlocked.Read(ref _droppedStale),
    };
}
