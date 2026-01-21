#include "Camera.h"
#include <vector>
#include <algorithm>

namespace vortex::camera {

	namespace {
		/// <summary>
		/// Internal camera data storage
		/// </summary>
		struct camera_data {
			math::v3 position;
			math::v4 rotation;  // Quaternion
			
			projection_type projection;
			f32 field_of_view;
			f32 orthographic_size;
			f32 near_clip;
			f32 far_clip;
			f32 aspect_ratio;
			
			clear_flags clear;
			f32 background_color[4];
			s32 depth;
			s32 culling_mask;
			
			camera_type type;
			bool is_enabled;
		};

		std::vector<id::generation_type> generations;
		std::vector<camera_data> cameras;
		std::vector<camera_id> free_ids;
		camera_id active_camera_id{ id::invalid_id };
	}

	component create(const init_info& info) {
		camera_id id;

		if (free_ids.size() > id::min_deleted_elements) {
			id = free_ids.front();
			free_ids.erase(free_ids.begin());
			id = camera_id{ id::new_generation(id) };
			generations[id::index(id)] = static_cast<id::generation_type>(id::generation(id));
		}
		else {
			id = camera_id{ static_cast<id::id_type>(cameras.size()) };
			generations.push_back(0);
			cameras.push_back({});
		}

		const id::id_type idx = id::index(id);
		camera_data& data = cameras[idx];
		
		data.position = { info.position[0], info.position[1], info.position[2] };
		data.rotation = { info.rotation[0], info.rotation[1], info.rotation[2], info.rotation[3] };
		data.projection = info.projection;
		data.field_of_view = info.field_of_view;
		data.orthographic_size = info.orthographic_size;
		data.near_clip = info.near_clip;
		data.far_clip = info.far_clip;
		data.aspect_ratio = info.aspect_ratio;
		data.clear = info.clear;
		data.background_color[0] = info.background_color[0];
		data.background_color[1] = info.background_color[1];
		data.background_color[2] = info.background_color[2];
		data.background_color[3] = info.background_color[3];
		data.depth = info.depth;
		data.culling_mask = info.culling_mask;
		data.type = info.type;
		data.is_enabled = info.is_enabled;

		// If this is the first main camera, set it as active
		if (info.type == camera_type::main_camera && !id::is_valid(active_camera_id)) {
			active_camera_id = id;
		}

		return component{ id };
	}

	void remove(component cam) {
		const camera_id id = cam.get_id();
		if (!is_alive(id)) return;

		const id::id_type idx = id::index(id);
		free_ids.push_back(id);

		// If this was the active camera, find a new one
		if (id == active_camera_id) {
			active_camera_id = camera_id{ id::invalid_id };
			// Try to find another main camera
			for (u32 i = 0; i < cameras.size(); ++i) {
				if (i != idx && cameras[i].type == camera_type::main_camera && cameras[i].is_enabled) {
					active_camera_id = camera_id{ static_cast<id::id_type>(i) | (static_cast<id::id_type>(generations[i]) << id::internal::index_bits) };
					break;
				}
			}
		}
	}

	bool is_alive(camera_id id) {
		if (!id::is_valid(id)) return false;
		const id::id_type idx = id::index(id);
		if (idx >= generations.size()) return false;
		return generations[idx] == id::generation(id);
	}

	component get_main_camera() {
		for (u32 i = 0; i < cameras.size(); ++i) {
			if (cameras[i].type == camera_type::main_camera && cameras[i].is_enabled) {
				const camera_id id{ static_cast<id::id_type>(i) | (static_cast<id::id_type>(generations[i]) << id::internal::index_bits) };
				if (is_alive(id)) {
					return component{ id };
				}
			}
		}
		return component{};
	}

	void get_all_cameras(component* out_cameras, u32& out_count, u32 max_cameras) {
		std::vector<std::pair<s32, component>> sorted_cameras;
		
		for (u32 i = 0; i < cameras.size(); ++i) {
			if (!cameras[i].is_enabled) continue;
			const camera_id id{ static_cast<id::id_type>(i) | (static_cast<id::id_type>(generations[i]) << id::internal::index_bits) };
			if (is_alive(id)) {
				sorted_cameras.push_back({ cameras[i].depth, component{ id } });
			}
		}

		std::sort(sorted_cameras.begin(), sorted_cameras.end(),
			[](const auto& a, const auto& b) { return a.first < b.first; });

		out_count = static_cast<u32>(std::min(sorted_cameras.size(), static_cast<size_t>(max_cameras)));
		for (u32 i = 0; i < out_count; ++i) {
			out_cameras[i] = sorted_cameras[i].second;
		}
	}

	u32 get_camera_count() {
		u32 count = 0;
		for (u32 i = 0; i < cameras.size(); ++i) {
			const camera_id id{ static_cast<id::id_type>(i) | (static_cast<id::id_type>(generations[i]) << id::internal::index_bits) };
			if (is_alive(id) && cameras[i].is_enabled) {
				++count;
			}
		}
		return count;
	}

	void set_active_camera(component cam) {
		if (cam.is_valid()) {
			active_camera_id = cam.get_id();
		}
	}

	component get_active_camera() {
		if (is_alive(active_camera_id)) {
			return component{ active_camera_id };
		}
		return get_main_camera();
	}

	// ============================================
	// Component Methods
	// ============================================

	namespace {
		camera_data* get_data(camera_id id) {
			if (!is_alive(id)) return nullptr;
			return &cameras[id::index(id)];
		}
	}

	math::v3 component::position() const {
		const auto* data = get_data(_id);
		return data ? data->position : math::v3{ 0, 0, 0 };
	}

	math::v4 component::rotation() const {
		const auto* data = get_data(_id);
		return data ? data->rotation : math::v4{ 0, 0, 0, 1 };
	}

	void component::set_position(const math::v3& pos) {
		auto* data = get_data(_id);
		if (data) data->position = pos;
	}

	void component::set_rotation(const math::v4& rot) {
		auto* data = get_data(_id);
		if (data) data->rotation = rot;
	}

	projection_type component::get_projection() const {
		const auto* data = get_data(_id);
		return data ? data->projection : projection_type::perspective;
	}

	void component::set_projection(projection_type proj) {
		auto* data = get_data(_id);
		if (data) data->projection = proj;
	}

	f32 component::get_field_of_view() const {
		const auto* data = get_data(_id);
		return data ? data->field_of_view : 60.0f;
	}

	void component::set_field_of_view(f32 fov) {
		auto* data = get_data(_id);
		if (data) data->field_of_view = fov;
	}

	f32 component::get_orthographic_size() const {
		const auto* data = get_data(_id);
		return data ? data->orthographic_size : 5.0f;
	}

	void component::set_orthographic_size(f32 size) {
		auto* data = get_data(_id);
		if (data) data->orthographic_size = size;
	}

	f32 component::get_near_clip() const {
		const auto* data = get_data(_id);
		return data ? data->near_clip : 0.1f;
	}

	void component::set_near_clip(f32 near_plane) {
		auto* data = get_data(_id);
		if (data) data->near_clip = near_plane;
	}

	f32 component::get_far_clip() const {
		const auto* data = get_data(_id);
		return data ? data->far_clip : 1000.0f;
	}

	void component::set_far_clip(f32 far_plane) {
		auto* data = get_data(_id);
		if (data) data->far_clip = far_plane;
	}

	f32 component::get_aspect_ratio() const {
		const auto* data = get_data(_id);
		return data ? data->aspect_ratio : 16.0f / 9.0f;
	}

	void component::set_aspect_ratio(f32 aspect) {
		auto* data = get_data(_id);
		if (data) data->aspect_ratio = aspect;
	}

	clear_flags component::get_clear_flags() const {
		const auto* data = get_data(_id);
		return data ? data->clear : clear_flags::skybox;
	}

	void component::set_clear_flags(clear_flags flags) {
		auto* data = get_data(_id);
		if (data) data->clear = flags;
	}

	void component::get_background_color(f32& r, f32& g, f32& b, f32& a) const {
		const auto* data = get_data(_id);
		if (data) {
			r = data->background_color[0];
			g = data->background_color[1];
			b = data->background_color[2];
			a = data->background_color[3];
		}
	}

	void component::set_background_color(f32 r, f32 g, f32 b, f32 a) {
		auto* data = get_data(_id);
		if (data) {
			data->background_color[0] = r;
			data->background_color[1] = g;
			data->background_color[2] = b;
			data->background_color[3] = a;
		}
	}

	s32 component::get_depth() const {
		const auto* data = get_data(_id);
		return data ? data->depth : 0;
	}

	void component::set_depth(s32 depth) {
		auto* data = get_data(_id);
		if (data) data->depth = depth;
	}

	s32 component::get_culling_mask() const {
		const auto* data = get_data(_id);
		return data ? data->culling_mask : -1;
	}

	void component::set_culling_mask(s32 mask) {
		auto* data = get_data(_id);
		if (data) data->culling_mask = mask;
	}

	camera_type component::get_type() const {
		const auto* data = get_data(_id);
		return data ? data->type : camera_type::game_camera;
	}

	void component::set_type(camera_type type) {
		auto* data = get_data(_id);
		if (data) {
			data->type = type;
			// If setting to main camera and no active camera, make this active
			if (type == camera_type::main_camera && !is_alive(active_camera_id)) {
				active_camera_id = _id;
			}
		}
	}

	bool component::is_enabled() const {
		const auto* data = get_data(_id);
		return data ? data->is_enabled : false;
	}

	void component::set_enabled(bool enabled) {
		auto* data = get_data(_id);
		if (data) data->is_enabled = enabled;
	}

	DirectX::XMMATRIX component::get_view_matrix() const {
		using namespace DirectX;
		const auto* data = get_data(_id);
		if (!data) return XMMatrixIdentity();

		XMVECTOR pos = XMLoadFloat3(&data->position);
		XMVECTOR rot = XMLoadFloat4(&data->rotation);
		
		// Calculate forward direction from rotation
		XMVECTOR forward = XMVector3Rotate(XMVectorSet(0, 0, 1, 0), rot);
		XMVECTOR up = XMVector3Rotate(XMVectorSet(0, 1, 0, 0), rot);
		
		return XMMatrixLookToLH(pos, forward, up);
	}

	DirectX::XMMATRIX component::get_projection_matrix() const {
		using namespace DirectX;
		const auto* data = get_data(_id);
		if (!data) return XMMatrixIdentity();

		if (data->projection == projection_type::perspective) {
			return XMMatrixPerspectiveFovLH(
				XMConvertToRadians(data->field_of_view),
				data->aspect_ratio,
				data->near_clip,
				data->far_clip
			);
		}
		else {
			const f32 width = data->orthographic_size * data->aspect_ratio;
			const f32 height = data->orthographic_size;
			return XMMatrixOrthographicLH(width * 2, height * 2, data->near_clip, data->far_clip);
		}
	}

	DirectX::XMMATRIX component::get_view_projection_matrix() const {
		return get_view_matrix() * get_projection_matrix();
	}

	math::v3 component::get_forward() const {
		using namespace DirectX;
		const auto* data = get_data(_id);
		if (!data) return { 0, 0, 1 };

		XMVECTOR rot = XMLoadFloat4(&data->rotation);
		XMVECTOR forward = XMVector3Rotate(XMVectorSet(0, 0, 1, 0), rot);
		math::v3 result;
		XMStoreFloat3(&result, forward);
		return result;
	}

	math::v3 component::get_right() const {
		using namespace DirectX;
		const auto* data = get_data(_id);
		if (!data) return { 1, 0, 0 };

		XMVECTOR rot = XMLoadFloat4(&data->rotation);
		XMVECTOR right = XMVector3Rotate(XMVectorSet(1, 0, 0, 0), rot);
		math::v3 result;
		XMStoreFloat3(&result, right);
		return result;
	}

	math::v3 component::get_up() const {
		using namespace DirectX;
		const auto* data = get_data(_id);
		if (!data) return { 0, 1, 0 };

		XMVECTOR rot = XMLoadFloat4(&data->rotation);
		XMVECTOR up = XMVector3Rotate(XMVectorSet(0, 1, 0, 0), rot);
		math::v3 result;
		XMStoreFloat3(&result, up);
		return result;
	}

	math::v3 component::screen_to_world_point(f32 screen_x, f32 screen_y, f32 depth) const {
		using namespace DirectX;
		
		// Normalize screen coordinates to [-1, 1]
		const f32 ndc_x = screen_x * 2.0f - 1.0f;
		const f32 ndc_y = 1.0f - screen_y * 2.0f;  // Flip Y

		XMMATRIX inv_vp = XMMatrixInverse(nullptr, get_view_projection_matrix());
		XMVECTOR screen_pos = XMVectorSet(ndc_x, ndc_y, depth, 1.0f);
		XMVECTOR world_pos = XMVector4Transform(screen_pos, inv_vp);
		world_pos = XMVectorDivide(world_pos, XMVectorSplatW(world_pos));

		math::v3 result;
		XMStoreFloat3(&result, world_pos);
		return result;
	}

	math::v2 component::world_to_screen_point(const math::v3& world_pos) const {
		using namespace DirectX;

		XMVECTOR pos = XMLoadFloat3(&world_pos);
		XMVECTOR clip_pos = XMVector4Transform(XMVectorSetW(pos, 1.0f), get_view_projection_matrix());
		
		// Perspective divide
		XMVECTOR ndc = XMVectorDivide(clip_pos, XMVectorSplatW(clip_pos));
		
		math::v4 ndc_values;
		XMStoreFloat4(&ndc_values, ndc);
		
		// Convert from [-1,1] to [0,1] screen space
		return {
			(ndc_values.x + 1.0f) * 0.5f,
			(1.0f - ndc_values.y) * 0.5f  // Flip Y
		};
	}
}
