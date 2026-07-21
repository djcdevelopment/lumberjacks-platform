extends Node3D
## Handles build mode: ghost preview + click-to-place.
## Added as a child of the world scene.

signal build_mode_changed(active: bool, type: String)

var build_mode: bool = false
var selected_type: String = "campfire"
var _ghost: MeshInstance3D = null
var _camera: Camera3D = null

const GHOST_ALPHA = 0.4

const STRUCTURE_TYPES = ["campfire", "wooden_wall", "test_beacon"]

## Visual configs matching structure_entity.gd
const GHOST_VISUALS = {
	"campfire": {"color": Color(0.9, 0.5, 0.1, GHOST_ALPHA), "mesh": "cylinder", "scale": Vector3(0.5, 0.3, 0.5)},
	"wooden_wall": {"color": Color(0.55, 0.35, 0.15, GHOST_ALPHA), "mesh": "box", "scale": Vector3(2.0, 1.5, 0.3)},
	"test_beacon": {"color": Color(0.9, 0.9, 0.1, GHOST_ALPHA), "mesh": "sphere", "scale": Vector3(0.4, 0.4, 0.4)},
}


func _ready() -> void:
	_create_ghost()
	_update_ghost_mesh()


func _create_ghost() -> void:
	_ghost = MeshInstance3D.new()
	_ghost.visible = false
	_ghost.name = "GhostPreview"
	add_child(_ghost)


func _update_ghost_mesh() -> void:
	if _ghost == null:
		return

	var visual = GHOST_VISUALS.get(selected_type, GHOST_VISUALS["campfire"])

	match visual["mesh"]:
		"cylinder":
			var mesh = CylinderMesh.new()
			mesh.top_radius = visual["scale"].x
			mesh.bottom_radius = visual["scale"].x
			mesh.height = visual["scale"].y
			_ghost.mesh = mesh
			_ghost.position.y = visual["scale"].y / 2.0
		"sphere":
			var mesh = SphereMesh.new()
			mesh.radius = visual["scale"].x
			mesh.height = visual["scale"].y * 2
			_ghost.mesh = mesh
			_ghost.position.y = visual["scale"].y
		_:
			var mesh = BoxMesh.new()
			mesh.size = visual["scale"]
			_ghost.mesh = mesh
			_ghost.position.y = visual["scale"].y / 2.0

	var mat = StandardMaterial3D.new()
	mat.albedo_color = visual["color"]
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	_ghost.material_override = mat


func _input(event: InputEvent) -> void:
	if event.is_action_pressed("toggle_build"):
		build_mode = !build_mode
		_ghost.visible = build_mode
		build_mode_changed.emit(build_mode, selected_type)
		if build_mode:
			print("[Build] Build mode ON — type: %s (scroll wheel to change)" % selected_type)
		else:
			print("[Build] Build mode OFF")

	# Scroll to cycle structure types
	if build_mode and event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_WHEEL_UP and event.pressed:
			_cycle_type(1)
		elif event.button_index == MOUSE_BUTTON_WHEEL_DOWN and event.pressed:
			_cycle_type(-1)


func _unhandled_input(event: InputEvent) -> void:
	if not build_mode:
		return

	if event.is_action_pressed("place_structure"):
		_place_at_ghost()


func _process(_delta: float) -> void:
	if not build_mode or _ghost == null:
		return

	# Find camera
	if _camera == null or not is_instance_valid(_camera):
		_camera = get_viewport().get_camera_3d()
	if _camera == null:
		return

	# Raycast from mouse to ground plane (y=0)
	var mouse_pos = get_viewport().get_mouse_position()
	var ray_origin = _camera.project_ray_origin(mouse_pos)
	var ray_dir = _camera.project_ray_normal(mouse_pos)

	# Intersect with y=0 plane
	if abs(ray_dir.y) > 0.001:
		var t = -ray_origin.y / ray_dir.y
		if t > 0:
			var hit = ray_origin + ray_dir * t
			_ghost.global_position.x = hit.x
			_ghost.global_position.z = hit.z
			# Keep y at mesh offset (set in _update_ghost_mesh)


func _cycle_type(direction: int) -> void:
	var idx = STRUCTURE_TYPES.find(selected_type)
	idx = (idx + direction) % STRUCTURE_TYPES.size()
	if idx < 0:
		idx += STRUCTURE_TYPES.size()
	selected_type = STRUCTURE_TYPES[idx]
	_update_ghost_mesh()
	build_mode_changed.emit(build_mode, selected_type)
	print("[Build] Selected: %s" % selected_type)


func _place_at_ghost() -> void:
	if _ghost == null:
		return

	var pos = _ghost.global_position
	# Use CoordinateMapper to sync with server space (ADR 0018)
	var server_pos = CoordinateMapper.godot_to_server(pos)

	NetworkManager.send_message("place_structure", {
		"structure_type": selected_type,
		"position": server_pos
	})
	print("[Build] Placed %s at server pos: %s" % [selected_type, server_pos])

	# Exit build mode after placing
	build_mode = false
	_ghost.visible = false
