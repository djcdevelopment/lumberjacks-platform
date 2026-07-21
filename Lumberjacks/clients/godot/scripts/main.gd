extends Node
## Entry point. Shows connect screen, then switches to world scene on join.
## Handles reconnection overlay when disconnected while in-world.

@onready var connect_screen: Control = $ConnectScreen
@onready var status_label: Label = $ConnectScreen/VBoxContainer/StatusLabel
@onready var url_input: LineEdit = $ConnectScreen/VBoxContainer/URLInput
@onready var reconnect_overlay: Control = $ReconnectOverlay
@onready var reconnect_label: Label = $ReconnectOverlay/VBoxContainer/ReconnectLabel
@onready var reconnect_countdown: Label = $ReconnectOverlay/VBoxContainer/CountdownLabel
@onready var back_button: Button = $ReconnectOverlay/VBoxContainer/BackButton

var _world_scene: PackedScene = preload("res://scenes/world.tscn")
var _world_instance: Node = null
var _in_world: bool = false

## Reconnection state
var _reconnect_attempts: int = 0
const MAX_RECONNECT_ATTEMPTS = 5

## Config file for persisting last URL
const CONFIG_PATH = "user://settings.cfg"


func _ready() -> void:
	NetworkManager.connected.connect(_on_connected)
	NetworkManager.disconnected.connect(_on_disconnected)
	NetworkManager.session_started.connect(_on_session_started)
	NetworkManager.world_snapshot.connect(_on_world_snapshot)
	NetworkManager.error_received.connect(_on_error)
	back_button.pressed.connect(_on_back_to_menu)

	reconnect_overlay.visible = false
	connect_screen.visible = true

	# Load last used URL
	_load_settings()


func _process(_delta: float) -> void:
	# Update reconnect countdown display
	if reconnect_overlay.visible and NetworkManager._should_reconnect:
		var remaining = NetworkManager._reconnect_timer
		reconnect_countdown.text = "Reconnecting in %.1f s  (attempt %d/%d)" % [
			maxf(remaining, 0.0), _reconnect_attempts + 1, MAX_RECONNECT_ATTEMPTS]


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		if _in_world and not reconnect_overlay.visible:
			# Disconnect and return to menu
			_on_back_to_menu()


func _on_connect_pressed(url: String) -> void:
	status_label.text = "Connecting..."
	_reconnect_attempts = 0
	_save_settings(url)
	NetworkManager.connect_to_server(url)


func _on_connected() -> void:
	status_label.text = "Connected, waiting for session..."
	reconnect_overlay.visible = false


func _on_disconnected() -> void:
	if _in_world:
		# Show reconnect overlay instead of going back to connect screen
		reconnect_overlay.visible = true
		reconnect_label.text = "Connection lost"
		_reconnect_attempts += 1

		if _reconnect_attempts >= MAX_RECONNECT_ATTEMPTS:
			# Give up — go back to connect screen
			_return_to_connect_screen("Reconnection failed after %d attempts" % MAX_RECONNECT_ATTEMPTS)
	else:
		status_label.text = "Disconnected"


func _on_session_started(payload: Dictionary) -> void:
	var player_id: String = payload.get("player_id", "?")
	var resumed: bool = payload.get("resumed", false)

	_reconnect_attempts = 0
	reconnect_overlay.visible = false

	if resumed:
		status_label.text = "Resumed session as %s" % player_id.left(8)
		print("[Main] Session resumed — player_id: ", player_id)
	else:
		status_label.text = "Session started: %s\nJoining region..." % player_id.left(8)

	# Auto-join the default region
	NetworkManager.send_message("join_region", {"region_id": "region-spawn"})


func _on_world_snapshot(_payload: Dictionary) -> void:
	# Switch from connect screen to world
	if _world_instance == null:
		_world_instance = _world_scene.instantiate()
		add_child(_world_instance)

	_in_world = true
	connect_screen.visible = false
	reconnect_overlay.visible = false


func _on_error(payload: Dictionary) -> void:
	var msg: String = payload.get("message", "unknown")
	status_label.text = "Error: %s" % msg

	# If we get an error during reconnection (e.g. expired token), go back
	if reconnect_overlay.visible:
		_return_to_connect_screen("Resume failed: %s" % msg)


func _on_back_to_menu() -> void:
	NetworkManager.disconnect_from_server()
	_return_to_connect_screen("Disconnected")


func _return_to_connect_screen(message: String) -> void:
	_in_world = false
	_reconnect_attempts = 0
	reconnect_overlay.visible = false

	# Tear down the world
	if _world_instance != null:
		_world_instance.queue_free()
		_world_instance = null

	# Clear game state
	GameState.entities.clear()
	GameState.my_player_id = ""
	GameState.region_id = ""

	connect_screen.visible = true
	status_label.text = message


func _save_settings(url: String) -> void:
	var config = ConfigFile.new()
	config.set_value("network", "last_url", url)
	config.save(CONFIG_PATH)


func _load_settings() -> void:
	var config = ConfigFile.new()
	if config.load(CONFIG_PATH) == OK:
		var last_url = config.get_value("network", "last_url", "")
		if last_url != "":
			url_input.text = last_url
