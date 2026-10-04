#version 450
// Render-scale composite: samples the scene RT onto the present target (postfx.metal BlitPS).
layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_src;

void main() { o_color = texture(u_src, v_uv); }
