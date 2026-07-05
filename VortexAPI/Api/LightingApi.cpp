#include "../ApiCommon.h"

EDITOR_INTERFACE void ClearLights()
{
	graphics::dx12::DX12Renderer::instance().clear_lights();
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
	graphics::dx12::DX12Renderer::instance().set_directional_light_full(
		{ dirX, dirY, dirZ },
		{ colorR, colorG, colorB },
		intensity,
		castShadows != 0, shadowStrength, shadowBias, shadowDistance
	);
}

// Add a point light (max 16 per frame). Shadow params (#25): castShadows != 0 requests a 6-face
// cube block in the point shadow atlas — the renderer takes the first two such lights per frame.
// Internal ABI: changed in lockstep with the editor's P/Invoke (both live in this repo).
EDITOR_INTERFACE void AddPointLight(
	float posX, float posY, float posZ,
	float colorR, float colorG, float colorB,
	float intensity, float range,
	int castShadows, float shadowStrength, float shadowBias)
{
	graphics::dx12::DX12Renderer::PointLightData light{};
	light.position = { posX, posY, posZ };
	light.color = { colorR, colorG, colorB };
	light.intensity = intensity;
	light.range = range;
	light.cast_shadows = castShadows != 0 ? 1u : 0u;
	light.shadow_strength = shadowStrength;
	light.shadow_bias = shadowBias;

	graphics::dx12::DX12Renderer::instance().add_point_light(light);
}

// Add a spot light (max 8 per frame). Shadow params (#23): castShadows != 0 requests THIS spot as the
// frame's shadow-casting light — the renderer takes the FIRST such spot (one shadow map in v1, the
// flashlight). Internal ABI: changed in lockstep with the editor's P/Invoke (both live in this repo).
EDITOR_INTERFACE void AddSpotLight(
	float posX, float posY, float posZ,
	float dirX, float dirY, float dirZ,
	float colorR, float colorG, float colorB,
	float intensity, float range,
	float spotAngle, float innerSpotAngle,
	int castShadows, float shadowStrength, float shadowBias, int shadowResolution)
{
	graphics::dx12::DX12Renderer::SpotLightData light{};
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

	graphics::dx12::DX12Renderer::instance().add_spot_light(light);
}

// Set ambient light strength
EDITOR_INTERFACE void SetAmbientStrength(float strength)
{
	graphics::dx12::DX12Renderer::instance().set_ambient_strength(strength);
}

// Scene-wide fog (Welle A #27): exp2 distance fog, optional ground mist below heightY when
// heightFalloff > 0. density <= 0 turns fog off. Colors are linear 0..1.
EDITOR_INTERFACE void SetFogParams(
	float colorR, float colorG, float colorB,
	float density, float heightY, float heightFalloff)
{
	graphics::dx12::DX12Renderer::instance().set_fog({ colorR, colorG, colorB }, density, heightY, heightFalloff);
}


// ============== SKYBOX API ==============

