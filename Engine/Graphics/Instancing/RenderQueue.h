#pragma once

// ============================================================================
// The scene render queue, shared by both render backends (DX12 and SDL GPU).
//
// Retained instances (#361). The editor's play mode and the player used to send the whole scene again every frame
// something moved, and the renderer merged and re-sorted all of it — frame cost grew with the entity count instead of
// with what changed. Now every rigid mesh of the scene is a persistent instance the managed scene addresses by a handle:
// it sends only what changed (an instance created, moved, re-coloured or removed) as one batch of InstanceOps per frame.
// A moved or re-coloured instance is updated in place; only a change of the set of (layer, material, mesh, winding)
// keys re-sorts — and then only the new instances, merged into the sorted rest — so frames that only move things keep
// the draw runs. The render queue is the retained instances (sorted) followed by the frame's own submits (skinned
// characters, terrain, foliage, water, editor previews …), which are sorted on their own.
//
// CpuStats: what the queue cost the CPU in the last frame — the swap of the submitted items into the render queue,
// the sort + draw-run build, the cull + instance pack of every view and the shadow caster gather. engine_stats and the
// player's FPS line show them, so a change to the submit path can be measured instead of guessed.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include <algorithm>
#include <chrono>
#include <cstring>
#include <vector>

namespace vortex::graphics::instancing
{
	struct CpuStats
	{
		float swap_ms{ 0.0f };     // submitted items -> render queue (merge, retained-instance sync)
		float sort_ms{ 0.0f };     // the sort and the draw-run build (0 on a frame that kept the layout)
		float cull_ms{ 0.0f };     // frustum / distance cull + instance pack, summed over every view of the frame
		float shadow_ms{ 0.0f };   // the shadow casters gathered and packed per tile
		u32 sorted{ 0 };           // items the sort touched this frame
		u32 queue{ 0 };            // items in the render queue
		u32 frame{ 0 };            // which frame these are (counts up), so a sampler sees each frame once

		// render_frame: publish the frame that just finished into `last` and start the next one
		void next_frame(CpuStats& last)
		{
			last = *this;
			const u32 f = frame + 1;
			*this = {};
			frame = f;
		}
	};

	using Clock = std::chrono::steady_clock;

	inline float ms_since(Clock::time_point t0)
	{
		return std::chrono::duration<float, std::milli>(Clock::now() - t0).count();
	}

	// Adds the milliseconds of its scope to a CpuStats field.
	struct ScopedMs
	{
		float& acc;
		Clock::time_point t0{ Clock::now() };
		explicit ScopedMs(float& accumulator) : acc(accumulator) {}
		~ScopedMs() { acc += ms_since(t0); }
		ScopedMs(const ScopedMs&) = delete;
		ScopedMs& operator=(const ScopedMs&) = delete;
	};

	// ---------------------------------------------------------------------------------------------
	// Retained instances
	// ---------------------------------------------------------------------------------------------

	enum : u32
	{
		OP_SET = 0,      // create or replace: mesh, material, layer, matrix and tint
		OP_MOVE = 1,     // matrix and tint only (the instance keeps its mesh, material and layer)
		OP_REMOVE = 2,
	};

	// One change of a retained instance as the managed scene sends it — 112 bytes, the layout of RenderInstanceOp in
	// Editor/DllWrapper/Rendering/VortexRendering.cs.
	struct InstanceOp
	{
		u32 handle;        // the scene's id of the instance: small, dense, reused after a remove
		u32 kind;          // OP_SET / OP_MOVE / OP_REMOVE
		u64 mesh;
		u64 material;
		u32 layer;         // 0 world, 1 first-person viewmodel (#175)
		u32 reserved;
		float world[16];   // row-major, row 3 = translation (the SubmitRenderItem layout)
		float color[4];    // the per-instance tint (#331)
	};
	static_assert(sizeof(InstanceOp) == 112, "InstanceOp is 112 bytes: RenderInstanceOp in VortexRendering.cs has the same layout");

	constexpr u32 MAX_HANDLES = 1u << 20;   // more than MAX_RENDER_OBJECTS; a larger handle is ignored

	// What a sync did to the retained part of the render queue.
	enum class Sync
	{
		None,        // nothing changed
		Patched,     // matrices / tints changed in place: the draw runs stay
		Reordered,   // the same keys in the same runs, the instances inside a run in another order: copy, keep the runs
		Layout,      // the set of keys changed: copy and rebuild the draw runs
	};

	// The queue order: layer first (the first-person runs form the tail, #175), then material, mesh and winding (#334) —
	// consecutive items with the same key draw as one instanced run.
	template <class Item>
	inline bool key_less(const Item& a, const Item& b)
	{
		if (a.layer != b.layer) return a.layer < b.layer;
		if (a.material_id != b.material_id) return a.material_id < b.material_id;
		if (a.mesh_id != b.mesh_id) return a.mesh_id < b.mesh_id;
		return a.mirrored < b.mirrored;
	}

	template <class Item>
	inline bool same_key(const Item& a, const Item& b)
	{
		return a.layer == b.layer && a.material_id == b.material_id && a.mesh_id == b.mesh_id && a.mirrored == b.mirrored;
	}

	// Inside a key, the nearer instance first (partial early-Z) — the order the whole queue always had.
	template <class Item>
	inline bool queue_less(const Item& a, const Item& b, const DirectX::XMFLOAT3& eye)
	{
		if (!same_key(a, b)) return key_less(a, b);
		const float ax = a.world_matrix._41 - eye.x, ay = a.world_matrix._42 - eye.y, az = a.world_matrix._43 - eye.z;
		const float bx = b.world_matrix._41 - eye.x, by = b.world_matrix._42 - eye.y, bz = b.world_matrix._43 - eye.z;
		return (ax * ax + ay * ay + az * az) < (bx * bx + by * by + bz * bz);
	}

	// A world matrix with a negative determinant (an odd number of negative scale axes) flips the triangle winding (#334).
	inline u32 mirrored_of(const DirectX::XMFLOAT4X4& m)
	{
		const float det = m._11 * (m._22 * m._33 - m._23 * m._32) - m._12 * (m._21 * m._33 - m._23 * m._31) + m._13 * (m._21 * m._32 - m._22 * m._31);
		return det < 0.0f ? 1u : 0u;
	}

	// The persistent instances of one renderer. apply() runs on the submit side, the rest on the swap — both under the
	// renderer's queue mutex.
	template <class Item>
	class RetainedInstances
	{
	public:
		static constexpr u32 NONE = 0xFFFFFFFFu;

		void apply(const InstanceOp* ops, u32 count)
		{
			if (!ops) return;
			for (u32 i = 0; i < count; ++i)
			{
				const InstanceOp& op = ops[i];
				if (op.handle >= MAX_HANDLES) continue;
				if (op.handle >= m_pos.size()) m_pos.resize((size_t)op.handle + 1, NONE);
				u32& pos = m_pos[op.handle];
				if (op.kind == OP_REMOVE)
				{
					if (pos != NONE) { kill(pos); pos = NONE; }
					continue;
				}
				if (op.kind == OP_MOVE)
				{
					if (pos == NONE) continue;   // never created (or dropped with a cleared store): the scene sends a SET
					Item& it = m_slots[pos].item;
					std::memcpy(&it.world_matrix, op.world, sizeof(it.world_matrix));
					it.color = { op.color[0], op.color[1], op.color[2], op.color[3] };
					const u32 mirrored = mirrored_of(it.world_matrix);
					if (mirrored == it.mirrored) { m_patched.push_back(pos); continue; }
					Item moved = it;   // the winding is part of the key: the instance moves to another run
					moved.mirrored = mirrored;
					kill(pos);
					pos = append(moved, op.handle);
					continue;
				}
				Item item{};
				item.mesh_id = op.mesh;
				item.material_id = op.material;
				item.layer = op.layer;
				std::memcpy(&item.world_matrix, op.world, sizeof(item.world_matrix));
				item.color = { op.color[0], op.color[1], op.color[2], op.color[3] };
				item.mirrored = mirrored_of(item.world_matrix);
				if (pos != NONE && same_key(m_slots[pos].item, item))
				{
					m_slots[pos].item = item;   // same run: in place
					m_patched.push_back(pos);
					continue;
				}
				if (pos != NONE) kill(pos);
				pos = append(item, op.handle);
			}
		}

		// Everything goes (scene switch, project switch, a mesh the instances drew was destroyed); the scene notices the new
		// epoch and sends its instances again.
		void clear()
		{
			m_slots.clear(); m_pos.clear(); m_patched.clear();
			m_sorted = 0; m_dead = 0; m_layout = true;
			++m_epoch;
		}

		u32 epoch() const { return m_epoch; }
		size_t size() const { return m_slots.size() - m_dead; }
		bool layout_pending() const { return m_layout; }

		// Swap time: removed instances leave, new ones are sorted (nearest to `eye` first inside a key) and merged into the
		// sorted rest. Layout when that changed the set of keys, else Patched / None by what apply() touched.
		Sync settle(const DirectX::XMFLOAT3& eye)
		{
			if (!m_layout) return m_patched.empty() ? Sync::None : Sync::Patched;
			// stable compaction keeps the sorted head sorted
			size_t w = 0, head = 0;
			for (size_t r = 0; r < m_slots.size(); ++r)
			{
				if (m_slots[r].handle == NONE) continue;
				if (r < m_sorted) ++head;
				if (w != r) m_slots[w] = m_slots[r];
				++w;
			}
			m_slots.resize(w);
			std::sort(m_slots.begin() + (std::ptrdiff_t)head, m_slots.end(),
				[&eye](const Slot& a, const Slot& b) { return queue_less(a.item, b.item, eye); });
			// both halves are sorted by key: a key-only merge keeps each key's instances together
			std::inplace_merge(m_slots.begin(), m_slots.begin() + (std::ptrdiff_t)head, m_slots.end(),
				[](const Slot& a, const Slot& b) { return key_less(a.item, b.item); });
			reindex();
			m_layout = false;
			return Sync::Layout;
		}

		// Re-sort every key's instances by the distance to `eye` (the nearest first) — after the camera moved far, so the
		// partial early-Z keeps working. The keys and their runs stay where they are.
		Sync refresh_distance(const DirectX::XMFLOAT3& eye)
		{
			if (m_layout) return settle(eye);
			if (m_slots.empty()) return Sync::None;
			std::sort(m_slots.begin(), m_slots.end(), [&eye](const Slot& a, const Slot& b) { return queue_less(a.item, b.item, eye); });
			reindex();
			return Sync::Reordered;
		}

		// The instances in queue order (valid after settle; dead slots never survive a settle).
		size_t count() const { return m_slots.size(); }
		const Item& at(size_t i) const { return m_slots[i].item; }
		// Positions apply() changed in place since the last take (duplicates possible).
		const std::vector<u32>& patched() const { return m_patched; }
		void clear_patched() { m_patched.clear(); }

		// A mesh was destroyed (ResourceRegistry::mesh_generation changed): does any instance still draw a mesh `alive`
		// rejects? The caller then clears the store and the scene sends its instances again.
		template <class AliveFn>
		bool uses_dead_mesh(AliveFn alive) const
		{
			id::id_type last = id::invalid_id;
			for (const Slot& s : m_slots)
			{
				if (s.handle == NONE || s.item.mesh_id == last) continue;
				if (!alive(s.item.mesh_id)) return true;
				last = s.item.mesh_id;
			}
			return false;
		}

	private:
		struct Slot { Item item; u32 handle; };

		u32 append(const Item& item, u32 handle)
		{
			m_slots.push_back({ item, handle });
			m_layout = true;
			return (u32)(m_slots.size() - 1);
		}

		void kill(u32 pos)
		{
			m_slots[pos].handle = NONE;
			++m_dead;
			m_layout = true;
		}

		void reindex()
		{
			for (size_t i = 0; i < m_slots.size(); ++i) m_pos[m_slots[i].handle] = (u32)i;
			m_sorted = m_slots.size();
			m_dead = 0;
			m_patched.clear();
		}

		std::vector<Slot> m_slots;    // [0, m_sorted) in queue order, appended ones after; a removed slot has handle NONE
		std::vector<u32> m_pos;       // per handle: its slot, or NONE
		std::vector<u32> m_patched;   // slots changed in place since the last settle / take
		size_t m_sorted{ 0 };
		u32 m_dead{ 0 };
		bool m_layout{ false };
		u32 m_epoch{ 1 };
	};

	constexpr u32 NO_RUN = 0xFFFFFFFFu;

	// The draw run in runs[first, last) — the runs one layer's retained instances made, in key order — that an item of the
	// frame's own submits joins: same material, mesh and winding (and layer), not skinned. The queue holds the two parts
	// one after the other, so a key both have (a placed palm and the painted ones of the same model) would otherwise draw
	// as two instanced runs. NO_RUN when there is none.
	template <class Run, class Item>
	inline u32 find_run(const std::vector<Run>& runs, size_t first, size_t last, const Item& item)
	{
		size_t lo = first, hi = last;
		while (lo < hi)
		{
			const size_t mid = lo + (hi - lo) / 2;
			const Run& r = runs[mid];
			const bool less = r.mat != item.material_id ? r.mat < item.material_id
				: r.mesh != item.mesh_id ? r.mesh < item.mesh_id
				: (u32)r.mirrored < item.mirrored;
			if (less) lo = mid + 1; else hi = mid;
		}
		if (lo >= last) return NO_RUN;
		const Run& r = runs[lo];
		return (!r.skinned && r.layer == item.layer && r.mat == item.material_id && r.mesh == item.mesh_id && (u32)r.mirrored == item.mirrored)
			? (u32)lo : NO_RUN;
	}

	// The render queue = the retained instances (when visible), then the frame's own submits as its tail. This brings the
	// retained part up to date, writing only what changed: moved instances in place, everything when the layout changed —
	// then a kept tail (a frame without new submits) moves behind it through `scratch`; with frame_changed the queue ends
	// after the retained part and the caller appends and sorts the new frame part (one copy per frame, as before #361).
	// `queue_retained` / `queue_retained_valid` describe the retained part the queue holds (the render pass may shrink it:
	// it then marks it invalid). Returns true when the draw runs have to be built again (the caller also rebuilds them for
	// a new frame part).
	template <class Item>
	inline bool assemble_retained(std::vector<Item>& queue, RetainedInstances<Item>& retained, Sync sync, bool frame_changed,
		bool visible, size_t& queue_retained, bool& queue_retained_valid, std::vector<Item>& scratch)
	{
		const size_t want = visible ? retained.count() : 0;
		const bool copy_all = sync == Sync::Layout || sync == Sync::Reordered || !queue_retained_valid || want != queue_retained;
		bool rebuild = false;
		if (copy_all)
		{
			const size_t tail = (std::min)(queue_retained, queue.size());
			if (!frame_changed) scratch.assign(queue.begin() + (std::ptrdiff_t)tail, queue.end());
			queue.clear();
			queue.reserve(want + (frame_changed ? 0 : scratch.size()));
			for (size_t i = 0; i < want; ++i) queue.push_back(retained.at(i));
			if (!frame_changed) { queue.insert(queue.end(), scratch.begin(), scratch.end()); scratch.clear(); }
			// a re-ordering inside the runs keeps them; anything else builds them again
			rebuild = !(sync == Sync::Reordered && queue_retained_valid && want == queue_retained);
		}
		else
		{
			// the same retained part: the moved / re-coloured instances in place — also on a frame whose own submits changed
			// (in play that is every frame: a move must not wait for the next layout change)
			if (sync == Sync::Patched)
				for (u32 p : retained.patched()) if (p < want) queue[p] = retained.at(p);
			if (frame_changed) queue.resize(want);
		}
		retained.clear_patched();
		queue_retained = want;
		queue_retained_valid = true;
		return rebuild;
	}
}
