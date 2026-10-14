#include "LightSelection.h"
#include <algorithm>
#include <cmath>

namespace vortex::graphics::lighting
{
	namespace
	{
		u64 splitmix64(u64 x)
		{
			x += 0x9E3779B97F4A7C15ull;
			x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ull;
			x = (x ^ (x >> 27)) * 0x94D049BB133111EBull;
			return x ^ (x >> 31);
		}

		// #335: intensity × (how deep the camera sits inside the reach + how close the reach is)
		float reach_score(const DirectX::XMFLOAT3& p, float range, float intensity, const DirectX::XMFLOAT3& eye)
		{
			const float dx = p.x - eye.x, dy = p.y - eye.y, dz = p.z - eye.z;
			const float d = sqrtf(dx * dx + dy * dy + dz * dz);
			float inside = range > 0.0f ? 1.0f - d / range : 1.0f;
			if (inside < 0.0f) inside = 0.0f;
			float nearness = range > 0.0f ? range / (d + 1e-3f) : 1.0f;
			if (nearness > 1.0f) nearness = 1.0f;
			return (intensity > 0.0f ? intensity : 0.0f) * (inside + nearness);
		}

		float visibility(float slack) { return slack >= 0.0f ? 1.0f : expf(slack / VISIBILITY_FALLOFF); }

		// How far a spot's lit volume reaches into the frustum (>= 0: it touches the inside of every plane). The volume —
		// the range sphere cut to the cone — lies inside the cone of height `range`; per plane the farthest point of that
		// cone is its apex or the rim of its base. Very wide cones fall back to the range sphere.
		float cone_frustum_slack(const ViewFrustum& f, const DirectX::XMFLOAT3& apex, DirectX::XMFLOAT3 dir, float range, float half_angle)
		{
			const float len = sqrtf(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
			if (len < 1e-6f || half_angle > 1.0472f)   // no direction, or wider than 120°
				return sphere_frustum_slack(f, apex.x, apex.y, apex.z, range);
			dir.x /= len; dir.y /= len; dir.z /= len;
			const float h = range > 0.0f ? range : 0.0f;
			const float rim = h * tanf(half_angle);
			const float qx = apex.x + dir.x * h, qy = apex.y + dir.y * h, qz = apex.z + dir.z * h;
			float slack = 3.4e38f;
			for (int i = 0; i < 6; ++i)
			{
				const float* p = f.p[i];
				const float at_apex = p[0] * apex.x + p[1] * apex.y + p[2] * apex.z + p[3];
				const float nd = p[0] * dir.x + p[1] * dir.y + p[2] * dir.z;
				const float across = 1.0f - nd * nd;
				const float at_rim = p[0] * qx + p[1] * qy + p[2] * qz + p[3] + rim * sqrtf(across > 0.0f ? across : 0.0f);
				const float s = at_apex > at_rim ? at_apex : at_rim;
				if (s < slack) slack = s;
			}
			return slack;
		}

		u64 quantize(float v, float scale)
		{
			if (!(v == v)) v = 0.0f;   // NaN
			v *= scale;
			if (v > 1e9f) v = 1e9f;
			if (v < -1e9f) v = -1e9f;
			return (u64)(s64)llroundf(v);
		}
	}

	float point_light_score(const DirectX::XMFLOAT3& position, float range, float intensity, const Viewer& viewer, bool* in_view)
	{
		const float slack = sphere_frustum_slack(viewer.frustum, position.x, position.y, position.z, range > 0.0f ? range : 0.0f);
		if (in_view) *in_view = slack >= 0.0f;
		return reach_score(position, range, intensity, viewer.eye) * visibility(slack);
	}

	float spot_light_score(const DirectX::XMFLOAT3& position, const DirectX::XMFLOAT3& direction, float range, float spot_angle_degrees,
		float intensity, const Viewer& viewer, bool* in_view)
	{
		// spot_angle is the full outer cone (the shaders take half of it)
		const float half_angle = (spot_angle_degrees > 0.0f ? spot_angle_degrees : 0.0f) * 0.5f * 0.01745329252f;
		const float slack = cone_frustum_slack(viewer.frustum, position, direction, range, half_angle);
		if (in_view) *in_view = slack >= 0.0f;
		return reach_score(position, range, intensity, viewer.eye) * visibility(slack);
	}

	u64 fallback_light_id(u32 kind, const DirectX::XMFLOAT3& color, float range, float spot_angle_degrees)
	{
		u64 h = 0xCBF29CE484222325ull;   // FNV-1a over the quantized look
		const u64 parts[6] = { (u64)kind, quantize(color.x, 1024.0f), quantize(color.y, 1024.0f), quantize(color.z, 1024.0f),
			quantize(range, 64.0f), quantize(spot_angle_degrees, 16.0f) };
		for (u64 v : parts) { h ^= v; h *= 0x100000001B3ull; }
		h = splitmix64(h);
		return h ? h : 1;
	}

	void LightSelector::reset()
	{
		m_states.clear();
		for (auto& s : m_slots) s = nullptr;
		m_selected.clear();
		m_settled = true;
	}

	int LightSelector::free_slot() const
	{
		for (size_t k = 0; k < m_slots.size(); ++k)
			if (!m_slots[k]) return (int)k;
		return -1;
	}

	int LightSelector::dimmest_leaving_slot() const
	{
		int best = -1;
		for (size_t k = 0; k < m_slots.size(); ++k)
		{
			const State* s = m_slots[k];
			if (!s || s->chosen) continue;
			if (best < 0 || s->fade < m_slots[best]->fade || (s->fade == m_slots[best]->fade && s->score < m_slots[best]->score)) best = (int)k;
		}
		return best;
	}

	// a light gives its slot (and its shadow map) back
	void LightSelector::release(size_t k)
	{
		State* s = m_slots[k];
		s->fade = 0.0f; s->slot = -1; s->shadow = false; s->shadow_fade = 0.0f;
		m_slots[k] = nullptr;
		++m_left;
	}

	void LightSelector::update(const Candidate* candidates, u32 count, u32 slots, u32 shadow_maps, float dt, bool snap)
	{
		m_entered = m_left = 0;
		if (!candidates) count = 0;
		if (snap || m_slots.size() != slots)
		{
			m_states.clear();
			m_slots.assign(slots, nullptr);
		}
		++m_update;
		// a view that last updated SETTLE_SECONDS ago or more (a preview rendered now and then) showed nothing in between:
		// every fade finishes at once
		const float step = dt >= SETTLE_SECONDS ? 1.0f : (std::min)((dt > 0.0f ? dt : 0.0f) / FADE_SECONDS, MAX_FADE_STEP);

		// 1) this frame's lights and their state. An id handed over twice in one frame (two lights without ids that look
		//    alike, a duplicated prefab) is told apart by its occurrence — stable as long as the submission order is.
		m_dupes.clear();
		m_by_index.resize(count);
		for (u32 i = 0; i < count; ++i)
		{
			u64 id = candidates[i].id ? candidates[i].id : 1;
			u32& seen_before = m_dupes[id];
			u64 key = seen_before == 0 ? id : splitmix64(id ^ (0xD1B54A32D192ED03ull * seen_before));
			++seen_before;
			auto it = m_states.try_emplace(key).first;
			while (it->second.seen == m_update)   // a derived key that collides with a light of this frame (practically never)
			{
				key = splitmix64(key);
				it = m_states.try_emplace(key).first;
			}
			State& s = it->second;
			s.fresh = s.seen == 0;   // a new state: the light was not submitted in the previous update
			s.was_in_view = !s.fresh && s.in_view;
			s.in_view = candidates[i].in_view;
			s.seen = m_update;
			s.index = i;
			s.score = candidates[i].score > 0.0f ? candidates[i].score : 0.0f;   // also maps NaN to 0
			s.wants_shadow = candidates[i].wants_shadow;
			m_by_index[i] = &s;
		}

		// 2) lights that were not submitted are gone at once: the game removed them or switched them off
		for (auto it = m_states.begin(); it != m_states.end();)
		{
			if (it->second.seen != m_update)
			{
				if (it->second.slot >= 0) release((size_t)it->second.slot);
				it = m_states.erase(it);
			}
			else ++it;
		}

		// 3) the chosen: the `slots` best by kept score — a light chosen in the last update counts KEEP_BONUS times, so a
		//    newcomer has to clearly outrank it (ties keep the submission order)
		m_order.resize(count);
		for (u32 i = 0; i < count; ++i) m_order[i] = i;
		std::stable_sort(m_order.begin(), m_order.end(), [this](u32 a, u32 b)
		{
			const State& sa = *m_by_index[a];
			const State& sb = *m_by_index[b];
			return (sa.chosen ? sa.score * KEEP_BONUS : sa.score) > (sb.chosen ? sb.score * KEEP_BONUS : sb.score);
		});
		const u32 chosen = count < slots ? count : slots;
		for (u32 r = 0; r < count; ++r) m_by_index[m_order[r]]->chosen = r < chosen;

		// 4) the slot holders fade towards their target. A leaving light gives its slot back once it is dark — or at once
		//    when its reach has left the view: it lights nothing on screen, there is nothing to fade
		for (size_t k = 0; k < m_slots.size(); ++k)
		{
			State* s = m_slots[k];
			if (!s) continue;
			if (s->chosen) { s->fade += step; if (s->fade > 1.0f) s->fade = 1.0f; }
			else
			{
				s->fade = s->in_view ? s->fade - step : 0.0f;
				if (s->fade <= 0.0f) release(k);
			}
		}

		// 5) chosen lights without a slot take a free one, best first. A light that fell out of the budget while in view
		//    fades in; one that was off-screen (a fast turn, a cut) or is off-screen enters at full intensity — nothing on
		//    screen showed it before — and one the game has just switched on shows at once and, when every slot is taken,
		//    takes the slot of the dimmest leaving light (there always is one: the chosen are at most `slots`, and this one
		//    holds none)
		for (u32 r = 0; r < chosen; ++r)
		{
			State& s = *m_by_index[m_order[r]];
			if (s.slot >= 0) continue;
			int k = free_slot();
			if (k < 0 && s.fresh)
			{
				k = dimmest_leaving_slot();
				if (k >= 0) release((size_t)k);
			}
			if (k < 0) continue;   // waits until a leaving light is dark
			m_slots[(size_t)k] = &s;
			s.slot = k;
			s.fade = (s.fresh || !s.was_in_view || !s.in_view) ? 1.0f : step;
			++m_entered;
		}

		// 6) shadow maps. When every caster holds a light slot — always so within the budget — the first `shadow_maps`
		//    casters in submission order get them at full strength: exactly the rule the renderers always had. Over budget a
		//    map goes to the most relevant chosen casters (a holder counts KEEP_BONUS times) and changes hands only after its
		//    holder's shadow has faded out; a new holder's shadow fades in (at once where nothing on screen showed it before).
		bool every_caster_lit = true;
		for (u32 i = 0; i < count && every_caster_lit; ++i)
			if (m_by_index[i]->wants_shadow && m_by_index[i]->slot < 0) every_caster_lit = false;
		if (every_caster_lit)
		{
			u32 given = 0;
			for (u32 i = 0; i < count; ++i)
			{
				State& s = *m_by_index[i];
				s.shadow = s.wants_shadow && given < shadow_maps;
				s.shadow_fade = s.shadow ? 1.0f : 0.0f;
				s.shadow_wanted = s.shadow;
				if (s.shadow) ++given;
			}
		}
		else
		{
			m_casters.clear();
			for (u32 i = 0; i < count; ++i)
			{
				State& s = *m_by_index[i];
				s.shadow_wanted = false;
				if (s.slot >= 0 && s.chosen && s.wants_shadow) m_casters.push_back(&s);
				else if (s.shadow && (s.slot < 0 || !s.wants_shadow)) { s.shadow = false; s.shadow_fade = 0.0f; }   // dark, or no longer casting
			}
			std::stable_sort(m_casters.begin(), m_casters.end(), [](const State* a, const State* b)
			{
				return (a->shadow ? a->score * KEEP_BONUS : a->score) > (b->shadow ? b->score * KEEP_BONUS : b->score);
			});
			for (size_t i = 0; i < m_casters.size() && i < shadow_maps; ++i) m_casters[i]->shadow_wanted = true;
			u32 held = 0;
			for (u32 i = 0; i < count; ++i)
			{
				State& s = *m_by_index[i];
				if (!s.shadow) continue;
				if (s.shadow_wanted) { s.shadow_fade += step; if (s.shadow_fade > 1.0f) s.shadow_fade = 1.0f; }
				else
				{
					s.shadow_fade = s.in_view ? s.shadow_fade - step : 0.0f;
					if (s.shadow_fade <= 0.0f) { s.shadow = false; s.shadow_fade = 0.0f; continue; }   // the map is free
				}
				++held;
			}
			for (State* s : m_casters)
			{
				if (held >= shadow_maps) break;
				if (!s->shadow_wanted || s->shadow) continue;
				s->shadow = true;
				s->shadow_fade = (s->fresh || !s->was_in_view || !s->in_view) ? 1.0f : step;
				++held;
			}
		}

		// 7) the result in submission order, and whether anything is still on its way
		m_selected.clear();
		m_settled = true;
		for (u32 i = 0; i < count; ++i)
		{
			const State& s = *m_by_index[i];
			if (s.slot >= 0) m_selected.push_back({ i, s.fade, s.shadow, s.shadow ? s.shadow_fade : 0.0f });
			if ((s.chosen && s.slot < 0) || (s.slot >= 0 && (!s.chosen || s.fade < 1.0f))
				|| (s.shadow && (s.shadow_fade < 1.0f || !s.shadow_wanted)) || (s.shadow_wanted && !s.shadow))
				m_settled = false;
		}
	}
}
