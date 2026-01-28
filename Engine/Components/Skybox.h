#pragma once

#include "ComponentsCommon.h"
#include "../Common/Id.h"

namespace vortex::skybox {

	DEFINE_TYPED_ID(skybox_id);

	/// <summary>
	/// Skybox rendering mode
	/// </summary>
	enum class skybox_mode : u8 {
		solid_color = 0,
		gradient = 1,
		cubemap = 2
	};

	/// <summary>
	/// Initialization info for creating a skybox
	/// </summary>
	struct init_info {
		skybox_mode mode{ skybox_mode::gradient };
		
		// Colors (linear RGB)
		f32 sky_color[3]{ 0.4f, 0.6f, 0.9f };
		f32 horizon_color[3]{ 0.7f, 0.8f, 0.95f };
		f32 ground_color[3]{ 0.3f, 0.25f, 0.2f };
		
		// Sun settings
		f32 sun_direction[3]{ -0.5f, -0.7f, 0.5f };
		f32 sun_color[3]{ 1.0f, 0.95f, 0.8f };
		f32 sun_intensity{ 1.0f };
		
		// Ambient
		f32 ambient_intensity{ 0.3f };
		f32 exposure{ 1.0f };
		
		bool is_enabled{ true };
	};

	/// <summary>
	/// Skybox component for environment rendering
	/// </summary>
	class component final {
	public:
		constexpr component() : _id{ id::invalid_id } {}
		constexpr explicit component(skybox_id id) : _id{ id } {}
		constexpr skybox_id get_id() const { return _id; }
		constexpr bool is_valid() const { return id::is_valid(_id); }

		// Mode
		skybox_mode get_mode() const;
		void set_mode(skybox_mode mode);

		// Colors
		void get_sky_color(f32& r, f32& g, f32& b) const;
		void set_sky_color(f32 r, f32 g, f32 b);
		
		void get_horizon_color(f32& r, f32& g, f32& b) const;
		void set_horizon_color(f32 r, f32 g, f32 b);
		
		void get_ground_color(f32& r, f32& g, f32& b) const;
		void set_ground_color(f32 r, f32 g, f32 b);

		// Sun
		void get_sun_direction(f32& x, f32& y, f32& z) const;
		void set_sun_direction(f32 x, f32 y, f32 z);
		
		void get_sun_color(f32& r, f32& g, f32& b) const;
		void set_sun_color(f32 r, f32 g, f32 b);
		
		f32 get_sun_intensity() const;
		void set_sun_intensity(f32 intensity);

		// Ambient
		f32 get_ambient_intensity() const;
		void set_ambient_intensity(f32 intensity);
		
		f32 get_exposure() const;
		void set_exposure(f32 exposure);

		// Enable/Disable
		bool is_enabled() const;
		void set_enabled(bool enabled);

		// Apply this skybox to the renderer
		void apply_to_renderer() const;

	private:
		skybox_id _id;
	};

	// Factory functions
	component create(const init_info& info);
	void remove(component c);
	bool is_alive(skybox_id id);
	
	// Get the active skybox (if any)
	component get_active_skybox();
	void set_active_skybox(component c);
}
