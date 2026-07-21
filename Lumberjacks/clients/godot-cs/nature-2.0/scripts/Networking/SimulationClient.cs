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
/// WebSocket client autoload. Thread-safe: ReceiveLoop queues, _Process emits.
/// Handles both binary (player updates) and JSON (snapshots, resources) messages.
/// </summary>
public partial class SimulationClient : Node
{
    [Signal] public delegate void SessionStartedEventHandler(string playerId, string resumeToken);
    [Signal] public delegate void WorldSnapshotReceivedEventHandler(string rawJson);
    [Signal] public delegate void EntityUpdatedEventHandler(string entityId, Vector3 position, Vector3 velocity, float heading, int lastInputSeq, long tick);
    [Signal] public delegate void EntityDataUpdatedEventHandler(string entityId, string entityType, string jsonData, long tick);
    [Signal] public delegate void EntityRemovedEventHandler(string entityId, long tick);
    [Signal] public delegate void ErrorReceivedEventHandler(string code, string message);
    [Signal] public delegate void ConnectedEventHandler();
    [Signal] public delegate void DisconnectedEventHandler();

    private ClientWebSocket _socket;
    private CancellationTokenSource _cts;
    private readonly ConcurrentQueue<Action> _queue = new();

    public override void _Process(double delta)
    {
        while (_queue.TryDequeue(out var action)) action();
    }

    public async Task Connect(string url)
    {
        if (_socket?.State == WebSocketState.Open) return;
        _socket = new ClientWebSocket();

        if (!url.Contains("protocol=binary"))
            url += url.Contains("?") ? "&protocol=binary" : "?protocol=binary";

        try
        {
            GD.Print($"SimulationClient: connecting to {url}");
            await _socket.ConnectAsync(new Uri(url), CancellationToken.None);
            _queue.Enqueue(() => EmitSignal(SignalName.Connected));
            _cts = new CancellationTokenSource();
            _ = ReceiveLoop(_cts.Token);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SimulationClient: connect failed: {ex.Message}");
            _queue.Enqueue(() => EmitSignal(SignalName.ErrorReceived, "CONN_FAILED", ex.Message));
        }
    }

    public async Task Disconnect()
    {
        _cts?.Cancel();
        if (_socket?.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    public async Task SendJson(string type, object payload)
    {
        if (_socket?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(new { type, payload, version = 1 });
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task SendPlayerInput(byte direction, byte speed, byte actionFlags, ushort seq)
    {
        if (_socket?.State != WebSocketState.Open) return;
        var pay = new byte[5];
        PayloadSerializers.WritePlayerInput(pay, direction, speed, actionFlags, seq);
        var frame = new byte[BinaryEnvelope.HeaderBytes + 5];
        var len = BinaryEnvelope.Write(frame, 1, MessageTypeId.PlayerInput, DeliveryLane.Reliable, 0, pay);
        await _socket.SendAsync(new ArraySegment<byte>(frame, 0, len), WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[262144];
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close) goto done;
                    ms.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = ms.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                    HandleJson(Encoding.UTF8.GetString(data));
                else
                    HandleBinary(data);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) GD.PrintErr($"SimulationClient: {ex.Message}");
        }
        done:
        _queue.Enqueue(() => EmitSignal(SignalName.Disconnected));
    }

    private void HandleJson(string json)
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
                    var pid = payload.GetProperty("player_id").GetString();
                    var tok = payload.GetProperty("resume_token").GetString();
                    _queue.Enqueue(() => EmitSignal(SignalName.SessionStarted, pid, tok));
                    break;

                case MessageType.WorldSnapshot:
                    var raw = payload.GetRawText();
                    _queue.Enqueue(() => EmitSignal(SignalName.WorldSnapshotReceived, raw));
                    break;

                case MessageType.EntityUpdate:
                    HandleJsonEntityUpdate(payload);
                    break;

                case MessageType.EntityRemoved:
                    var eid = payload.GetProperty("entity_id").GetString();
                    var t = payload.TryGetProperty("tick", out var te) ? te.GetInt64() : 0L;
                    _queue.Enqueue(() => EmitSignal(SignalName.EntityRemoved, eid, t));
                    break;

                case MessageType.Error:
                    var c = payload.GetProperty("code").GetString();
                    var m = payload.GetProperty("message").GetString();
                    _queue.Enqueue(() => EmitSignal(SignalName.ErrorReceived, c, m));
                    break;
            }
        }
        catch (Exception ex) { GD.PrintErr($"SimulationClient JSON error: {ex.Message}"); }
    }

    private void HandleJsonEntityUpdate(JsonElement payload)
    {
        var entityId = payload.GetProperty("entity_id").GetString();
        var entityType = payload.GetProperty("entity_type").GetString();
        var tick = payload.TryGetProperty("tick", out var te) ? te.GetInt64() : 0L;

        if (entityType == "player" && payload.TryGetProperty("data", out var data))
        {
            var pos = data.GetProperty("position");
            var vel = data.TryGetProperty("velocity", out var v) ? v : default;
            var heading = data.TryGetProperty("heading", out var h) ? (float)h.GetDouble() : 0f;
            var seq = data.TryGetProperty("last_input_seq", out var s) ? s.GetInt32() : 0;

            var gPos = CoordinateMapper.ServerToGodot(new Game.Contracts.Entities.Vec3(
                pos.GetProperty("x").GetDouble(), pos.GetProperty("y").GetDouble(), pos.GetProperty("z").GetDouble()));
            var gVel = vel.ValueKind != JsonValueKind.Undefined
                ? CoordinateMapper.ServerToGodot(new Game.Contracts.Entities.Vec3(
                    vel.GetProperty("x").GetDouble(), vel.GetProperty("y").GetDouble(), vel.GetProperty("z").GetDouble()))
                : Vector3.Zero;

            _queue.Enqueue(() => EmitSignal(SignalName.EntityUpdated, entityId, gPos, gVel,
                CoordinateMapper.ServerHeadingToGodot(heading), seq, tick));
        }
        else
        {
            var dj = payload.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
            _queue.Enqueue(() => EmitSignal(SignalName.EntityDataUpdated, entityId, entityType, dj, tick));
        }
    }

    private void HandleBinary(byte[] data)
    {
        try
        {
            var header = BinaryEnvelope.ReadHeader(data);
            var payload = BinaryEnvelope.GetPayload(data, header);

            switch (header.Type)
            {
                case MessageTypeId.EntityUpdate:
                    var u = PayloadSerializers.ReadEntityUpdate(payload);
                    var p = CoordinateMapper.ServerToGodot(u.Position);
                    var v = CoordinateMapper.ServerToGodot(u.Velocity);
                    var h = CoordinateMapper.ServerHeadingToGodot((float)u.Heading);
                    _queue.Enqueue(() => EmitSignal(SignalName.EntityUpdated, u.EntityId, p, v, h, (int)u.LastInputSeq, (long)u.Tick));
                    break;

                case MessageTypeId.EntityRemoved:
                    var r = PayloadSerializers.ReadEntityRemoved(payload);
                    _queue.Enqueue(() => EmitSignal(SignalName.EntityRemoved, r.EntityId, (long)r.Tick));
                    break;
            }
        }
        catch (Exception ex) { GD.PrintErr($"SimulationClient binary error: {ex.Message}"); }
    }
}
