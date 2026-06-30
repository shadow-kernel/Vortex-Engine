#include "../ApiCommon.h"

EDITOR_INTERFACE id::id_type CreateMeshRenderer(id::id_type entity_id, id::id_type mesh_id, id::id_type material_id)
{
	components::mesh_renderer_init_info info{};
	info.mesh_id = mesh_id;
	info.material_id = material_id;
	info.cast_shadows = true;
	info.receive_shadows = true;
	
	return components::mesh_renderer::create(entity_id, info).value;
}

EDITOR_INTERFACE void RemoveMeshRenderer(id::id_type renderer_id)
{
	components::mesh_renderer::remove(components::mesh_renderer_id{ renderer_id });
}

EDITOR_INTERFACE void SetMeshRendererMesh(id::id_type renderer_id, id::id_type mesh_id)
{
	components::mesh_renderer::set_mesh(components::mesh_renderer_id{ renderer_id }, mesh_id);
}

EDITOR_INTERFACE void SetMeshRendererMaterial(id::id_type renderer_id, id::id_type material_id)
{
	components::mesh_renderer::set_material(components::mesh_renderer_id{ renderer_id }, material_id);
}

EDITOR_INTERFACE id::id_type GetMeshRendererMesh(id::id_type renderer_id)
{
	return components::mesh_renderer::get_mesh(components::mesh_renderer_id{ renderer_id });
}

EDITOR_INTERFACE id::id_type GetMeshRendererMaterial(id::id_type renderer_id)
{
	return components::mesh_renderer::get_material(components::mesh_renderer_id{ renderer_id });
}

// Submit all active MeshRenderers to the render queue
EDITOR_INTERFACE void SubmitAllMeshRenderers()
{
	constexpr u32 MAX_RENDERERS = 4096;
	static id::id_type renderer_ids[MAX_RENDERERS];
	
	u32 count = components::mesh_renderer::get_all_renderers(renderer_ids, MAX_RENDERERS);
	
	auto& renderer = graphics::dx12::DX12Renderer::instance();
	
	for (u32 i = 0; i < count; ++i)
	{
		components::mesh_renderer_id renderer_id{ renderer_ids[i] };
		id::id_type mesh_id = components::mesh_renderer::get_mesh(renderer_id);
		id::id_type material_id = components::mesh_renderer::get_material(renderer_id);
		id::id_type entity_id = components::mesh_renderer::get_entity(renderer_id);
		
		if (!id::is_valid(mesh_id) || !id::is_valid(entity_id))
			continue;
		
		// Get entity transform - for now use identity matrix
		// TODO: Get actual transform from entity
		graphics::dx12::RenderItem item{};
		item.mesh_id = mesh_id;
		item.material_id = material_id;
		DirectX::XMStoreFloat4x4(&item.world_matrix, DirectX::XMMatrixIdentity());
		
	renderer.submit_render_item(item);
	}
}

// ============== LIGHTING SYSTEM API ==============

// Clear all dynamic lights (call before submitting new lights each frame)
