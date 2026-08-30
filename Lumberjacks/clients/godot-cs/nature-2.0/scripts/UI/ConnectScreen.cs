using Godot;
using System.IO;

namespace CommunitySurvival.UI;

/// <summary>
/// Connection UI. The credential-bearing field pass is read locally and never printed.
/// </summary>
public partial class ConnectScreen : Control
{
    [Signal] public delegate void ConnectRequestedEventHandler(string accessJson);

    private Button _importButton;
    private FileDialog _fileDialog;
    private Label _statusLabel;

    public override void _Ready()
    {
        _importButton = GetNode<Button>("VBox/ImportButton");
        _fileDialog = GetNode<FileDialog>("FieldPassDialog");
        _statusLabel = GetNode<Label>("VBox/StatusLabel");

        _importButton.Pressed += () => _fileDialog.PopupCenteredRatio(0.65f);
        _fileDialog.FileSelected += OnFileSelected;
    }

    private void OnFileSelected(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            _statusLabel.Text = "Reading field pass…";
            EmitSignal(SignalName.ConnectRequested, json);
        }
        catch
        {
            _statusLabel.Text = "Could not read that field pass.";
        }
    }

    public void SetStatus(string text) => _statusLabel.Text = text;
}
