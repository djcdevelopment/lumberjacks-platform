using Godot;
using CommunitySurvival.Networking;
using CommunitySurvival.Core;
using CommunitySurvival.Entities;

namespace CommunitySurvival.Player;

/// <summary>
/// Local player input. WASD sends binary PlayerInput at 20Hz.
/// F1 toggles debug fly mode. Space = up, Ctrl = down.
/// </summary>
public partial class PlayerController : Node
{
    private SimulationClient _net;
    private GameState _state;
    private ushort _seq;
    private double _timer;
    private double _debugTimer;
    private const double Interval = 0.05;
    private const float FlySpeed = 30f;

    public bool DebugFly { get; private set; }
    private float _flyY;

    public override void _Ready()
    {
        _net = GetNode<SimulationClient>("/root/SimulationClient");
        _state = GetNode<GameState>("/root/GameState");

        var parent = GetParent<Node3D>();
        if (parent != null && _state.HasTerrain)
        {
            float terrainY = TerrainGenerator.GetAltitudeAt(
                _state.AltitudeGrid, _state.GridWidth, _state.GridHeight,
                parent.Position.X, parent.Position.Z);
            GD.Print($"PlayerController: pos={parent.Position}, terrainY={terrainY}, global={parent.GlobalPosition}");
        }
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.F1)
        {
            DebugFly = !DebugFly;
            var parent = GetParent<Node3D>();
            if (DebugFly)
            {
                _flyY = parent.Position.Y;
            }
            else if (_state.HasTerrain)
            {
                float terrainY = TerrainGenerator.GetAltitudeAt(
                    _state.AltitudeGrid, _state.GridWidth, _state.GridHeight,
                    parent.Position.X, parent.Position.Z);
                parent.Position = new Vector3(parent.Position.X, terrainY, parent.Position.Z);
                if (parent is RemoteEntity re)
                    re.ForcePosition(parent.Position);
            }
            if (parent is RemoteEntity re2)
                re2.DebugOverrideY = DebugFly;
            GD.Print($"Debug fly: {(DebugFly ? "ON" : "OFF")} pos={parent.Position}");
        }
    }

    public override void _Process(double delta)
    {
        var parent = GetParent<Node3D>();

        if (DebugFly && parent != null)
        {
            if (Input.IsKeyPressed(Key.Space)) _flyY += FlySpeed * (float)delta;
            if (Input.IsKeyPressed(Key.Ctrl)) _flyY -= FlySpeed * (float)delta;
            parent.Position = new Vector3(parent.Position.X, _flyY, parent.Position.Z);
        }

        // Print position every 2 seconds for debugging
        _debugTimer += delta;
        if (_debugTimer > 2.0 && parent != null)
        {
            _debugTimer = 0;
            var re = parent as RemoteEntity;
            GD.Print($"TICK: pos={parent.Position:F1} target={re?._targetPosition:F1}");
        }

        _timer += delta;
        if (_timer < Interval) return;
        _timer = 0;

        var input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");

        byte dir = 0;
        byte speed = 0;
        byte action = 0;

        if (input.Length() > 0.1f)
        {
            speed = 100;
            float angle = Mathf.Atan2(input.X, -input.Y);
            dir = CoordinateMapper.GodotRotationToServerByte(angle);
        }

        if (Input.IsActionPressed("interact"))
            action |= 0x04;

        _seq++;
        _ = _net.SendPlayerInput(dir, speed, action, _seq);
    }
}
