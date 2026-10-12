#version 450
// Temporal anti-aliasing (#141): see taa.metal — the previous resolved frame reprojected, clamped to the 3x3 neighbourhood,
// blended 88 / 12 with the new frame.
layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_cur;
layout(set = 2, binding = 1) uniform sampler2D u_hist;
layout(set = 2, binding = 2) uniform sampler2D u_depth;

layout(set = 3, binding = 0, std140) uniform TaaCB
{
	mat4 reproj;
	vec2 texel; float blend; float valid;
} c;

void main()
{
	vec2 uv = v_uv;
	vec3 c0 = texture(u_cur, uv).rgb;
	vec3 mn = c0, mx = c0;
	for (int y = -1; y <= 1; ++y)
		for (int x = -1; x <= 1; ++x)
		{
			if (x == 0 && y == 0) continue;
			vec3 n = texture(u_cur, uv + vec2(x, y) * c.texel).rgb;
			mn = min(mn, n); mx = max(mx, n);
		}
	float z = texture(u_depth, uv).r;
	vec4 pc = c.reproj * vec4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, z, 1.0);
	float w = c.blend;
	vec3 h = c0;
	if (c.valid > 0.5 && pc.w > 0.0001)
	{
		vec2 puv = vec2(pc.x / pc.w * 0.5 + 0.5, 0.5 - pc.y / pc.w * 0.5);
		if (puv.x >= 0.0 && puv.x <= 1.0 && puv.y >= 0.0 && puv.y <= 1.0) h = clamp(texture(u_hist, puv).rgb, mn, mx);
		else w = 1.0;
	}
	else w = 1.0;
	o_color = vec4(mix(h, c0, w), 1.0);
}
