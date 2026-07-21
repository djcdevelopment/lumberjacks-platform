extends Node3D
## World scene root. Spawns/removes entity nodes based on GameState signals.

var entity_nodes: Dictionary = {}  # entity_id → Node3D

var _player_scene: PackedScene = preload("res://scenes/entities/player.tscn")
var _structure_scene: PackedScene = preload("res://scenes/entities/structure.tscn")
var _tree_scene: PackedScene = preload("res://scenes/entities/tree.tscn")


func _ready() -> void:
	GameState.entity_added.connect(_on_entity_added)
	GameState.entity_changed.connect(_on_entity_changed)
	GameState.entity_deleted.connect(_on_entity_deleted)

	# Spawn any entities that already exist in GameState (from snapshot)
	for eid in GameState.entities:
		_on_entity_added(eid, GameState.entities[eid])


func _exit_tree() -> void:
	# Disconnect signals when world is freed (returning to menu)
	if GameState.entity_added.is_connected(_on_entity_added):
		GameState.entity_added.disconnect(_on_entity_added)
	if GameState.entity_changed.is_connected(_on_entity_changed):
		GameState.entity_changed.disconnect(_on_entity_changed)
	if GameState.entity_deleted.is_connected(_on_entity_deleted):
		GameState.entity_deleted.disconnect(_on_entity_deleted)
	entity_nodes.clear()


func _on_entity_added(entity_id: String, entity: Dictionary) -> void:
	if entity_id in entity_nodes:
		return  # Already spawned

	var id = entity_id
	var type = entity.get("entity_type", "unknown")
	var node = null

	match type:
		"player":
			node = _spawn_player(id, entity)
		"structure":
			node = _spawn_structure(id, entity)
		"tree", "natural_resource", "oak_tree":
			node = _spawn_tree(id, entity)

	if node != null:
		entity_nodes[id] = node
		add_child(node)
		
		# Initialize AFTER adding to tree so @onready variables are populated
		var is_local = (entity.get("player_id", id) == GameState.my_player_id) or (id == GameState.my_player_id)
		if node.has_method("initialize"):
			node.initialize(entity, is_local)


func _on_entity_changed(entity_id: String, entity: Dictionary) -> void:
	if entity_id not in entity_nodes:
		# Might be a new entity arriving via entity_update
		_on_entity_added(entity_id, entity)
		return

	var node = entity_nodes[entity_id]
	if node.has_method("update_from_server"):
		node.update_from_server(entity)


func _on_entity_deleted(entity_id: String) -> void:
	if entity_id in entity_nodes:
		var node = entity_nodes[entity_id]
		entity_nodes.erase(entity_id)
		node.queue_free()


func _spawn_player(entity_id: String, entity: Dictionary) -> Node3D:
	var node = _player_scene.instantiate()
	var is_local = (entity.get("player_id", entity_id) == GameState.my_player_id) or (entity_id == GameState.my_player_id)

	node.name = "Player_" + entity_id.left(8)
	node.set_meta("entity_id", entity_id)

	# Set initial position (ADR 0018)
	node.position = entity.get("_pos_godot", Vector3.ZERO)
	node.rotation.y = entity.get("_heading_godot", 0.0)

	# Configure as local or remote player
	if is_local:
		node.set_meta("is_local", true)
	else:
		node.set_meta("is_local", false)

	return node


func _spawn_structure(entity_id: String, entity: Dictionary) -> Node3D:
	var node = _structure_scene.instantiate()
	node.name = "Structure_" + entity_id.left(8)
	node.set_meta("entity_id", entity_id)

	# Set initial position (ADR 0018)
	node.position = entity.get("_pos_godot", Vector3.ZERO)
	node.rotation.y = entity.get("_heading_godot", 0.0)

	return node
func _spawn_tree(entity_id: String, entity: Dictionary) -> Node3D:
	var node = _tree_scene.instantiate()
	node.name = "Tree_" + entity_id.left(8)
	node.set_meta("entity_id", entity_id)

	# Set initial position (ADR 0018)
	node.position = entity.get("_pos_godot", Vector3.ZERO)
	# Heading is handled by the tree's own initialize/update
	
	return node
