#version 450
#extension GL_GOOGLE_include_directive : require
// Sky/horizon/ground gradient + sun disc + glow, and the equirect texture sky with the sun's disc (#499) (skybox.metal SkyPS).
#define SET_UNIFORM 3
#include "common.glsl"
#include "skybox_common.glsl"

layout(set = 2, binding = 0) uniform sampler2D u_sky;   // the equirect sky (#326), white when unused

layout(location = 0) in  vec3 v_world_dir;
layout(location = 0) out vec4 o_color;

// The sun's disc at the scene's directional light (#499) — see skybox.metal: hdr = linear radiance before the tonemapper,
// else display values; the disc is the light's (the picture's rotation / exposure do not move or dim it).
vec3 sun_disc(vec3 dir, bool hdr)
{
	vec3 to_sun = -c.glow_dir.xyz;
	float d = length(dir - to_sun);
	float t = d / max(c.sun_disc.x, 1e-5);
	float aa = clamp(fwidth(t), 0.02, 0.5);   // before the branch below
	if (d > max(0.3, 9.0 * c.sun_disc.x)) return vec3(0.0);   // the aureole is gone 17° out, the corona 9 radii out
	float disc = 1.0 - smoothstep(1.0 - aa, 1.0 + aa, t);
	float limb = 0.6 + 0.4 * sqrt(saturate(1.0 - t * t));
	float corona = exp(-max(t - 1.0, 0.0) * 0.8);
	float aureole = pow(saturate(1.0 - 0.5 * d * d), 256.0);
	vec3 col = c.glow_color.rgb;
	return hdr ? col * (disc * limb * 40.0 * c.sun_disc.y + c.sun_disc.z * (1.5 * corona + 0.35 * aureole))
	           : col * (disc * limb * 10.0 * c.sun_disc.y + c.sun_disc.z * (0.5 * corona + 0.5 * aureole));
}

void main()
{
	vec3 dir = normalize(v_world_dir);
	float y = dir.y;

	// Equirect texture sky (#326): sampled at the far plane, no depth write, centred on the rendering camera.
	if (c.params.x > 0.5)
	{
		const float two_pi = 6.28318530718;
		float u = atan(dir.x, dir.z) / two_pi + 0.5 + c.params.z / two_pi;
		float v = acos(clamp(y, -1.0, 1.0)) / 3.14159265359;
		vec3 sky = textureLod(u_sky, vec2(u, v), 0.0).rgb * c.params.y;
		bool sun = c.sun_disc.w > 0.5 && c.glow_color.w > 0.001;
		if (c.params.w > 0.0 && c.haze.w > 0.0)
		{
			// aerial perspective on the sky: the horizon whitens into the scene's haze, the sun glows in it, the zenith keeps its blue
			float h = max(y, 0.0);
			float f = 1.0 - exp(-c.haze.w * 40.0 / (h + 0.04));
			f = max(f, smoothstep(0.0, 0.08, -y));   // below the horizon: the haze, not the picture's blurred ground (see skybox.metal)
			float mu = saturate(dot(dir, normalize(-c.glow_dir.xyz)));
			vec3 hz = c.haze.rgb + c.glow_color.rgb * (c.glow_dir.w * (0.12 * mu * mu + 0.88 * pow(mu, 10.0)));
			sky = mix(sky, hz, f);
		}
		if (sun && c.params.w > 0.0) sky += sun_disc(dir, true);   // on top of the haze: a setting sun stays a disc
		if (c.params.w < 0.0) sky = pow(saturate(sky), vec3(1.0 / 2.2));   // a float picture the classic way: gamma-encoded, clamped
		else if (c.params.w > 0.0)
		{
			// an HDR picture: the scene exposure + ACES + gamma of the lit surfaces, so the sky and the world agree
			sky = pow(tonemap_scene(sky, c.params.w, c.tonemapper > 0.5 ? 1u : 0u), vec3(1.0 / 2.2));
		}
		if (sun && c.params.w <= 0.0) sky += sun_disc(dir, false);   // an 8-bit picture / the classic float one: display values
		o_color = vec4(sky, 1.0);
		return;
	}
	vec3 color;
	if (y > 0.0)
	{
		float t = pow(y, 0.4);
		color = mix(c.horizon_color, c.sky_color, t);
	}
	else
	{
		float t = pow(-y, 0.7);
		color = mix(c.horizon_color, c.ground_color, t);
	}

	// the sun at the scene's directional light (#499 — it used to sit at a fixed direction whatever the light did)
	if (c.sun_disc.w > 0.5 && c.glow_color.w > 0.001)
	{
		color += sun_disc(dir, false);
		float horizon_glow = pow(1.0 - abs(y), 4.0) * pow(max(dot(dir, normalize(-c.glow_dir.xyz)), 0.0), 2.0);
		color += c.glow_color.rgb * horizon_glow * 0.3 * c.sun_disc.z;
	}

	float noise = fract(sin(dot(dir.xz, vec2(12.9898, 78.233))) * 43758.5453);
	color += (noise - 0.5) * 0.01;
	o_color = vec4(color, 1.0);
}
