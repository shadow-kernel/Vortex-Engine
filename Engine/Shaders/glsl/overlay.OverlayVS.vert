#version 450
// 2D UI overlay (overlay.metal OverlayVS): screen-space position in overlay units -> NDC.
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec2 a_uv;
layout(location = 2) in vec4 a_color;
layout(location = 3) in vec4 a_misc;    // (mode, radius, half_w, half_h)
layout(location = 4) in vec2 a_local;   // rect-local pixel position for the rounded-corner distance field

layout(location = 0) out vec2 v_uv;
layout(location = 1) out vec4 v_color;
layout(location = 2) out vec4 v_misc;
layout(location = 3) out vec2 v_local;

layout(set = 1, binding = 0, std140) uniform OverlayConstants
{
	vec2 screen_size;
	vec2 padding;
} c;

void main()
{
	vec2 ndc = vec2(a_pos.x / c.screen_size.x * 2.0 - 1.0, 1.0 - a_pos.y / c.screen_size.y * 2.0);
	gl_Position = vec4(ndc, 0.0, 1.0);
	v_uv = a_uv;
	v_color = a_color;
	v_misc = a_misc;
	v_local = a_local;
}
