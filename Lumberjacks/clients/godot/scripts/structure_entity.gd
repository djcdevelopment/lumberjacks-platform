extends "res://scripts/remote_entity.gd"
## Structure entity — box/cylinder mesh, color by type.

@onready var mesh_instance: MeshInstance3D = $MeshInstance3D

## Structure type → visual config
const STRUCTURE_VISUALS = {
	"campfire": {"color": Color(0.9, 0.5, 0.1), "mesh": "cylinder", "scale": Vector3(0.5, 0.3, 0.5)},
	"wooden_wall": {"color": Color(0.55, 0.35, 0.15), "mesh": "box", "scale": Vector3(2.0, 1.5, 0.3)},
	"test_beacon": {"color": Color(0.9, 0.9, 0.1), "mesh": "sphere", "scale": Vector3(0.4, 0.4, 0.4)},
}

const DEFAULT_VISUAL = {"color": Color(0.5, 0.5, 0.5), "mesh": "box", "scale": Vector3(1.0, 1.0, 1.0)}


func _ready() -> void:
	should_interpolate = false  # Structures don't move


func initialize(entity: Dictionary, is_local: bool) -> void:
	var structure_type: String = entity.get("type", entity.get("structure_type", "unknown"))
	var visual = STRUCTURE_VISUALS.get(structure_type, DEFAULT_VISUAL)

	# Set mesh shape
	match visual["mesh"]:
		"cylinder":
			var mesh = CylinderMesh.new()
			mesh.top_radius = visual["scale"].x
			mesh.bottom_radius = visual["scale"].x
			mesh.height = visual["scale"].y
			mesh_instance.mesh = mesh
		"sphere":
			var mesh = SphereMesh.new()
			mesh.radius = visual["scale"].x
			mesh.height = visual["scale"].y * 2
			mesh_instance.mesh = mesh
		_:  # box
			var mesh = BoxMesh.new()
			mesh.size = visual["scale"]
			mesh_instance.mesh = mesh

	# Set color
	var mat = StandardMaterial3D.new()
	mat.albedo_color = visual["color"]
	mesh_instance.material_override = mat

	# Set rotation (ADR 0018: Use the mapped heading from GameState)
	rotation.y = entity.get("_heading_godot", 0.0)

	# Position mesh so it sits on the ground
	mesh_instance.position.y = visual["scale"].y / 2.0
