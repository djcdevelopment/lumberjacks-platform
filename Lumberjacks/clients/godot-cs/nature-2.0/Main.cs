using Godot;
using Game.Contracts.Protocol;
using CommunitySurvival.Networking;
using CommunitySurvival.Core;

/// <summary>
/// Root node. Manages connect screen → world lifecycle.
/// </summary>
public partial class Main : Node
{
	private Control _connectScreen;
	private Label _statusLabel;
	private Control _reconnectOverlay;
	private PackedScene _worldScene;
	private Node _worldInstance;
	private bool _inWorld;

	private SimulationClient _net;
	private GameState _state;
	private bool _standaloneLab;

	public override void _Ready()
	{
		_net = GetNode<SimulationClient>("/root/SimulationClient");
		_state = GetNode<GameState>("/root/GameState");

		_connectScreen = GetNode<Control>("ConnectScreen");
		_statusLabel = GetNode<Label>("ConnectScreen/VBox/StatusLabel");
		_reconnectOverlay = GetNode<Control>("ReconnectOverlay");

		var requestedLab = System.Array.Find(
			OS.GetCmdlineUserArgs(),
			arg => arg.StartsWith("--lab=", System.StringComparison.OrdinalIgnoreCase));
		if (string.Equals(requestedLab, "--lab=forest-storm", System.StringComparison.OrdinalIgnoreCase))
		{
			StartForestLab();
			return;
		}
		if (string.Equals(requestedLab, "--lab=axe-swing", System.StringComparison.OrdinalIgnoreCase) ||
			string.Equals(requestedLab, "--lab=axe-arc", System.StringComparison.OrdinalIgnoreCase) ||
			string.Equals(requestedLab, "--lab=axe-head", System.StringComparison.OrdinalIgnoreCase))
		{
			StartStandaloneLab("res://scenes/AxeSwingLab.tscn");
			return;
		}

		_worldScene = GD.Load<PackedScene>("res://scenes/World.tscn");

		// Signals
		_net.Connected += () => { _statusLabel.Text = "Connected, waiting for session..."; _reconnectOverlay.Hide(); };
		_net.Disconnected += () => { if (_inWorld) _reconnectOverlay.Show(); else _statusLabel.Text = "Disconnected"; };
		_net.SessionStarted += OnSession;
		_net.WorldSnapshotReceived += OnSnapshot;
		_net.ErrorReceived += (c, m) => _statusLabel.Text = $"[{c}] {m}";

		var cs = _connectScreen as CommunitySurvival.UI.ConnectScreen;
		cs.ConnectRequested += OnConnect;
		cs.ForestLabRequested += StartForestLab;
		GetNode<Button>("ReconnectOverlay/VBox/BackButton").Pressed += BackToMenu;

		_reconnectOverlay.Hide();
		_connectScreen.Show();

		var serverArgument = System.Array.Find(
			OS.GetCmdlineUserArgs(),
			arg => arg.StartsWith("--server=", System.StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(serverArgument))
		{
			var serverAddress = serverArgument["--server=".Length..];
			cs.SetServerAddress(serverAddress);
			Callable.From(() => OnConnect(serverAddress)).CallDeferred();
		}
	}

	public override void _UnhandledInput(InputEvent ev)
	{
		if (_standaloneLab && ev is InputEventKey { Pressed: true, Keycode: Key.Escape })
		{
			GetTree().Quit();
			return;
		}
		if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.Escape && _inWorld) BackToMenu();
	}

    private void OnConnect(string serverAddress)
    {
		try
		{
			var access = NativeAccessConfig.ForPrivateRAndD(serverAddress);
			_statusLabel.Text = "Opening the Northwoods…";
			_ = _net.Connect(access);
		}
		catch (System.Exception ex)
		{
			_statusLabel.Text = ex.Message;
		}
	}

	private void StartForestLab()
	{
		StartStandaloneLab("res://scenes/ForestStormLab.tscn");
	}

	private void StartStandaloneLab(string scenePath)
	{
		_standaloneLab = true;
		_connectScreen.Hide();
		_reconnectOverlay.Hide();
		_worldInstance = GD.Load<PackedScene>(scenePath).Instantiate();
		AddChild(_worldInstance);
	}

	private async void OnSession(string playerId, string resumeToken)
	{
		_statusLabel.Text = "Joining region...";
		await _net.SendJson(MessageType.JoinRegion, new { region_id = "region-spawn" });
	}

	private void OnSnapshot(string rawJson)
	{
		if (!_inWorld)
		{
			_worldInstance = _worldScene.Instantiate();
			AddChild(_worldInstance);
			_inWorld = true;
			_connectScreen.Hide();
		}
	}

	private async void BackToMenu()
	{
		_worldInstance?.QueueFree();
		_worldInstance = null;
		_inWorld = false;
		_state.Clear();
		_reconnectOverlay.Hide();
		_connectScreen.Show();
		_statusLabel.Text = "Ready to reconnect";
		await _net.Disconnect();
	}
}
