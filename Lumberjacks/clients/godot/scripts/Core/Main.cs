using Godot;
using System;
using Game.Contracts.Protocol;
using CommunitySurvival.Networking;

namespace CommunitySurvival.Core;

/// <summary>
/// Root node for the application.
/// Switches between ConnectScreen and World based on session state.
/// </summary>
public partial class Main : Node
{
    private Control _connectScreen;
    private Label _statusLabel;
    private Control _reconnectOverlay;
    private Label _reconnectLabel;
    private Button _backButton;
    private PackedScene _worldScene;
    private Node _worldInstance;
    private bool _inWorld = false;

    private SimulationClient _network;
    private GameState _gameState;

    public override void _Ready()
    {
        _network = GetNode<SimulationClient>("/root/SimulationClient");
        _gameState = GetNode<GameState>("/root/GameState");

        _connectScreen = GetNode<Control>("ConnectScreen");
        _statusLabel = GetNode<Label>("ConnectScreen/VBoxContainer/StatusLabel");
        _reconnectOverlay = GetNode<Control>("ReconnectOverlay");
        _reconnectLabel = GetNode<Label>("ReconnectOverlay/VBoxContainer/ReconnectLabel");
        _backButton = GetNode<Button>("ReconnectOverlay/VBoxContainer/BackButton");
        _worldScene = GD.Load<PackedScene>("res://scenes/world.tscn");

        _network.Connected += OnConnected;
        _network.Disconnected += OnDisconnected;
        _network.SessionStarted += OnSessionStarted;
        _network.WorldSnapshotReceived += OnWorldSnapshotReceived;
        _network.ErrorReceived += OnError;

        _backButton.Pressed += OnBackToMenu;

        _reconnectOverlay.Hide();
        _connectScreen.Show();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape && _inWorld)
        {
            OnBackToMenu();
        }
    }

    private void OnConnected()
    {
        _statusLabel.Text = "Connected, waiting for session...";
        _reconnectOverlay.Hide();
    }

    private void OnDisconnected()
    {
        if (_inWorld)
        {
            _reconnectOverlay.Show();
            _reconnectLabel.Text = "Connection lost";
        }
        else
        {
            _statusLabel.Text = "Disconnected";
        }
    }

    private async void OnSessionStarted(string sessionId, string playerId, string worldId, string resumeToken)
    {
        _statusLabel.Text = "Session started. Joining region...";
        _reconnectOverlay.Hide();

        await _network.SendMessageJson(MessageType.JoinRegion, new { region_id = "region-spawn" });
    }

    private void OnWorldSnapshotReceived(string rawJson)
    {
        if (!_inWorld)
        {
            _worldInstance = _worldScene.Instantiate();
            AddChild(_worldInstance);
            _inWorld = true;
            _connectScreen.Hide();
        }
    }

    private void OnError(string code, string message)
    {
        _statusLabel.Text = $"Error [{code}]: {message}";
    }

    private async void OnBackToMenu()
    {
        if (_worldInstance != null)
        {
            _worldInstance.QueueFree();
            _worldInstance = null;
        }
        _inWorld = false;
        _gameState.Clear();
        _reconnectOverlay.Hide();
        _connectScreen.Show();
        _statusLabel.Text = "Enter server address and click Connect";
        await _network.Disconnect();
    }

    // Called by ConnectScreen signal
    private void _on_connect_pressed(string url)
    {
        _statusLabel.Text = "Connecting...";
        _ = _network.Connect(url);
    }
}
