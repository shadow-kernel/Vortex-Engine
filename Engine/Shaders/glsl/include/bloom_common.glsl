// Shared bloom declarations + the 13-tap downsample filter (bloom.metal).
#ifndef VORTEX_BLOOM_COMMON_GLSL
#define VORTEX_BLOOM_COMMON_GLSL

layout(set = 2, binding = 0) uniform sampler2D u_src;

layout(set = 3, binding = 0, std140) uniform BloomCB
{
	vec2  src_texel;     // 1 / size of the SOURCE level
	float threshold;
	float knee;
	float sample_scale;  // upsample tent radius in source texels
	float weight;        // upsample additive weight (scatter)
	vec2  pad;
} c;

vec3 down13(vec2 uv, vec2 t)
{
	vec3 a = texture(u_src, uv + t * vec2(-1.0, -1.0)).rgb;
	vec3 b = texture(u_src, uv + t * vec2( 0.0, -1.0)).rgb;
	vec3 cc = texture(u_src, uv + t * vec2( 1.0, -1.0)).rgb;
	vec3 d = texture(u_src, uv + t * vec2(-0.5, -0.5)).rgb;
	vec3 e = texture(u_src, uv + t * vec2( 0.5, -0.5)).rgb;
	vec3 f = texture(u_src, uv + t * vec2(-1.0,  0.0)).rgb;
	vec3 g = texture(u_src, uv).rgb;
	vec3 h = texture(u_src, uv + t * vec2( 1.0,  0.0)).rgb;
	vec3 i = texture(u_src, uv + t * vec2(-0.5,  0.5)).rgb;
	vec3 j = texture(u_src, uv + t * vec2( 0.5,  0.5)).rgb;
	vec3 k = texture(u_src, uv + t * vec2(-1.0,  1.0)).rgb;
	vec3 l = texture(u_src, uv + t * vec2( 0.0,  1.0)).rgb;
	vec3 m = texture(u_src, uv + t * vec2( 1.0,  1.0)).rgb;
	vec3 o = (d + e + i + j) * (0.5 * 0.25);
	o += (a + b + f + g) * (0.125 * 0.25);
	o += (b + cc + g + h) * (0.125 * 0.25);
	o += (f + g + k + l) * (0.125 * 0.25);
	o += (g + h + l + m) * (0.125 * 0.25);
	return o;
}

#endif
