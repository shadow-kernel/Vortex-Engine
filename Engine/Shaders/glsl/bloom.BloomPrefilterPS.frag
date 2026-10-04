#version 450
#extension GL_GOOGLE_include_directive : require
// Bloom soft-knee prefilter (bloom.metal BloomPrefilterPS).
#include "bloom_common.glsl"

layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

void main()
{
	vec3 col = down13(v_uv, c.src_texel);
	float br = max(col.r, max(col.g, col.b));
	float soft = clamp(br - c.threshold + c.knee, 0.0, 2.0 * c.knee);
	soft = soft * soft / (4.0 * c.knee + 1e-4);
	float contrib = max(soft, br - c.threshold) / max(br, 1e-4);
	o_color = vec4(col * contrib, 1.0);
}
