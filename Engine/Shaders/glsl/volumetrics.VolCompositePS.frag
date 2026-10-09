#version 450
// Volumetric fog composite (volumetrics.metal VolCompositePS): the half-res fog target (S in rgb, T in a) blended over
// the scene as scene * T + S by the pipeline's blend state (ONE / SRC_ALPHA).
layout(location = 0) in vec2 v_uv;
layout(location = 0) out vec4 o_color;
layout(set = 2, binding = 0) uniform sampler2D u_fog;
void main() { o_color = texture(u_fog, v_uv); }
