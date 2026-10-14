#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	void DX12Renderer::set_directional_light(const DirectX::XMFLOAT3& dir, const DirectX::XMFLOAT3& col)
	{
		m_light_direction = dir; m_light_color = col;
	}


	void DX12Renderer::set_ambient_strength(float s) { m_ambient_strength = s; }


	void DX12Renderer::update_per_frame_constants(int view_slot)
	{
	using namespace DirectX;
	XMVECTOR eye = XMLoadFloat3(&m_camera_position);
	XMVECTOR at = XMLoadFloat3(&m_camera_target);
	XMVECTOR up = XMLoadFloat3(&m_camera_up);

	XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
	float aspect = (float)m_swapchain.width() / (float)m_swapchain.height();
	XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_fov_degrees), aspect, m_near_clip, m_far_clip); // settable FOV + clip planes (game camera, #327)
	XMMATRIX vp = view * proj;

	// Track last frame's VP (motion vectors + DLSS clipToPrevClip) before overwriting it.
	m_prev_view_projection = m_frame_constants.view_projection;
	XMStoreFloat4x4(&m_frame_constants.view_projection, vp);
	// #335 / #416: this view's choice from ALL submitted lights, every frame, with its own hysteresis + fades (the editor
	// frame and the game window keep separate ones)
	select_lights(m_light_views[view_slot == 1 ? 1 : 0], m_camera_position, m_frame_constants.view_projection);
	m_frame_constants.camera_position = m_camera_position;
	m_frame_constants.light_direction = m_light_direction;
	m_frame_constants.directional_intensity = m_directional_intensity;
	m_frame_constants.light_color = m_light_color;
	m_frame_constants.ambient_strength = m_ambient_strength;
	m_frame_constants.point_light_count = static_cast<u32>(m_view_points.size());
	m_frame_constants.spot_light_count = static_cast<u32>(m_view_spots.size());
	// SSAO (#32): the standard PS samples t10 only when this is set; if a view's record_ssao
	// can't run, its reserved t10 slot still holds a valid (stale/neutral) texture.
	m_frame_constants.ssao_enabled = (m_ssao_enabled && m_ssao_pso) ? 1.0f : 0.0f;
	m_frame_constants.gi_strength = (m_ssao_enabled && m_ssao_pso && m_gi_enabled) ? m_gi_strength : 0.0f;   // screen-space GI rides the AO texture
	{
		const float env_on = (m_skybox_enabled && (m_skybox_mode == SkyboxMode::Gradient || m_skybox_mode == SkyboxMode::Texture)) ? 1.0f : 0.0f;
		const bool env_map = env_on > 0.5f && m_env_map != id::invalid_id && m_env_mips > 0;
		m_frame_constants.env_sky = XMFLOAT4(m_env_sky.x, m_env_sky.y, m_env_sky.z, env_map ? 2.0f : env_on);   // 2 = a reflection map is bound at t11
		m_frame_constants.env_horizon = XMFLOAT4(m_env_horizon.x, m_env_horizon.y, m_env_horizon.z, env_map ? (float)m_env_mips : 0.0f);
		// w: a texture sky's rotation (radians, #446) — the reflection map is baked unrotated, the lookups add it like the
		// skybox pass does, so reflections line up with the visible sky (a gradient sky has no rotation)
		const bool sky_rotated = env_map && m_skybox_mode == SkyboxMode::Texture && m_sky_texture != id::invalid_id;
		m_frame_constants.env_ground = XMFLOAT4(m_env_ground.x, m_env_ground.y, m_env_ground.z, sky_rotated ? m_sky_rotation : 0.0f);
		m_frame_constants.camera_clip = XMFLOAT4(m_near_clip, m_far_clip, 0.0f, 0.0f);
	}

	// Spot shadows (#23): select the shadow-casting spot + fill the shadow fields BEFORE the upload
	// below (also primes the shadow pass's own light-VP b0 clone).
	prepare_shadow_pass();

	if (m_per_frame_cb_mapped)
	memcpy(m_per_frame_cb_mapped, &m_frame_constants, sizeof(m_frame_constants));

	// Viewmodel b0 clone (#175): identical frame constants with ONLY the projection swapped to the
	// viewmodel FOV (same view, same near/far, same aspect) — world FOV distortion never reaches the
	// first-person arms/weapon. Written here (view matrix is local) like the main copy above.
	{
		XMMATRIX vmProj = XMMatrixPerspectiveFovLH(XMConvertToRadians(m_viewmodel_fov), aspect, VIEWMODEL_NEAR, VIEWMODEL_FAR);
		XMStoreFloat4x4(&m_viewmodel_view_projection, view * vmProj);   // the viewmodel particle layer (#117) projects with it too
	}
	if (m_viewmodel_cb_mapped)
	{
		PerFrameConstants vm = m_frame_constants;
		vm.view_projection = m_viewmodel_view_projection;
		memcpy(m_viewmodel_cb_mapped, &vm, sizeof(vm));
	}
			
	// Update light buffer
	if (m_light_cb_mapped)
	{
	u8* ptr = static_cast<u8*>(m_light_cb_mapped);
			
	// Copy point lights: the view's choice in slot order, a light entering or leaving the slots faded (#416)
	for (size_t i = 0; i < m_view_points.size() && i < MAX_POINT_LIGHTS; ++i)
	{
		GPUPointLight gpu_light{};
		const lighting::Selected& sel = m_view_points[i];
		if (sel.index < m_point_lights.size())
		{
			const PointLightData& pl = m_point_lights[sel.index];
			gpu_light.position = pl.position;
			gpu_light.range = pl.range;
			gpu_light.color = pl.color;
			gpu_light.intensity = pl.intensity * sel.fade;
		}
		memcpy(ptr + i * sizeof(GPUPointLight), &gpu_light, sizeof(GPUPointLight));
	}
			
	// Copy spot lights (after point lights), the same way
	u8* spot_ptr = ptr + MAX_POINT_LIGHTS * sizeof(GPUPointLight);
	for (size_t i = 0; i < m_view_spots.size() && i < MAX_SPOT_LIGHTS; ++i)
	{
	GPUSpotLight gpu_light{};
	gpu_light.shadow_slot = -1.0f;
	const lighting::Selected& sel = m_view_spots[i];
	if (sel.index >= m_spot_lights.size()) { memcpy(spot_ptr + i * sizeof(GPUSpotLight), &gpu_light, sizeof(GPUSpotLight)); continue; }
	const SpotLightData& sl = m_spot_lights[sel.index];
	gpu_light.position = sl.position;
	gpu_light.range = sl.range;
		
	// Normalize direction
	DirectX::XMFLOAT3 dir = sl.direction;
	float len = sqrtf(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
	if (len > 0.0001f) {
	dir.x /= len;
	dir.y /= len;
	dir.z /= len;
	}
	gpu_light.direction = dir;
		
	gpu_light.spot_angle = sl.spot_angle;
	gpu_light.color = sl.color;
	gpu_light.intensity = sl.intensity * sel.fade;
	gpu_light.inner_spot_angle = sl.inner_spot_angle;

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
			float st = sl.shadow_strength;
			gpu_light.shadow_strength = (st < 0.0f ? 0.0f : (st > 1.0f ? 1.0f : st)) * sel.shadow_fade;   // a handover fades (#416)
			gpu_light.shadow_bias = sl.shadow_bias;
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

		// CSM tail (#24) @1280 (buffer grown to 1536): CascadeVP[3] + CascadeSplits + DirShadowParams —
		// byte-matched to standard.hlsl's LightBuffer. Cascade count 0 keeps the shader's sample a no-op.
		u8* csm_ptr = vp_ptr + (size_t)MAX_SHADOW_SPOTS * 64;
		DirectX::XMFLOAT4X4 ident; DirectX::XMStoreFloat4x4(&ident, DirectX::XMMatrixIdentity());
		for (u32 c = 0; c < CSM_CASCADES; ++c)
			memcpy(csm_ptr + (size_t)c * 64, (c < m_csm_count) ? &m_csm_vp[c] : &ident, 64);
		float splits[4] = {
			CSM_CASCADES > 0 ? m_csm_splits[0] : 0.0f,
			CSM_CASCADES > 1 ? m_csm_splits[1] : 0.0f,
			CSM_CASCADES > 2 ? m_csm_splits[2] : 0.0f,
			m_dir_shadow_distance };
		memcpy(csm_ptr + (size_t)CSM_CASCADES * 64, splits, 16);
		float params[4] = { m_dir_shadow_strength, m_dir_shadow_bias, (float)m_csm_count, m_dir_shadow_softness };   // w = PCF radius (texels)
		memcpy(csm_ptr + (size_t)CSM_CASCADES * 64 + 16, params, 16);

		// Point shadow tail (#25) @1504: PointShadows[2] entries + PointFaceVP[12] — byte-matched
		// to standard.hlsl. Entry x = -1 keeps the shader's scan a no-op for unshadowed frames.
		u8* pt_ptr = csm_ptr + (size_t)CSM_CASCADES * 64 + 32;
		for (u32 p = 0; p < MAX_SHADOW_POINTS; ++p)
		{
			float entry[4] = { -1.0f, 0.0f, 0.0f, 0.0f };
			if (p < m_shadow_point_count)
			{
				entry[0] = (float)m_shadow_points[p].light_index;
				entry[1] = m_shadow_points[p].strength;
				entry[2] = m_shadow_points[p].bias;
			}
			memcpy(pt_ptr + (size_t)p * 16, entry, 16);
		}
		u8* fv_ptr = pt_ptr + (size_t)MAX_SHADOW_POINTS * 16;
		for (u32 p = 0; p < MAX_SHADOW_POINTS; ++p)
			for (u32 f = 0; f < 6; ++f)
				memcpy(fv_ptr + ((size_t)p * 6 + f) * 64,
					(p < m_shadow_point_count) ? &m_shadow_points[p].face_vp[f] : &ident, 64);
		// Sky light tail (IBL step 1) @2304: SH9 irradiance per channel as 9 float4 (rgb = coefficient, w of [0] = on)
		u8* sh_ptr = ptr + 2304;
		for (u32 k = 0; k < 9; ++k)
		{
			float c4[4] = { m_sky_sh[k * 3], m_sky_sh[k * 3 + 1], m_sky_sh[k * 3 + 2], (k == 0 && m_sky_sh_on) ? 1.0f : 0.0f };
			memcpy(sh_ptr + (size_t)k * 16, c4, 16);
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
	

	void DX12Renderer::set_directional_light_full(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity,
		bool cast_shadows, float shadow_strength, float shadow_bias, float shadow_distance)
	{
	m_light_direction = direction;
	m_light_color = color;
	m_directional_intensity = intensity;
	m_dir_cast_shadows = cast_shadows;
	m_dir_shadow_strength = shadow_strength < 0.0f ? 0.0f : (shadow_strength > 1.0f ? 1.0f : shadow_strength);
	m_dir_shadow_bias = shadow_bias > 0.0f ? shadow_bias : 0.0008f;
	m_dir_shadow_distance = shadow_distance > 5.0f ? shadow_distance : 5.0f;
	}
	

	// every submitted light is kept here, never cut; select_lights() picks the ones that hold the shader slots, per view and
	// frame (#335, #416)
	void DX12Renderer::add_point_light(const PointLightData& light) { if (m_point_lights.size() < MAX_SUBMITTED_LIGHTS) m_point_lights.push_back(light); }
	void DX12Renderer::add_spot_light(const SpotLightData& light) { if (m_spot_lights.size() < MAX_SUBMITTED_LIGHTS) m_spot_lights.push_back(light); }

	// Lights beyond the shader's slots are chosen by relevance to the camera, not by submission order (#335) — since #416 from
	// ALL submitted lights on every rendered frame, so a camera that moves between two scene submits is followed, and with the
	// view's choice held still by hysteresis and its changes faded (lighting::LightSelector, the same on SDL GPU).
	void DX12Renderer::select_lights(LightViewState& state, const DirectX::XMFLOAT3& eye, const DirectX::XMFLOAT4X4& view_projection)
	{
		const auto now = std::chrono::steady_clock::now();
		const float dt = state.primed ? std::chrono::duration<float>(now - state.last).count() : 0.0f;
		const lighting::Viewer viewer = lighting::make_viewer(eye, view_projection);
		const DirectX::XMFLOAT3 dir = lighting::view_direction(viewer);
		// the view's first frame, a cut, a teleport or a whip-around: start over, the chosen lights show at once
		const bool snap = !state.primed || lighting::camera_cut(state.eye, state.forward, eye, dir);
		state.last = now; state.eye = eye; state.forward = dir; state.primed = true;
		auto& c = m_light_candidates;
		c.clear();
		for (const PointLightData& p : m_point_lights)
		{
			bool in_view = true;
			const float score = lighting::point_light_score(p.position, p.range, p.intensity, viewer, &in_view);
			c.push_back({ p.id ? p.id : lighting::fallback_light_id(1, p.color, p.range, 0.0f), score,
				p.cast_shadows != 0 && p.shadow_strength > 0.0f, in_view });
		}
		state.points.update(c.data(), (u32)c.size(), MAX_POINT_LIGHTS, MAX_SHADOW_POINTS, dt, snap);
		m_view_points = state.points.selected();
		c.clear();
		for (const SpotLightData& s : m_spot_lights)
		{
			const bool aimed = s.direction.x * s.direction.x + s.direction.y * s.direction.y + s.direction.z * s.direction.z > 1e-6f;
			bool in_view = true;
			const float score = lighting::spot_light_score(s.position, s.direction, s.range, s.spot_angle, s.intensity, viewer, &in_view);
			c.push_back({ s.id ? s.id : lighting::fallback_light_id(2, s.color, s.range, s.spot_angle), score,
				s.cast_shadows != 0 && s.shadow_strength > 0.0f && aimed, in_view });
		}
		state.spots.update(c.data(), (u32)c.size(), MAX_SPOT_LIGHTS, MAX_SHADOW_SPOTS, dt, snap);
		m_view_spots = state.spots.selected();
		const size_t points = m_point_lights.size(), spots = m_spot_lights.size();
		if ((points > MAX_POINT_LIGHTS || spots > MAX_SPOT_LIGHTS) && m_light_cap_log++ % 600 == 0)
			OutputDebugStringA(("lights: " + std::to_string(points) + " point / " + std::to_string(spots) + " spot submitted, "
				+ std::to_string(MAX_POINT_LIGHTS) + " / " + std::to_string(MAX_SPOT_LIGHTS) + " fit — the most relevant for the camera hold the slots, "
				"changes fade over 0.5 s (#335, #416)\n").c_str());
	}
	
	// ============== Multi-Viewport Rendering ==============
	

}
