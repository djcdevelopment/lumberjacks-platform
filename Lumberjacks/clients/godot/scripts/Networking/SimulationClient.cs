using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Contracts.Protocol.Binary;
using Game.Contracts.Protocol;
using CommunitySurvival.Core;

namespace CommunitySurvival.Networking;

/// <summary>
/// C# Autoload for handling all network traffic.
/// Uses a thread-safe message queue — ReceiveLoop enqueues, _Process dequeues and emits signals.
/// </summary>
public partial class SimulationClient : Node
{
    [Signal] public delegate void SessionStartedEventHandler(string sessionId, string playerId, string worldId, string resumeToken);
    [Signal] public delegate void WorldSnapshotReceivedEventHandler(string rawJson);
    [Signal] public delegate void EntityUpdatedEventHandler(string entityId, Vector3 position, Vector3 velocity, float heading, int lastInputSeq, long tick);
    [Signal] public delegate void EntityDataUpdatedEventHandler(string entityId, string entityType, string jsonData, long tick);
    [Signal] public delegate void EntityRemovedEventHandler(string entityId, long tick);
    [Signal] public delegate void ErrorReceivedEventHandler(string code, string message);
    [Signal] public delegate void ConnectedEventHandler();
    [Signal] public delegate void DisconnectedEventHandler();

    private ClientWebSocket _socket;
    private CancellationTokenSource _cts;
    private string _lastUrl;

    // Thread-safe queue: ReceiveLoop writes, _Process reads
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();

    public override void _Ready()
    {
        GD.Print("SimulationClient: Ready");
    }

    public override void _Process(double delta)
    {
        // Drain the queue on the main thread where EmitSignal is safe
        while (_mainThreadQueue.TryDequeue(out var action))
        {
            action();
        }
    }

    public async Task Connect(string url)
    {
        if (_socket?.State == WebSocketState.Open) return;

        _lastUrl = url;
        _socket = new ClientWebSocket();

        // Request binary protocol from server
        if (!url.Contains("protocol=binary"))
        {
            url += url.Contains("?") ? "&protocol=binary" : "?protocol=binary";
        }

        try
        {
            GD.Print($"SimulationClient: Connecting to {url}...");
            await _socket.ConnectAsync(new Uri(url), CancellationToken.None);

            _mainThreadQueue.Enqueue(() => EmitSignal(SignalName.Connected));

            _cts = new CancellationTokenSource();
            _ = ReceiveLoop(_cts.Token);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SimulationClient: Connection failed: {ex.Message}");
            _mainThreadQueue.Enqueue(() => EmitSignal(SignalName.ErrorReceived, "CONN_FAILED", ex.Message));
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[262144]; // 256KB for large snapshots with RegionProfile

        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                // Accumulate multi-frame messages
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closed", ct);
                        goto done;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = ms.ToArray();

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(data);
                    HandleJsonMessage(json);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    HandleBinaryMessage(new ReadOnlySpan<byte>(data));
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                GD.PrintErr($"SimulationClient: Receive error: {ex.Message}");
        }

        done:
        _mainThreadQueue.Enqueue(() => EmitSignal(SignalName.Disconnected));
    }

    private void HandleJsonMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var payload = root.GetProperty("payload");

            switch (type)
            {
                case MessageType.SessionStarted:
                {
                    var sessionId = payload.GetProperty("session_id").GetString();
                    var playerId = payload.GetProperty("player_id").GetString();
                    var worldId = payload.GetProperty("world_id").GetString();
                    var resumeToken = payload.GetProperty("resume_token").GetString();
                    _mainThreadQueue.Enqueue(() =>
                        EmitSignal(SignalName.SessionStarted, sessionId, playerId, worldId, resumeToken));
                    break;
                }

                case MessageType.WorldSnapshot:
                {
                    // Pass the raw payload JSON so GameState can parse entities + region_profile
                    var rawPayload = payload.GetRawText();
                    _mainThreadQueue.Enqueue(() =>
                        EmitSignal(SignalName.WorldSnapshotReceived, rawPayload));
                    break;
                }

                case MessageType.EntityUpdate:
                {
                    var entityId = payload.GetProperty("entity_id").GetString();
                    var entityType = payload.GetProperty("entity_type").GetString();
                    var tick = payload.TryGetProperty("tick", out var tickEl) ? tickEl.GetInt64() : 0L;

                    if (entityType == "player" && payload.TryGetProperty("data", out var data))
                    {
                        // Player updates: extract position/velocity/heading for interpolation
                        var pos = data.GetProperty("position");
                        var vel = data.TryGetProperty("velocity", out var velEl) ? velEl : default;
                        var heading = data.TryGetProperty("heading", out var hEl) ? (float)hEl.GetDouble() : 0f;
                        var lastSeq = data.TryGetProperty("last_input_seq", out var seqEl) ? seqEl.GetInt32() : 0;

                        var position = CoordinateMapper.ServerToGodot(new Game.Contracts.Entities.Vec3(
                            pos.GetProperty("x").GetDouble(),
                            pos.GetProperty("y").GetDouble(),
                            pos.GetProperty("z").GetDouble()));

                        var velocity = vel.ValueKind != JsonValueKind.Undefined
                            ? CoordinateMapper.ServerToGodot(new Game.Contracts.Entities.Vec3(
                                vel.GetProperty("x").GetDouble(),
                                vel.GetProperty("y").GetDouble(),
                                vel.GetProperty("z").GetDouble()))
                            : Vector3.Zero;

                        var headingRad = CoordinateMapper.ServerHeadingToGodot(heading);

                        _mainThreadQueue.Enqueue(() =>
                            EmitSignal(SignalName.EntityUpdated, entityId, position, velocity, headingRad, lastSeq, tick));
                    }
                    else
                    {
                        // Non-player entities (resources, structures): pass raw data JSON
                        var dataJson = payload.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
                        _mainThreadQueue.Enqueue(() =>
                            EmitSignal(SignalName.EntityDataUpdated, entityId, entityType, dataJson, tick));
                    }
                    break;
                }

                case MessageType.EntityRemoved:
                {
                    var entityId = payload.GetProperty("entity_id").GetString();
                    var tick = payload.TryGetProperty("tick", out var tickEl) ? tickEl.GetInt64() : 0L;
                    _mainThreadQueue.Enqueue(() =>
                        EmitSignal(SignalName.EntityRemoved, entityId, tick));
                    break;
                }

                case MessageType.Error:
                {
                    var code = payload.GetProperty("code").GetString();
                    var message = payload.GetProperty("message").GetString();
                    _mainThreadQueue.Enqueue(() =>
                        EmitSignal(SignalName.ErrorReceived, code, message));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SimulationClient: JSON Parse error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void HandleBinaryMessage(ReadOnlySpan<byte> buffer)
    {
        try
        {
            var header = BinaryEnvelope.ReadHeader(buffer);
            var payload = BinaryEnvelope.GetPayload(buffer, header);

            switch (header.Type)
            {
                case MessageTypeId.EntityUpdate:
                    var update = PayloadSerializers.ReadEntityUpdate(payload);
                    // Apply coordinate mapping (negate Z)
                    var position = CoordinateMapper.ServerToGodot(update.Position);
                    var velocity = CoordinateMapper.ServerToGodot(update.Velocity);
                    var headingRad = CoordinateMapper.ServerHeadingToGodot((float)update.Heading);

                    _mainThreadQueue.Enqueue(() =>
                        EmitSignal(SignalName.EntityUpdated,
                            update.EntityId,
                            position,
                            velocity,
                            headingRad,
                            (int)update.LastInputSeq,
                            (long)update.Tick));
                    break;

                case MessageTypeId.EntityRemoved:
                    var removed = PayloadSerializers.ReadEntityRemoved(payload);
                    _mainThreadQueue.Enqueue(() =>
                        EmitSignal(SignalName.EntityRemoved, removed.EntityId, (long)removed.Tick));
                    break;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SimulationClient: Binary Parse error: {ex.Message}");
        }
    }

    public async Task SendMessageJson(string type, object payload)
    {
        if (_socket?.State != WebSocketState.Open) return;

        var envelope = new { type, payload, version = 1 };
        var json = JsonSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task SendPlayerInput(byte direction, byte speedPercent, byte actionFlags, ushort inputSeq)
    {
        if (_socket?.State != WebSocketState.Open) return;

        Span<byte> payloadBuf = stackalloc byte[5];
        PayloadSerializers.WritePlayerInput(payloadBuf, direction, speedPercent, actionFlags, inputSeq);

        Span<byte> frameBuf = stackalloc byte[BinaryEnvelope.HeaderBytes + 5];
        var frameLen = BinaryEnvelope.Write(
            frameBuf,
            version: 1,
            MessageTypeId.PlayerInput,
            DeliveryLane.Reliable,
            seq: 0,
            payloadBuf);

        await _socket.SendAsync(new ArraySegment<byte>(frameBuf[..frameLen].ToArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    public async Task Disconnect()
    {
        _cts?.Cancel();
        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
            }
            catch { /* ignore close errors */ }
        }
    }
}
