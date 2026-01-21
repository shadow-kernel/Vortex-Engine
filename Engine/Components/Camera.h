#pragma once

#include "ComponentsCommon.h"
#include "../Utilities/MathTypes.h"
#include "../Common/Id.h"

namespace vortex::camera {

	DEFINE_TYPED_ID(camera_id);

	/// <summary>
	/// Camera projection type
	/// </summary>
	enum class projection_type : u8 {
		perspective = 0,
		orthographic = 1
	};

	/// <summary>
	/// Camera clear flags - what to clear before rendering
	/// </summary>
	enum class clear_flags : u8 {
		skybox = 0,
		solid_color = 1,
		depth_only = 2,
		nothing = 3
	};

	/// <summary>
	/// Camera type - determines priority and behavior
	/// </summary>
	enum class camera_type : u8 {
		game_camera = 0,    // Regular game camera
		main_camera = 1,    // The primary player camera (purple in editor)
		editor_camera = 2   // Editor-only camera (not included in builds)
	};

	/// <summary>
	/// Initialization info for creating a camera
	/// </summary>
	struct init_info {
		// Transform data
		f32 position[3]{ 0, 0, -10 };
		f32 rotation[4]{ 0, 0, 0, 1 }; // Quaternion

		// Projection
		projection_type projection{ projection_type::perspective };
		f32 field_of_view{ 60.0f };     // FOV for perspective (degrees)
		f32 orthographic_size{ 5.0f };  // Half-height for orthographic
		f32 near_clip{ 0.1f };
		f32 far_clip{ 1000.0f };
		f32 aspect_ratio{ 16.0f / 9.0f };

		// Rendering
		clear_flags clear{ clear_flags::skybox };
		f32 background_color[4]{ 0.1f, 0.1f, 0.2f, 1.0f };
		s32 depth{ 0 };  // Render order (lower = rendered first)
		s32 culling_mask{ -1 };  // All layers by default

		// Type
		camera_type type{ camera_type::game_camera };
		bool is_enabled{ true };
	};

	/// <summary>
	/// Camera component for rendering viewports
	/// </summary>
	class component final {
	public:
		constexpr component() : _id{ id::invalid_id } {}
		constexpr explicit component(camera_id id) : _id{ id } {}
		constexpr camera_id get_id() const { return _id; }
		constexpr bool is_valid() const { return id::is_valid(_id); }

		// Transform
		math::v3 position() const;
		math::v4 rotation() const;
		void set_position(const math::v3& pos);
		void set_rotation(const math::v4& rot);

		// Camera properties
		projection_type get_projection() const;
		void set_projection(projection_type proj);

		f32 get_field_of_view() const;
		void set_field_of_view(f32 fov);

		f32 get_orthographic_size() const;
		void set_orthographic_size(f32 size);

		f32 get_near_clip() const;
		void set_near_clip(f32 near_plane);

		f32 get_far_clip() const;
		void set_far_clip(f32 far_plane);

		f32 get_aspect_ratio() const;
		void set_aspect_ratio(f32 aspect);

		clear_flags get_clear_flags() const;
		void set_clear_flags(clear_flags flags);

		void get_background_color(f32& r, f32& g, f32& b, f32& a) const;
		void set_background_color(f32 r, f32 g, f32 b, f32 a);

		s32 get_depth() const;
		void set_depth(s32 depth);

		s32 get_culling_mask() const;
		void set_culling_mask(s32 mask);

		camera_type get_type() const;
		void set_type(camera_type type);

		bool is_enabled() const;
		void set_enabled(bool enabled);

		// Matrix calculations
		DirectX::XMMATRIX get_view_matrix() const;
		DirectX::XMMATRIX get_projection_matrix() const;
		DirectX::XMMATRIX get_view_projection_matrix() const;

		// Helper methods
		math::v3 get_forward() const;
		math::v3 get_right() const;
		math::v3 get_up() const;

		// Screen/World space conversions
		math::v3 screen_to_world_point(f32 screen_x, f32 screen_y, f32 depth) const;
		math::v2 world_to_screen_point(const math::v3& world_pos) const;

	private:
		camera_id _id;
	};

	// ============================================
	// Camera System Functions
	// ============================================

	/// <summary>
	/// Create a new camera
	/// </summary>
	component create(const init_info& info);

	/// <summary>
	/// Remove a camera
	/// </summary>
	void remove(component cam);

	/// <summary>
	/// Check if a camera is alive
	/// </summary>
	bool is_alive(camera_id id);

	/// <summary>
	/// Get the main camera (first camera with main_camera type)
	/// </summary>
	component get_main_camera();

	/// <summary>
	/// Get all cameras sorted by depth
	/// </summary>
	void get_all_cameras(component* out_cameras, u32& out_count, u32 max_cameras);

	/// <summary>
	/// Get camera count
	/// </summary>
	u32 get_camera_count();

	/// <summary>
	/// Set the active camera for rendering
	/// </summary>
	void set_active_camera(component cam);

	/// <summary> 
	/// Get the currently active camera
	/// </summary>
	component get_active_camera();
}
