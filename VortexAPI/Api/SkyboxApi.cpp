#include "../ApiCommon.h"

EDITOR_INTERFACE void SetSkyboxEnabled(bool enabled)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_enabled(enabled);
}

EDITOR_INTERFACE bool IsSkyboxEnabled()
{
	return graphics::dx12::DX12Renderer::instance().is_skybox_enabled();
}

EDITOR_INTERFACE void SetSkyboxMode(unsigned int mode)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_mode(
		static_cast<graphics::dx12::DX12Renderer::SkyboxMode>(mode));
}

EDITOR_INTERFACE unsigned int GetSkyboxMode()
{
	return static_cast<unsigned int>(graphics::dx12::DX12Renderer::instance().get_skybox_mode());
}

EDITOR_INTERFACE void SetSkyboxColors(
	float skyR, float skyG, float skyB,
	float horizonR, float horizonG, float horizonB,
	float groundR, float groundG, float groundB)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_colors(
		{ skyR, skyG, skyB },
		{ horizonR, horizonG, horizonB },
		{ groundR, groundG, groundB }
	);
}

EDITOR_INTERFACE void SetSkyboxSolidColor(float r, float g, float b)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_solid_color({ r, g, b });
}

EDITOR_INTERFACE void SetSkyboxSun(float dirX, float dirY, float dirZ, float colorR, float colorG, float colorB, float intensity)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_sun(
		{ dirX, dirY, dirZ },
		{ colorR, colorG, colorB },
		intensity);
}

// ============== SKYBOX COMPONENT API (Runtime) ==============

namespace {
	struct skybox_descriptor {
		u8 mode; // 0 = solid, 1 = gradient, 2 = cubemap
		f32 sky_color[3];
		f32 horizon_color[3];
		f32 ground_color[3];
		f32 sun_direction[3];
		f32 sun_color[3];
		f32 sun_intensity;
		f32 ambient_intensity;
		f32 exposure;
		bool is_enabled;
	};
}

EDITOR_INTERFACE id::id_type CreateSkyboxComponent(skybox_descriptor* desc)
{
	if (!desc) return id::invalid_id;
	
	skybox::init_info info{};
	info.mode = static_cast<skybox::skybox_mode>(desc->mode);
	memcpy(info.sky_color, desc->sky_color, sizeof(f32) * 3);
	memcpy(info.horizon_color, desc->horizon_color, sizeof(f32) * 3);
	memcpy(info.ground_color, desc->ground_color, sizeof(f32) * 3);
	memcpy(info.sun_direction, desc->sun_direction, sizeof(f32) * 3);
	memcpy(info.sun_color, desc->sun_color, sizeof(f32) * 3);
	info.sun_intensity = desc->sun_intensity;
	info.ambient_intensity = desc->ambient_intensity;
	info.exposure = desc->exposure;
	info.is_enabled = desc->is_enabled;
	
	return skybox::create(info).get_id();
}

EDITOR_INTERFACE void RemoveSkyboxComponent(id::id_type skybox_id)
{
	skybox::remove(skybox::component{ skybox::skybox_id{skybox_id} });
}

EDITOR_INTERFACE void ApplySkyboxToRenderer(id::id_type skybox_id)
{
	skybox::component skybox{ skybox::skybox_id{skybox_id} };
	if (skybox.is_valid())
	{
		skybox.apply_to_renderer();
	}
}

EDITOR_INTERFACE void ApplyActiveSkybox()
{
	auto skybox = skybox::get_active_skybox();
	if (skybox.is_valid())
	{
		skybox.apply_to_renderer();
	}
}

EDITOR_INTERFACE void SetActiveSkyboxComponent(id::id_type skybox_id)
{
	skybox::set_active_skybox(skybox::component{ skybox::skybox_id{skybox_id} });
}
