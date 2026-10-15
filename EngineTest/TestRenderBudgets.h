#pragma once

#include "../Engine/Graphics/Skinning/BonePalettes.h"
#include "../Engine/Graphics/Lighting/LightSelection.h"
#include "../Engine/Graphics/Lighting/CascadeSplits.h"

#include "Test.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <set>
#include <type_traits>
#include <vector>

using namespace vortex::graphics;

// The renderers' frame budgets (VortexRenderBudgetTest), headless — the backend-neutral logic both renderers run:
//   * bone palettes (#415): the submeshes of a character share ONE palette copy, and over budget the farthest /
//     off-screen characters lose their animation first, with the fallbacks counted;
//   * the light budget (#416): beyond 16 point / 8 spot lights the choice holds still (hysteresis), changes fade where
//     they would show, a fast turn or a cut is lit right at once, a light the game switches on shows at once, the spot
//     cone and the view frustum count, shadow maps stay put (within the budget: the first casters, as before) and a view
//     rendered now and then never stops half-faded;
//   * the sun's cascade splits (#466): the classic scheme up to 80 m, beyond it the first cascade grows only slowly and
//     the boxes reach back towards the light by no more than the near cascade did at 80 m.
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
		lights_road_walk();
		lights_jitter();
		lights_switched_on_show_at_once();
		lights_under_budget_unchanged();
		lights_cone_and_frustum();
		lights_fast_turn();
		lights_shadow_maps_stay();
		lights_preview_settles();
		cascades_classic_up_to_80m();
		cascades_adapt_beyond_80m();
		cascades_pull_back();
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

	// ------------------------------------------------------------------------------------------ the light budget (#416)

	static_assert(!std::is_copy_constructible_v<lighting::LightSelector> && !std::is_copy_assignable_v<lighting::LightSelector>,
		"a LightSelector points into its own state map: no copies");

	struct TestLight
	{
		u64 id;
		DirectX::XMFLOAT3 pos;
		float range, intensity;
		bool shadow{ false };
		bool spot{ false };
		DirectX::XMFLOAT3 dir{ 0.0f, 0.0f, 1.0f };
		float angle{ 60.0f };
	};

	// What one update left for a light holding a slot.
	struct Lit { float fade; bool shadow; float shadow_fade; bool in_view; float score; };

	// A view the way the renderers drive it: score every light for the camera, start over on a cut (lighting::camera_cut),
	// update the selector -> the slot holders by id.
	struct View
	{
		lighting::LightSelector sel;
		DirectX::XMFLOAT3 eye{}, dir{};
		bool primed{ false };
		std::map<u64, bool> in_view;   // every light's in_view in the last update

		std::map<u64, Lit> frame(const std::vector<TestLight>& lights, const DirectX::XMFLOAT3& e, const DirectX::XMFLOAT3& at, u32 slots,
			u32 shadow_maps, float dt)
		{
			const lighting::Viewer viewer = lighting::make_viewer(e, view_projection(e, at));
			const DirectX::XMFLOAT3 d = lighting::view_direction(viewer);
			const bool snap = !primed || lighting::camera_cut(eye, dir, e, d);
			eye = e; dir = d; primed = true;
			std::vector<lighting::Candidate> c;
			std::vector<float> scores;
			in_view.clear();
			for (const TestLight& l : lights)
			{
				bool v = true;
				const float s = l.spot ? lighting::spot_light_score(l.pos, l.dir, l.range, l.angle, l.intensity, viewer, &v)
					: lighting::point_light_score(l.pos, l.range, l.intensity, viewer, &v);
				c.push_back({ l.id, s, l.shadow, v });
				scores.push_back(s);
				in_view[l.id] = v;
			}
			sel.update(c.data(), (u32)c.size(), slots, shadow_maps, dt, snap);
			std::map<u64, Lit> out;
			for (const lighting::Selected& s : sel.selected())
				out[lights[s.index].id] = { s.fade, s.shadow, s.shadow_fade, c[s.index].in_view, scores[s.index] };
			return out;
		}
	};

	// the road of the report: 40 street lamps 10 m apart beside it, range 15 m, all equally bright
	static std::vector<TestLight> road_lamps(bool shadows = false)
	{
		std::vector<TestLight> lamps;
		for (u32 i = 0; i < 40; ++i) lamps.push_back({ 100u + i, { 4.0f, 5.0f, 10.0f * (float)i }, 15.0f, 10.0f, shadows });
		return lamps;
	}

	// the car at z: two tail lights (points) and two shadowed headlights (spots) ride with it; the camera follows 6 m behind
	static void car(float z, const std::vector<TestLight>& lamps, std::vector<TestLight>& points, std::vector<TestLight>& spots,
		DirectX::XMFLOAT3& eye, DirectX::XMFLOAT3& at)
	{
		points = lamps;
		points.push_back({ 1, { -0.7f, 0.8f, z - 2.2f }, 6.0f, 4.0f });
		points.push_back({ 2, { 0.7f, 0.8f, z - 2.2f }, 6.0f, 4.0f });
		spots.clear();
		spots.push_back({ 3, { -0.7f, 0.7f, z + 2.2f }, 30.0f, 20.0f, true, true, { 0.0f, -0.1f, 1.0f }, 50.0f });
		spots.push_back({ 4, { 0.7f, 0.7f, z + 2.2f }, 30.0f, 20.0f, true, true, { 0.0f, -0.1f, 1.0f }, 50.0f });
		eye = { 0.0f, 2.5f, z - 6.0f };
		at = { 0.0f, 1.0f, z + 10.0f };
	}

	// The largest change of a light's intensity (or, with shadows, of the darkening its shadow causes: intensity x shadow
	// strength) between two frames that anyone could see: only lights whose reach was in the view in both frames count. A
	// light out of the view lights nothing on screen, so entering at full intensity from there, or leaving at once to there,
	// shows no step.
	static float max_visible_step(const std::map<u64, Lit>& a, const std::map<u64, Lit>& b, const std::map<u64, bool>& was_in_view,
		const std::map<u64, bool>& is_in_view, bool shadows = false)
	{
		auto value = [shadows](const std::map<u64, Lit>& m, u64 id)
		{
			const auto it = m.find(id);
			if (it == m.end()) return 0.0f;
			return shadows ? (it->second.shadow ? it->second.fade * it->second.shadow_fade : 0.0f) : it->second.fade;
		};
		float worst = 0.0f;
		for (const auto& [id, now] : is_in_view)
		{
			const auto was = was_in_view.find(id);
			if (!now || was == was_in_view.end() || !was->second) continue;
			const float d = fabsf(value(b, id) - value(a, id));
			if (d > worst) worst = d;
		}
		return worst;
	}

	static bool same_set(const std::map<u64, Lit>& a, const std::map<u64, Lit>& b)
	{
		if (a.size() != b.size()) return false;
		for (const auto& [id, l] : a)
			if (b.find(id) == b.end()) return false;
		return true;
	}

	void lights_road_walk()
	{
		std::printf("light budget: 40 lamps along a road, the camera follows a car at 10 m/s for 43 s (#416)\n");
		const auto lamps = road_lamps();
		const float dt = 1.0f / 60.0f;
		View points, spots;
		std::map<u64, Lit> prev_p, cur_p, prev_s, cur_s;
		std::map<u64, bool> prev_view_p, prev_view_s;
		std::map<u64, int> entries, exits;
		std::vector<TestLight> pts, sps;
		DirectX::XMFLOAT3 eye, at;
		float worst_step = 0.0f;
		int frames = 0, set_changes = 0, last_change = -100000, min_gap = 100000;
		bool own_lights_full = true, near_lamps_lit = true, slots_kept = true;
		for (frames = 0; frames <= 2580; ++frames)
		{
			const float z = -20.0f + 10.0f * dt * (float)frames;
			car(z, lamps, pts, sps, eye, at);
			cur_p = points.frame(pts, eye, at, 16, 2, dt);
			cur_s = spots.frame(sps, eye, at, 8, 4, dt);
			slots_kept = slots_kept && cur_p.size() <= 16 && cur_s.size() <= 8;
			if (frames > 0)
			{
				const float step = (std::max)(max_visible_step(prev_p, cur_p, prev_view_p, points.in_view), max_visible_step(prev_s, cur_s, prev_view_s, spots.in_view));
				if (step > worst_step) worst_step = step;
				if (!same_set(prev_p, cur_p))
				{
					++set_changes;
					if (frames - last_change < min_gap) min_gap = frames - last_change;
					last_change = frames;
					for (const auto& [id, l] : cur_p) if (prev_p.find(id) == prev_p.end()) ++entries[id];
					for (const auto& [id, l] : prev_p) if (cur_p.find(id) == cur_p.end()) ++exits[id];
				}
				// the car's own lights never flicker
				for (u64 id : { 1ull, 2ull }) { const auto it = cur_p.find(id); own_lights_full = own_lights_full && it != cur_p.end() && it->second.fade == 1.0f; }
				for (u64 id : { 3ull, 4ull }) { const auto it = cur_s.find(id); own_lights_full = own_lights_full && it != cur_s.end() && it->second.fade == 1.0f; }
				// the lamps from just behind the car to 40 m ahead of it light what the camera looks at: always on, full
				for (const TestLight& l : lamps)
				{
					if (l.pos.z < z - 10.0f || l.pos.z > z + 40.0f) continue;
					const auto it = cur_p.find(l.id);
					near_lamps_lit = near_lamps_lit && it != cur_p.end() && it->second.fade == 1.0f;
				}
			}
			prev_p = cur_p; prev_s = cur_s; prev_view_p = points.in_view; prev_view_s = spots.in_view;
		}
		int most_toggles = 0;
		for (const auto& [id, n] : entries) most_toggles = (std::max)(most_toggles, n);
		for (const auto& [id, n] : exits) most_toggles = (std::max)(most_toggles, n);
		std::printf("    %d frames, %d changes of the lit set (closest %d frames apart), largest visible intensity step %.3f\n", frames, set_changes, min_gap, worst_step);
		check(slots_kept, "never more than 16 point / 8 spot lights hold a slot, fading ones included");
		check(worst_step <= lighting::MAX_FADE_STEP + 1e-5f, "no light in view changes its intensity by more than 0.1 between two frames");
		check(min_gap >= 10, "the lit set changes at most once per 10 frames");
		check(most_toggles <= 1, "no lamp is switched on or off more than once per pass");
		check(set_changes >= 20, "and it follows the car: lamps ahead come on, lamps behind go out");
		check(own_lights_full, "the car's own tail lights and headlights never flicker");
		check(near_lamps_lit, "the lamps around and ahead of the car are lit at full intensity");
	}

	void lights_jitter()
	{
		std::printf("light budget: a camera that shakes by 5 cm changes nothing (#416)\n");
		const auto lamps = road_lamps();
		const float dt = 1.0f / 60.0f;
		bool stable = true;
		for (float stop : { 60.0f, 135.0f, 212.0f, 333.0f })
		{
			View points, spots;
			std::map<u64, Lit> p, sp, p0, s0;
			std::vector<TestLight> pts, sps;
			DirectX::XMFLOAT3 eye, at;
			// drive there, then stand for 2 s so every fade is done
			for (float z = -20.0f; z < stop; z += 10.0f * dt)
			{
				car(z, lamps, pts, sps, eye, at);
				points.frame(pts, eye, at, 16, 2, dt);
				spots.frame(sps, eye, at, 8, 4, dt);
			}
			car(stop, lamps, pts, sps, eye, at);
			for (int i = 0; i < 120; ++i) { p = points.frame(pts, eye, at, 16, 2, dt); sp = spots.frame(sps, eye, at, 8, 4, dt); }
			p0 = p; s0 = sp;
			// +-5 cm along x, y and z in turn, every frame, for 3 s
			for (int i = 0; i < 180; ++i)
			{
				const float o = (i & 1) ? 0.05f : -0.05f;
				DirectX::XMFLOAT3 e = eye, a = at;
				const int axis = (i / 2) % 3;
				(axis == 0 ? e.x : axis == 1 ? e.y : e.z) += o;
				(axis == 0 ? a.x : axis == 1 ? a.y : a.z) += o;
				p = points.frame(pts, e, a, 16, 2, dt);
				sp = spots.frame(sps, e, a, 8, 4, dt);
				stable = stable && same_set(p, p0) && same_set(sp, s0);
				for (const auto& [id, l] : p) stable = stable && l.fade == p0[id].fade && l.shadow == p0[id].shadow;
				for (const auto& [id, l] : sp) stable = stable && l.fade == s0[id].fade && l.shadow == s0[id].shadow;
			}
		}
		check(stable, "at 4 stops along the road, 3 s of +-5 cm camera shake leave the lit set and every intensity as they were");
	}

	void lights_switched_on_show_at_once()
	{
		std::printf("light budget: a light the game switches on shows at once, also when every slot is taken (#416)\n");
		const auto lamps = road_lamps();
		const float dt = 1.0f / 60.0f;
		View points;
		std::map<u64, Lit> p, before, prev;
		std::map<u64, bool> prev_view;
		std::vector<TestLight> pts, sps;
		DirectX::XMFLOAT3 eye, at;
		car(100.0f, lamps, pts, sps, eye, at);
		for (int i = 0; i < 120; ++i) p = points.frame(pts, eye, at, 16, 2, dt);
		before = p;
		// a muzzle flash next to the camera for three frames
		auto flashing = pts;
		flashing.push_back({ 999, { eye.x, eye.y, eye.z + 1.0f }, 8.0f, 50.0f });
		p = points.frame(flashing, eye, at, 16, 2, dt);
		const bool full_at_once = before.size() == 16 && p.count(999) && p[999].fade == 1.0f && p.size() <= 16;
		points.frame(flashing, eye, at, 16, 2, dt);
		p = points.frame(flashing, eye, at, 16, 2, dt);
		const bool held = p.count(999) && p[999].fade == 1.0f;
		p = points.frame(pts, eye, at, 16, 2, dt);   // the flash is over
		const bool gone = !p.count(999);
		// the slot it took is refilled by a lamp fading in
		bool smooth = true;
		prev = p; prev_view = points.in_view;
		for (int i = 0; i < 90; ++i)
		{
			p = points.frame(pts, eye, at, 16, 2, dt);
			smooth = smooth && max_visible_step(prev, p, prev_view, points.in_view) <= lighting::MAX_FADE_STEP + 1e-5f;
			prev = p; prev_view = points.in_view;
		}
		bool full_again = p.size() == 16;
		for (const auto& [id, l] : p) full_again = full_again && l.fade == 1.0f;
		check(full_at_once, "a muzzle flash next to the camera is lit at full intensity in its first frame, within the 16 slots");
		check(held, "and stays lit while it is submitted");
		check(gone, "switched off: gone at once");
		check(smooth && full_again, "the freed slot is refilled by a lamp fading in, back to 16 lamps at full intensity");
	}

	void lights_under_budget_unchanged()
	{
		std::printf("light budget: up to 16 point lights everything shows, at once, as before (#416)\n");
		std::vector<TestLight> few;
		for (u32 i = 0; i < 10; ++i) few.push_back({ 500u + i, { (float)i * 3.0f, 2.0f, 20.0f }, 8.0f, 5.0f });
		View points;
		const float dt = 1.0f / 60.0f;
		auto p = points.frame(few, { 0.0f, 2.0f, 0.0f }, { 0.0f, 2.0f, 10.0f }, 16, 2, dt);
		bool all = p.size() == 10;
		for (const auto& [id, l] : p) all = all && l.fade == 1.0f;
		bool in_order = true;   // the light buffer keeps the submission order, as it always had
		u32 n = 0;
		for (const lighting::Selected& s : points.sel.selected()) in_order = in_order && s.index == n++;
		// far away and looking elsewhere: they all still fit, nothing fades
		bool still = true;
		for (int i = 0; i < 60; ++i)
		{
			p = points.frame(few, { 0.0f, 2.0f, -500.0f }, { 0.0f, 2.0f, -600.0f }, 16, 2, dt);
			still = still && p.size() == 10;
			for (const auto& [id, l] : p) still = still && l.fade == 1.0f;
		}
		auto nine = few;
		nine.pop_back();
		p = points.frame(nine, { 0.0f, 2.0f, -500.0f }, { 0.0f, 2.0f, -600.0f }, 16, 2, dt);
		const bool removed = p.size() == 9 && !p.count(509);
		p = points.frame(few, { 0.0f, 2.0f, -500.0f }, { 0.0f, 2.0f, -600.0f }, 16, 2, dt);
		const bool readded = p.size() == 10 && p.count(509) && p[509].fade == 1.0f;
		check(all && in_order, "10 lights: all lit at full intensity in the first frame, in submission order");
		check(still, "and still all at full intensity while the camera is far away, looking elsewhere");
		check(removed && readded, "one switched off and on again: gone at once, back at once");
	}

	void lights_cone_and_frustum()
	{
		std::printf("light budget: the spot cone and the view frustum count (#416)\n");
		const float dt = 1.0f / 60.0f;
		const DirectX::XMFLOAT3 eye{ 0.0f, 2.0f, 0.0f }, at{ 0.0f, 2.0f, 10.0f };
		// 8 bright spots 3 m behind the camera pointing away from the view, and one dimmer spot ahead lighting what it sees
		std::vector<TestLight> spots;
		for (u32 i = 0; i < 8; ++i) spots.push_back({ 600u + i, { -7.0f + 2.0f * (float)i, 3.0f, -3.0f }, 12.0f, 30.0f, false, true, { 0.0f, -0.3f, -1.0f }, 40.0f });
		spots.push_back({ 700, { 0.0f, 4.0f, 15.0f }, 12.0f, 10.0f, false, true, { 0.0f, -1.0f, 0.2f }, 60.0f });
		View s1;
		auto f = s1.frame(spots, eye, at, 8, 4, dt);
		check(f.count(700) == 1, "a dimmer spot lighting the view beats brighter ones behind the camera that point away from it");
		// the same 8 spots turned round towards the view keep their slots
		for (u32 i = 0; i < 8; ++i) spots[i].dir = { 0.0f, -0.3f, 1.0f };
		View s2;
		f = s2.frame(spots, eye, at, 8, 4, dt);
		check(f.count(700) == 0, "turned towards the view they win again (it is the cone, not the position)");
		// 16 bright point lights whose reach ends 6 m behind the camera, and one dim light in view
		std::vector<TestLight> pts;
		for (u32 i = 0; i < 16; ++i) pts.push_back({ 800u + i, { -15.0f + 2.0f * (float)i, 2.0f, -12.0f }, 6.0f, 40.0f });
		pts.push_back({ 900, { 0.0f, 1.0f, 25.0f }, 6.0f, 3.0f });
		View p1;
		f = p1.frame(pts, eye, at, 16, 2, dt);
		check(f.count(900) == 1, "a dim point light in view beats bright ones whose reach lies entirely behind the camera");
	}

	// light that reaches the screen: the score of every lit light in view times its intensity
	static float light_on_screen(const std::map<u64, Lit>& lit)
	{
		float sum = 0.0f;
		for (const auto& [id, l] : lit) if (l.in_view) sum += l.score * l.fade;
		return sum;
	}

	void lights_fast_turn()
	{
		std::printf("light budget: after a whip-around or a cut the lights in view are right in the first frame (#416)\n");
		const auto lamps = road_lamps();
		const float dt = 1.0f / 60.0f;
		const DirectX::XMFLOAT3 eye{ 0.0f, 2.5f, 200.0f };
		auto look = [&](float yaw_degrees)
		{
			const float r = yaw_degrees * 0.01745329252f;
			return DirectX::XMFLOAT3{ eye.x + sinf(r) * 10.0f, 1.5f, eye.z + cosf(r) * 10.0f };
		};
		auto expected = [&](float yaw)   // what a view that has always looked this way shows
		{
			View fresh;
			return light_on_screen(fresh.frame(lamps, eye, look(yaw), 16, 2, dt));
		};
		// a 180° whip-around in one frame (a camera cut) and in 6 frames of 30° (a fast mouse turn)
		float worst = 1.0f;
		for (int frames_to_turn : { 1, 6 })
		{
			View v;
			for (int i = 0; i < 120; ++i) v.frame(lamps, eye, look(0.0f), 16, 2, dt);
			std::map<u64, Lit> lit;
			for (int k = 1; k <= frames_to_turn; ++k) lit = v.frame(lamps, eye, look(180.0f * (float)k / (float)frames_to_turn), 16, 2, dt);
			const float share = light_on_screen(lit) / expected(180.0f);
			std::printf("    turned in %d frame(s): %.0f %% of the light in view in the first frame\n", frames_to_turn, share * 100.0f);
			worst = (std::min)(worst, share);
		}
		check(worst >= 0.95f, "after a 180 degree turn, in 1 or in 6 frames, at least 95 % of the light in view in the first frame");
	}

	void lights_shadow_maps_stay()
	{
		std::printf("light budget: within the budget the shadow maps go to the first casters, as before (#416)\n");
		// the Tactical Range case: 12 shadow-casting point lights (2 cube maps), under budget, the camera walks past them
		std::vector<TestLight> pts;
		for (u32 i = 0; i < 12; ++i) pts.push_back({ 1000u + i, { (i % 2 ? 6.0f : -6.0f), 3.0f, 8.0f * (float)i }, 10.0f, 8.0f, true });
		View v;
		const float dt = 1.0f / 60.0f;
		int handovers = 0;
		bool first_two = true;
		std::map<u64, Lit> prev;
		for (int i = 0; i <= 600; ++i)
		{
			const float z = -10.0f + 10.0f * dt * (float)i;
			auto lit = v.frame(pts, { 0.0f, 1.7f, z }, { 0.0f, 1.5f, z + 10.0f }, 16, 2, dt);
			for (const auto& [id, l] : lit)
				first_two = first_two && l.shadow == (id == 1000 || id == 1001) && (!l.shadow || l.shadow_fade == 1.0f);
			if (i > 0)
				for (const auto& [id, l] : lit)
					if (prev.count(id) && prev[id].shadow != l.shadow) { ++handovers; break; }
			prev = lit;
		}
		check(first_two && handovers == 0, "12 shadowed point lights, 10 s of walking: the first two in submission order keep both maps at full strength");

		std::printf("light budget: over budget a shadow map changes hands only after its holder's shadow faded out (#416)\n");
		// 30 shadow-casting lamps along the road, 4 spot shadow maps, 8 slots: the camera drives past them
		std::vector<TestLight> spots;
		for (u32 i = 0; i < 30; ++i) spots.push_back({ 2000u + i, { 3.0f, 6.0f, 10.0f * (float)i }, 14.0f, 15.0f, true, true, { -0.2f, -1.0f, 0.0f }, 70.0f });
		View s;
		std::map<u64, Lit> before;
		std::map<u64, bool> view_before;
		float worst_step = 0.0f;
		bool at_most_4 = true, handed_after_fade = true;
		int gains = 0;
		for (int i = 0; i <= 1800; ++i)
		{
			const float z = -10.0f + 10.0f * dt * (float)i;
			auto lit = s.frame(spots, { 0.0f, 2.0f, z }, { 0.0f, 1.5f, z + 10.0f }, 8, 4, dt);
			int holders = 0;
			for (const auto& [id, l] : lit) holders += l.shadow ? 1 : 0;
			at_most_4 = at_most_4 && holders <= 4;
			if (i > 0)
			{
				worst_step = (std::max)(worst_step, max_visible_step(before, lit, view_before, s.in_view, true));
				for (const auto& [id, l] : lit)
				{
					const bool had = before.count(id) && before[id].shadow;
					if (l.shadow && !had) ++gains;
					// a holder in view gives its map up only once its shadow has faded out
					if (!l.shadow && had && view_before[id] && s.in_view[id] && before[id].fade * before[id].shadow_fade > lighting::MAX_FADE_STEP + 1e-5f) handed_after_fade = false;
				}
			}
			before = lit; view_before = s.in_view;
		}
		std::printf("    %d shadow maps handed over while driving past 30 shadowed lamps, largest visible shadow step %.3f\n", gains, worst_step);
		check(at_most_4, "never more than the 4 spot shadow maps in use");
		check(handed_after_fade && worst_step <= lighting::MAX_FADE_STEP + 1e-5f, "a map changes hands only after its holder's shadow faded out, no shadow pops in view");
		check(gains >= 10, "and the maps do follow the camera along the road");
	}

	void lights_preview_settles()
	{
		std::printf("light budget: a view rendered now and then never stops half-faded (#416)\n");
		const auto lamps = road_lamps();
		std::vector<TestLight> pts, sps;
		DirectX::XMFLOAT3 eye, at;
		View v;
		car(100.0f, lamps, pts, sps, eye, at);
		for (int i = 0; i < 60; ++i) v.frame(pts, eye, at, 16, 2, 1.0f / 60.0f);
		const bool settled_still = v.sel.settled();
		// the camera moves on (15 m: no cut): lamps change places, and the view renders only every 100 ms (a camera preview)
		car(115.0f, lamps, pts, sps, eye, at);
		v.frame(pts, eye, at, 16, 2, 0.1f);
		const bool busy = !v.sel.settled();
		int renders = 1;
		while (!v.sel.settled() && renders < 100) { v.frame(pts, eye, at, 16, 2, 0.1f); ++renders; }
		std::printf("    every 100 ms: settled after %d renders\n", renders);
		// a render 0.3 s after the last one finishes every fade at once
		car(130.0f, lamps, pts, sps, eye, at);
		v.frame(pts, eye, at, 16, 2, 0.1f);
		const bool busy_again = !v.sel.settled();
		auto lit = v.frame(pts, eye, at, 16, 2, lighting::SETTLE_SECONDS + 0.05f);
		bool full = v.sel.settled();
		for (const auto& [id, l] : lit) full = full && l.fade == 1.0f;
		check(settled_still && busy && renders <= 12, "a view that renders every 100 ms is unsettled while a light fades and settles within ~1 s");
		check(busy_again && full, "a render after more than 0.25 s finishes every fade at once: the view is settled");
	}

	// ---------------------------------------------------------------------------------------- the sun's cascades (#466)

	// the scheme both renderers ran before #466: lambda 0.6 at any distance
	static float classic_split(float far_z, unsigned c)
	{
		const float near_z = 0.1f, f = (float)(c + 1) / 3.0f;
		const float log_split = near_z * powf(far_z / near_z, f);
		const float uni_split = near_z + (far_z - near_z) * f;
		return uni_split + 0.6f * (log_split - uni_split);
	}

	void cascades_classic_up_to_80m()
	{
		std::printf("sun cascades: the classic split scheme up to the default 80 m (#466)\n");
		bool same = true;
		for (float d : { 5.0f, 20.0f, 40.0f, 80.0f })
		{
			float s[3];
			lighting::cascade_splits(d, 3, s);
			for (unsigned c = 0; c < 3; ++c) same = same && s[c] == classic_split(d, c);
		}
		float s80[3];
		lighting::cascade_splits(80.0f, 3, s80);
		std::printf("    80 m: %.2f / %.2f / %.2f m\n", s80[0], s80[1], s80[2]);
		check(same, "up to 80 m the splits are exactly the classic lambda-0.6 scheme");
		check(s80[2] == 80.0f, "the last cascade ends at the shadow distance");
	}

	void cascades_adapt_beyond_80m()
	{
		std::printf("sun cascades: a longer distance keeps the near cascade small (#466)\n");
		float s80[3], s300[3], s1000[3];
		lighting::cascade_splits(80.0f, 3, s80);
		lighting::cascade_splits(300.0f, 3, s300);
		lighting::cascade_splits(1000.0f, 3, s1000);
		std::printf("    300 m: %.2f / %.2f / %.2f m (lambda %.3f), the classic scheme would give %.2f / %.2f\n",
			s300[0], s300[1], s300[2], lighting::cascade_lambda(300.0f, 3), classic_split(300.0f, 0), classic_split(300.0f, 1));
		std::printf("    1000 m: %.2f / %.2f / %.2f m (lambda %.3f)\n", s1000[0], s1000[1], s1000[2], lighting::cascade_lambda(1000.0f, 3));
		check(s300[0] > s80[0] && s300[0] < 1.5f * s80[0] && s300[0] < 0.5f * classic_split(300.0f, 0),
			"300 m: the first cascade grows by less than half (the classic scheme: x3.6)");
		check(s1000[0] < 2.0f * s80[0], "1000 m: the first cascade stays under twice its 80 m size");
		bool ordered = true, lambda_ok = true;
		float prev_first = 0.0f;
		for (float d = 80.0f; d <= 5000.0f; d *= 1.25f)
		{
			float s[3];
			lighting::cascade_splits(d, 3, s);
			ordered = ordered && s[0] > lighting::CASCADE_NEAR_Z && s[0] < s[1] && s[1] < s[2] && s[2] == d && s[0] >= prev_first;
			const float l = lighting::cascade_lambda(d, 3);
			lambda_ok = lambda_ok && l >= lighting::CASCADE_CLASSIC_LAMBDA && l <= lighting::CASCADE_MAX_LAMBDA;
			prev_first = s[0];
		}
		check(ordered, "80 m .. 5 km: the splits rise strictly, the last is the distance, the first never shrinks");
		check(lambda_ok, "the blend stays between the classic 0.6 and 0.97");
	}

	void cascades_pull_back()
	{
		std::printf("sun cascades: how far a cascade's box reaches back towards the light (#466)\n");
		check(lighting::cascade_pull_back(80.0f, 14.0f) == 80.0f && lighting::cascade_pull_back(80.0f, 120.0f) == 80.0f
			&& lighting::cascade_pull_back(40.0f, 10.0f) == 40.0f, "up to 80 m: the shadow distance, as before");
		check(lighting::cascade_pull_back(300.0f, 20.0f) == 80.0f && lighting::cascade_pull_back(300.0f, 57.0f) == 114.0f
			&& lighting::cascade_pull_back(300.0f, 370.0f) == 300.0f, "300 m: 80 m for the near cascade, the cascade's diameter beyond, at most the distance");
	}
};
