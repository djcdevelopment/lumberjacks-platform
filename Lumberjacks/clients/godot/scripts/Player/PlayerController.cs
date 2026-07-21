using Godot;
using System;
using CommunitySurvival.Networking;
using CommunitySurvival.Core;

namespace CommunitySurvival.Player;

/// <summary>
/// Script for capturing local player input and sending it to the server.
/// Throttled to 20Hz to match the server tick rate.
/// </summary>
public partial class PlayerController : Node
{
    private SimulationClient _network;
    private ushort _inputSeq = 0;
    
    private double _timeSinceLastInput = 0;
    private const double InputInterval = 0.05; // 20Hz

    public override void _Ready()
    {
        _network = GetNode<SimulationClient>("/root/SimulationClient");
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        _timeSinceLastInput += delta;
        if (_timeSinceLastInput < InputInterval) return;

        _timeSinceLastInput = 0;
        SendInput();
    }

    private void SendInput()
    {
        Vector2 inputDir = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        
        byte directionByte = 255;
        byte speedPercent = 0;
        
        if (inputDir.Length() > 0.1f)
        {
            speedPercent = 100;
            // Get angle in radians (0 = North/-Z in Godot screen space conversion? 
            // Wait, CoordinateMapper handles rotation.y. 
            // inputDir.y is -1 for forward (W). atan2(x, -y) gives 0 for North.
            float angle = Mathf.Atan2(inputDir.X, -inputDir.Y); 
            directionByte = CoordinateMapper.GodotRotationToServerByte(angle);
        }

        _inputSeq++;
        _ = _network.SendPlayerInput(directionByte, speedPercent, 0, _inputSeq);
    }
}
