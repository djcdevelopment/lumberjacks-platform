using Godot;

namespace CommunitySurvival.UI;

/// <summary>
/// Connection UI — URL input + connect button. Emits ConnectRequested.
/// </summary>
public partial class ConnectScreen : Control
{
    [Signal] public delegate void ConnectRequestedEventHandler(string url);

    private LineEdit _urlInput;
    private Button _connectButton;
    private Label _statusLabel;

    public override void _Ready()
    {
        _urlInput = GetNode<LineEdit>("VBox/URLInput");
        _connectButton = GetNode<Button>("VBox/ConnectButton");
        _statusLabel = GetNode<Label>("VBox/StatusLabel");

        _connectButton.Pressed += OnConnectPressed;
        _urlInput.TextSubmitted += _ => OnConnectPressed();
        _urlInput.Text = "ws://localhost:4000";
    }

    private void OnConnectPressed()
    {
        var url = _urlInput.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "ws://localhost:4000";
        GD.Print($"ConnectScreen: requesting {url}");
        EmitSignal(SignalName.ConnectRequested, url);
    }

    public void SetStatus(string text) => _statusLabel.Text = text;
}
