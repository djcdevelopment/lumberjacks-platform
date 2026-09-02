using Godot;

namespace CommunitySurvival.UI;

/// <summary>Deliberately small R&amp;D entry screen: an address and a play button.</summary>
public partial class ConnectScreen : Control
{
    [Signal] public delegate void ConnectRequestedEventHandler(string serverAddress);
    [Signal] public delegate void ForestLabRequestedEventHandler();

    private LineEdit _serverAddress = null!;
    private Label _statusLabel = null!;

    public override void _Ready()
    {
        _serverAddress = GetNode<LineEdit>("VBox/ServerAddress");
        _statusLabel = GetNode<Label>("VBox/StatusLabel");
        GetNode<Button>("VBox/ConnectButton").Pressed += Connect;
        GetNode<Button>("VBox/LabButton").Pressed += () =>
            EmitSignal(SignalName.ForestLabRequested);
        _serverAddress.TextSubmitted += _ => Connect();
    }

    private void Connect()
    {
        _statusLabel.Text = "Opening the Northwoods…";
        EmitSignal(SignalName.ConnectRequested, _serverAddress.Text);
    }

    public void SetServerAddress(string value) => _serverAddress.Text = value;
    public void SetStatus(string text) => _statusLabel.Text = text;
}
