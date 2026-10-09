// The interpolants standard.metal's VSOut carries (positions/locations shared by VSMain, VSSkinned and PSMain).
#ifndef VORTEX_STANDARD_VSOUT_GLSL
#define VORTEX_STANDARD_VSOUT_GLSL

#ifdef VORTEX_VSOUT_IN
#define VORTEX_VSOUT_QUAL in
#else
#define VORTEX_VSOUT_QUAL out
#endif

layout(location = 0) VORTEX_VSOUT_QUAL vec3 v_world_pos;
layout(location = 1) VORTEX_VSOUT_QUAL vec3 v_norm;
layout(location = 2) VORTEX_VSOUT_QUAL vec2 v_uv;
layout(location = 3) VORTEX_VSOUT_QUAL vec3 v_tangent;
layout(location = 4) VORTEX_VSOUT_QUAL vec3 v_bitangent;
layout(location = 5) VORTEX_VSOUT_QUAL vec4 v_tint;        // per-instance tint (#331)

#endif
