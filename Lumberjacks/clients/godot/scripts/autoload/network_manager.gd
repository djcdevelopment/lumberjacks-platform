extends Node
## WebSocket connection manager. Autoload singleton.
## Connects to the Gateway, parses JSON envelopes, emits typed signals.

signal connected
signal disconnected
signal attempting_reconnect(attempt: int)
signal session_started(payload: Dictionary)
signal world_snapshot(payload: Dictionary)
signal entity_updated(payload: Dictionary)
signal entity_removed(payload: Dictionary)
signal event_emitted(payload: Dictionary)
signal error_received(payload: Dictionary)

var _socket: WebSocketPeer = null
var _url: String = ""
var _seq: int = 0
var _connected: bool = false

## Resume support
var resume_token: String = ""
var last_url: String = ""

const SESSION_CONFIG_PATH = "user://session.cfg"
var _reconnect_timer: float = 0.0
var _reconnect_delay: float = 2.0
var _should_reconnect: bool = false
var _reconnect_attempt: int = 0

func _ready() -> void:
	_load_session()


func connect_to_server(url: String, resume: String = "") -> void:
	if _socket != null:
		_socket.close()
		_socket = null

	_connected = false
	last_url = url

	var connect_url = url
	# Ensure trailing slash if no path is present (Godot 4 URL parsing fix for query params)
	if not connect_url.ends_with("/") and not "?" in connect_url:
		connect_url += "/"

	if resume != "":
		# Append resume token as query parameter
		if "?" in connect_url:
			connect_url += "&resume=" + resume
		else:
			connect_url += "?resume=" + resume

	print("[Network] Connecting to ", connect_url)
	_socket = WebSocketPeer.new()
	# Increase buffers for large world snapshots (dense forests)
	_socket.set_inbound_buffer_size(1024 * 1024) # 1MB
	_socket.set_outbound_buffer_size(1024 * 1024) # 1MB
	
	var err = _socket.connect_to_url(connect_url)
	if err != OK:
		print("[Network] Connection failed: ", err)
		_socket = null


func disconnect_from_server() -> void:
	_should_reconnect = false
	if _socket != null:
		_socket.close()


func send_message(type: String, payload: Dictionary = {}) -> void:
	if _socket == null or not _connected:
		return
	_seq += 1
	var envelope = {
		"type": type,
		"payload": payload,
		"seq": _seq
	}
	var json_str = JSON.stringify(envelope)
	_socket.send_text(json_str)


func reconnect() -> void:
	if resume_token != "" and last_url != "":
		print("[Network] Reconnecting with resume token...")
		connect_to_server(last_url, resume_token)
	elif last_url != "":
		print("[Network] Reconnecting without resume...")
		connect_to_server(last_url)


func is_connected_to_server() -> bool:
	return _connected


func _process(delta: float) -> void:
	if _socket == null:
		# Handle reconnection timer
		if _should_reconnect:
			_reconnect_timer -= delta
			if _reconnect_timer <= 0.0:
				_should_reconnect = false
				_reconnect_attempt += 1
				attempting_reconnect.emit(_reconnect_attempt)
				reconnect()
		return

	_socket.poll()
	var state = _socket.get_ready_state()

	match state:
		WebSocketPeer.STATE_OPEN:
			if not _connected:
				_connected = true
				_should_reconnect = false
				_reconnect_attempt = 0
				print("[Network] Connected")
				connected.emit()

			# Process all available messages
			while _socket.get_available_packet_count() > 0:
				var packet = _socket.get_packet()
				var text = packet.get_string_from_utf8()
				_handle_message(text)

		WebSocketPeer.STATE_CLOSING:
			pass  # Wait for it to close

		WebSocketPeer.STATE_CLOSED:
			var code = _socket.get_close_code()
			var reason = _socket.get_close_reason()
			print("[Network] Disconnected (code: %d, reason: %s)" % [code, reason])
			_socket = null
			var was_connected = _connected
			_connected = false

			if was_connected:
				disconnected.emit()
				# Auto-reconnect if we were previously connected
				_should_reconnect = true
				_reconnect_timer = _reconnect_delay
				print("[Network] Will reconnect in %.1f seconds..." % _reconnect_delay)


func _handle_message(text: String) -> void:
	var json = JSON.new()
	var err = json.parse(text)
	if err != OK:
		print("[Network] Failed to parse message: ", text.left(100))
		return

	var envelope: Dictionary = json.data
	var msg_type: String = envelope.get("type", "")
	var payload: Dictionary = envelope.get("payload", {})

	match msg_type:
		"session_started":
			resume_token = payload.get("resume_token", "")
			_save_session()
			print("[Network] Session started — player_id: ", payload.get("player_id", "?"))
			session_started.emit(payload)

		"world_snapshot":
			print("[Network] World snapshot — %d entities" % payload.get("entities", []).size())
			world_snapshot.emit(payload)

		"entity_update":
			entity_updated.emit(payload)

		"entity_removed":
			entity_removed.emit(payload)

		"event_emitted":
			print("[Network] Event: ", payload.get("event_type", "?"))
			event_emitted.emit(payload)

		"error":
			print("[Network] Error: ", payload.get("message", "unknown"))
			error_received.emit(payload)

		_:
			print("[Network] Unknown message type: ", msg_type)


func _save_session() -> void:
	var config = ConfigFile.new()
	config.set_value("session", "resume_token", resume_token)
	config.set_value("session", "last_url", last_url)
	config.save(SESSION_CONFIG_PATH)


func _load_session() -> void:
	var config = ConfigFile.new()
	if config.load(SESSION_CONFIG_PATH) == OK:
		resume_token = config.get_value("session", "resume_token", "")
		last_url = config.get_value("session", "last_url", "")
		if resume_token != "":
			print("[Network] Loaded resume token: ", resume_token.left(8), "...")
