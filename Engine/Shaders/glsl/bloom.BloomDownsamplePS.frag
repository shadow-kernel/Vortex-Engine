#version 450
#extension GL_GOOGLE_include_directive : require
// Bloom 13-tap downsample (bloom.metal BloomDownsamplePS).
#include "bloom_common.glsl"

layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

void main() { o_color = vec4(down13(v_uv, c.src_texel), 1.0); }
