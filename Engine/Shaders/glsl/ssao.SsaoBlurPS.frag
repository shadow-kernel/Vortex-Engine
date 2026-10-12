#version 450
// SSAO 4x4 box blur (ssao.metal SsaoBlurPS).
layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_src;

layout(set = 3, binding = 0, std140) uniform SsaoCB
{
	mat4  inv_proj;
	vec2  texel;
	float radius;
	float intensity;
	float bias;
	float proj_scale;
	vec2  pad;
	mat4  proj;              // @96  view -> clip (screen-space GI, #257 step 1)
	mat4  view_to_prev_clip; // @160 view -> the previous frame's clip
	float gi_strength;       // @224
	float scene_exposure;
	float prev_valid;
	float gi_radius;
} c;

void main()
{
	// a 4x4 box: exactly one Bayer block of SsaoPS, so every rotation is averaged (the sampler is point/clamp)
	vec4 s = vec4(0.0);
	for (int y = -1; y <= 2; ++y)
		for (int x = -1; x <= 2; ++x)
			s += texture(u_src, v_uv + c.texel * vec2(float(x), float(y)));
	o_color = s * (1.0 / 16.0);   // rgb = GI, a = AO
}
