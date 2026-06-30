#include "../ApiCommon.h"

EDITOR_INTERFACE void ClearLights()
{
	graphics::dx12::DX12Renderer::instance().clear_lights();
}

// Set the primary directional light
EDITOR_INTERFACE void SetDirectionalLight(
	float dirX, float dirY, float dirZ,
	float colorR, float colorG, float colorB,
	float intensity)
{
	graphics::dx12::DX12Renderer::instance().set_directional_light_full(
		{ dirX, dirY, dirZ },
		{ colorR, colorG, colorB },
		intensity
	);
}

// Add a point light (max 16 per frame)
EDITOR_INTERFACE void AddPointLight(
	float posX, float posY, float posZ,
	float colorR, float colorG, float colorB,
	float intensity, float range)
{
	graphics::dx12::DX12Renderer::PointLightData light{};
	light.position = { posX, posY, posZ };
	light.color = { colorR, colorG, colorB };
	light.intensity = intensity;
	light.range = range;
	
	graphics::dx12::DX12Renderer::instance().add_point_light(light);
}

// Add a spot light (max 8 per frame)
EDITOR_INTERFACE void AddSpotLight(
	float posX, float posY, float posZ,
	float dirX, float dirY, float dirZ,
	float colorR, float colorG, float colorB,
	float intensity, float range,
	float spotAngle, float innerSpotAngle)
{
	graphics::dx12::DX12Renderer::SpotLightData light{};
	light.position = { posX, posY, posZ };
	light.direction = { dirX, dirY, dirZ };
	light.color = { colorR, colorG, colorB };
	light.intensity = intensity;
	light.range = range;
	light.spot_angle = spotAngle;
	light.inner_spot_angle = innerSpotAngle;
	
	
	graphics::dx12::DX12Renderer::instance().add_spot_light(light);
}

// Set ambient light strength
EDITOR_INTERFACE void SetAmbientStrength(float strength)
{
	graphics::dx12::DX12Renderer::instance().set_ambient_strength(strength);
}


// ============== SKYBOX API ==============

