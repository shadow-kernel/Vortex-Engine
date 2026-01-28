#include "Skybox.h"
#include "../Graphics/DX12/DX12Renderer.h"
#include <vector>

namespace vortex::skybox {

	namespace {
		struct skybox_data {
			skybox_mode mode{ skybox_mode::gradient };
			
			f32 sky_color[3]{ 0.4f, 0.6f, 0.9f };
			f32 horizon_color[3]{ 0.7f, 0.8f, 0.95f };
			f32 ground_color[3]{ 0.3f, 0.25f, 0.2f };
			
			f32 sun_direction[3]{ -0.5f, -0.7f, 0.5f };
			f32 sun_color[3]{ 1.0f, 0.95f, 0.8f };
			f32 sun_intensity{ 1.0f };
			
			f32 ambient_intensity{ 0.3f };
			f32 exposure{ 1.0f };
			
			bool is_enabled{ true };
		};

		std::vector<skybox_data> skyboxes;
		std::vector<skybox_id> free_ids;
		skybox_id active_skybox_id{ id::invalid_id };

		skybox_data& get_data(skybox_id id) {
			assert(id::is_valid(id));
			return skyboxes[id::index(id)];
		}
	}

	// Factory functions
	component create(const init_info& info) {
		skybox_id id;
		
		if (!free_ids.empty()) {
			id = free_ids.back();
			free_ids.pop_back();
		} else {
			id = skybox_id{ static_cast<id::id_type>(skyboxes.size()) };
			skyboxes.emplace_back();
		}

		skybox_data& data = get_data(id);
		data.mode = info.mode;
		memcpy(data.sky_color, info.sky_color, sizeof(f32) * 3);
		memcpy(data.horizon_color, info.horizon_color, sizeof(f32) * 3);
		memcpy(data.ground_color, info.ground_color, sizeof(f32) * 3);
		memcpy(data.sun_direction, info.sun_direction, sizeof(f32) * 3);
		memcpy(data.sun_color, info.sun_color, sizeof(f32) * 3);
		data.sun_intensity = info.sun_intensity;
		data.ambient_intensity = info.ambient_intensity;
		data.exposure = info.exposure;
		data.is_enabled = info.is_enabled;

		// If this is the first skybox or no active skybox, make it active
		if (!id::is_valid(active_skybox_id)) {
			active_skybox_id = id;
		}

		return component{ id };
	}

	void remove(component c) {
		if (!c.is_valid()) return;
		
		skybox_id id = c.get_id();
		free_ids.push_back(id);
		
		if (active_skybox_id == id) {
			active_skybox_id = skybox_id{ id::invalid_id };
		}
	}

	bool is_alive(skybox_id id) {
		if (!id::is_valid(id)) return false;
		u32 index = id::index(id);
		if (index >= skyboxes.size()) return false;
		// Check if not in free list
		for (auto& free_id : free_ids) {
			if (free_id == id) return false;
		}
		return true;
	}

	component get_active_skybox() {
		return component{ active_skybox_id };
	}

	void set_active_skybox(component c) {
		active_skybox_id = c.get_id();
	}

	// Component methods
	skybox_mode component::get_mode() const {
		return get_data(_id).mode;
	}

	void component::set_mode(skybox_mode mode) {
		get_data(_id).mode = mode;
	}

	void component::get_sky_color(f32& r, f32& g, f32& b) const {
		auto& data = get_data(_id);
		r = data.sky_color[0];
		g = data.sky_color[1];
		b = data.sky_color[2];
	}

	void component::set_sky_color(f32 r, f32 g, f32 b) {
		auto& data = get_data(_id);
		data.sky_color[0] = r;
		data.sky_color[1] = g;
		data.sky_color[2] = b;
	}

	void component::get_horizon_color(f32& r, f32& g, f32& b) const {
		auto& data = get_data(_id);
		r = data.horizon_color[0];
		g = data.horizon_color[1];
		b = data.horizon_color[2];
	}

	void component::set_horizon_color(f32 r, f32 g, f32 b) {
		auto& data = get_data(_id);
		data.horizon_color[0] = r;
		data.horizon_color[1] = g;
		data.horizon_color[2] = b;
	}

	void component::get_ground_color(f32& r, f32& g, f32& b) const {
		auto& data = get_data(_id);
		r = data.ground_color[0];
		g = data.ground_color[1];
		b = data.ground_color[2];
	}

	void component::set_ground_color(f32 r, f32 g, f32 b) {
		auto& data = get_data(_id);
		data.ground_color[0] = r;
		data.ground_color[1] = g;
		data.ground_color[2] = b;
	}

	void component::get_sun_direction(f32& x, f32& y, f32& z) const {
		auto& data = get_data(_id);
		x = data.sun_direction[0];
		y = data.sun_direction[1];
		z = data.sun_direction[2];
	}

	void component::set_sun_direction(f32 x, f32 y, f32 z) {
		auto& data = get_data(_id);
		data.sun_direction[0] = x;
		data.sun_direction[1] = y;
		data.sun_direction[2] = z;
	}

	void component::get_sun_color(f32& r, f32& g, f32& b) const {
		auto& data = get_data(_id);
		r = data.sun_color[0];
		g = data.sun_color[1];
		b = data.sun_color[2];
	}

	void component::set_sun_color(f32 r, f32 g, f32 b) {
		auto& data = get_data(_id);
		data.sun_color[0] = r;
		data.sun_color[1] = g;
		data.sun_color[2] = b;
	}

	f32 component::get_sun_intensity() const {
		return get_data(_id).sun_intensity;
	}

	void component::set_sun_intensity(f32 intensity) {
		get_data(_id).sun_intensity = intensity;
	}

	f32 component::get_ambient_intensity() const {
		return get_data(_id).ambient_intensity;
	}

	void component::set_ambient_intensity(f32 intensity) {
		get_data(_id).ambient_intensity = intensity;
	}

	f32 component::get_exposure() const {
		return get_data(_id).exposure;
	}

	void component::set_exposure(f32 exposure) {
		get_data(_id).exposure = exposure;
	}

	bool component::is_enabled() const {
		return get_data(_id).is_enabled;
	}

	void component::set_enabled(bool enabled) {
		get_data(_id).is_enabled = enabled;
	}

	void component::apply_to_renderer() const {
		if (!is_valid() || !is_enabled()) {
			graphics::dx12::DX12Renderer::instance().set_skybox_enabled(false);
			return;
		}

		auto& data = get_data(_id);
		auto& renderer = graphics::dx12::DX12Renderer::instance();

		renderer.set_skybox_enabled(true);
		renderer.set_skybox_mode(static_cast<graphics::dx12::DX12Renderer::SkyboxMode>(data.mode));

		f32 exp = data.exposure;
		
		switch (data.mode) {
		case skybox_mode::solid_color:
			renderer.set_skybox_solid_color({
				data.sky_color[0] * exp,
				data.sky_color[1] * exp,
				data.sky_color[2] * exp
			});
			break;

		case skybox_mode::gradient:
			renderer.set_skybox_colors(
				{ data.sky_color[0] * exp, data.sky_color[1] * exp, data.sky_color[2] * exp },
				{ data.horizon_color[0] * exp, data.horizon_color[1] * exp, data.horizon_color[2] * exp },
				{ data.ground_color[0] * exp, data.ground_color[1] * exp, data.ground_color[2] * exp }
			);
			break;

		case skybox_mode::cubemap:
			// TODO: Implement cubemap texture loading
			renderer.set_skybox_colors(
				{ data.sky_color[0] * exp, data.sky_color[1] * exp, data.sky_color[2] * exp },
				{ data.horizon_color[0] * exp, data.horizon_color[1] * exp, data.horizon_color[2] * exp },
				{ data.ground_color[0] * exp, data.ground_color[1] * exp, data.ground_color[2] * exp }
			);
			break;
		}

		renderer.set_skybox_sun(
			{ data.sun_direction[0], data.sun_direction[1], data.sun_direction[2] },
			{ data.sun_color[0], data.sun_color[1], data.sun_color[2] },
			data.sun_intensity
		);

		renderer.set_ambient_strength(data.ambient_intensity);
	}
}
