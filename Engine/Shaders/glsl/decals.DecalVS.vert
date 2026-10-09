#version 450
#extension GL_GOOGLE_include_directive : require
// Decal boxes (decals.metal DecalVS): the unit cube, 36 vertices per instance, instances pulled from a read-only
// storage buffer (set 0 for vertex-stage storage buffers). The pass culls FRONT faces and draws the back faces.
#define SET_UNIFORM 1
#include "common.glsl"
#include "decals_common.glsl"

layout(set = 0, binding = 0, std430) readonly buffer Decals
{
	GpuDecal items[];
} inst;

layout(location = 0) flat out vec4 v_inv0;
layout(location = 1) flat out vec4 v_inv1;
layout(location = 2) flat out vec4 v_inv2;
layout(location = 3) flat out vec4 v_inv3;
layout(location = 4) flat out vec3 v_axis;
layout(location = 5) flat out vec4 v_color;
layout(location = 6) flat out vec4 v_params;

// clockwise from outside — the same table as decals.hlsl (SDL GPU keeps the facing convention across backends)
const vec3 CUBE[36] = vec3[36](
	vec3(-0.5, -0.5, -0.5), vec3(-0.5,  0.5, -0.5), vec3( 0.5,  0.5, -0.5),
	vec3(-0.5, -0.5, -0.5), vec3( 0.5,  0.5, -0.5), vec3( 0.5, -0.5, -0.5),
	vec3(-0.5, -0.5,  0.5), vec3( 0.5, -0.5,  0.5), vec3( 0.5,  0.5,  0.5),
	vec3(-0.5, -0.5,  0.5), vec3( 0.5,  0.5,  0.5), vec3(-0.5,  0.5,  0.5),
	vec3(-0.5, -0.5, -0.5), vec3(-0.5, -0.5,  0.5), vec3(-0.5,  0.5,  0.5),
	vec3(-0.5, -0.5, -0.5), vec3(-0.5,  0.5,  0.5), vec3(-0.5,  0.5, -0.5),
	vec3( 0.5, -0.5, -0.5), vec3( 0.5,  0.5, -0.5), vec3( 0.5,  0.5,  0.5),
	vec3( 0.5, -0.5, -0.5), vec3( 0.5,  0.5,  0.5), vec3( 0.5, -0.5,  0.5),
	vec3(-0.5, -0.5, -0.5), vec3( 0.5, -0.5, -0.5), vec3( 0.5, -0.5,  0.5),
	vec3(-0.5, -0.5, -0.5), vec3( 0.5, -0.5,  0.5), vec3(-0.5, -0.5,  0.5),
	vec3(-0.5,  0.5, -0.5), vec3(-0.5,  0.5,  0.5), vec3( 0.5,  0.5,  0.5),
	vec3(-0.5,  0.5, -0.5), vec3( 0.5,  0.5,  0.5), vec3( 0.5,  0.5, -0.5));

void main()
{
	GpuDecal d = inst.items[b.base + uint(gl_InstanceIndex)];
	// the C++ matrices are row-major (row vectors); loaded column-major here they are the transposes -> M * v
	vec3 wp = (d.world * vec4(CUBE[uint(gl_VertexIndex) % 36u], 1.0)).xyz;
	gl_Position = f.view_projection * vec4(wp, 1.0);
	// rows of the row-major inverse = columns of the transposed mat4
	v_inv0 = d.inv_world[0]; v_inv1 = d.inv_world[1]; v_inv2 = d.inv_world[2]; v_inv3 = d.inv_world[3];
	v_axis = normalize(d.world[1].xyz);   // the box's local +Y in world space (row 1 of the row-major matrix)
	v_color = d.color;
	v_params = d.params;
}
