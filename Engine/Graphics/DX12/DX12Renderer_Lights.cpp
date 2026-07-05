#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	void DX12Renderer::set_directional_light(const DirectX::XMFLOAT3& dir, const DirectX::XMFLOAT3& col)
	{
		m_light_direction = dir; m_light_color = col;
	}


	void DX12Renderer::set_ambient_strength(float s) { m_ambient_strength = s; }


	void DX12Renderer::update_per_frame_constants()
	{
	using namespace DirectX;
	XMVECTOR eye = XMLoadFloat3(&m_camera_position);
	XMVECTOR at = XMLoadFloat3(&m_camera_target);
	XMVECTOR up = XMLoadFloat3(&m_camera_up);

	XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
	float aspect = (float)m_swapchain.width() / (float)m_swapchain.height();
	XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, 0.1f, 1000.0f); // settable FOV (game camera)
	XMMATRIX vp = view * proj;

	// Track last frame's VP (motion vectors + DLSS clipToPrevClip) before overwriting it.
	m_prev_view_projection = m_frame_constants.view_projection;
	XMStoreFloat4x4(&m_frame_constants.view_projection, vp);
	m_frame_constants.camera_position = m_camera_position;
	m_frame_constants.light_direction = m_light_direction;
	m_frame_constants.directional_intensity = m_directional_intensity;
	m_frame_constants.light_color = m_light_color;
	m_frame_constants.ambient_strength = m_ambient_strength;
	m_frame_constants.point_light_count = static_cast<u32>(m_point_lights.size());
	m_frame_constants.spot_light_count = static_cast<u32>(m_spot_lights.size());

	// Spot shadows (#23): select the shadow-casting spot + fill the shadow fields BEFORE the upload
	// below (also primes the shadow pass's own light-VP b0 clone).
	prepare_shadow_pass();

	if (m_per_frame_cb_mapped)
	memcpy(m_per_frame_cb_mapped, &m_frame_constants, sizeof(m_frame_constants));
			
	// Update light buffer
	if (m_light_cb_mapped)
	{
	u8* ptr = static_cast<u8*>(m_light_cb_mapped);
			
	// Copy point lights
	size_t point_light_size = m_point_lights.size() * sizeof(GPUPointLight);
	if (point_light_size > 0)
	{
	for (size_t i = 0; i < m_point_lights.size() && i < MAX_POINT_LIGHTS; ++i)
	{
		GPUPointLight gpu_light{};
		gpu_light.position = m_point_lights[i].position;
		gpu_light.range = m_point_lights[i].range;
		gpu_light.color = m_point_lights[i].color;
		gpu_light.intensity = m_point_lights[i].intensity;
		memcpy(ptr + i * sizeof(GPUPointLight), &gpu_light, sizeof(GPUPointLight));
	}
	}
			
	// Copy spot lights (after point lights)
	u8* spot_ptr = ptr + MAX_POINT_LIGHTS * sizeof(GPUPointLight);
	for (size_t i = 0; i < m_spot_lights.size() && i < MAX_SPOT_LIGHTS; ++i)
	{
	GPUSpotLight gpu_light{};
	gpu_light.position = m_spot_lights[i].position;
	gpu_light.range = m_spot_lights[i].range;
		
	// Normalize direction
	DirectX::XMFLOAT3 dir = m_spot_lights[i].direction;
	float len = sqrtf(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
	if (len > 0.0001f) {
	dir.x /= len;
	dir.y /= len;
	dir.z /= len;
	}
	gpu_light.direction = dir;
		
	gpu_light.spot_angle = m_spot_lights[i].spot_angle;
	gpu_light.color = m_spot_lights[i].color;
	gpu_light.intensity = m_spot_lights[i].intensity;
	gpu_light.inner_spot_angle = m_spot_lights[i].inner_spot_angle;

	// Spot shadows (#23): map this spot to its atlas tile (prepare_shadow_pass ran just above).
	// slot -1 = no shadow for this spot; the shader skips the sample entirely.
	gpu_light.shadow_slot = -1.0f;
	gpu_light.shadow_strength = 0.0f;
	gpu_light.shadow_bias = 0.0f;
	for (u32 t = 0; t < m_shadow_spot_count; ++t)
	{
		if (m_shadow_spots[t].spot_index == (int)i)
		{
			gpu_light.shadow_slot = (float)t;
			float st = m_spot_lights[i].shadow_strength;
			gpu_light.shadow_strength = st < 0.0f ? 0.0f : (st > 1.0f ? 1.0f : st);
			gpu_light.shadow_bias = m_spot_lights[i].shadow_bias;
			break;
		}
	}
	memcpy(spot_ptr + i * sizeof(GPUSpotLight), &gpu_light, sizeof(GPUSpotLight));
	}

	// Shadow atlas VPs (#23): the light buffer tail @1024 (16*32B points + 8*64B spots) carries the
	// four tile view-projections — the buffer was created 1280 bytes wide, so the tail fits exactly.
	{
		u8* vp_ptr = ptr + MAX_POINT_LIGHTS * sizeof(GPUPointLight) + MAX_SPOT_LIGHTS * sizeof(GPUSpotLight);
		for (u32 t = 0; t < MAX_SHADOW_SPOTS; ++t)
		{
			if (t < m_shadow_spot_count)
				memcpy(vp_ptr + (size_t)t * 64, &m_shadow_spots[t].vp, 64);
			else
			{
				DirectX::XMFLOAT4X4 ident; DirectX::XMStoreFloat4x4(&ident, DirectX::XMMatrixIdentity());
				memcpy(vp_ptr + (size_t)t * 64, &ident, 64);   // unused tiles: never sampled (slot -1)
			}
		}
	}
	}
	}


	void DX12Renderer::clear_lights()
	{
	m_point_lights.clear();
	m_spot_lights.clear();
	}

	void DX12Renderer::set_fog(const DirectX::XMFLOAT3& color, float density, float height_y, float height_falloff)
	{
		// Persistent frame state: update_per_frame_constants re-uploads m_frame_constants every frame,
		// and the offscreen path copies it too — so one call here reaches every render path.
		m_frame_constants.fog_color = color;
		m_frame_constants.fog_density = density > 0.0f ? density : 0.0f;   // NaN/negative-proof (shader gates on <= 0)
		m_frame_constants.fog_height_y = height_y;
		m_frame_constants.fog_height_falloff = height_falloff > 0.0f ? height_falloff : 0.0f;
		m_frame_constants.fog_mode = 0;
	}
	

	void DX12Renderer::set_directional_light_full(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity)
	{
	m_light_direction = direction;
	m_light_color = color;
	m_directional_intensity = intensity;
	}
	

	void DX12Renderer::add_point_light(const PointLightData& light)
	{
	if (m_point_lights.size() < MAX_POINT_LIGHTS)
	{
	m_point_lights.push_back(light);
	}
	}
	

	void DX12Renderer::add_spot_light(const SpotLightData& light)
	{
	if (m_spot_lights.size() < MAX_SPOT_LIGHTS)
	{
	m_spot_lights.push_back(light);
	}
	}
	
	// ============== Multi-Viewport Rendering ==============
	

}
