class_name CoordinateMapper
extends Object

## Handles the mapping between Server space (+Z = North) and Godot space (-Z = Forward).
## Based on ADR 0018.

## Converts a server-side Pos/Vel (x, y, z) to a Godot Vector3.
## Server: X, Y (up), Z (North)
## Godot: X, Y (up), -Z (Forward)
static func server_to_godot(vec: Dictionary) -> Vector3:
	var x = float(vec.get("x", vec.get("X", 0.0)))
	var y = float(vec.get("y", vec.get("Y", 0.0)))
	var z = float(vec.get("z", vec.get("Z", 0.0)))
	return Vector3(x, y, -z)

## Converts a Godot Vector3 to a server-side Dictionary.
static func godot_to_server(godot_vec: Vector3) -> Dictionary:
	return { "x": godot_vec.x, "y": godot_vec.y, "z": -godot_vec.z }

## Converts a server-side heading (0-360, 0 = North/+Z) to radians for Godot.
## In Godot, rotation.y = 0 points towards -Z (Forward).
static func server_heading_to_rad(server_heading: float) -> float:
	# Server 0 (North) -> Godot 0 (-Z)
	return deg_to_rad(-server_heading)

## Converts a Godot rotation (radians) to a server-side direction byte (0-255).
static func godot_rotation_to_server_byte(godot_rotation_y: float) -> int:
	var deg = -rad_to_deg(godot_rotation_y)
	while deg < 0: deg += 360
	while deg >= 360: deg -= 360
	return int(floor((deg / 360.0) * 256.0)) % 256
