#pragma once

// Render smoke test: opens a native window through the GameHost, renders a lit PBR scene (cube, sphere,
// cylinder, ground plane, grid, gradient skybox, point + spot lights, a first-person "viewmodel" layer, a
// wireframe gizmo sphere, 2D overlay text), captures a frame to a BMP and exits. Non-interactive.
//   VORTEX_RENDER_CAPTURE   output path (default: vortex_render_test.bmp in the working directory)
//   VORTEX_RENDER_FRAMES    frames to run (default 90; the capture is taken at 2/3)
#include "../Engine/Common/Platform.h"
#include "../Engine/Graphics/Backend.h"
#include "../Engine/Runtime/GameHost.h"
#include "Test.h"

#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <iostream>
#include <string>

using namespace vortex;

class engine_test : public test
{
public:
	bool initialize() override
	{
		std::cout << "Render Test Initialized (backend: " << graphics::backend_name << ")\n";
		const std::string frames = platform::env_string("VORTEX_RENDER_FRAMES");
		if (!frames.empty()) s_total_frames = std::max(10, std::atoi(frames.c_str()));
		s_capture_path = platform::env_string("VORTEX_RENDER_CAPTURE");
		if (s_capture_path.empty()) s_capture_path = "vortex_render_test.bmp";
		return true;
	}

	void run() override
	{
		runtime::GameHost::set_tick_callback(&engine_test::tick);
		const bool ok = runtime::GameHost::run_utf8(1280, 720, "Vortex Render Test — SDL GPU");
		std::cout << (ok ? "[PASS] game host ran\n" : "[FAIL] game host failed\n");
		FILE* f = nullptr;
		const bool captured = fopen_s(&f, s_capture_path.c_str(), "rb") == 0 && f;
		if (f) fclose(f);
		std::cout << (captured ? "[PASS] frame captured: " : "[FAIL] no capture at: ") << s_capture_path << "\n";
		std::cout << "[info] frames rendered: " << s_frame << ", last fps " << s_last_fps << ", draw calls " << s_last_draws << "\n";
		std::cout << "RESULT: " << ((ok && captured) ? "ALL PASSED" : "FAILURES!") << "\n";
	}

	void shutdown() override { std::cout << "Render Test Shutdown\n"; }

private:
	static inline int s_frame = 0;
	static inline int s_total_frames = 90;
	static inline int s_last_fps = 0;
	static inline int s_last_draws = 0;
	static inline std::string s_capture_path;
	static inline bool s_created = false;
	static inline id::id_type s_cube, s_sphere, s_cylinder, s_plane, s_wire_sphere;
	static inline id::id_type s_mat_red, s_mat_metal, s_mat_ground, s_mat_glass, s_mat_gizmo, s_mat_viewmodel;

	static void create_scene()
	{
		auto& reg = graphics::ResourceRegistry::instance();
		s_cube = reg.create_primitive_cube(1.0f);
		s_sphere = reg.create_primitive_sphere(0.6f, 48, 24);
		s_cylinder = reg.create_primitive_cylinder(0.4f, 1.4f, 32);
		s_plane = reg.create_primitive_plane(20.0f, 20.0f);
		s_wire_sphere = reg.create_primitive_sphere(1.4f, 24, 12);

		auto make = [&](const char* name, float r, float g, float b, float metallic, float roughness)
		{
			id::id_type id = reg.create_material(name);
			if (auto* m = reg.get_material(id)) { m->set_base_color({ r, g, b, 1.0f }); m->set_metallic(metallic); m->set_roughness(roughness); }
			return id;
		};
		s_mat_red = make("red", 0.85f, 0.15f, 0.12f, 0.0f, 0.45f);
		s_mat_metal = make("metal", 0.9f, 0.9f, 0.95f, 1.0f, 0.2f);
		s_mat_ground = make("ground", 0.35f, 0.38f, 0.4f, 0.0f, 0.9f);
		s_mat_glass = make("glass", 0.2f, 0.6f, 1.0f, 0.0f, 0.1f);
		if (auto* m = reg.get_material(s_mat_glass)) { m->set_base_color({ 0.2f, 0.6f, 1.0f, 0.45f }); m->set_blend_mode(1); }
		s_mat_gizmo = make("gizmo", 0.2f, 1.0f, 0.3f, 0.0f, 1.0f);
		if (auto* m = reg.get_material(s_mat_gizmo)) m->set_unlit(true);
		s_mat_viewmodel = make("viewmodel", 0.95f, 0.75f, 0.2f, 0.3f, 0.4f);
		s_created = true;
	}

	static void world(float* out, float sx, float sy, float sz, float yaw, float tx, float ty, float tz)
	{
		using namespace DirectX;
		XMMATRIX m = XMMatrixScaling(sx, sy, sz) * XMMatrixRotationY(yaw) * XMMatrixTranslation(tx, ty, tz);
		XMFLOAT4X4 f; XMStoreFloat4x4(&f, m);
		memcpy(out, &f, 64);
	}

	static void tick(float dt)
	{
		(void)dt;
		auto& r = graphics::Renderer::instance();
		if (!s_created) create_scene();
		const float t = (float)s_frame / 60.0f;

		// orbiting camera
		const float ang = 0.6f + t * 0.35f;
		r.set_camera({ 7.0f * cosf(ang), 3.2f, 7.0f * sinf(ang) }, { 0.0f, 0.6f, 0.0f }, { 0.0f, 1.0f, 0.0f });
		r.set_field_of_view(60.0f);

		r.set_directional_light_full({ -0.4f, -1.0f, 0.35f }, { 1.0f, 0.96f, 0.9f }, 2.2f);
		r.set_ambient_strength(0.45f);
		r.clear_lights();
		graphics::Renderer::PointLightData pl{};
		pl.position = { 2.5f * cosf(t * 1.3f), 1.8f, 2.5f * sinf(t * 1.3f) }; pl.range = 8.0f; pl.color = { 0.3f, 0.6f, 1.0f }; pl.intensity = 6.0f;
		r.add_point_light(pl);
		graphics::Renderer::SpotLightData sl{};
		sl.position = { -3.0f, 4.0f, -2.0f }; sl.direction = { 0.5f, -1.0f, 0.4f }; sl.range = 14.0f;
		sl.color = { 1.0f, 0.85f, 0.6f }; sl.intensity = 10.0f; sl.spot_angle = 40.0f; sl.inner_spot_angle = 25.0f;
		r.add_spot_light(sl);
		r.set_skybox_enabled(true);
		r.set_skybox_colors({ 0.25f, 0.45f, 0.85f }, { 0.75f, 0.82f, 0.9f }, { 0.22f, 0.22f, 0.25f });
		r.set_skybox_sun({ -0.4f, -1.0f, 0.35f }, { 1.0f, 0.95f, 0.85f }, 1.0f);
		r.set_grid_visible(true);
		r.set_fog({ 0.6f, 0.65f, 0.7f }, 0.012f, 0.0f, 0.0f);

		// scene (re-submitted every frame: the renderer would reuse a static queue, but the cube spins)
		float m[16];
		world(m, 20.0f, 1.0f, 20.0f, 0.0f, 0.0f, 0.0f, 0.0f);  r.submit_mesh_instances(s_plane, s_mat_ground, m, 1);
		world(m, 1.0f, 1.0f, 1.0f, t * 0.9f, 0.0f, 0.5f, 0.0f);  r.submit_mesh_instances(s_cube, s_mat_red, m, 1);
		world(m, 1.0f, 1.0f, 1.0f, 0.0f, 2.2f, 0.6f, 0.0f);      r.submit_mesh_instances(s_sphere, s_mat_metal, m, 1);
		world(m, 1.0f, 1.0f, 1.0f, 0.0f, -2.2f, 0.7f, 0.0f);     r.submit_mesh_instances(s_cylinder, s_mat_red, m, 1);
		world(m, 1.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.6f, 2.4f);      r.submit_mesh_instances(s_sphere, s_mat_glass, m, 1);
		// a small crowd to exercise instancing + the frustum cull
		float crowd[16 * 40];
		for (int i = 0; i < 40; ++i) world(crowd + i * 16, 0.3f, 0.3f, 0.3f, (float)i, -4.5f + (float)(i % 10), 0.15f, -6.0f + (float)(i / 10) * 1.2f);
		r.submit_mesh_instances(s_cube, s_mat_metal, crowd, 40);
		// first-person viewmodel layer (own projection, cleared depth)
		world(m, 0.35f, 0.25f, 0.9f, 0.0f, 0.55f, -0.45f, 1.4f);
		{
			// the viewmodel is authored in camera space: place it relative to the current camera
			using namespace DirectX;
			XMVECTOR eye = XMVectorSet(7.0f * cosf(ang), 3.2f, 7.0f * sinf(ang), 1.0f);
			XMVECTOR at = XMVectorSet(0.0f, 0.6f, 0.0f, 1.0f);
			XMMATRIX view = XMMatrixLookAtLH(eye, at, XMVectorSet(0, 1, 0, 0));
			XMMATRIX inv = XMMatrixInverse(nullptr, view);
			XMMATRIX local = XMMatrixScaling(0.35f, 0.25f, 0.9f) * XMMatrixTranslation(0.55f, -0.45f, 1.4f);
			XMFLOAT4X4 f; XMStoreFloat4x4(&f, local * inv);
			r.submit_mesh_instances(s_cube, s_mat_viewmodel, &f._11, 1, 1);
		}
		// wire gizmo (always on top)
		graphics::backend::RenderItem gz{};
		gz.mesh_id = s_wire_sphere; gz.material_id = s_mat_gizmo;
		world(&gz.world_matrix._11, 1.0f, 1.0f, 1.0f, 0.0f, 2.2f, 0.6f, 0.0f);
		r.submit_gizmo_wire_item(gz);

		// 2D overlay
		const float cw = (float)runtime::GameHost::client_width(), ch = (float)runtime::GameHost::client_height();
		r.ui_begin(cw, ch);
		r.ui_rect(24.0f, 24.0f, 360.0f, 92.0f, 0.05f, 0.05f, 0.07f, 0.72f, 14.0f);
		// The platform and the GPU API this build actually renders with (Metal on macOS, Vulkan elsewhere).
#if VORTEX_PLATFORM_MACOS
		const char* platform_name = "macOS";
		const char* gpu_api = "Metal";
#elif VORTEX_PLATFORM_LINUX
		const char* platform_name = "Linux";
		const char* gpu_api = "Vulkan";
#else
		const char* platform_name = "Windows";
		const char* gpu_api = "D3D12";
#endif
		char title[96];
		snprintf(title, sizeof(title), "Vortex Engine · %s", platform_name);
		r.ui_text_utf8(40.0f, 32.0f, 330.0f, 36.0f, title, 22.0f, 1.0f, 1.0f, 1.0f, 1.0f, 0, 700);
		char line[128];
		snprintf(line, sizeof(line), "SDL GPU / %s · %d fps · %d draws · Ärger-frei ✓", gpu_api,
			r.get_current_fps(), r.get_draw_call_count());
		r.ui_text_utf8(40.0f, 68.0f, 330.0f, 30.0f, line, 15.0f, 0.75f, 0.85f, 1.0f, 1.0f, 0, 400);
		r.ui_line(40.0f, 64.0f, 360.0f, 64.0f, 0.4f, 0.6f, 1.0f, 0.9f, 1.5f);

		s_last_fps = r.get_current_fps();
		s_last_draws = r.get_draw_call_count();
		if (s_frame == (s_total_frames * 2) / 3) r.request_capture(s_capture_path.c_str());
		if (++s_frame >= s_total_frames) runtime::GameHost::request_exit();
	}
};
