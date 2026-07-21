extends "res://scripts/remote_entity.gd"
## Player entity — capsule mesh, nametag, optional local camera + input.

@onready var mesh_instance: MeshInstance3D = $MeshInstance3D
@onready var nametag: Label3D = $Nametag
@onready var camera_pivot: Node3D = $CameraPivot
@onready var camera: Camera3D = $CameraPivot/Camera3D

var is_local_player: bool = false
var _player_controller: Node = null

## Colors
const LOCAL_COLOR = Color(0.2, 0.8, 0.2)   # Green
const REMOTE_COLOR = Color(0.3, 0.5, 0.9)  # Blue


func initialize(entity: Dictionary, is_local: bool) -> void:
	is_local_player = is_local

	# Set nametag
	var player_name: String = entity.get("name", "Player")
	nametag.text = player_name

	# Set color
	var mat = StandardMaterial3D.new()
	mat.albedo_color = LOCAL_COLOR if is_local else REMOTE_COLOR
	mesh_instance.material_override = mat

	if is_local:
		# Enable camera for local player
		camera.current = true
		# Add player controller for input
		_player_controller = preload("res://scripts/player_controller.gd").new()
		_player_controller.name = "PlayerController"
		add_child(_player_controller)
		# Hide nametag for local player (it's above our camera)
		nametag.visible = false
	else:
		# Disable camera for remote players
		camera_pivot.visible = false
		camera.current = false


func update_from_server(entity: Dictionary) -> void:
	super.update_from_server(entity)

	# Update connected status visually
	var connected: bool = entity.get("connected", true)
	if not connected and not is_local_player:
		# Fade out disconnected players
		if mesh_instance.material_override:
			mesh_instance.material_override.albedo_color.a = 0.3
