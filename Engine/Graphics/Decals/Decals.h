#pragma once

// ============================================================================
// Decals (#120) — the backend-neutral half of the projected decal system.
//
// A decal is a unit box (-0.5 .. 0.5) placed in the world by a matrix; it projects along its local +Y axis onto
// whatever opaque geometry lies inside the box: the decal pass draws the box's back faces without a depth test,
// reconstructs the scene position of every covered pixel from the depth buffer, rejects pixels outside the box,
// and blends the decal material (its albedo texture × base colour × the decal tint) over the lit scene — lit like
// a surface, multiplied into it (grime, blood) or added (glow). The editor submits the scene's Decal components
// and the script API's spawned decals every SubmitScene; the renderer keeps the list until the next one.
// The GPU instance layout is byte-matched to decals.hlsl / decals.metal / decals_common.glsl.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include <DirectXMath.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace vortex::graphics::decals
{
	constexpr u32 MAX_DECALS = 1024;
	enum : u32 { BLEND_LIT = 0, BLEND_MULTIPLY = 1, BLEND_ADDITIVE = 2 };

	// One decal as submitted by the API.
	struct Decal
	{
		DirectX::XMFLOAT4X4 world;                 // unit box -> world (row-major, row 3 = translation); local +Y = projection axis
		id::id_type material{ id::invalid_id };    // albedo texture + base colour (invalid = untextured white)
		DirectX::XMFLOAT4 color{ 1, 1, 1, 1 };     // tint rgb + opacity
		float angle_fade{ 0.5f };                  // facing (surface normal · axis) at which the decal is fully opaque; 0 = hard
		float fade_distance{ 0.0f };               // 0 = never; else fully faded at this view distance (m)
		u32 blend{ BLEND_LIT };
		std::int32_t sort_order{ 0 };              // higher draws later (on top)
	};

	// GPU instance (160 bytes), byte-matched to the shaders.
	struct GpuDecal
	{
		DirectX::XMFLOAT4X4 world;
		DirectX::XMFLOAT4X4 inv_world;
		DirectX::XMFLOAT4 color;
		float angle_fade, fade_distance, pad0, pad1;
	};
	static_assert(sizeof(GpuDecal) == 160, "GpuDecal must byte-match decals.hlsl / decals.metal / decals_common.glsl");

	// A draw: `count` instances from `first` that share a blend mode and a material.
	struct Batch
	{
		u32 first, count;
		u32 blend;
		id::id_type material;
	};

	// Orders the frame's decals (sort order, then blend and material so they batch) and builds the GPU instances.
	// False when there is nothing to draw.
	inline bool build(const std::vector<Decal>& in, std::vector<GpuDecal>& instances, std::vector<Batch>& batches)
	{
		instances.clear();
		batches.clear();
		if (in.empty()) return false;
		std::vector<u32> order(in.size());
		for (u32 i = 0; i < (u32)in.size(); ++i) order[i] = i;
		std::stable_sort(order.begin(), order.end(), [&](u32 a, u32 b)
		{
			const Decal& A = in[a]; const Decal& B = in[b];
			if (A.sort_order != B.sort_order) return A.sort_order < B.sort_order;
			if (A.blend != B.blend) return A.blend < B.blend;
			return A.material < B.material;
		});
		using namespace DirectX;
		instances.reserve(in.size());
		for (u32 idx : order)
		{
			const Decal& d = in[idx];
			XMVECTOR det;
			XMMATRIX inv = XMMatrixInverse(&det, XMLoadFloat4x4(&d.world));
			if (std::fabs(XMVectorGetX(det)) < 1e-12f) continue;   // a box with no volume projects onto nothing
			GpuDecal g{};
			g.world = d.world;
			XMStoreFloat4x4(&g.inv_world, inv);
			g.color = d.color;
			g.angle_fade = d.angle_fade;
			g.fade_distance = d.fade_distance;
			const u32 i = (u32)instances.size();
			if (!batches.empty() && batches.back().blend == d.blend && batches.back().material == d.material) ++batches.back().count;
			else batches.push_back(Batch{ i, 1, d.blend, d.material });
			instances.push_back(g);
		}
		return !instances.empty();
	}
}
