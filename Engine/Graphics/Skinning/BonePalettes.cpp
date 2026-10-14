#include "BonePalettes.h"
#include <algorithm>
#include <cstring>

namespace vortex::graphics::skinning
{
	u32 PaletteStage::add(const float* matrices, u32 count)
	{
		if (!matrices || count == 0) return NO_PALETTE;
		// The submeshes of one character hand over the same array: share the copy staged for it this frame (#415). The
		// content is compared as well, so a caller that refills one buffer for several characters still gets one copy
		// per pose (and an array the GC moved into a freed one's place is never mistaken for it).
		auto same = [&](u32 index)
		{
			const Palette& p = m_palettes[index];
			return p.count == count && memcmp(m_data.data() + (size_t)p.offset * 16, matrices, (size_t)count * 64) == 0;
		};
		const auto it = m_by_source.find(matrices);
		if (it != m_by_source.end() && same(it->second)) return it->second;
		// the array moved between two submeshes (a collection while the managed side was submitting): the previous palette
		// still holds the same pose
		if (!m_palettes.empty() && same((u32)m_palettes.size() - 1))
		{
			m_by_source[matrices] = (u32)m_palettes.size() - 1;
			return (u32)m_palettes.size() - 1;
		}
		const u32 offset = matrix_count();
		if ((u64)offset + count > MAX_STAGED_BONE_MATRICES) { ++m_overflow; return NO_PALETTE; }
		const u32 index = (u32)m_palettes.size();
		m_palettes.push_back({ offset, count });
		m_data.insert(m_data.end(), matrices, matrices + (size_t)count * 16);
		m_by_source[matrices] = index;
		return index;
	}

	void PaletteStage::clear()
	{
		m_data.clear();
		m_palettes.clear();
		m_by_source.clear();
		m_overflow = 0;
	}

	void PaletteStage::swap(PaletteStage& other) noexcept
	{
		m_data.swap(other.m_data);
		m_palettes.swap(other.m_palettes);
		m_by_source.swap(other.m_by_source);
		std::swap(m_overflow, other.m_overflow);
	}

	PaletteBudget assign_palette_budget(const PaletteStage& stage, const std::vector<PaletteUse>& uses, const ViewFrustum& frustum,
		const DirectX::XMFLOAT3& eye, u32 capacity, std::vector<u32>& gpu_offset, std::vector<float>& packed)
	{
		PaletteBudget budget;
		budget.capacity = capacity;
		const auto& palettes = stage.palettes();
		gpu_offset.assign(palettes.size(), NO_PALETTE);
		const u32 total = stage.matrix_count();
		if (total <= capacity)
		{
			// the normal case: every palette fits, the GPU buffer is the stage as it is
			for (size_t i = 0; i < palettes.size(); ++i) gpu_offset[i] = palettes[i].offset;
			budget.used = total;
			budget.fallbacks = stage.overflow();
			return budget;
		}

		// Over budget: rank every palette by its most relevant draw — inside the view (or the first-person layer) before
		// outside, then nearest first — and fill the buffer in that order. A palette no draw uses goes last.
		struct Rank { u32 palette; u32 outside; float d2; };
		std::vector<Rank> rank(palettes.size());
		for (size_t i = 0; i < palettes.size(); ++i) rank[i] = { (u32)i, 2u, 3.4e38f };
		std::vector<unsigned char> in_view(uses.size(), 0);
		for (size_t u = 0; u < uses.size(); ++u)
		{
			const PaletteUse& use = uses[u];
			if (use.palette >= palettes.size()) continue;
			const bool visible = use.always || sphere_frustum_slack(frustum, use.x, use.y, use.z, use.radius) >= 0.0f;
			in_view[u] = visible ? 1 : 0;
			const float dx = use.x - eye.x, dy = use.y - eye.y, dz = use.z - eye.z;
			const u32 outside = visible ? 0u : 1u;
			const float d2 = use.always ? 0.0f : dx * dx + dy * dy + dz * dz;
			Rank& r = rank[use.palette];
			if (outside < r.outside || (outside == r.outside && d2 < r.d2)) { r.outside = outside; r.d2 = d2; }
		}
		std::sort(rank.begin(), rank.end(), [](const Rank& a, const Rank& b)
		{
			if (a.outside != b.outside) return a.outside < b.outside;
			if (a.d2 != b.d2) return a.d2 < b.d2;
			return a.palette < b.palette;
		});

		packed.clear();
		packed.reserve((size_t)capacity * 16);
		u32 used = 0;
		for (const Rank& r : rank)
		{
			const PaletteStage::Palette& p = palettes[r.palette];
			if (used + p.count > capacity) continue;   // a smaller palette further down may still fit
			gpu_offset[r.palette] = used;
			const float* src = stage.data() + (size_t)p.offset * 16;
			packed.insert(packed.end(), src, src + (size_t)p.count * 16);
			used += p.count;
		}
		budget.used = used;
		budget.compacted = true;
		budget.fallbacks = stage.overflow();
		for (size_t u = 0; u < uses.size(); ++u)
			if (in_view[u] && gpu_offset[uses[u].palette] == NO_PALETTE) ++budget.fallbacks;
		return budget;
	}
}
