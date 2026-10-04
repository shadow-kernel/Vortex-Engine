// The interpolants particles.metal's PVOut carries.
#ifndef VORTEX_PARTICLES_PVOUT_GLSL
#define VORTEX_PARTICLES_PVOUT_GLSL

#ifdef VORTEX_PVOUT_IN
#define VORTEX_PVOUT_QUAL in
#else
#define VORTEX_PVOUT_QUAL out
#endif

layout(location = 0) VORTEX_PVOUT_QUAL vec2 v_uv0;
layout(location = 1) VORTEX_PVOUT_QUAL vec2 v_uv1;
layout(location = 2) VORTEX_PVOUT_QUAL vec2 v_local;    // quad-local [-1,1] (procedural disc) / ribbon: y = across
layout(location = 3) VORTEX_PVOUT_QUAL float v_blend_t;
layout(location = 4) VORTEX_PVOUT_QUAL float v_view_z;
layout(location = 5) VORTEX_PVOUT_QUAL float v_fog;
layout(location = 6) VORTEX_PVOUT_QUAL vec4 v_color;
layout(location = 7) VORTEX_PVOUT_QUAL vec3 v_light;

#endif
