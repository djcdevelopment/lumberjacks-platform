shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float model_height = 7.0;
uniform float is_foliage = 0.0;
uniform float archetype_bend = 1.0;
uniform float storm_time = 0.0;
uniform float storm_intensity : hint_range(0.0, 1.0) = 0.0;
uniform vec2 wind_direction = vec2(0.82, 0.57);
uniform float peak_wind_mps = 27.0;
uniform float turbulence : hint_range(0.0, 1.0) = 0.72;
uniform vec2 storm_center = vec2(0.0);
uniform float storm_radius = 240.0;
uniform float moisture : hint_range(0.0, 1.0) = 0.78;
uniform float trunk_frequency = 0.34;
uniform float detail_frequency = 2.7;
uniform float trunk_amplitude = 1.0;
uniform float detail_amplitude = 1.0;
uniform float debug_layer : hint_range(0.0, 3.0) = 0.0;
instance uniform float use_tree_traits_override = 0.0;
instance uniform vec4 tree_traits_override = vec4(0.0);

varying float tree_motion;
varying vec3 tree_traits;

float wave(float value) {
	return sin(value) * 0.62 + sin(value * 1.73 + 1.31) * 0.26 + sin(value * 3.17 + 0.47) * 0.12;
}

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	float height_weight = smoothstep(0.01, 1.0, clamp((VERTEX.y + 0.25) / max(model_height, 0.1), 0.0, 1.0));
	vec4 traits = mix(INSTANCE_CUSTOM, tree_traits_override, use_tree_traits_override);
	float phase = traits.r * 6.2831853;
	float stiffness = max(traits.g, 0.2);
	float crown_mass = max(traits.b, 0.2);
	float tree_moisture = clamp(traits.a, 0.0, 1.0);
	float radius = max(storm_radius, 1.0);
	float radial = 1.0 - smoothstep(radius * 0.72, radius, distance(origin.xz, storm_center));
	float front = dot(origin.xz - storm_center, normalize(wind_direction)) * 0.045;
	float spatial_gust = wave(storm_time * 0.91 - front + phase * 0.31) * 0.5 + 0.5;
	float gust = mix(0.62, 1.18, spatial_gust * turbulence);
	float wind_scale = clamp(peak_wind_mps / 40.0, 0.0, 2.0) * storm_intensity * radial * gust;
	float slow = wave(storm_time * trunk_frequency + phase);
	float cross = wave(storm_time * trunk_frequency * 0.71 + phase * 1.37 + origin.x * 0.015);
	float flexibility = archetype_bend * mix(1.16, 0.82, tree_moisture) / stiffness;
	float bend = slow * wind_scale * trunk_amplitude * flexibility * height_weight * height_weight;
	vec3 world_wind = normalize(vec3(wind_direction.x, 0.0, wind_direction.y));
	vec3 local_wind = normalize(inverse(mat3(MODEL_MATRIX)) * world_wind);
	vec3 local_cross = vec3(-local_wind.z, 0.0, local_wind.x);
	VERTEX += local_wind * bend * model_height * 0.085;
	VERTEX += local_cross * cross * abs(bend) * model_height * 0.022;

	float detail_mask = is_foliage * height_weight;
	float flutter = wave(storm_time * detail_frequency + phase * 2.7 + VERTEX.x * 1.9 + VERTEX.z * 1.3);
	VERTEX += NORMAL * flutter * detail_mask * detail_amplitude * wind_scale * (0.018 + crown_mass * 0.025);
	tree_motion = clamp(abs(bend) * 2.2 + abs(flutter) * detail_mask * wind_scale * 0.24, 0.0, 1.0);
	tree_traits = traits.gba;
}

void fragment() {
	vec4 tex = texture(albedo_texture, UV);
	ALBEDO = tex.rgb * mix(vec3(1.0), vec3(0.70, 0.79, 0.72), moisture * storm_intensity * 0.34);
	ROUGHNESS = mix(0.86, 0.58, moisture * storm_intensity);
	ALPHA = tex.a;
	ALPHA_SCISSOR_THRESHOLD = 0.2;
	if (debug_layer > 0.5 && debug_layer < 1.5) ALBEDO = vec3(tree_motion, 0.08, 1.0 - tree_motion);
	if (debug_layer > 1.5 && debug_layer < 2.5) ALBEDO = mix(vec3(0.18, 0.10, 0.04), vec3(0.18, 0.82, 0.26), is_foliage);
	if (debug_layer > 2.5) ALBEDO = tree_traits;
}
