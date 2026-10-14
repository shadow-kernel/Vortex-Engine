#pragma once

#include "../Engine/Graphics/Skinning/BonePalettes.h"

#include "Test.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <set>
#include <vector>

using namespace vortex::graphics;

// The renderers' frame budgets (VortexRenderBudgetTest), headless — the backend-neutral logic both renderers run:
//   * bone palettes (#415): the submeshes of a character share ONE palette copy, and over budget the farthest /
//     off-screen characters lose their animation first, with the fallbacks counted.
// Prints "ALL PASSED" or "FAILURES!" and exits non-zero on a failure.
class engine_test : public test
{
public:
	bool initialize() override { return true; }

	void run() override
	{
		palette_sharing();
		palette_content_is_checked();
		palette_budget_by_relevance();
		palette_stage_overflow();
		std::printf(m_failures ? "FAILURES!\n" : "ALL PASSED\n");
	}

	void shutdown() override { if (m_failures) std::exit(1); }

private:
	int m_failures{ 0 };

	void check(bool ok, const char* what)
	{
		std::printf("  %-78s %s\n", what, ok ? "ok" : "FAIL");
		if (!ok) ++m_failures;
	}

	static DirectX::XMFLOAT4X4 view_projection(const DirectX::XMFLOAT3& eye, const DirectX::XMFLOAT3& at, float fov_degrees = 60.0f)
	{
		using namespace DirectX;
		const XMMATRIX view = XMMatrixLookAtLH(XMLoadFloat3(&eye), XMLoadFloat3(&at), XMVectorSet(0, 1, 0, 0));
		const XMMATRIX proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(fov_degrees), 16.0f / 9.0f, 0.1f, 1000.0f);
		XMFLOAT4X4 vp;
		XMStoreFloat4x4(&vp, view * proj);
		return vp;
	}

	// a character's pose: bone b of character c, distinct values so a mixed-up palette shows
	static void fill_pose(std::vector<float>& pose, u32 bones, u32 character)
	{
		pose.assign((size_t)bones * 16, 0.0f);
		for (u32 b = 0; b < bones; ++b)
			for (u32 e = 0; e < 16; ++e) pose[(size_t)b * 16 + e] = (float)character * 1000.0f + (float)b + (float)e * 0.01f;
	}

	// ---------------------------------------------------------------------------------------- bone palettes (#415)

	void palette_sharing()
	{
		std::printf("bone palettes: the submeshes of a character share one copy\n");
		constexpr u32 BONES = 58, SUBMESHES = 6, CHARACTERS = 300;
		skinning::PaletteStage stage;
		std::vector<float> pose;
		fill_pose(pose, BONES, 7);
		u32 first = skinning::NO_PALETTE;
		bool same = true;
		for (u32 s = 0; s < SUBMESHES; ++s)
		{
			const u32 p = stage.add(pose.data(), BONES);
			if (s == 0) first = p; else same = same && p == first;
		}
		check(same && first != skinning::NO_PALETTE, "6 submits of one palette return the same palette");
		check(stage.matrix_count() == BONES, "6 submits of one 58-bone palette stage 58 matrices, not 348");

		// the crowd of the report: 300 characters x 58 bones x 6 submeshes, every one in view
		stage.clear();
		std::vector<std::vector<float>> poses(CHARACTERS);
		std::vector<skinning::PaletteUse> uses;
		for (u32 c = 0; c < CHARACTERS; ++c)
		{
			fill_pose(poses[c], BONES, c);
			for (u32 s = 0; s < SUBMESHES; ++s)
			{
				const u32 p = stage.add(poses[c].data(), BONES);
				uses.push_back({ p, (float)(c % 30) * 2.0f - 30.0f, 0.0f, 20.0f + (float)(c / 30) * 2.0f, 1.0f, false });
			}
		}
		check(stage.palettes().size() == CHARACTERS, "300 characters -> 300 palettes");
		check(stage.matrix_count() == CHARACTERS * BONES, "bone matrices used = characters x bones (17400), not x submeshes");
		const DirectX::XMFLOAT3 eye{ 0.0f, 1.7f, 0.0f };
		std::vector<u32> gpu;
		std::vector<float> packed;
		const auto budget = skinning::assign_palette_budget(stage, uses, view_frustum(view_projection(eye, { 0.0f, 1.7f, 10.0f })), eye,
			skinning::MAX_BONE_MATRICES_PER_FRAME, gpu, packed);
		bool identity = true;
		for (size_t i = 0; i < stage.palettes().size(); ++i) identity = identity && gpu[i] == stage.palettes()[i].offset;
		check(!budget.compacted && identity, "it fits: the GPU buffer is the stage as it is");
		check(budget.used == CHARACTERS * BONES && budget.fallbacks == 0, "300 animated characters: no bind-pose fallback");
		std::printf("    used %u / %u matrices, %u fallbacks\n", budget.used, budget.capacity, budget.fallbacks);
	}

	void palette_content_is_checked()
	{
		std::printf("bone palettes: a refilled buffer is not mistaken for the palette before\n");
		skinning::PaletteStage stage;
		std::vector<float> buffer;
		fill_pose(buffer, 20, 1);
		const u32 a = stage.add(buffer.data(), 20);
		fill_pose(buffer, 20, 2);   // the same array, the next character's pose
		const u32 b = stage.add(buffer.data(), 20);
		const u32 b2 = stage.add(buffer.data(), 20);
		check(a != b, "same array, new content -> a palette of its own");
		check(b == b2, "and its next submesh shares that one");
		check(stage.matrix_count() == 40, "two poses staged");
		const auto& pal = stage.palettes();
		check(stage.data()[(size_t)pal[a].offset * 16] == 1000.0f && stage.data()[(size_t)pal[b].offset * 16] == 2000.0f,
			"each copy holds its own pose");
		const u32 c = stage.add(buffer.data(), 19);
		check(c != b, "same array, other bone count -> a palette of its own");
		std::vector<float> moved(buffer.begin(), buffer.begin() + 19 * 16);   // the GC moved the array between two submeshes
		const u32 d = stage.add(moved.data(), 19);
		check(d == c && stage.matrix_count() == 59, "the same pose from a moved array right after is still shared");
	}

	void palette_budget_by_relevance()
	{
		std::printf("bone palettes: over budget the farthest / off-screen characters fall back first\n");
		constexpr u32 BONES = 58, SUBMESHES = 6, CHARACTERS = 2000;   // 116000 matrices > 65536
		const DirectX::XMFLOAT3 eye{ 0.0f, 1.7f, 0.0f };
		const ViewFrustum frustum = view_frustum(view_projection(eye, { 0.0f, 1.7f, 10.0f }));
		std::vector<std::vector<float>> poses(CHARACTERS);
		for (u32 c = 0; c < CHARACTERS; ++c) fill_pose(poses[c], BONES, c);

		// characters on a grid around the camera: every 4th behind it (outside the view), the rest in front; one far
		// behind the camera is the first-person layer (the player's own arms) and must always keep its palette
		auto position = [](u32 c, float& x, float& z)
		{
			const float row = (float)(c / 40), col = (float)(c % 40);
			x = col * 3.0f - 59.3f;   // no two characters at exactly the same distance
			z = (c % 4 == 3) ? -(5.0f + row * 3.0f) : 5.0f + row * 3.0f;
		};
		constexpr u32 ARMS = 1999;
		auto run = [&](bool reverse, std::vector<u32>& gpu, std::vector<float>& packed, skinning::PaletteStage& stage,
			std::vector<skinning::PaletteUse>& uses)
		{
			stage.clear(); uses.clear();
			for (u32 i = 0; i < CHARACTERS; ++i)
			{
				const u32 c = reverse ? CHARACTERS - 1 - i : i;   // the hierarchy order must not matter
				float x, z; position(c, x, z);
				for (u32 s = 0; s < SUBMESHES; ++s)
				{
					const u32 p = stage.add(poses[c].data(), BONES);
					uses.push_back({ p, x, 0.9f, z, 1.2f, c == ARMS });
				}
			}
			return skinning::assign_palette_budget(stage, uses, frustum, eye, skinning::MAX_BONE_MATRICES_PER_FRAME, gpu, packed);
		};

		skinning::PaletteStage stage;
		std::vector<skinning::PaletteUse> uses;
		std::vector<u32> gpu;
		std::vector<float> packed;
		const auto budget = run(false, gpu, packed, stage, uses);
		check(budget.compacted, "116000 matrices > 65536: the budget compacts");
		check(budget.used <= budget.capacity && budget.used + BONES > budget.capacity, "the GPU buffer is filled up to its capacity");

		// per character: kept? in view? distance
		struct Info { bool kept; bool visible; float d; u32 palette; };
		std::vector<Info> info(CHARACTERS);
		u32 visible_dropped_uses = 0;
		for (size_t u = 0; u < uses.size(); ++u)
		{
			const u32 c = (u32)(u / SUBMESHES);
			const auto& use = uses[u];
			const bool visible = sphere_frustum_slack(frustum, use.x, use.y, use.z, use.radius) >= 0.0f;
			const float dx = use.x - eye.x, dz = use.z - eye.z;
			info[c] = { gpu[use.palette] != skinning::NO_PALETTE, visible, sqrtf(dx * dx + dz * dz), use.palette };
			if ((visible || use.always) && gpu[use.palette] == skinning::NO_PALETTE) ++visible_dropped_uses;
		}
		float nearest_dropped_visible = 1e30f, farthest_kept_visible = 0.0f;
		bool offscreen_kept = false, visible_dropped = false;
		for (u32 c = 0; c < CHARACTERS; ++c)
		{
			if (c == ARMS) continue;
			const Info& i = info[c];
			if (i.visible && i.kept && i.d > farthest_kept_visible) farthest_kept_visible = i.d;
			if (i.visible && !i.kept) { visible_dropped = true; if (i.d < nearest_dropped_visible) nearest_dropped_visible = i.d; }
			if (!i.visible && i.kept) offscreen_kept = true;
		}
		check(info[ARMS].kept, "the first-person layer keeps its palette although it is far behind the camera");
		check(!(visible_dropped && offscreen_kept), "off-screen characters lose their palette before any character in view");
		check(farthest_kept_visible <= nearest_dropped_visible + 1e-3f, "in view, the nearest keep their palette, the farthest fall back");
		check(budget.fallbacks == visible_dropped_uses, "fallbacks = the skinned draws in view drawn in the bind pose");
		std::printf("    used %u / %u, %u bind-pose draws in view, kept up to %.1f m, dropped from %.1f m\n", budget.used, budget.capacity,
			budget.fallbacks, farthest_kept_visible, nearest_dropped_visible);

		bool data_ok = true;
		for (u32 c = 0; c < CHARACTERS; c += 37)
		{
			const u32 p = info[c].palette;
			if (gpu[p] == skinning::NO_PALETTE) continue;
			const float* src = stage.data() + (size_t)stage.palettes()[p].offset * 16;
			for (u32 e = 0; e < BONES * 16; ++e) data_ok = data_ok && packed[(size_t)gpu[p] * 16 + e] == src[e];
		}
		check(data_ok, "a kept palette's GPU copy is its pose");

		// the same crowd submitted in the opposite order keeps the same characters
		skinning::PaletteStage stage2;
		std::vector<skinning::PaletteUse> uses2;
		std::vector<u32> gpu2;
		std::vector<float> packed2;
		run(true, gpu2, packed2, stage2, uses2);
		std::set<u32> kept1, kept2;
		for (size_t u = 0; u < uses.size(); u += SUBMESHES)
		{
			if (gpu[uses[u].palette] != skinning::NO_PALETTE) kept1.insert((u32)(u / SUBMESHES));
			if (gpu2[uses2[u].palette] != skinning::NO_PALETTE) kept2.insert(CHARACTERS - 1 - (u32)(u / SUBMESHES));
		}
		check(kept1 == kept2, "the hierarchy order does not decide who keeps the animation");
	}

	void palette_stage_overflow()
	{
		std::printf("bone palettes: the CPU stage has a ceiling\n");
		skinning::PaletteStage stage;
		std::vector<float> big((size_t)skinning::MAX_BONE_MATRICES_PER_FRAME * 16, 1.0f);
		bool ok = true;
		for (u32 i = 0; i < 4; ++i)
		{
			big[0] = (float)i;   // other content each time: no sharing
			ok = ok && stage.add(big.data(), skinning::MAX_BONE_MATRICES_PER_FRAME) != skinning::NO_PALETTE;
		}
		big[0] = 99.0f;
		const u32 over = stage.add(big.data(), 1);
		check(ok && over == skinning::NO_PALETTE && stage.overflow() == 1, "beyond 4x the GPU budget a submit is refused and counted");
		std::vector<u32> gpu;
		std::vector<float> packed;
		const auto budget = skinning::assign_palette_budget(stage, {}, ViewFrustum{}, { 0, 0, 0 }, skinning::MAX_BONE_MATRICES_PER_FRAME, gpu, packed);
		check(budget.fallbacks == 1 && budget.used == skinning::MAX_BONE_MATRICES_PER_FRAME, "the refused submit counts as a bind-pose fallback");
	}
};
