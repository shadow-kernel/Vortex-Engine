// The fullscreen-triangle vertex shader shared by BlitVS, SsaoVS, BloomVS and SnapVS (one .spv each, because
// SPIR-V modules carry a single `main`). Identical math to the .metal twins, including the position Y negate:
// that is the UV convention (NDC +1 = top = uv.y 0 under SDL's unified coordinate system), not an NDC fix.
#ifndef VORTEX_FULLSCREEN_VS_GLSL
#define VORTEX_FULLSCREEN_VS_GLSL

layout(location = 0) out vec2 v_uv;

void main()
{
	uint id = uint(gl_VertexIndex);
	vec2 uv = vec2(float((id << 1u) & 2u), float(id & 2u));
	gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
	gl_Position.y = -gl_Position.y;
	v_uv = uv;
}

#endif
