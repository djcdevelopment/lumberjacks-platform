using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunitySurvival.Core;

namespace CommunitySurvival.Replay;

/// <summary>
/// Slice 0 replay driver. Loads a PullReplay JSON file (schema v1), validates
/// it strictly, and feeds GameState's signal layer at the replay's frameStepMs
/// cadence. Bypasses SimulationClient — replay is its own deterministic event
/// source, not a live network.
///
/// Validation is loud-failure: any schema/identity violation throws and
/// surfaces via ReplayFailed signal + GD.PrintErr + the debug HUD. No silent
/// fallback to defaults.
/// </summary>
public partial class ReplayLoader : Node
{
    [Export] public string ReplayPath = "";
    [Export] public float YardToMeter = 1.0f;

    private const string ExpectedSchemaVersion = "v1";
    private static readonly Regex UuidRegex = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    private GameState _gameState;
    private ReplayData _replay;
    private Timer _tickTimer;
    private int _frameIndex = 0;
    private bool _isPaused = false;
    private float _speedMultiplier = 1.0f;

    // Public read-only accessors for DebugHud + ScrubberHud.
    public string PullId => _replay?.PullId;
    public string SchemaVersion => _replay?.SchemaVersion;
    public string BossName => _replay?.BossName;
    public int CurrentTimeMs => _frameIndex * (_replay?.FrameStepMs ?? 0);
    public int DurationMs => Math.Max(0, ((_replay?.Frames?.Count ?? 1) - 1) * (_replay?.FrameStepMs ?? 0));
    public bool LoadFailed { get; private set; }
    public string LoadError { get; private set; }
    public bool IsPaused => _isPaused;
    public float SpeedMultiplier => _speedMultiplier;

    [Signal] public delegate void ReplayLoadedEventHandler();
    [Signal] public delegate void ReplayFailedEventHandler(string error);

    public override void _Ready()
    {
        _gameState = GetNode<GameState>("/root/GameState");

        try
        {
            _replay = LoadAndValidate(ReplayPath);
            GD.Print($"ReplayLoader: loaded {_replay.PullId} ({_replay.Entities.Count} entities, {_replay.Frames.Count} frames, boss '{_replay.BossName}')");

            // Position the scene's Camera3D to frame this arena.
            FrameCameraToArena();
            HideLegacyHud();

            SpawnEntities();
            StartPlayback();
            EmitSignal(SignalName.ReplayLoaded);
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            LoadError = ex.Message;
            GD.PrintErr($"ReplayLoader: FAILED — {ex.Message}");
            EmitSignal(SignalName.ReplayFailed, ex.Message);
        }
    }

    public override void _ExitTree()
    {
        if (_replay?.Entities != null && _gameState != null)
        {
            foreach (var e in _replay.Entities)
            {
                _gameState.IngestReplayRemove(e.EntityId);
            }
        }
    }

    // ---- validation -------------------------------------------------------

    private static ReplayData LoadAndValidate(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new Exception("ReplayPath is empty — set it on the ReplayLoader node");
        if (!File.Exists(path))
            throw new Exception($"file not found: {path}");

        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { throw new Exception($"read failed: {ex.Message}"); }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (Exception ex) { throw new Exception($"JSON parse failed: {ex.Message}"); }

        var root = doc.RootElement;

        // schemaVersion
        if (!root.TryGetProperty("schemaVersion", out var svEl))
            throw new Exception("missing schemaVersion field");
        var sv = svEl.GetString();
        if (sv != ExpectedSchemaVersion)
            throw new Exception($"schemaVersion mismatch — got '{sv}', expected '{ExpectedSchemaVersion}'");

        // pullId (UUID)
        if (!root.TryGetProperty("pullId", out var pidEl))
            throw new Exception("missing pullId field");
        var pullId = pidEl.GetString() ?? "";
        if (!UuidRegex.IsMatch(pullId))
            throw new Exception($"pullId is not a valid UUID: '{pullId}'");

        // frameStepMs
        if (!root.TryGetProperty("frameStepMs", out var fsmEl))
            throw new Exception("missing frameStepMs field");
        var frameStepMs = fsmEl.GetInt32();
        if (frameStepMs <= 0)
            throw new Exception($"frameStepMs must be > 0, got {frameStepMs}");

        // arenaYd
        if (!root.TryGetProperty("arenaYd", out var arenaEl))
            throw new Exception("missing arenaYd field");
        var arenaWidth = arenaEl.GetProperty("width").GetDouble();
        var arenaHeight = arenaEl.GetProperty("height").GetDouble();
        if (arenaWidth <= 0 || arenaHeight <= 0)
            throw new Exception($"arenaYd dimensions must be > 0 — got {arenaWidth}x{arenaHeight}");

        // bossName (required, may be null)
        if (!root.TryGetProperty("bossName", out var bnEl))
            throw new Exception("missing bossName field");
        var bossName = bnEl.ValueKind == JsonValueKind.Null ? null : bnEl.GetString();

        // entities
        if (!root.TryGetProperty("entities", out var entsEl))
            throw new Exception("missing entities field");
        var entities = new List<ReplayEntity>();
        foreach (var entEl in entsEl.EnumerateArray())
        {
            var entity = new ReplayEntity
            {
                EntityId = entEl.GetProperty("entityId").GetString(),
                Kind = entEl.GetProperty("kind").GetString(),
                DisplayName = entEl.TryGetProperty("displayName", out var dnEl) && dnEl.ValueKind != JsonValueKind.Null
                    ? dnEl.GetString() : null,
                Class = entEl.TryGetProperty("class", out var clEl) && clEl.ValueKind != JsonValueKind.Null
                    ? clEl.GetString() : null,
                Role = entEl.TryGetProperty("role", out var rlEl) && rlEl.ValueKind != JsonValueKind.Null
                    ? rlEl.GetString() : null,
            };
            // Class enum check: Player entities with non-null class must be in
            // the canonical 13 (corruption signal). null class is legitimate
            // (participant lookup miss) — falls back to role color.
            if (entity.Kind == "Player" && entity.Class != null &&
                !WowClassColors.KnownClasses.Contains(entity.Class))
            {
                throw new Exception($"entity {entity.EntityId} has unknown class '{entity.Class}' (not in canonical 13)");
            }
            entities.Add(entity);
        }
        if (entities.Count == 0)
            throw new Exception("entities array is empty");

        // frames
        if (!root.TryGetProperty("frames", out var framesEl))
            throw new Exception("missing frames field");
        var frames = new List<ReplayFrame>();
        long prevT = -1;
        int expectedPosLen = 2 * entities.Count;
        foreach (var fEl in framesEl.EnumerateArray())
        {
            var t = fEl.GetProperty("t").GetInt64();
            if (t < 0)
                throw new Exception($"frame.t < 0: {t}");
            if (t % frameStepMs != 0)
                throw new Exception($"frame.t={t} not divisible by frameStepMs={frameStepMs}");
            if (prevT >= 0 && t <= prevT)
                throw new Exception($"frames not monotonic: t={t} <= prevT={prevT}");
            prevT = t;

            var posEl = fEl.GetProperty("entityPositions");
            if (posEl.GetArrayLength() != expectedPosLen)
                throw new Exception($"frame.t={t} entityPositions.length={posEl.GetArrayLength()}, expected {expectedPosLen}");

            var positions = new float[expectedPosLen];
            int i = 0;
            foreach (var v in posEl.EnumerateArray())
            {
                var d = v.GetDouble();
                if (d < 0 || d > 1)
                    throw new Exception($"frame.t={t} position[{i}]={d} outside [0,1]");
                positions[i++] = (float)d;
            }

            frames.Add(new ReplayFrame { T = t, EntityPositions = positions });
        }
        if (frames.Count == 0)
            throw new Exception("frames array is empty");
        if (frames[0].T != 0)
            throw new Exception($"frames[0].t={frames[0].T}, expected 0");

        return new ReplayData
        {
            SchemaVersion = sv,
            PullId = pullId,
            FrameStepMs = frameStepMs,
            ArenaWidth = (float)arenaWidth,
            ArenaHeight = (float)arenaHeight,
            BossName = bossName,
            Entities = entities,
            Frames = frames,
        };
    }

    // ---- spawning / playback ---------------------------------------------

    private void FrameCameraToArena()
    {
        // Replay scene parents the Camera3D as a sibling. Frame the arena
        // proportionally so different-sized pulls auto-fit.
        var parent = GetParent();
        if (parent == null || !parent.HasNode("Camera3D")) return;
        var camera = parent.GetNode<Camera3D>("Camera3D");
        // Frame the arena with a small margin: at FOV 50°, view spans roughly
        // 2*D*tan(25°) ≈ 0.93*D at the focal plane. Camera at (0, diag, 0.7*diag)
        // sits at distance ~1.22*diag, covering ~1.13*diag — fits any arena up
        // to ~89% of the diag distance with margin.
        var diag = MathF.Max(_replay.ArenaWidth, _replay.ArenaHeight) * YardToMeter;
        camera.Position = new Vector3(0f, diag * 1.0f, diag * 0.7f);
        camera.LookAt(Vector3.Zero, Vector3.Up);
    }

    private void SpawnEntities()
    {
        for (int i = 0; i < _replay.Entities.Count; i++)
        {
            var e = _replay.Entities[i];
            var color = e.Kind switch
            {
                "Boss" => WowClassColors.Boss,
                "Add" => WowClassColors.Add,
                _ => WowClassColors.For(e.Class, e.Role),
            };
            var pos = NormalizedToWorld(
                _replay.Frames[0].EntityPositions[i * 2],
                _replay.Frames[0].EntityPositions[i * 2 + 1]);

            var metadata = new Godot.Collections.Dictionary
            {
                { "class_color", color },
                { "name", e.DisplayName ?? e.EntityId },
                { "kind", e.Kind },
                { "wow_class", e.Class ?? "" },
                { "wow_role", e.Role ?? "" },
            };

            // Type "player" routes through World.cs's player-scene branch,
            // where the class_color metadata triggers the replay path.
            _gameState.IngestReplayEntity(e.EntityId, "player", pos, 0f, metadata);
        }
    }

    private void StartPlayback()
    {
        _tickTimer = new Timer
        {
            WaitTime = (_replay.FrameStepMs / 1000.0) / _speedMultiplier,
            Autostart = false,
            OneShot = false,
        };
        AddChild(_tickTimer);
        _tickTimer.Timeout += OnTick;
        _tickTimer.Start();
        _frameIndex = 0;
    }

    private void OnTick()
    {
        _frameIndex++;
        if (_frameIndex >= _replay.Frames.Count)
        {
            _tickTimer.Stop();
            return;
        }

        var frame = _replay.Frames[_frameIndex];
        for (int i = 0; i < _replay.Entities.Count; i++)
        {
            var pos = NormalizedToWorld(frame.EntityPositions[i * 2], frame.EntityPositions[i * 2 + 1]);
            _gameState.IngestReplayPosition(_replay.Entities[i].EntityId, pos, frame.T);
        }
    }

    private Vector3 NormalizedToWorld(float nx, float ny)
    {
        // Schema: (0,0)=bbox-min corner, (1,1)=opposite corner. Centering
        // around origin so the camera framing math is symmetric.
        var x = (nx - 0.5f) * _replay.ArenaWidth * YardToMeter;
        var z = (ny - 0.5f) * _replay.ArenaHeight * YardToMeter;
        return new Vector3(x, 0f, z);
    }

    // ---- playback control (slice 1) ---------------------------------------

    public void TogglePaused() => SetPaused(!_isPaused);

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        if (_tickTimer == null) return;
        if (paused) _tickTimer.Stop();
        else _tickTimer.Start();
    }

    public void SetSpeed(float multiplier)
    {
        if (multiplier <= 0) return;
        _speedMultiplier = multiplier;
        if (_tickTimer != null && _replay != null)
        {
            _tickTimer.WaitTime = (_replay.FrameStepMs / 1000.0) / _speedMultiplier;
        }
    }

    public void SeekToMs(int ms)
    {
        if (_replay == null) return;
        var newIndex = Mathf.Clamp(ms / _replay.FrameStepMs, 0, _replay.Frames.Count - 1);
        _frameIndex = newIndex;
        var frame = _replay.Frames[_frameIndex];
        for (int i = 0; i < _replay.Entities.Count; i++)
        {
            var pos = NormalizedToWorld(frame.EntityPositions[i * 2], frame.EntityPositions[i * 2 + 1]);
            _gameState.IngestReplayPosition(_replay.Entities[i].EntityId, pos, frame.T);
        }
    }

    // ---- legacy HUD hide (slice 0.5 cleanup) ------------------------------

    private void HideLegacyHud()
    {
        // world.tscn carries a CanvasLayer named "HUD" with a "disconnected"
        // status dot and "Players: N | Tick: 0" labels — useful for live
        // game, noise in replay mode. Hide it once the replay is driving.
        var world = GetParent()?.GetNodeOrNull<Node>("World");
        if (world != null && world.HasNode("HUD"))
        {
            var hud = world.GetNodeOrNull<CanvasLayer>("HUD");
            if (hud != null) hud.Visible = false;
        }
    }

    // ---- internal data ----------------------------------------------------

    private class ReplayData
    {
        public string SchemaVersion;
        public string PullId;
        public int FrameStepMs;
        public float ArenaWidth;
        public float ArenaHeight;
        public string BossName;
        public List<ReplayEntity> Entities;
        public List<ReplayFrame> Frames;
    }

    private class ReplayEntity
    {
        public string EntityId;
        public string Kind;
        public string DisplayName;
        public string Class;
        public string Role;
    }

    private class ReplayFrame
    {
        public long T;
        public float[] EntityPositions;
    }
}
