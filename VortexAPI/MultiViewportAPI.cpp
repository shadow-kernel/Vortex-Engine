// ============== MULTI-VIEWPORT RENDERING API ==============

#ifndef EDITOR_INTERFACE
#define EDITOR_INTERFACE extern "C" __declspec(dllexport)
#endif

#include "CommonHeaders.h"
#include "..\Engine\Graphics\DX12\DX12Renderer.h"

/// Viewport camera parameters for secondary viewports.
/// Packed to ensure consistent layout with C# interop.
#pragma pack(push, 4)
struct viewport_camera_desc
{
	f32 position[3];
	f32 target[3];
	f32 up[3];
	f32 fov_degrees;
	f32 near_clip;
	f32 far_clip;
	u8 orthographic;  // Use u8 instead of bool for predictable size
	u8 _padding[3];   // Explicit padding for alignment
	f32 ortho_size;
};
#pragma pack(pop)

EDITOR_INTERFACE unsigned int CreateRenderTarget(unsigned int width, unsigned int height)
{
	return vortex::graphics::dx12::DX12Renderer::instance().create_render_target(width, height);
}

EDITOR_INTERFACE void DestroyRenderTarget(unsigned int target_id)
{
	vortex::graphics::dx12::DX12Renderer::instance().destroy_render_target(target_id);
}

EDITOR_INTERFACE bool ResizeRenderTarget(unsigned int target_id, unsigned int width, unsigned int height)
{
	return vortex::graphics::dx12::DX12Renderer::instance().resize_render_target(target_id, width, height);
}

EDITOR_INTERFACE bool HasRenderTarget(unsigned int target_id)
{
	return vortex::graphics::dx12::DX12Renderer::instance().has_render_target(target_id);
}

EDITOR_INTERFACE void RenderToTarget(unsigned int target_id, viewport_camera_desc* camera, bool render_grid)
{
	if (!camera) return;
	
	vortex::graphics::dx12::ViewportCamera cam{};
	cam.position = { camera->position[0], camera->position[1], camera->position[2] };
	cam.target = { camera->target[0], camera->target[1], camera->target[2] };
	cam.up = { camera->up[0], camera->up[1], camera->up[2] };
	cam.fov_degrees = camera->fov_degrees;
	cam.near_clip = camera->near_clip;
	cam.far_clip = camera->far_clip;
	cam.orthographic = camera->orthographic != 0;
	cam.ortho_size = camera->ortho_size;
	
	vortex::graphics::dx12::DX12Renderer::instance().render_to_target(target_id, cam, render_grid);
}

EDITOR_INTERFACE bool PrepareRenderTargetReadback(unsigned int target_id)
{
	return vortex::graphics::dx12::DX12Renderer::instance().prepare_render_target_readback(target_id);
}

EDITOR_INTERFACE const void* ReadRenderTargetPixels(unsigned int target_id, 
	unsigned int* out_width, unsigned int* out_height, unsigned int* out_row_pitch)
{
	unsigned int w = 0, h = 0, pitch = 0;
	const void* data = vortex::graphics::dx12::DX12Renderer::instance().read_render_target_pixels(target_id, w, h, pitch);
	if (out_width) *out_width = w;
	if (out_height) *out_height = h;
	if (out_row_pitch) *out_row_pitch = pitch;
	return data;
}

EDITOR_INTERFACE void ReleaseRenderTargetPixels(unsigned int target_id)
{
	vortex::graphics::dx12::DX12Renderer::instance().release_render_target_pixels(target_id);
}
