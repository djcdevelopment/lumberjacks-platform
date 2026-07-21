extends Node3D
## Captures WASD input and sends player_input messages to the server.
## Only active on the local player.

var input_seq: int = 0
var _send_interval: float = 1.0 / 20.0  # 20Hz max send rate
var _send_timer: float = 0.0
var _last_direction: int = 255
var _last_speed: int = 0
var _is_axe_equipped: bool = true # Default to true for Nature 2.0 testing
var _is_swinging: bool = false

@onready var axe = get_node_or_null("../Axe") # Local player is child of World, but script might be deep. Correct path needed.

func _process(delta: float) -> void:
    # Attempt to find axe if not found yet (since we are on a child node usually)
    if axe == null:
        axe = get_parent().get_node_or_null("Axe")

	_send_timer += delta
	if _send_timer < _send_interval:
		return
	_send_timer = 0.0

	# Gather input
	var input_dir = Vector2.ZERO
	if Input.is_action_pressed("move_forward"):
		input_dir.y -= 1
	if Input.is_action_pressed("move_back"):
		input_dir.y += 1
	if Input.is_action_pressed("move_left"):
		input_dir.x -= 1
	if Input.is_action_pressed("move_right"):
		input_dir.x += 1

	var direction_byte: int
	var speed_percent: int

	if input_dir.length() > 0.1:
		input_dir = input_dir.normalized()
		# Convert to compass direction byte (0-255)
		# Server: 0 = North (+Z), 64 = East (+X), 128 = South (-Z), 192 = West (-X)
		# Input: y negative = forward (north), x positive = right (east)
		var angle = atan2(input_dir.x, -input_dir.y)  # 0 = north
		if angle < 0:
			angle += TAU
		direction_byte = int((angle / TAU) * 256) % 256
		speed_percent = 100
	else:
		direction_byte = 255
		speed_percent = 0

	var action_flags = 0
	if Input.is_action_pressed("interact"):
		action_flags |= 0x04 # Bit 2: Interact/Chop
		_play_swing_animation()

func _play_swing_animation() -> void:
	if _is_swinging or axe == null:
		return
	
	_is_swinging = true
	var tween = create_tween()
	# Fast swing forward
	tween.tween_property(axe, "rotation:x", deg_to_rad(-60), 0.1).set_trans(Tween.TRANS_CUBIC).set_ease(Tween.EASE_OUT)
	# Slower return
	tween.tween_property(axe, "rotation:x", 0, 0.3).set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	tween.finished.connect(func(): _is_swinging = false)

	# Only send if input changed, or send idle occasionally to keep alive
	if direction_byte == _last_direction and speed_percent == _last_speed and speed_percent == 0:
		return

	_last_direction = direction_byte
	_last_speed = speed_percent

	input_seq += 1
	NetworkManager.send_message("player_input", {
		"direction": direction_byte,
		"speed_percent": speed_percent,
		"action_flags": action_flags,
		"input_seq": input_seq
	})

	# Register for ping measurement (every 20th input)
	if input_seq % 20 == 0:
		var hud = get_node_or_null("/root/Main/World/HUD")
		if hud and hud.has_method("register_ping_sample"):
			hud.register_ping_sample(input_seq)
