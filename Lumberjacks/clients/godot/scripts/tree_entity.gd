extends "res://scripts/remote_entity.gd"

## Nature 2.0: Biomimetic Tree Entity
## Handles visual states for Growth History, Stumps, and Axe-driven Falling.

@onready var trunk = $Trunk
@onready var canopy = $Canopy
@onready var stump = $Stump
@onready var sapling = $Sapling

var _is_felled: bool = false
var _fall_heading: float = 0.0
var _health: float = 100.0
var _stump_health: float = 50.0
var _regrowth: float = 0.0

func initialize(entity: Dictionary, is_local: bool) -> void:
	super.initialize(entity, is_local)
	_update_visuals(entity)
	
	# Apply "Twist" history (Phase 2)
	var history = entity.get("growth_history", {})
	if history.has("twist"):
		var twist = float(history["twist"])
		# Rotate canopy slightly to represent wind-shifted growth
		if canopy:
			canopy.rotate_y(deg_to_rad(twist * 10.0))

func update_from_server(entity: Dictionary) -> void:
	super.update_from_server(entity)
	_update_visuals(entity)

func _update_visuals(entity: Dictionary) -> void:
	var prev_health = _health
	_health = entity.get("health", 100.0)
	_stump_health = entity.get("stump_health", 50.0)
	_regrowth = entity.get("regrowth_progress", 0.0)
	
	# Detect just felled (Phase 3 Axe Geometry)
	if prev_health > 0 and _health <= 0:
		_trigger_fall_animation(entity)

	# 1. State: Sapling (Regrowth Phase)
	if _regrowth > 0 and _regrowth < 1.0:
		sapling.visible = true
		trunk.visible = false
		canopy.visible = false
		stump.visible = true
		sapling.scale = Vector3.ONE * (0.2 + (_regrowth * 0.8))
	# 2. State: Falling / Felled
	elif _health <= 0:
		sapling.visible = false
		canopy.visible = false
		stump.visible = (_stump_health > 0)
		# Trunk is handled by the fall animation/state
		if not _is_felled:
			_is_felled = true
			trunk.visible = true 
	# 3. State: Healthy Tree
	else:
		_is_felled = false
		_target_fall_rotation = 0.0
		sapling.visible = false
		trunk.visible = true
		canopy.visible = true
		stump.visible = false
		trunk.rotation = Vector3.ZERO # Reset from fall

var _target_fall_rotation: float = 0.0

func _trigger_fall_animation(entity: Dictionary) -> void:
	var history = entity.get("growth_history", {})
	if history.has("fall_heading"):
		_fall_heading = float(history["fall_heading"])
		# Rotate entire trunk mesh towards fall heading
		trunk.rotation.y = deg_to_rad(_fall_heading)
		_target_fall_rotation = PI / 2.0 # 90 degrees fall
		
		# Simple tween for the fall
		var tween = create_tween()
		tween.tween_property(trunk, "rotation:x", PI/2.0, 1.2).set_trans(Tween.TRANS_BOUNCE).set_ease(Tween.EASE_OUT)

func _process(delta: float) -> void:
	if not _is_felled:
		super._process(delta)
	# If felled, we don't interpolate position/y-rotation of the ROOT, 
	# we stay at the stump while the trunk mesh is rotated away.
