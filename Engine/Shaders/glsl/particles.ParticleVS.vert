#version 450
#extension GL_GOOGLE_include_directive : require
// Billboard particles (particles.metal ParticleVS): camera-facing / stretched / flat quads, 6 vertices per
// instance pulled from a read-only storage buffer. SDL GPU puts vertex-stage storage buffers in set 0 after
// the sampled textures (there are none here).
#define SET_UNIFORM 1
#define VORTEX_NEED_PLIGHTS
#include "common.glsl"
#include "particles_common.glsl"
#include "particles_pvout.glsl"

struct GpuParticle
{
	vec3  pos;  float size;
	vec3  axis; float rot;
	uint  color; float frame; float aspect; uint flags;
};                              // = 48

layout(set = 0, binding = 0, std430) readonly buffer Particles
{
	GpuParticle parts[];
} inst;

vec4 unpack_color(uint c)
{
	return vec4(float(c & 255u), float((c >> 8) & 255u), float((c >> 16) & 255u), float(c >> 24)) * (1.0 / 255.0);
}

void main()
{
	const vec2 corners[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(-1, 1), vec2(1, -1), vec2(1, 1));
	GpuParticle p = inst.parts[b.base + uint(gl_InstanceIndex)];
	vec2 c = corners[uint(gl_VertexIndex) % 6u];
	vec3 center = p.pos;
	float half_h = p.size * 0.5;
	float half_w = half_h * p.aspect;
	float s = sin(p.rot), co = cos(p.rot);
	vec2 rc = vec2(c.x * co - c.y * s, c.x * s + c.y * co);
	vec3 wp;
	if (b.mode == 1u)
	{
		// stretched along the velocity: the streak vector is in axis, width = size
		vec3 axis = p.axis;
		vec3 dir = normalize(axis + vec3(0, 1e-6, 0));
		vec3 tocam = f.depth_params.z > 0.5 ? -f.cam_forward.xyz : (f.eye.xyz - center);
		vec3 side = cross(dir, tocam);
		float sl = length(side);
		side = sl > 1e-6 ? side / sl : f.cam_right.xyz;
		wp = center + side * (c.x * half_w) + axis * (c.y * 0.5);
	}
	else if (b.mode == 2u)
	{
		// horizontal (lies on the XZ plane: ground ripples, decal-like splats)
		wp = center + vec3(rc.x * half_w, 0.0, rc.y * half_h);
	}
	else if (b.mode == 3u)
	{
		// vertical (Y-axis aligned, faces the camera around Y: fire sheets)
		vec3 tocam = f.eye.xyz - center; tocam.y = 0.0;
		float tl = length(tocam);
		vec3 fwd = tl > 1e-5 ? tocam / tl : vec3(0, 0, -1);
		vec3 right = normalize(cross(vec3(0, 1, 0), fwd));
		wp = center + right * (rc.x * half_w) + vec3(0, 1, 0) * (rc.y * half_h);
	}
	else
	{
		wp = center + f.cam_right.xyz * (rc.x * half_w) + f.cam_up.xyz * (rc.y * half_h);
	}

	gl_Position = f.view_projection * vec4(wp, 1.0);
	vec2 uv = vec2(c.x * 0.5 + 0.5, 0.5 - c.y * 0.5);
	v_uv0 = tile_uv(uv, p.frame, b.tiles_x, b.tiles_y);
	v_uv1 = tile_uv(uv, p.frame + 1.0, b.tiles_x, b.tiles_y);
	v_blend_t = fract(p.frame);
	v_local = c;
	v_view_z = dot(wp - f.eye.xyz, f.cam_forward.xyz);
	v_color = unpack_color(p.color);
	v_fog = fog_amount(center);
	v_light = b.lit != 0u ? particle_light(center) : vec3(1.0);
}
