// synthclient — measuring synthetic-client harness for the Lumberjacks game protocol.
//
// Drives one or more virtual clients through connect -> join_region -> steady player_input,
// measures round-trip time by matching entity_update's last_input_seq back to input send
// timestamps, tracks input loss, and records which transport channel delivered each update
// (ws-text / ws-binary / udp). Prints a single JSON summary object to stdout at the end.
//
// Transports (SYNTH_MODE):
//   json   — WebSocket text frames, JSON envelopes.
//   binary — WebSocket binary frames, bit-packed binary envelopes.
//   udp    — binary WS for control (session_started/join/snapshot), player_input sent over a
//            bound UDP datagram endpoint; entity_update returns over UDP.
//
// BCL-only networking: System.Net.WebSockets.ClientWebSocket + System.Net.Sockets.UdpClient.
// References Game.Contracts to reuse the exact wire serializers (BinaryEnvelope / PayloadSerializers).

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using OpenTelemetry;
using OpenTelemetry.Metrics;

// ─────────────────────────────────────────────────────────────────────────────
//  Config (env vars with defaults)
// ─────────────────────────────────────────────────────────────────────────────
static string Env(string key, string dflt) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : dflt;

var target       = Env("SYNTH_TARGET", "ws://localhost:4000/");
var regionLabel  = Env("SYNTH_REGION_LABEL", "local");
var mode         = Env("SYNTH_MODE", "json").ToLowerInvariant();
var regionId     = Env("SYNTH_REGION_ID", "region-spawn");
var rateHz       = double.Parse(Env("SYNTH_RATE_HZ", "10"));
var durationS    = double.Parse(Env("SYNTH_DURATION_S", "30"));
var clientCount  = int.Parse(Env("SYNTH_CLIENTS", "1"));

if (mode is not ("json" or "binary" or "udp"))
{
    Console.Error.WriteLine($"Invalid SYNTH_MODE '{mode}' (expected json|binary|udp)");
    return 2;
}

var isBinaryWire = mode is "binary" or "udp"; // WS handshake uses ?protocol=binary
Console.Error.WriteLine(
    $"synthclient: mode={mode} target={target} region={regionId} label={regionLabel} " +
    $"clients={clientCount} rate={rateHz}Hz duration={durationS}s");

// Grace window: an input counts as delivered if an update acking it (last_input_seq >= seq)
// arrives within 2s of the input being sent.
long graceTicks = (long)(2.0 * Stopwatch.Frequency);

var clients = new List<VirtualClient>();
for (int i = 0; i < clientCount; i++)
    clients.Add(new VirtualClient(i, target, regionId, mode, isBinaryWire, rateHz, durationS, graceTicks));

var sw = Stopwatch.StartNew();
await Task.WhenAll(clients.Select(c => c.RunAsync()));
sw.Stop();

// ─────────────────────────────────────────────────────────────────────────────
//  Aggregate + emit summary
// ─────────────────────────────────────────────────────────────────────────────
long totalSent = 0, totalUpdates = 0, totalLost = 0;
long txWsText = 0, txWsBinary = 0, txUdp = 0;
var allRtt = new List<double>();

foreach (var c in clients)
{
    var (sent, updates, lost, rtt, wsText, wsBin, udp) = c.Collect();
    totalSent    += sent;
    totalUpdates += updates;
    totalLost    += lost;
    txWsText     += wsText;
    txWsBinary   += wsBin;
    txUdp        += udp;
    allRtt.AddRange(rtt);
}

allRtt.Sort();
double Pct(double p)
{
    if (allRtt.Count == 0) return 0.0;
    int idx = (int)Math.Ceiling(p / 100.0 * allRtt.Count) - 1; // nearest-rank
    idx = Math.Clamp(idx, 0, allRtt.Count - 1);
    return allRtt[idx];
}

double lossRate = totalSent == 0 ? 0.0 : (double)totalLost / totalSent;

var summary = new
{
    region_label = regionLabel,
    mode,
    clients = clientCount,
    target,
    inputs_sent = totalSent,
    updates_received = totalUpdates,
    rtt_ms = new
    {
        p50 = Round(Pct(50)),
        p95 = Round(Pct(95)),
        p99 = Round(Pct(99)),
        max = Round(allRtt.Count == 0 ? 0.0 : allRtt[^1]),
        count = allRtt.Count,
    },
    loss_rate = Round(lossRate),
    transport = new { ws_text = txWsText, ws_binary = txWsBinary, udp = txUdp },
    duration_s = Round(sw.Elapsed.TotalSeconds),
};

Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

// ─────────────────────────────────────────────────────────────────────────────
//  Emit computed-percentile GAUGES via OpenTelemetry OTLP.
//
//  A synthetic probe pre-computes its percentiles, so we ship the already-derived
//  p50/p99/loss values as observable gauges (not histograms) tagged with region +
//  transport. Endpoint/protocol come from OTEL_EXPORTER_OTLP_ENDPOINT /
//  OTEL_EXPORTER_OTLP_PROTOCOL (via .AddOtlpExporter()). Through the SUT/fleet Ops
//  Agent OTLP receiver these surface as workload.googleapis.com/synthclient.*.
//  We ForceFlush + Dispose so a short-lived run actually exports before exit.
// ─────────────────────────────────────────────────────────────────────────────
var p50Ms = Round(Pct(50));
var p99Ms = Round(Pct(99));

// Explicit RTT bucket boundaries (ms). OTel's default histogram buckets top out around 10s and
// are useless for millisecond-scale RTT, so we pin sensible boundaries via a metrics View below.
double[] rttBucketBoundaries = { 5, 10, 20, 30, 50, 75, 100, 150, 200, 300, 500 };

using (var meter = new Meter("synthclient"))
{
    var gaugeTags = new KeyValuePair<string, object?>[]
    {
        new("region", regionLabel),
        new("transport", mode),
    };

    meter.CreateObservableGauge(
        "synthclient.rtt_p50_ms", () => new Measurement<double>(p50Ms, gaugeTags), unit: "ms");
    meter.CreateObservableGauge(
        "synthclient.rtt_p99_ms", () => new Measurement<double>(p99Ms, gaugeTags), unit: "ms");
    meter.CreateObservableGauge(
        "synthclient.loss_rate", () => new Measurement<double>(lossRate, gaugeTags), unit: "1");

    // Full RTT distribution as a real histogram (complements the pre-computed percentile gauges):
    // every raw RTT sample is recorded here, tagged the same region+transport as the gauges, so a
    // dashboard can derive arbitrary percentiles / heatmaps server-side rather than trusting the
    // probe's own p50/p99.
    var rttHistogram = meter.CreateHistogram<double>(
        "synthclient.rtt_ms", unit: "ms", description: "Per-sample synthetic-client RTT distribution.");

    using var meterProvider = Sdk.CreateMeterProviderBuilder()
        .AddMeter("synthclient")
        // Override the default (seconds-scale) histogram buckets for the RTT instrument only.
        .AddView(
            "synthclient.rtt_ms",
            new ExplicitBucketHistogramConfiguration { Boundaries = rttBucketBoundaries })
        .AddOtlpExporter()
        .Build();

    // Record every RTT sample into the histogram before flushing.
    foreach (var sample in allRtt)
        rttHistogram.Record(sample, gaugeTags);

    // Trigger a collection of the observable gauges + histogram and push them to the exporter,
    // then let the using-dispose flush/shut down the provider cleanly.
    meterProvider?.ForceFlush(5000);
}

return 0;

static double Round(double v) => Math.Round(v, 4);


// ─────────────────────────────────────────────────────────────────────────────
//  Virtual client
// ─────────────────────────────────────────────────────────────────────────────
sealed class VirtualClient
{
    private readonly int _id;
    private readonly string _target;
    private readonly string _regionId;
    private readonly string _mode;
    private readonly bool _binaryWire;
    private readonly double _rateHz;
    private readonly double _durationS;
    private readonly long _graceTicks;

    // Shared state (written by both send + receive loops).
    private readonly object _lock = new();
    private readonly Dictionary<ushort, long> _sentTicks = new();     // seq -> send timestamp
    private readonly List<(ushort seq, long ticks)> _sentList = new(); // ordered sends, for loss calc
    private readonly HashSet<ushort> _rttMatched = new();
    private readonly List<(ushort seq, long ticks)> _acks = new();     // observed (last_input_seq, arrival)
    private readonly List<double> _rttMs = new();

    private long _inputsSent;
    private long _updatesReceived;
    private long _txWsText, _txWsBinary, _txUdp;

    private string _playerId = "";

    public VirtualClient(int id, string target, string regionId, string mode,
        bool binaryWire, double rateHz, double durationS, long graceTicks)
    {
        _id = id; _target = target; _regionId = regionId; _mode = mode;
        _binaryWire = binaryWire; _rateHz = rateHz; _durationS = durationS; _graceTicks = graceTicks;
    }

    public (long sent, long updates, long lost, List<double> rtt, long wsText, long wsBin, long udp) Collect()
    {
        long lost = 0;
        lock (_lock)
        {
            // An input is delivered if some ack with seq >= input.seq arrived within its 2s grace.
            foreach (var (seq, ticks) in _sentList)
            {
                bool acked = false;
                foreach (var (ackSeq, ackTicks) in _acks)
                {
                    if (ackSeq >= seq && ackTicks <= ticks + _graceTicks) { acked = true; break; }
                }
                if (!acked) lost++;
            }
            return (_inputsSent, _updatesReceived, lost, new List<double>(_rttMs),
                _txWsText, _txWsBinary, _txUdp);
        }
    }

    public async Task RunAsync()
    {
        var uri = BuildUri();
        using var ws = new ClientWebSocket();
        UdpClient? udp = null;
        try
        {
            await ws.ConnectAsync(uri, CancellationToken.None);

            // 1. session_started — always JSON text, first message.
            var started = await ReceiveTextEnvelopeAsync(ws, "session_started");
            var payload = started.GetProperty("payload");
            _playerId = payload.GetProperty("player_id").GetString() ?? "";
            var udpToken = payload.TryGetProperty("udp_token", out var tokEl) ? tokEl.GetString() ?? "" : "";
            var udpPort = payload.TryGetProperty("udp_port", out var portEl) ? portEl.GetInt32() : 0;

            // 2. join_region.
            await SendJoinRegionAsync(ws);

            // 3. UDP bind (udp mode only): send an initial player_input datagram so the server
            //    maps our source endpoint to the session and starts delivering over UDP.
            if (_mode == "udp")
            {
                if (udpPort <= 0 || !ulong.TryParse(udpToken, out var tokenValue))
                    throw new InvalidOperationException(
                        $"udp mode requires a numeric udp_token + udp_port>0 (got token='{udpToken}', port={udpPort})");
                _udpTokenValue = tokenValue;
                var host = uri.DnsSafeHost;
                udp = new UdpClient();
                udp.Connect(host, udpPort); // fixes remote endpoint; binds a local source port
            }

            using var stopCts = new CancellationTokenSource();

            // Receive loops run until stopCts fires (after the send phase + drain).
            var wsRecv = Task.Run(() => WsReceiveLoopAsync(ws, stopCts.Token));
            var udpRecv = udp != null ? Task.Run(() => UdpReceiveLoopAsync(udp, stopCts.Token)) : Task.CompletedTask;

            // 4. Steady player_input loop.
            await InputLoopAsync(ws, udp);

            // 5. Drain: keep receiving 2s so late acks/updates land within grace.
            try { await Task.Delay(TimeSpan.FromSeconds(2.2)); } catch { }

            stopCts.Cancel();
            try { await Task.WhenAll(wsRecv, udpRecv); } catch { }

            // Graceful close (server may race us; tolerate any close exception).
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }
        catch (OperationCanceledException) { /* close race — expected */ }
        catch (WebSocketException ex) { Console.Error.WriteLine($"[client {_id}] ws error: {ex.Message}"); }
        catch (Exception ex) { Console.Error.WriteLine($"[client {_id}] error: {ex.Message}"); }
        finally { udp?.Dispose(); }
    }

    private Uri BuildUri()
    {
        var t = _target;
        if (_binaryWire)
            t += (t.Contains('?') ? "&" : "?") + "protocol=binary";
        return new Uri(t);
    }

    // ── Sending ────────────────────────────────────────────────────────────────
    private async Task SendJoinRegionAsync(ClientWebSocket ws)
    {
        if (!_binaryWire)
        {
            var env = EnvelopeFactory.Create(MessageType.JoinRegion, new { region_id = _regionId });
            var bytes = Encoding.UTF8.GetBytes(EnvelopeFactory.Serialize(env));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        else
        {
            // Binary handshake: wrap the JSON payload in a binary envelope (middleware bridges
            // non-hot-path types through JSON). Payload is just the object, not the envelope.
            var payloadJson = Encoding.UTF8.GetBytes($"{{\"region_id\":\"{_regionId}\"}}");
            var frame = new byte[BinaryEnvelope.HeaderBytes + payloadJson.Length];
            var len = BinaryEnvelope.Write(frame, 1, MessageTypeId.JoinRegion, DeliveryLane.Reliable, 0, payloadJson);
            await ws.SendAsync(new ArraySegment<byte>(frame, 0, len), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
    }

    private async Task InputLoopAsync(ClientWebSocket ws, UdpClient? udp)
    {
        var period = TimeSpan.FromSeconds(1.0 / _rateHz);
        var end = Stopwatch.GetTimestamp() + (long)(_durationS * Stopwatch.Frequency);
        var pbuf = new byte[8]; // reused player_input payload scratch buffer
        ushort seq = 0;

        while (Stopwatch.GetTimestamp() < end)
        {
            seq++;
            // Vary direction so the player keeps moving (=> a changed entity => tick echo).
            byte direction = (byte)((seq * 11) & 0xFF);
            const byte speedPercent = 100;
            const byte actionFlags = 0;

            long now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                _sentTicks[seq] = now;
                _sentList.Add((seq, now));
                _inputsSent++;
            }

            try
            {
                if (_mode == "json")
                {
                    var env = EnvelopeFactory.Create(MessageType.PlayerInput, new
                    {
                        direction,
                        speed_percent = speedPercent,
                        action_flags = actionFlags,
                        input_seq = (uint)seq,
                    });
                    var bytes = Encoding.UTF8.GetBytes(EnvelopeFactory.Serialize(env));
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                else
                {
                    // Build a binary player_input frame (5-byte payload + 6-byte header).
                    var plen = PayloadSerializers.WritePlayerInput(pbuf, direction, speedPercent, actionFlags, seq);
                    var frame = new byte[BinaryEnvelope.HeaderBytes + plen];
                    var flen = BinaryEnvelope.Write(frame, 1, MessageTypeId.PlayerInput, DeliveryLane.Datagram, seq, pbuf[..plen]);

                    if (_mode == "binary")
                    {
                        await ws.SendAsync(new ArraySegment<byte>(frame, 0, flen), WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                    else // udp
                    {
                        // Packet = [8-byte udp_token LE][binary envelope]. UdpClient is Connect()ed.
                        var packet = new byte[UdpTokenBytes + flen];
                        BitConverter.TryWriteBytes(packet.AsSpan(0, UdpTokenBytes), _udpTokenValue);
                        Array.Copy(frame, 0, packet, UdpTokenBytes, flen);
                        await udp!.SendAsync(packet, packet.Length);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            var wait = period - TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - now) / (double)Stopwatch.Frequency);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
        }
    }

    // The udp token as a ulong (parsed once in RunAsync for udp mode).
    private ulong _udpTokenValue;
    private const int UdpTokenBytes = 8;

    // ── Receiving ────────────────────────────────────────────────────────────────
    private async Task WsReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16384];
        var msg = new List<byte>(16384);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                msg.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    msg.AddRange(new ArraySegment<byte>(buf, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                    HandleJsonMessage(msg.ToArray(), Channel.WsText);
                else
                    HandleBinaryFrame(msg.ToArray(), Channel.WsBinary);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task UdpReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(ct);
                var data = res.Buffer;
                if (data.Length < UdpTokenBytes + BinaryEnvelope.HeaderBytes) continue;
                // Strip the leading 8-byte udp token, then parse the binary envelope.
                HandleBinaryFrame(data.AsSpan(UdpTokenBytes).ToArray(), Channel.Udp);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private enum Channel { WsText, WsBinary, Udp }

    private void HandleJsonMessage(byte[] bytes, Channel channel)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            if (typeEl.GetString() != MessageType.EntityUpdate) return;
            if (!root.TryGetProperty("payload", out var p)) return;
            if (!p.TryGetProperty("entity_id", out var eidEl)) return;
            if (eidEl.GetString() != _playerId) return;
            if (!p.TryGetProperty("data", out var data)) return;
            if (!data.TryGetProperty("last_input_seq", out var seqEl)) return; // skip join/connect updates

            var lastSeq = (ushort)seqEl.GetUInt32();
            RecordUpdate(lastSeq, channel);
        }
        catch { /* ignore malformed */ }
    }

    private void HandleBinaryFrame(byte[] bytes, Channel channel)
    {
        try
        {
            if (bytes.Length < BinaryEnvelope.HeaderBytes) return;
            var header = BinaryEnvelope.ReadHeader(bytes);
            if (header.Type != MessageTypeId.EntityUpdate) return;
            var payload = BinaryEnvelope.GetPayload(bytes, header);
            var upd = PayloadSerializers.ReadEntityUpdate(payload);
            if (upd.EntityId != _playerId) return;
            RecordUpdate(upd.LastInputSeq, channel);
        }
        catch { /* ignore malformed */ }
    }

    private void RecordUpdate(ushort lastSeq, Channel channel)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _updatesReceived++;
            switch (channel)
            {
                case Channel.WsText: _txWsText++; break;
                case Channel.WsBinary: _txWsBinary++; break;
                case Channel.Udp: _txUdp++; break;
            }

            _acks.Add((lastSeq, now));

            // RTT: match this echoed seq to its send timestamp (first match only).
            if (!_rttMatched.Contains(lastSeq) && _sentTicks.TryGetValue(lastSeq, out var sentTs))
            {
                _rttMatched.Add(lastSeq);
                _rttMs.Add((now - sentTs) * 1000.0 / Stopwatch.Frequency);
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private async Task<JsonElement> ReceiveTextEnvelopeAsync(ClientWebSocket ws, string _)
    {
        var buf = new byte[16384];
        var msg = new List<byte>();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, CancellationToken.None);
            msg.AddRange(new ArraySegment<byte>(buf, 0, result.Count));
        } while (!result.EndOfMessage);

        var doc = JsonDocument.Parse(msg.ToArray());
        return doc.RootElement.Clone();
    }
}
