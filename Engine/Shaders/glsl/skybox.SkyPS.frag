#version 450
#extension GL_GOOGLE_include_directive : require
// Sky/horizon/ground gradient + sun disc + glow, the equirect texture sky with the sun's disc (#499), and up to two more
// pictures cross-faded over either (#464) (skybox.metal SkyPS).
#define SET_UNIFORM 3
#include "common.glsl"
#include "skybox_common.glsl"

layout(set = 2, binding = 0) uniform sampler2D u_sky;      // the equirect sky (#326), white when unused
layout(set = 2, binding = 1) uniform sampler2D u_blend1;   // the cross-fade pictures (#464), white when unused
layout(set = 2, binding = 2) uniform sampler2D u_blend2;

layout(location = 0) in  vec3 v_world_dir;
layout(location = 0) out vec4 o_color;

// The sun's disc at the scene's directional light (#499), its corona and aureole, as that light's (skybox.metal sun_light):
// in its colour, brightening with its intensity, cut off by the horizon. hdr = linear radiance before the tonemapper, else
// display values for sun_over(); the disc is the light's (the picture's rotation / exposure do not move or dim it).
vec3 sun_light(vec3 dir, bool hdr)
{
	vec3 to_sun = -c.glow_dir.xyz;
	float d = length(dir - to_sun);
	float t = d / max(c.sun_disc.x, 1e-5);
	float aa = clamp(fwidth(t), 0.02, 0.5);   // before the branch below
	if (d > max(0.3, 9.0 * c.sun_disc.x)) return vec3(0.0);   // the aureole is gone 17° out, the corona 9 radii out
	float above = smoothstep(-0.01, 0.01, dir.y);   // the horizon hides what is below it
	float disc = 1.0 - smoothstep(1.0 - aa, 1.0 + aa, t);
	float limb = sqrt(saturate(1.0 - t * t));
	float aureole = pow(saturate(1.0 - 0.5 * d * d), 256.0);
	float li = c.glow_color.w * 0.4;   // the light's intensity against the default sun's 2.5
	if (hdr)
	{
		float corona = exp(-max(t - 1.0, 0.0) * 0.8);
		return c.glow_color.rgb * (li * above) * (disc * (0.6 + 0.4 * limb) * 40.0 * c.sun_disc.y + c.sun_disc.z * (1.5 * corona + 0.35 * aureole));
	}
	float corona = exp(-max(t - 1.0, 0.0) * 2.5);
	vec3 hue = c.glow_color.rgb / max(max(c.glow_color.r, c.glow_color.g), max(c.glow_color.b, 1e-4));
	return hue * (saturate(li) * above) * (disc * (0.55 + 0.45 * limb) * c.sun_disc.y + (1.0 - disc) * c.sun_disc.z * (0.18 * corona + 0.15 * aureole));
}

// Display values: the sun laid over the sky like a light (screen), never past white.
vec3 sun_over(vec3 sky, vec3 sun) { return 1.0 - (1.0 - saturate(sky)) * (1.0 - saturate(sun)); }

// One equirect picture as the display values the pass writes (skybox.metal sky_picture): mode > 0 through the haze, the
// scene exposure and the tonemapper; < 0 a float picture the classic way; 0 an 8-bit picture as it is.
// (u0, v): the view direction's equirect coordinates without a yaw, shared by the layers.
vec3 sky_picture(vec3 dir, float u0, float v, sampler2D tex, float exposure, float rotation, float mode, bool sun)
{
	float y = dir.y;
	vec3 sky = textureLod(tex, vec2(u0 + rotation / 6.28318530718, v), 0.0).rgb * exposure;
	if (mode > 0.0 && c.haze.w > 0.0)
	{
		// aerial perspective on the sky: the horizon whitens into the scene's haze, the sun glows in it, the zenith keeps its blue
		float h = max(y, 0.0);
		float f = 1.0 - exp(-c.haze.w * 40.0 / (h + 0.04));
		f = max(f, smoothstep(0.0, 0.08, -y));   // below the horizon: the haze, not the picture's blurred ground (see skybox.metal)
		float mu = saturate(dot(dir, normalize(-c.glow_dir.xyz)));
		vec3 hz = c.haze.rgb + c.glow_color.rgb * (c.glow_dir.w * (0.12 * mu * mu + 0.88 * pow(mu, 10.0)));
		sky = mix(sky, hz, f);
	}
	if (sun && mode > 0.0) sky += sun_light(dir, true);   // on top of the haze: a setting sun stays a disc
	if (mode < 0.0) sky = pow(saturate(sky), vec3(1.0 / 2.2));   // a float picture the classic way: gamma-encoded, clamped
	else if (mode > 0.0)
	{
		// an HDR picture: the scene exposure + ACES + gamma of the lit surfaces, so the sky and the world agree
		sky = pow(tonemap_scene(sky, mode, c.tonemapper > 0.5 ? 1u : 0u), vec3(1.0 / 2.2));
	}
	if (sun && mode <= 0.0) sky = sun_over(sky, sun_light(dir, false));   // an 8-bit picture / the classic float one: display values
	return sky;
}

// The three-colour gradient sky, its sun and a faint noise.
vec3 sky_gradient(vec3 dir, bool sun)
{
	float y = dir.y;
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
	if (sun)
	{
		float horizon_glow = pow(1.0 - abs(y), 4.0) * pow(max(dot(dir, normalize(-c.glow_dir.xyz)), 0.0), 2.0);
		color += c.glow_color.rgb * horizon_glow * 0.3 * c.sun_disc.z * saturate(c.glow_color.w * 0.4);
		color = sun_over(color, sun_light(dir, false));
	}

	float noise = fract(sin(dot(dir.xz, vec2(12.9898, 78.233))) * 43758.5453);
	color += (noise - 0.5) * 0.01;
	return color;
}

void main()
{
	vec3 dir = normalize(v_world_dir);
	// the layers that draw the sun's disc (#499): bit 0 the sky, bits 1 / 2 the cross-fade pictures
	uint sun = c.glow_color.w > 0.001 ? uint(c.sun_disc.w) : 0u;
	// the equirect coordinates once for every picture layer (a gradient sky alone needs none)
	float u0 = 0.0, v = 0.0;
	if (c.params.x > 0.5 || c.blend1.x > 0.0 || c.blend2.x > 0.0)
	{
		u0 = atan(dir.x, dir.z) / 6.28318530718 + 0.5;
		v = acos(clamp(dir.y, -1.0, 1.0)) / 3.14159265359;
	}
	// the sky: an equirect picture (#326) at the far plane, centred on the rendering camera — or the gradient
	vec3 col = c.params.x > 0.5 ? sky_picture(dir, u0, v, u_sky, c.params.y, c.params.z, c.params.w, (sun & 1u) != 0u)
	                            : sky_gradient(dir, (sun & 1u) != 0u);
	// the cross-fade (#464): up to two more pictures over it, each by its amount — one at 0 is not sampled at all
	if (c.blend1.x > 0.0) col = mix(col, sky_picture(dir, u0, v, u_blend1, c.blend1.y, c.blend1.z, c.blend1.w, (sun & 2u) != 0u), c.blend1.x);
	if (c.blend2.x > 0.0) col = mix(col, sky_picture(dir, u0, v, u_blend2, c.blend2.y, c.blend2.z, c.blend2.w, (sun & 4u) != 0u), c.blend2.x);
	o_color = vec4(col, 1.0);
}
