using Godot;
using CommunitySurvival.Core;

namespace CommunitySurvival.UI;

/// <summary>
/// HUD overlay — displays player position, player count, tick, and connection status.
/// </summary>
public partial class HUD : CanvasLayer
{
    private ColorRect _statusDot;
    private Label _infoLabel;
    private Label _positionLabel;
    private Label _pingLabel;
    private Label _errorLabel;
    private Label _buildLabel;

    private GameState _gameState;
    private Networking.SimulationClient _network;
    private float _errorTimer;

    public override void _Ready()
    {
        _gameState = GetNode<GameState>("/root/GameState");
        _network = GetNode<Networking.SimulationClient>("/root/SimulationClient");

        _statusDot = GetNode<ColorRect>("TopLeft/StatusDot");
        _infoLabel = GetNode<Label>("TopLeft/InfoLabel");
        _positionLabel = GetNode<Label>("BottomLeft/PositionLabel");
        _pingLabel = GetNode<Label>("TopRight/PingLabel");
        _errorLabel = GetNode<Label>("Center/ErrorLabel");
        _buildLabel = GetNode<Label>("BottomCenter/BuildLabel");

        _network.Connected += () => _statusDot.Color = new Color(0, 1, 0);
        _network.Disconnected += () => _statusDot.Color = new Color(1, 0, 0);
        _network.ErrorReceived += OnError;

        _buildLabel.Text = "[B] Build Mode";
    }

    public override void _Process(double delta)
    {
        // Update info
        _infoLabel.Text = $"Players: {_gameState.PlayerCount} | Tick: {_gameState.CurrentTick}";

        // Update position
        if (_gameState.MyPlayerId != null)
        {
            var pos = _gameState.GetEntityPosition(_gameState.MyPlayerId);
            _positionLabel.Text = $"Pos: {pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}";
        }

        // Fade error label
        if (_errorTimer > 0)
        {
            _errorTimer -= (float)delta;
            if (_errorTimer <= 0)
                _errorLabel.Text = "";
        }
    }

    private void OnError(string code, string message)
    {
        _errorLabel.Text = $"[{code}] {message}";
        _errorTimer = 4f;
    }
}
