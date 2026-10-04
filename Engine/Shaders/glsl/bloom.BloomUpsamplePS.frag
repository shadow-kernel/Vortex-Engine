#version 450
#extension GL_GOOGLE_include_directive : require
// Bloom additive 9-tap tent upsample (bloom.metal BloomUpsamplePS).
#include "bloom_common.glsl"

layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

void main()
{
	vec4 d = c.src_texel.xyxy * vec4(1.0, 1.0, -1.0, 0.0) * c.sample_scale;
	vec3 s;
	s  = texture(u_src, v_uv - d.xy).rgb;
	s += texture(u_src, v_uv - d.wy).rgb * 2.0;
	s += texture(u_src, v_uv - d.zy).rgb;
	s += texture(u_src, v_uv + d.zw).rgb * 2.0;
	s += texture(u_src, v_uv).rgb * 4.0;
	s += texture(u_src, v_uv + d.xw).rgb * 2.0;
	s += texture(u_src, v_uv + d.zy).rgb;
	s += texture(u_src, v_uv + d.wy).rgb * 2.0;
	s += texture(u_src, v_uv + d.xy).rgb;
	o_color = vec4(s * (1.0 / 16.0) * c.weight, 1.0);
}
