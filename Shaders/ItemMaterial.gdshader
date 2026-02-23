shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_prepass_alpha;

uniform sampler2D diffuse_texture : source_color, filter_nearest, repeat_enable;
uniform int item_options = 0;
uniform vec3 glow_color = vec3(0.6, 0.5, 0.0);
uniform bool is_ancient = false;
uniform bool is_excellent = false;
uniform vec3 light_direction = vec3(0.707, -0.707, 0.0);
uniform float alpha_cutoff = 0.1;
uniform float glow_intensity_scale = 0.8;
uniform float shadow_strength = 0.5;
uniform float effect_mix = 0.55;

const float MONOGAME_GAMMA = 2.2;

vec3 get_custom_spectrum(float phase, float blue_scale) {
	phase = fract(phase) * 3.0;
	if (phase < 1.0) {
		return mix(vec3(0.0, 0.3, 1.0) * vec3(1.0, 1.0, blue_scale), vec3(1.0, 0.5, 0.0), fract(phase));
	} else if (phase < 2.0) {
		return mix(vec3(1.0, 0.5, 0.0), vec3(0.6, 0.0, 0.8), fract(phase));
	} else {
		return mix(vec3(0.6, 0.0, 0.8), vec3(0.0, 0.3, 1.0) * vec3(1.0, 1.0, blue_scale), fract(phase));
	}
}

vec3 to_monogame_space(vec3 linear_rgb) {
	return pow(max(linear_rgb, vec3(0.0)), vec3(1.0 / MONOGAME_GAMMA));
}

vec3 from_monogame_space(vec3 mg_rgb) {
	return pow(max(mg_rgb, vec3(0.0)), vec3(MONOGAME_GAMMA));
}

vec4 sample_monogame_space(vec2 uv) {
	vec4 s = texture(diffuse_texture, uv);
	s.rgb = to_monogame_space(s.rgb);
	return s;
}

void fragment() {
	vec4 color = sample_monogame_space(UV);
	if (color.a < alpha_cutoff) {
		discard;
	}

	float item_options_f = max(0.0, float(item_options));
	float item_level = item_options_f - floor(item_options_f * (1.0 / 16.0)) * 16.0;
	bool excellent_active = is_excellent;

	vec3 normal = normalize(NORMAL);
	float light_intensity = max(0.1, dot(normal, -normalize(light_direction)));
	color.rgb *= light_intensity;
	vec3 base_lit_color = color.rgb;
	float wave_base = fract(TIME * 0.001) * 10000.0 * 0.0001;
	vec3 view = normalize(VIEW) + normal + vec3(10000.5, 10000.5, 10000.5);

	vec3 effect_color = glow_color * glow_intensity_scale;
	float brightness = 1.0;
	float ghost_intensity = 0.0;

	if (item_level < 7.0) {
		brightness = 1.0;
		ghost_intensity = 0.0;
	} else if (item_level < 9.0) {
		brightness = 1.6 + (item_level - 8.0) * 0.2;
		ghost_intensity = 0.30;
	} else if (item_level < 10.0) {
		brightness = 1.8 + (item_level - 9.0) * 0.2;
		ghost_intensity = 0.8;
	} else {
		brightness = 1.8 + (item_level - 10.0) * 0.2;
		ghost_intensity = 0.7 + (item_level * (1.0 / 30.0));
	}

	float subtle_pulse = (1.0 + sin(TIME * 0.8)) * 0.03 + 0.97;
	float shimmer = (1.0 + sin(TIME * 8.0 + normal.x * 12.0)) * 0.15 + 0.85;

	vec2 ghost_offset1 = vec2(sin(TIME * 0.8) * 0.035, cos(TIME * 0.7) * 0.035) * ghost_intensity;
	vec2 ghost_offset2 = vec2(sin(TIME * 1.0 + 2.1) * 0.025, cos(TIME * 0.9 + 1.8) * 0.025) * ghost_intensity;
	vec2 ghost_offset3 = vec2(sin(TIME * 1.2 + 4.2) * 0.02, cos(TIME * 1.1 + 3.7) * 0.02) * ghost_intensity;
	vec2 ghost_offset4 = vec2(sin(TIME * 0.6 + 1.1) * 0.015, cos(TIME * 1.3 + 2.3) * 0.015) * ghost_intensity;

	vec2 ancient_offset1 = vec2(sin(TIME * 0.5) * 0.02, cos(TIME * 0.4) * 0.02);
	vec2 ancient_offset2 = vec2(sin(TIME * 0.7 + 1.0) * 0.015, cos(TIME * 0.6 + 1.5) * 0.015);

	vec2 excellent_offset1 = vec2(sin(TIME * 0.6) * 0.03, cos(TIME * 0.5) * 0.03);
	vec2 excellent_offset2 = vec2(sin(TIME * 0.8 + 1.2) * 0.025, cos(TIME * 0.7 + 1.8) * 0.025);
	vec2 excellent_offset3 = vec2(sin(TIME * 1.0 + 2.4) * 0.02, cos(TIME * 0.9 + 2.6) * 0.02);
	vec2 excellent_offset4 = vec2(sin(TIME * 0.5 + 3.6) * 0.015, cos(TIME * 1.1 + 3.2) * 0.015);
	vec2 excellent_offset5 = vec2(sin(TIME * 0.7 + 4.8) * 0.035, cos(TIME * 0.6 + 4.4) * 0.035);
	vec2 excellent_offset6 = vec2(sin(TIME * 0.9 + 6.0) * 0.028, cos(TIME * 0.8 + 5.5) * 0.028);

	vec4 ghost1 = sample_monogame_space(UV + ghost_offset1);
	vec4 ghost2 = sample_monogame_space(UV + ghost_offset2);
	vec4 ghost3 = sample_monogame_space(UV + ghost_offset3);
	vec4 ghost4 = sample_monogame_space(UV + ghost_offset4);
	vec4 ancient_ghost1 = sample_monogame_space(UV + ancient_offset1);
	vec4 ancient_ghost2 = sample_monogame_space(UV + ancient_offset2);
	vec4 excellent_ghost1 = sample_monogame_space(UV + excellent_offset1);
	vec4 excellent_ghost2 = sample_monogame_space(UV + excellent_offset2);
	vec4 excellent_ghost3 = sample_monogame_space(UV + excellent_offset3);
	vec4 excellent_ghost4 = sample_monogame_space(UV + excellent_offset4);
	vec4 excellent_ghost5 = sample_monogame_space(UV + excellent_offset5);
	vec4 excellent_ghost6 = sample_monogame_space(UV + excellent_offset6);

	if (item_level >= 7.0) {
		vec3 metallic = effect_color * 0.8;
		color.rgb = color.rgb * metallic * brightness * subtle_pulse;
		color.rgb += ghost1.rgb * (0.8 * ghost_intensity) * shimmer * glow_intensity_scale;
		color.rgb += ghost2.rgb * (0.6 * ghost_intensity) * shimmer * glow_intensity_scale;
		color.rgb += ghost3.rgb * (0.5 * ghost_intensity) * shimmer * glow_intensity_scale;
		color.rgb += ghost4.rgb * (0.4 * ghost_intensity) * shimmer * glow_intensity_scale;
	} else {
		color.rgb *= brightness;
	}

	float extra_glow = (item_level - 9.0) * 0.1;
	float glow_effect = (1.0 + sin(TIME * 1.0)) * 0.03 + 0.2;
	if (item_level >= 10.0) {
		color.rgb += effect_color * glow_effect * extra_glow;
	}

	float ancient_enabled = is_ancient ? 1.0 : 0.0;
	vec3 ancient_color = vec3(0.3, 0.5, 1.0);

	float cycle_speed = 0.1;
	float sweep_portion = 0.15;
	float cycle_progress = fract(TIME * cycle_speed);
	float sweep_progress = clamp(cycle_progress / sweep_portion, 0.0, 1.0);

	float sweep_position = sweep_progress;
	float mesh_position = UV.x;
	float beam_width = 0.15;
	float dist_from_beam = abs(mesh_position - sweep_position);
	float beam_intensity = 1.0 - clamp(dist_from_beam / beam_width, 0.0, 1.0);
	beam_intensity = pow(beam_intensity, 2.0) * 3.0;
	float fade_out = 1.0 - smoothstep(0.85, 1.0, sweep_progress);
	beam_intensity *= fade_out;

	float wave2 = sin(TIME * 3.0 + UV.y * 6.0) * 0.3 + 0.7;
	float combined_wave = beam_intensity * wave2;
	float level_boost = item_level >= 9.0 ? 2.0 : 1.0;
	color.rgb += ancient_ghost1.rgb * ancient_color * combined_wave * 1.5 * level_boost * ancient_enabled;
	color.rgb += ancient_ghost2.rgb * ancient_color * combined_wave * 1.1 * level_boost * ancient_enabled;

	float base_glow = sin(TIME * 0.8) * 0.08 + 0.15;
	float base_glow_intensity = item_level >= 9.0 ? 0.5 : 0.25;
	color.rgb += color.rgb * ancient_color * base_glow * base_glow_intensity * ancient_enabled;

	float excellent_sweep_enabled = (excellent_active && item_level >= 7.0) ? 1.0 : 0.0;
	vec3 excellent_sweep_color = vec3(0.5, 0.3, 0.7);
	float ex_cycle_speed = 0.12;
	float ex_sweep_portion = 0.15;
	float ex_cycle_progress = fract(TIME * ex_cycle_speed);
	float ex_sweep_progress = clamp(ex_cycle_progress / ex_sweep_portion, 0.0, 1.0);
	float ex_sweep_position = ex_sweep_progress;
	float ex_mesh_position = UV.x;
	float ex_beam_width = 0.18;
	float ex_dist_from_beam = abs(ex_mesh_position - ex_sweep_position);
	float ex_beam_intensity = 1.0 - clamp(ex_dist_from_beam / ex_beam_width, 0.0, 1.0);
	ex_beam_intensity = pow(ex_beam_intensity, 2.0) * 2.0;
	float ex_fade_out = 1.0 - smoothstep(0.85, 1.0, ex_sweep_progress);
	ex_beam_intensity *= ex_fade_out;
	float ex_wave2 = sin(TIME * 3.5 + UV.y * 6.0) * 0.3 + 0.7;
	float ex_combined_wave = ex_beam_intensity * ex_wave2;
	float ex_level_boost = item_level >= 9.0 ? 1.5 : 1.0;
	color.rgb += excellent_ghost1.rgb * excellent_sweep_color * ex_combined_wave * 1.2 * ex_level_boost * excellent_sweep_enabled;
	color.rgb += excellent_ghost2.rgb * excellent_sweep_color * ex_combined_wave * 0.9 * ex_level_boost * excellent_sweep_enabled;
	float ex_base_glow = sin(TIME * 0.9) * 0.08 + 0.12;
	float ex_base_glow_intensity = item_level >= 9.0 ? 0.4 : 0.2;
	color.rgb += color.rgb * excellent_sweep_color * ex_base_glow * ex_base_glow_intensity * excellent_sweep_enabled;

	float excellent_enabled = excellent_active ? 1.0 : 0.0;
	vec3 view_dir = normalize(VIEW);
	float fresnel = 1.0 - clamp(dot(view_dir, normal), 0.0, 1.0);
	fresnel = pow(fresnel, 2.5);

	float hue_base = fract(TIME * 0.15);
	float hue_spatial = UV.x * 0.3 + UV.y * 0.2;
	float hue_normal = (normal.x + normal.y) * 0.1;
	float blue_scale = item_level >= 7.0 ? 0.01 : 1.0;
	float ex_scale = 1.8;
	vec3 rainbow1 = get_custom_spectrum(hue_base + hue_spatial, blue_scale);
	vec3 rainbow2 = get_custom_spectrum(hue_base + hue_spatial + 0.33, blue_scale);
	vec3 rainbow3 = get_custom_spectrum(hue_base + hue_spatial + 0.66, blue_scale);
	vec3 rainbow4 = get_custom_spectrum(hue_base + hue_normal + 0.5, blue_scale);

	float sweep_speed1 = TIME * 1.5;
	float sweep_speed2 = TIME * 1.2;
	float sweep_speed3 = TIME * 0.9;
	float sweep1 = UV.x + UV.y * 0.5;
	float sweep2 = UV.x * 0.7 - UV.y * 0.3;
	float sweep3 = UV.y + UV.x * 0.3;
	float beam1 = pow(sin(sweep1 * 6.0 - sweep_speed1) * 0.5 + 0.5, 8.0);
	float beam2 = pow(sin(sweep2 * 8.0 + sweep_speed2) * 0.5 + 0.5, 10.0);
	float beam3 = pow(sin(sweep3 * 5.0 - sweep_speed3) * 0.5 + 0.5, 6.0);
	float combined_beams = beam1 * 0.7 + beam2 * 0.5 + beam3 * 0.4;

	float pulse1 = sin(TIME * 1.2) * 0.5 + 0.5;
	float pulse2 = sin(TIME * 0.8 + 1.5) * 0.5 + 0.5;
	float pulse3 = sin(TIME * 1.5 + 3.0) * 0.5 + 0.5;
	float combined_pulse = (pulse1 + pulse2 + pulse3) / 3.0;

	float color_wave1 = sin(TIME * 0.6 + UV.x * 4.0) * 0.5 + 0.5;
	float color_wave2 = sin(TIME * 0.5 + UV.y * 3.0 + 1.0) * 0.5 + 0.5;
	float color_wave3 = sin(TIME * 0.7 + (UV.x + UV.y) * 2.5) * 0.5 + 0.5;
	vec3 wave_color = rainbow1 * color_wave1 + rainbow2 * color_wave2 + rainbow3 * (1.0 - color_wave1 * color_wave2);
	wave_color = normalize(wave_color) * length(wave_color) * 0.4;
	color.rgb += vec3((wave_base + view.x + color_wave3) * 0.0);

	float ghost_base_intensity = 0.2 * combined_pulse + 0.1;
	color.rgb += excellent_ghost1.rgb * rainbow1 * ghost_base_intensity * (1.0 * ex_scale) * excellent_enabled;
	color.rgb += excellent_ghost2.rgb * rainbow2 * ghost_base_intensity * (0.9 * ex_scale) * excellent_enabled;
	color.rgb += excellent_ghost3.rgb * rainbow3 * ghost_base_intensity * (0.8 * ex_scale) * excellent_enabled;
	color.rgb += excellent_ghost4.rgb * rainbow4 * ghost_base_intensity * (0.7 * ex_scale) * excellent_enabled;
	color.rgb += excellent_ghost5.rgb * wave_color * ghost_base_intensity * (0.6 * ex_scale) * excellent_enabled;
	color.rgb += excellent_ghost6.rgb * rainbow1 * ghost_base_intensity * (0.5 * ex_scale) * excellent_enabled;

	vec3 beam_color = mix(rainbow1, rainbow2, sin(TIME * 0.4) * 0.5 + 0.5);
	color.rgb += beam_color * combined_beams * 0.05 * excellent_enabled;
	vec3 rim_color = mix(rainbow3, rainbow4, fresnel);
	color.rgb += rim_color * fresnel * 0.05 * excellent_enabled;
	vec3 overlay_color = wave_color * 0.015;
	color.rgb += color.rgb * overlay_color * excellent_enabled;
	color.rgb *= mix(1.0, 1.4, excellent_enabled);
	float shadow_term = 1.0;
	float shadow_mix = mix(1.0 - shadow_strength, 1.0, shadow_term);
	color.rgb *= shadow_mix;
	color.rgb = mix(base_lit_color, color.rgb, clamp(effect_mix, 0.0, 1.0));

	ALBEDO = from_monogame_space(clamp(color.rgb, vec3(0.0), vec3(1.0)));
	ALPHA = color.a;
	ALPHA_SCISSOR_THRESHOLD = alpha_cutoff;
}
