extends Node3D

## Handles smooth interpolation of server-authoritative state.
## Based on ADR 0017 (Interpolation Debt).

@export var interpolation_speed: float = 10.0
@export var should_interpolate: bool = true

var _target_pos: Vector3 = Vector3.ZERO
var _target_heading: float = 0.0

var _last_update_time: float = 0.0
var _update_interval: float = 0.05 # Default to 20Hz

func initialize(entity: Dictionary, is_local: bool) -> void:
	# Local player sets current = true for their camera
	if is_local:
		if has_node("CameraPivot/Camera3D"):
			get_node("CameraPivot/Camera3D").current = true
		if has_node("Nametag"):
			get_node("Nametag").modulate = Color(0.2, 1.0, 0.2)
	
	_target_pos = entity.get("_pos_godot", position)
	_target_heading = entity.get("_heading_godot", rotation.y)
	
	position = _target_pos
	rotation.y = _target_heading

func update_from_server(entity: Dictionary) -> void:
	var new_pos = entity.get("_pos_godot", _target_pos)
	var new_heading = entity.get("_heading_godot", _target_heading)
	
	# Track update interval for smoothing 5Hz vs 20Hz chunks (ADR 0017)
	var current_time = Time.get_ticks_msec() / 1000.0
	if _last_update_time > 0:
		_update_interval = current_time - _last_update_time
	_last_update_time = current_time
	
	_target_pos = new_pos
	_target_heading = new_heading

func _process(delta: float) -> void:
	if not should_interpolate:
		position = _target_pos
		rotation.y = _target_heading
		return

	# ADR 0017: If update rate is slower (5Hz Zone), we scale the lerp speed 
	# to avoid "reaching the target too soon" and stuttering.
	var alpha = clampf(delta * interpolation_speed, 0.0, 1.0)
	
	if _update_interval > 0.1: # 5Hz zone (0.2s interval)
		alpha *= (0.05 / _update_interval) # Scale down lerp
	
	position = position.lerp(_target_pos, alpha)
	rotation.y = lerp_angle(rotation.y, _target_heading, alpha)
