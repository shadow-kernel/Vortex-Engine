#version 450
#extension GL_GOOGLE_include_directive : require
// Skinned mesh vertex stage (standard.metal VSSkinned). The bone palette is a read-only storage buffer of
// float4 rows; SDL GPU puts vertex-stage storage buffers in set 0 after the sampled textures (there are none).
#define SET_UNIFORM 1
#include "standard_common.glsl"
#include "standard_vsout.glsl"

layout(location = 0) in vec3  a_pos;
layout(location = 1) in vec3  a_norm;
layout(location = 2) in vec2  a_uv;
layout(location = 3) in uvec4 a_bone_indices;   // SDL_GPU_VERTEXELEMENTFORMAT_UBYTE4
layout(location = 4) in vec4  a_bone_weights;
layout(location = 5) in vec4  a_iw0;
layout(location = 6) in vec4  a_iw1;
layout(location = 7) in vec4  a_iw2;
layout(location = 8) in vec4  a_iw3;

layout(set = 1, binding = 1, std140) uniform SkinParamsBlock
{
	uint bone_base;      // first matrix of this draw's palette in the bone storage buffer
	uint padding0;
	uint padding1;
	uint padding2;
} skin_params;

layout(set = 0, binding = 0, std430) readonly buffer BoneRows
{
	vec4 rows[];
} bone_rows;

mat4 load_bone(uint i)
{
	uint b = i * 4u;
	return mat4(bone_rows.rows[b], bone_rows.rows[b + 1u], bone_rows.rows[b + 2u], bone_rows.rows[b + 3u]);
}

void main()
{
	uint base = skin_params.bone_base;
	mat4 skin =
		a_bone_weights.x * load_bone(base + a_bone_indices.x) +
		a_bone_weights.y * load_bone(base + a_bone_indices.y) +
		a_bone_weights.z * load_bone(base + a_bone_indices.z) +
		a_bone_weights.w * load_bone(base + a_bone_indices.w);

	vec4 skinned_pos = skin * vec4(a_pos, 1.0);
	mat3 skin3 = mat3(skin[0].xyz, skin[1].xyz, skin[2].xyz);
	vec3 skinned_norm = normalize(skin3 * a_norm);

	// per-instance tint (#331): the fourth column of the instance matrix carries (r-1, g-1, b-1, a) — an affine
	// matrix never uses it, so an untinted instance is an exact matrix
	vec4 tint = vec4(1.0 + a_iw0.w, 1.0 + a_iw1.w, 1.0 + a_iw2.w, a_iw3.w);
	mat4 world = mat4(vec4(a_iw0.xyz, 0.0), vec4(a_iw1.xyz, 0.0), vec4(a_iw2.xyz, 0.0), vec4(a_iw3.xyz, 1.0));
	vec4 world_pos = world * skinned_pos;
	v_world_pos = world_pos.xyz;
	gl_Position = frame.view_projection * world_pos;
	mat3 world3 = mat3(a_iw0.xyz, a_iw1.xyz, a_iw2.xyz);
	v_norm = normalize(world3 * skinned_norm);
	v_uv = a_uv;
	tangent_basis(v_norm, v_tangent, v_bitangent);
	v_tint = tint;
}
