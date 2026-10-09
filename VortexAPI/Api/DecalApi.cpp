#include "../ApiCommon.h"
#include "../../Engine/Graphics/Decals/Decals.h"
#include <cstring>

// ============================================================================================
// Decal interop (#120) — the extern "C" surface over the renderer's decal list (Engine/Graphics/Decals/Decals.h).
// The editor's DecalService clears and refills the list on every SubmitScene (like the lights): the scene's Decal
// components and the script API's spawned decals. Both backends draw the list between the opaque and the
// transparent meshes of the world pass. Internal ABI: changed in lockstep with Editor/DllWrapper/VortexAPI.Decals.cs.
// ============================================================================================
namespace dcl = vortex::graphics::decals;

EDITOR_INTERFACE void ClearDecals()
{
	graphics::Renderer::instance().clear_decals();
}

// world16 = the unit box's world matrix, 16 floats row-major (row 3 = translation, the SubmitRenderItem layout); the
// box projects along its local +Y. material_id = an engine material id (its albedo texture + base colour; invalid =
// untextured white). blend: 0 lit, 1 multiply, 2 additive. Capped at MAX_DECALS per frame.
EDITOR_INTERFACE void AddDecal(const float* world16, id::id_type material_id, float r, float g, float b, float a,
	float angle_fade, float fade_distance, int32_t blend, int32_t sort_order)
{
	if (!world16) return;
	dcl::Decal d{};
	memcpy(&d.world, world16, sizeof(DirectX::XMFLOAT4X4));
	d.material = material_id;
	d.color = { r, g, b, a };
	d.angle_fade = angle_fade < 0.0f ? 0.0f : (angle_fade > 1.0f ? 1.0f : angle_fade);
	d.fade_distance = fade_distance < 0.0f ? 0.0f : fade_distance;
	d.blend = blend < 0 ? 0u : (blend > 2 ? 2u : (u32)blend);
	d.sort_order = sort_order;
	graphics::Renderer::instance().add_decal(d);
}

EDITOR_INTERFACE int32_t GetDecalCount()
{
	return (int32_t)graphics::Renderer::instance().decal_list().size();
}
