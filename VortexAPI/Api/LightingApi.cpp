#include "../ApiCommon.h"
#include "../../Engine/Graphics/Volumetrics/Volumetrics.h"

EDITOR_INTERFACE void ClearLights()
{
	graphics::Renderer::instance().clear_lights();
}

// Set the primary directional light. Shadow params (#24): castShadows != 0 renders cascaded shadow
// maps for the sun (3 snapped ortho cascades out to shadowDistance world units). Internal ABI:
// changed in lockstep with the editor's P/Invoke (both live in this repo).
EDITOR_INTERFACE void SetDirectionalLight(
	float dirX, float dirY, float dirZ,
	float colorR, float colorG, float colorB,
	float intensity,
	int castShadows, float shadowStrength, float shadowBias, float shadowDistance)
{
	graphics::Renderer::instance().set_directional_light_full(
		{ dirX, dirY, dirZ },
		{ colorR, colorG, colorB },
		intensity,
		castShadows != 0, shadowStrength, shadowBias, shadowDistance
	);
}

// The sun's shadow softness: the PCF radius in shadow texels (1 = the classic 3x3 box; Light ▸ Soft passes 4 = a 16-tap
// Poisson disk, a real penumbra). Persistent like the light itself.
EDITOR_INTERFACE void SetDirectionalShadowSoftness(float texels)
{
	graphics::Renderer::instance().set_directional_shadow_softness(texels);
}

// Add a point light for this frame — any number: the renderer gives its 16 shader slots to the most relevant ones per view
// (#335, #416). Shadow params (#25): castShadows != 0 requests a 6-face cube block in the point shadow atlas (two per
// view). lightId (#416) is the light's stable identity — the entity — so the renderer can keep its slot from frame to
// frame and fade it in / out when the budget changes; 0 = derived from the light's look.
// Internal ABI: changed in lockstep with the editor's P/Invoke (both live in this repo).
EDITOR_INTERFACE void AddPointLightWithId(
	float posX, float posY, float posZ,
	float colorR, float colorG, float colorB,
	float intensity, float range,
	int castShadows, float shadowStrength, float shadowBias, uint64_t lightId)
{
	graphics::Renderer::PointLightData light{};
	light.position = { posX, posY, posZ };
	light.color = { colorR, colorG, colorB };
	light.intensity = intensity;
	light.range = range;
	light.cast_shadows = castShadows != 0 ? 1u : 0u;
	light.shadow_strength = shadowStrength;
	light.shadow_bias = shadowBias;
	light.id = lightId;

	graphics::Renderer::instance().add_point_light(light);
}

// The same without an identity (older hosts): the renderer derives one from the light's look.
EDITOR_INTERFACE void AddPointLight(
	float posX, float posY, float posZ,
	float colorR, float colorG, float colorB,
	float intensity, float range,
	int castShadows, float shadowStrength, float shadowBias)
{
	AddPointLightWithId(posX, posY, posZ, colorR, colorG, colorB, intensity, range, castShadows, shadowStrength, shadowBias, 0);
}

// Add a spot light for this frame — any number: the renderer gives its 8 shader slots to the most relevant ones per view
// (#335, #416), the spot cone counting. Shadow params (#23): castShadows != 0 requests one of the spot shadow maps (four
// per view, kept by the spots that hold them unless clearly outranked). lightId (#416): the stable identity (the entity),
// 0 = derived from the light's look. Internal ABI: changed in lockstep with the editor's P/Invoke (both live in this repo).
EDITOR_INTERFACE void AddSpotLightWithId(
	float posX, float posY, float posZ,
	float dirX, float dirY, float dirZ,
	float colorR, float colorG, float colorB,
	float intensity, float range,
	float spotAngle, float innerSpotAngle,
	int castShadows, float shadowStrength, float shadowBias, int shadowResolution, uint64_t lightId)
{
	graphics::Renderer::SpotLightData light{};
	light.position = { posX, posY, posZ };
	light.direction = { dirX, dirY, dirZ };
	light.color = { colorR, colorG, colorB };
	light.intensity = intensity;
	light.range = range;
	light.spot_angle = spotAngle;
	light.inner_spot_angle = innerSpotAngle;
	light.cast_shadows = castShadows != 0 ? 1u : 0u;
	light.shadow_strength = shadowStrength;
	light.shadow_bias = shadowBias;
	light.shadow_resolution = shadowResolution > 0 ? (u32)shadowResolution : 2048u;
	light.id = lightId;

	graphics::Renderer::instance().add_spot_light(light);
}

// The same without an identity (older hosts): the renderer derives one from the light's look.
EDITOR_INTERFACE void AddSpotLight(
	float posX, float posY, float posZ,
	float dirX, float dirY, float dirZ,
	float colorR, float colorG, float colorB,
	float intensity, float range,
	float spotAngle, float innerSpotAngle,
	int castShadows, float shadowStrength, float shadowBias, int shadowResolution)
{
	AddSpotLightWithId(posX, posY, posZ, dirX, dirY, dirZ, colorR, colorG, colorB, intensity, range, spotAngle, innerSpotAngle,
		castShadows, shadowStrength, shadowBias, shadowResolution, 0);
}

// The light budget of the main view's last frame (#416): point / spot lights submitted, those holding one of the shader's
// 16 point / 8 spot slots, and the lights that got or gave up a slot in that frame.
EDITOR_INTERFACE void GetLightBudgetStats(int* pointsSubmitted, int* spotsSubmitted, int* pointsShown, int* spotsShown, int* slotChanges)
{
	const auto& s = graphics::Renderer::instance().budget_stats();
	if (pointsSubmitted) *pointsSubmitted = (int)s.point_lights_submitted;
	if (spotsSubmitted) *spotsSubmitted = (int)s.spot_lights_submitted;
	if (pointsShown) *pointsShown = (int)s.point_lights_shown;
	if (spotsShown) *spotsShown = (int)s.spot_lights_shown;
	if (slotChanges) *slotChanges = (int)s.light_slot_changes;
}

// Set ambient light strength
EDITOR_INTERFACE void SetAmbientStrength(float strength)
{
	graphics::Renderer::instance().set_ambient_strength(strength);
}

// Scene-wide fog (Welle A #27): exp2 distance fog, optional ground mist below heightY when
// heightFalloff > 0. density <= 0 turns fog off. Colors are linear 0..1.
EDITOR_INTERFACE void SetFogParams(
	float colorR, float colorG, float colorB,
	float density, float heightY, float heightFalloff)
{
	graphics::Renderer::instance().set_fog({ colorR, colorG, colorB }, density, heightY, heightFalloff);
}

// Scene exposure: the multiplier the surface shaders apply BEFORE the ACES curve (0.5 = the classic look, one stop up
// = x2). The editor passes 0.5 * 2^stops. Persistent frame state like the fog.
EDITOR_INTERFACE void SetSceneExposure(float multiplier)
{
	graphics::Renderer::instance().set_scene_exposure(multiplier);
}

// The tonemapper of every lit surface, particle, decal and the HDR sky: 0 = the ACES fit (the classic look, desaturates
// bright colours), 1 = Khronos PBR Neutral (keeps hue and saturation; a sunlit desert stays orange).
EDITOR_INTERFACE void SetTonemapper(int32_t mode)
{
	graphics::Renderer::instance().set_tonemapper(mode > 0 ? 1u : 0u);
}

// Aerial perspective: the sun's forward-scatter glow in the fog when looking towards it (0 = the flat fog colour).
EDITOR_INTERFACE void SetFogSunGlow(float glow)
{
	graphics::Renderer::instance().set_fog_sun_glow(glow);
}

// Volumetric fog (#119) — Engine/Graphics/Volumetrics/Volumetrics.h. enabled == 0 or density <= 0 = the analytic fog alone.
// Internal ABI: changed in lockstep with Editor/DllWrapper/VortexAPI.Volumetrics.cs.
EDITOR_INTERFACE void SetVolumetricFogParams(int32_t enabled, float density, float anisotropy, float maxDistance,
	float noiseStrength, float noiseScale, float noiseSpeed, float sun, float lights, float ambient, int32_t steps, int32_t shadows)
{
	auto clampf = [](float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); };
	graphics::volumetrics::Params p{};
	p.enabled = enabled != 0 && density > 0.0f;
	p.density = density > 0.0f ? density : 0.0f;
	p.anisotropy = clampf(anisotropy, -0.95f, 0.95f);
	p.max_distance = maxDistance < 1.0f ? 1.0f : maxDistance;
	p.noise_strength = clampf(noiseStrength, 0.0f, 1.0f);
	p.noise_scale = noiseScale < 0.1f ? 0.1f : noiseScale;
	p.noise_speed = noiseSpeed;
	p.sun = sun < 0.0f ? 0.0f : sun;
	p.lights = lights < 0.0f ? 0.0f : lights;
	p.ambient = ambient < 0.0f ? 0.0f : ambient;
	p.steps = (u32)(steps < 4 ? 4 : (steps > 64 ? 64 : steps));
	p.shadows = shadows != 0;
	graphics::Renderer::instance().set_volumetric_fog(p);
}


// ============== SKYBOX API ==============

