#pragma once

// ============================================================================
// Bone palettes for GPU skinning (#415) — the backend-neutral part both renderers (DX12, SDL GPU) share.
//
// A skinned draw reads its bone matrices from one per-frame GPU buffer of MAX_BONE_MATRICES_PER_FRAME matrices, the
// same capacity on every backend. The scene submits one draw per SUBMESH, each with its character's palette — the
// same array for every submesh of the character — so PaletteStage keeps ONE copy of a palette per frame: a 58-bone
// character of 6 submeshes costs 58 matrices, not 348.
// The stage may hold more than the GPU buffer (up to MAX_STAGED_BONE_MATRICES). assign_palette_budget() decides per
// view which palettes get GPU space: all of them when they fit (the stage layout is used as it is); over budget the
// characters inside the view first, nearest first, so the farthest / off-screen ones fall back to the bind pose — never
// hierarchy order — and the renderer counts and reports them (engine_stats, a rate-limited log line).
// ============================================================================
#include "../ViewFrustum.h"
#include <unordered_map>
#include <vector>

namespace vortex::graphics::skinning
{
	constexpr u32 MAX_BONE_MATRICES_PER_FRAME = 65536;                          // 4 MiB of float4x4 per frame
	constexpr u32 MAX_STAGED_BONE_MATRICES = 4 * MAX_BONE_MATRICES_PER_FRAME;   // CPU-side ceiling of one frame's palettes
	constexpr u32 NO_PALETTE = 0xFFFFFFFFu;

	class PaletteStage
	{
	public:
		struct Palette { u32 offset; u32 count; };   // in matrices

		// Stage a palette for this frame and return its index (palettes()[index].offset = its first matrix). The same
		// palette handed over again — the next submesh of the character — is shared, not copied. NO_PALETTE when the
		// stage is full (the draw falls back to the bind pose; counted in overflow()).
		u32 add(const float* matrices, u32 count);
		void clear();
		void swap(PaletteStage& other) noexcept;

		const float* data() const { return m_data.data(); }
		u32 matrix_count() const { return (u32)(m_data.size() / 16); }
		const std::vector<Palette>& palettes() const { return m_palettes; }
		u32 overflow() const { return m_overflow; }

	private:
		std::vector<float> m_data;
		std::vector<Palette> m_palettes;
		std::unordered_map<const float*, u32> m_by_source;   // the caller's array -> its palette (the content is checked)
		u32 m_overflow{ 0 };
	};

	// One skinned draw of a view: its palette and the world-space sphere around the character.
	struct PaletteUse
	{
		u32 palette;
		float x, y, z, radius;
		bool always;   // the first-person layer (the player's own arms): never culled, nearest of all
	};

	struct PaletteBudget
	{
		u32 used{ 0 };            // matrices in the GPU buffer for this view
		u32 capacity{ 0 };
		u32 fallbacks{ 0 };       // skinned draws in the view without room for their palette: drawn in the bind pose
		bool compacted{ false };  // over budget: the GPU layout differs from the stage, `packed` holds it
	};

	// gpu_offset[palette] = where the palette sits in the GPU buffer (in matrices); NO_PALETTE = no room (bind pose).
	// Fits: gpu_offset = the stage offsets, `packed` is untouched (upload the stage as it is). Over budget: `packed` is the
	// buffer content, palettes ranked by their most relevant draw — inside the view before outside, then nearest first.
	PaletteBudget assign_palette_budget(const PaletteStage& stage, const std::vector<PaletteUse>& uses, const ViewFrustum& frustum,
		const DirectX::XMFLOAT3& eye, u32 capacity, std::vector<u32>& gpu_offset, std::vector<float>& packed);
}
