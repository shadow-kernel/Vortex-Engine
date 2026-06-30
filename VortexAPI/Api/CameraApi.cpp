#include "../ApiCommon.h"

namespace {
	struct camera_descriptor {
		f32 position[3];
		f32 rotation[4];  // Quaternion
		u8 projection;    // 0 = perspective, 1 = orthographic
		f32 field_of_view;
		f32 orthographic_size;
		f32 near_clip;
		f32 far_clip;
		f32 aspect_ratio;
		u8 clear_flags;
		f32 background_color[4];
		s32 depth;
		s32 culling_mask;
		u8 camera_type;  // 0 = game, 1 = main, 2 = editor
		bool is_enabled;
	};
}

EDITOR_INTERFACE id::id_type CreateCamera(camera_descriptor* desc)
{
	if (!desc) return id::invalid_id;
	
	camera::init_info info{};
	memcpy(info.position, desc->position, sizeof(f32) * 3);
	memcpy(info.rotation, desc->rotation, sizeof(f32) * 4);
	info.projection = static_cast<camera::projection_type>(desc->projection);
	info.field_of_view = desc->field_of_view;
	info.orthographic_size = desc->orthographic_size;
	info.near_clip = desc->near_clip;
	info.far_clip = desc->far_clip;
	info.aspect_ratio = desc->aspect_ratio;
	info.clear = static_cast<camera::clear_flags>(desc->clear_flags);
	memcpy(info.background_color, desc->background_color, sizeof(f32) * 4);
	info.depth = desc->depth;
	info.culling_mask = desc->culling_mask;
	info.type = static_cast<camera::camera_type>(desc->camera_type);
	info.is_enabled = desc->is_enabled;
	
	return camera::create(info).get_id();
}

EDITOR_INTERFACE void RemoveCamera(id::id_type camera_id)
{
	camera::remove(camera::component{ camera::camera_id{camera_id} });
}

EDITOR_INTERFACE bool IsCameraAlive(id::id_type camera_id)
{
	return camera::is_alive(camera::camera_id{ camera_id });
}

EDITOR_INTERFACE id::id_type GetMainCamera()
{
	return camera::get_main_camera().get_id();
}

EDITOR_INTERFACE id::id_type GetActiveCamera()
{
	return camera::get_active_camera().get_id();
}

EDITOR_INTERFACE void SetActiveCamera(id::id_type camera_id)
{
	camera::set_active_camera(camera::component{ camera::camera_id{camera_id} });
}

EDITOR_INTERFACE unsigned int GetCameraCount()
{
	return camera::get_camera_count();
}

EDITOR_INTERFACE void SetCameraPosition(id::id_type camera_id, float x, float y, float z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_position({ x, y, z });
	}
}

EDITOR_INTERFACE void GetCameraPosition(id::id_type camera_id, float* x, float* y, float* z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z) {
		auto pos = cam.position();
		*x = pos.x;
		*y = pos.y;
		*z = pos.z;
	}
}

EDITOR_INTERFACE void SetCameraRotation(id::id_type camera_id, float x, float y, float z, float w)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_rotation({ x, y, z, w });
	}
}

EDITOR_INTERFACE void GetCameraRotation(id::id_type camera_id, float* x, float* y, float* z, float* w)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z && w) {
		auto rot = cam.rotation();
		*x = rot.x;
		*y = rot.y;
		*z = rot.z;
		*w = rot.w;
	}
}

EDITOR_INTERFACE void SetCameraFOV(id::id_type camera_id, float fov)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_field_of_view(fov);
	}
}

EDITOR_INTERFACE float GetCameraFOV(id::id_type camera_id)
{
	camera::component cam{ camera::camera_id{camera_id} };
	return cam.is_valid() ? cam.get_field_of_view() : 60.0f;
}

EDITOR_INTERFACE void SetCameraClipPlanes(id::id_type camera_id, float near_clip, float far_clip)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_near_clip(near_clip);
		cam.set_far_clip(far_clip);
	}
}

EDITOR_INTERFACE void SetCameraProjection(id::id_type camera_id, unsigned char projection)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_projection(static_cast<camera::projection_type>(projection));
	}
}

EDITOR_INTERFACE void SetCameraType(id::id_type camera_id, unsigned char type)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_type(static_cast<camera::camera_type>(type));
	}
}

EDITOR_INTERFACE unsigned char GetCameraType(id::id_type camera_id)
{
	camera::component cam{ camera::camera_id{camera_id} };
	return cam.is_valid() ? static_cast<unsigned char>(cam.get_type()) : 0;
}

EDITOR_INTERFACE void SetCameraEnabled(id::id_type camera_id, bool enabled)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_enabled(enabled);
	}
}

EDITOR_INTERFACE bool IsCameraEnabled(id::id_type camera_id)
{
	camera::component cam{ camera::camera_id{camera_id} };
	return cam.is_valid() ? cam.is_enabled() : false;
}

EDITOR_INTERFACE void SetCameraAspectRatio(id::id_type camera_id, float aspect)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_aspect_ratio(aspect);
	}
}

EDITOR_INTERFACE void SetCameraBackgroundColor(id::id_type camera_id, float r, float g, float b, float a)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_background_color(r, g, b, a);
	}
}

EDITOR_INTERFACE void SetCameraDepth(id::id_type camera_id, int depth)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_depth(depth);
	}
}

EDITOR_INTERFACE void GetCameraForward(id::id_type camera_id, float* x, float* y, float* z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z) {
		auto fwd = cam.get_forward();
		*x = fwd.x;
		*y = fwd.y;
		*z = fwd.z;
	}
}

EDITOR_INTERFACE void GetCameraRight(id::id_type camera_id, float* x, float* y, float* z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z) {
		auto right = cam.get_right();
		*x = right.x;
		*y = right.y;
		*z = right.z;
	}
}

EDITOR_INTERFACE void GetCameraUp(id::id_type camera_id, float* x, float* y, float* z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z) {
		auto up = cam.get_up();
		*x = up.x;
		*y = up.y;
		*z = up.z;
	}
}

EDITOR_INTERFACE void GetCameraViewMatrix(id::id_type camera_id, float* out_matrix)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && out_matrix) {
		DirectX::XMMATRIX view = cam.get_view_matrix();
		DirectX::XMFLOAT4X4 mat;
		DirectX::XMStoreFloat4x4(&mat, view);
		memcpy(out_matrix, &mat, sizeof(float) * 16);
	}
}

EDITOR_INTERFACE void GetCameraProjectionMatrix(id::id_type camera_id, float* out_matrix)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && out_matrix) {
		DirectX::XMMATRIX proj = cam.get_projection_matrix();
		DirectX::XMFLOAT4X4 mat;
		DirectX::XMStoreFloat4x4(&mat, proj);
		memcpy(out_matrix, &mat, sizeof(float) * 16);
	}
}

// Render a camera gizmo (wireframe frustum) at the camera's position
EDITOR_INTERFACE void RenderCameraGizmo(id::id_type camera_id, float r, float g, float b)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (!cam.is_valid()) return;

	auto pos = cam.position();
	auto fwd = cam.get_forward();
	auto right = cam.get_right();
	auto up = cam.get_up();
	
	// Calculate frustum corners for visualization
	f32 fov = cam.get_field_of_view();
	f32 aspect = cam.get_aspect_ratio();
	f32 near_dist = 0.5f;  // Visual near plane
	f32 far_dist = 2.0f;   // Visual far plane
	
	f32 near_height = near_dist * tanf(DirectX::XMConvertToRadians(fov * 0.5f));
	f32 near_width = near_height * aspect;
	f32 far_height = far_dist * tanf(DirectX::XMConvertToRadians(fov * 0.5f));
	f32 far_width = far_height * aspect;
	
	// Render using the DX12 renderer's line rendering
	graphics::dx12::DX12Renderer::instance().render_camera_gizmo(
		{ pos.x, pos.y, pos.z },
		{ fwd.x, fwd.y, fwd.z },
		{ right.x, right.y, right.z },
		{ up.x, up.y, up.z },
		near_width, near_height, far_width, far_height,
		near_dist, far_dist,
		{ r, g, b, 1.0f }
	);
}

// Apply a camera's view/projection to the renderer
EDITOR_INTERFACE void ApplyCameraToRenderer(id::id_type camera_id)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (!cam.is_valid()) return;

	auto pos = cam.position();
	auto fwd = cam.get_forward();
	auto up = cam.get_up();
	
	// Calculate look-at target
	math::v3 target = { pos.x + fwd.x, pos.y + fwd.y, pos.z + fwd.z };
	
	graphics::dx12::DX12Renderer::instance().set_camera(
		{ pos.x, pos.y, pos.z },
		{ target.x, target.y, target.z },
		{ up.x, up.y, up.z }
	);
	
	// Also set projection parameters
	graphics::dx12::DX12Renderer::instance().set_projection(
		cam.get_field_of_view(),
		cam.get_aspect_ratio(),
		cam.get_near_clip(),
		cam.get_far_clip()
	);
}


// Model Import API
