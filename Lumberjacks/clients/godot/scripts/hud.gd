extends CanvasLayer
## HUD overlay — connection status, position, player count, ping, build mode.

@onready var status_dot: ColorRect = $TopLeft/StatusDot
@onready var info_label: Label = $TopLeft/InfoLabel
@onready var position_label: Label = $BottomLeft/PositionLabel
@onready var ping_label: Label = $TopRight/PingLabel
@onready var error_label: Label = $Center/ErrorLabel
@onready var build_label: Label = $BottomCenter/BuildLabel

var _error_timer: float = 0.0

## Ping estimation
var _pending_pings: Dictionary = {}  # input_seq → timestamp_ms
var current_ping: int = 0
var _ping_sample_interval: float = 1.0
var _ping_timer: float = 0.0


func _ready() -> void:
	NetworkManager.connected.connect(func(): _set_status(true))
	NetworkManager.disconnected.connect(func(): _set_status(false))
	NetworkManager.entity_updated.connect(_on_entity_updated)
	NetworkManager.error_received.connect(_on_error)
	error_label.text = ""
	build_label.text = ""
	_set_status(NetworkManager.is_connected_to_server())


func _process(delta: float) -> void:
	# Update position from local player entity
	if GameState.my_player_id != "" and GameState.my_player_id in GameState.entities:
		var ent = GameState.entities[GameState.my_player_id]
		var pos = GameState.get_entity_position(ent)
		position_label.text = "Pos: %.1f, %.1f, %.1f" % [pos.x, pos.y, pos.z]
	else:
		# Try to find local player by scanning entities
		for eid in GameState.entities:
			var ent = GameState.entities[eid]
			if ent.get("player_id", "") == GameState.my_player_id:
				var pos = GameState.get_entity_position(ent)
				position_label.text = "Pos: %.1f, %.1f, %.1f" % [pos.x, pos.y, pos.z]
				break

	# Player count
	var player_count = 0
	for eid in GameState.entities:
		if GameState.entities[eid].get("entity_type", "") == "player":
			player_count += 1
	info_label.text = "Players: %d | Tick: %d" % [player_count, GameState.current_tick]

	# Ping display
	ping_label.text = "Ping: %d ms" % current_ping

	# Error fade
	if _error_timer > 0:
		_error_timer -= delta
		if _error_timer <= 0:
			error_label.text = ""

	# Build mode indicator
	# Check if structure_placer exists as a sibling
	var placer = get_node_or_null("/root/Main/World/StructurePlacer")
	if placer and placer.build_mode:
		build_label.text = "[B] Build: %s (scroll to change, click to place)" % placer.selected_type
	else:
		build_label.text = "[B] Build Mode"


func _set_status(is_connected: bool) -> void:
	if status_dot:
		status_dot.color = Color.GREEN if is_connected else Color.RED


func _on_entity_updated(payload: Dictionary) -> void:
	# Ping estimation: check if this is our player's update with input_seq
	var data = payload.get("data", {})
	var last_seq = data.get("last_input_seq", 0)
	if last_seq > 0 and last_seq in _pending_pings:
		var rtt = Time.get_ticks_msec() - _pending_pings[last_seq]
		current_ping = rtt
		_pending_pings.erase(last_seq)
		# Clean old entries
		var cutoff = Time.get_ticks_msec() - 5000
		for seq in _pending_pings.keys():
			if _pending_pings[seq] < cutoff:
				_pending_pings.erase(seq)


func _on_error(payload: Dictionary) -> void:
	error_label.text = payload.get("message", "Error")
	_error_timer = 4.0


## Called by player_controller to register a ping sample
func register_ping_sample(seq: int) -> void:
	_pending_pings[seq] = Time.get_ticks_msec()
