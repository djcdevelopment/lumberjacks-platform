using Godot;
using System;

namespace CommunitySurvival.UI;

/// <summary>
/// Handles the initial connection UI. 
/// Signals Main.cs to connect.
/// </summary>
public partial class ConnectScreen : Control
{
    [Signal] public delegate void ConnectRequestedEventHandler(string url);

    private LineEdit _urlInput;
    private Button _connectButton;

    public override void _Ready()
    {
        _urlInput = GetNode<LineEdit>("VBoxContainer/URLInput");
        _connectButton = GetNode<Button>("VBoxContainer/ConnectButton");

        _connectButton.Pressed += OnConnectPressed;
        
        // Default URL
        _urlInput.Text = "ws://localhost:4000";
    }

    private void OnConnectPressed()
    {
        string url = _urlInput.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "ws://localhost:4000";
        
        GD.Print($"ConnectScreen: Connect requested to {url}");
        EmitSignal(SignalName.ConnectRequested, url);
    }
}
