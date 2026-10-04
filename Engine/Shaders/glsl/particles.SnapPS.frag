#version 450
// Collision snapshot (particles.metal SnapPS): scene depth -> linear view depth in metres, point-downsampled.
layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_depth;

layout(set = 3, binding = 0, std140) uniform SnapCB
{
	vec4 depth_params;   // x near, y far, z ortho
	vec2 src_size;
	vec2 dst_size;
} c;                     // = 32

void main()
{
	vec2 p = clamp(floor(v_uv * c.src_size), vec2(0.0), c.src_size - 1.0);
	float d = texelFetch(u_depth, ivec2(p), 0).r;
	float n = c.depth_params.x, fa = c.depth_params.y;
	float z = c.depth_params.z > 0.5 ? n + d * (fa - n) : (n * fa) / max(fa - d * (fa - n), 1e-6);
	o_color = vec4(z, 0.0, 0.0, 1.0);
}
