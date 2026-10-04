// Editor viewport floor grid constants (grid.metal). MSL's trailing `float pad[2]` is spelled out as two
// scalars so the std140 block stays byte-identical (a std140 float[2] would have a 16-byte stride).
#ifndef VORTEX_GRID_COMMON_GLSL
#define VORTEX_GRID_COMMON_GLSL

layout(set = SET_UNIFORM, binding = 0, std140) uniform GridConstants
{
	mat4  view_projection;          // @0
	mat4  inverse_view_projection;  // @64
	vec3  camera_position;          // @128
	float spacing;                  // @140
	float extent;                   // @144
	float major;                    // @148
	float pad0;                     // @152
	float pad1;                     // @156
} c;

#endif
