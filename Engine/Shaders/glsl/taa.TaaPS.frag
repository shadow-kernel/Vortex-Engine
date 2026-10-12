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


// The history read with a Catmull-Rom kernel (nine texels, five bilinear fetches): a bilinear tap re-blurs the history a
// little every frame and the picture went soft; the cubic keeps the detail the clamp lets through.
vec3 sample_history(vec2 uv, vec2 texel)
{
	vec2 size = 1.0 / texel;
	vec2 pos = uv * size;
	vec2 center = floor(pos - 0.5) + 0.5;
	vec2 f = pos - center;
	vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
	vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
	vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
	vec2 w3 = f * f * (-0.5 + 0.5 * f);
	vec2 w12 = w1 + w2;
	vec2 off12 = w2 / w12;
	vec2 p0 = (center - 1.0) * texel, p3 = (center + 2.0) * texel, p12 = (center + off12) * texel;
	vec3 r = texture(u_hist, vec2(p12.x, p0.y)).rgb * (w12.x * w0.y);
	r += texture(u_hist, vec2(p0.x, p12.y)).rgb * (w0.x * w12.y);
	r += texture(u_hist, vec2(p12.x, p12.y)).rgb * (w12.x * w12.y);
	r += texture(u_hist, vec2(p3.x, p12.y)).rgb * (w3.x * w12.y);
	r += texture(u_hist, vec2(p12.x, p3.y)).rgb * (w12.x * w3.y);
	return max(r, vec3(0.0));
}

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
		if (puv.x >= 0.0 && puv.x <= 1.0 && puv.y >= 0.0 && puv.y <= 1.0) h = clamp(sample_history(puv, c.texel), mn, mx);
		else w = 1.0;
	}
	else w = 1.0;
	o_color = vec4(mix(h, c0, w), 1.0);
}
