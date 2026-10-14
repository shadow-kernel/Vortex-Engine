#pragma once

// ============================================================================
// The light budget (#335, #416) — the backend-neutral part both renderers (DX12, SDL GPU) share: which of the
// submitted point / spot lights hold the shader's 16 point / 8 spot slots and the shadow maps in one view, from
// frame to frame.
//
// Every rendered frame scores ALL submitted lights for the view's camera (point_light_score / spot_light_score: the
// brightness, weighted by how deep the camera sits in the light's reach and how close that reach is, times how much
// of it the view can see — a range sphere or spot cone outside the frustum drops out of contention smoothly). A
// LightSelector per view then keeps a little state per light id:
//   * within the budget nothing changes: every light shows at full intensity and the shadow maps go to the first
//     casters in submission order, exactly as before;
//   * hysteresis: a chosen light counts KEEP_BONUS times its score, so a newcomer must clearly outrank it — lamps of
//     equal brightness at similar distances no longer swap places from frame to frame;
//   * fades: a light that enters or leaves the slots because of the budget fades its intensity over FADE_SECONDS. A
//     leaving light keeps its slot until it is dark, so the slots are never exceeded. Where nothing on screen could
//     show a fade, there is none: a light whose reach left the view leaves at once, one that was off-screen enters at
//     full intensity (a fast turn or a cut is lit right in its first frame), a light the game switched on shows at
//     once (a muzzle flash must not wait) and one it switched off is gone at once;
//   * over budget a shadow map goes to the most relevant casters, stays with its holder unless clearly outranked, and
//     changes hands only after the holder's shadow has faded out (the shadow strength fades like the light).
// Each renderer keeps one state per view (the main view, the game window, every secondary render target), so a
// camera preview never disturbs the main view's choice.
// ============================================================================
#include "../ViewFrustum.h"
#include <unordered_map>
#include <vector>

namespace vortex::graphics::lighting
{
	constexpr float KEEP_BONUS = 1.5f;          // score factor of a chosen light / a light holding a shadow map
	constexpr float FADE_SECONDS = 0.5f;        // a budget change fades a light (and a shadow) in / out over this time
	constexpr float MAX_FADE_STEP = 0.1f;       // per update: a long frame never moves a light by more than 10 %
	constexpr float SETTLE_SECONDS = 0.25f;     // a view that last updated longer ago (a preview rendered now and then) finishes every fade at once
	constexpr float SNAP_DISTANCE = 25.0f;      // metres the camera jumps between two updates (a cut, a teleport) to start over
	constexpr float SNAP_TURN_COS = 0.7071f;    // cos 45°: a camera that turns further between two updates starts over too
	constexpr float VISIBILITY_FALLOFF = 1.0f;  // metres outside the frustum per e-fold of a light's score

	// The camera a view scores its lights for.
	struct Viewer
	{
		DirectX::XMFLOAT3 eye;
		ViewFrustum frustum;
	};
	inline Viewer make_viewer(const DirectX::XMFLOAT3& eye, const DirectX::XMFLOAT4X4& view_projection)
	{
		return Viewer{ eye, view_frustum(view_projection) };
	}
	// The direction the view looks in: the normal of its frustum's near plane.
	inline DirectX::XMFLOAT3 view_direction(const Viewer& viewer)
	{
		return { viewer.frustum.p[4][0], viewer.frustum.p[4][1], viewer.frustum.p[4][2] };
	}
	// A cut between two updates of a view — the camera jumped more than SNAP_DISTANCE or turned more than 45° (a teleport,
	// a camera switch, a whip-around) — after which the selection starts over: the renderers pass it as `snap`.
	inline bool camera_cut(const DirectX::XMFLOAT3& prev_eye, const DirectX::XMFLOAT3& prev_dir, const DirectX::XMFLOAT3& eye, const DirectX::XMFLOAT3& dir)
	{
		const float jx = eye.x - prev_eye.x, jy = eye.y - prev_eye.y, jz = eye.z - prev_eye.z;
		return jx * jx + jy * jy + jz * jz > SNAP_DISTANCE * SNAP_DISTANCE
			|| dir.x * prev_dir.x + dir.y * prev_dir.y + dir.z * prev_dir.z < SNAP_TURN_COS;
	}

	// The relevance of a light for a view (#335): intensity × (how deep the camera sits inside the light's reach + how
	// close that reach is: 2 at the light, 1 at the edge of its range, range / distance beyond), times its visibility:
	// 1 while the range sphere (a spot: its cone) touches the view frustum, e^(-d / VISIBILITY_FALLOFF) when it lies d
	// metres outside. Continuous everywhere: a camera that moves a few centimetres changes a score by a few percent at
	// most, far below the KEEP_BONUS margin. in_view (optional): the reach touches the frustum — otherwise the light
	// cannot light anything on screen.
	float point_light_score(const DirectX::XMFLOAT3& position, float range, float intensity, const Viewer& viewer, bool* in_view = nullptr);
	float spot_light_score(const DirectX::XMFLOAT3& position, const DirectX::XMFLOAT3& direction, float range, float spot_angle_degrees,
		float intensity, const Viewer& viewer, bool* in_view = nullptr);

	// The id of a light submitted without one (#416): its type and look (colour, range, cone), not its position or
	// intensity, so a light that moves or flickers keeps it. Lights that look alike are told apart by their order in
	// the submission (LightSelector::update).
	u64 fallback_light_id(u32 kind, const DirectX::XMFLOAT3& color, float range, float spot_angle_degrees);

	struct Candidate
	{
		u64 id;             // stable identity (the entity; fallback_light_id), 0 is treated as 1
		float score;        // relevance for this view
		bool wants_shadow;  // casts shadows
		bool in_view;       // its reach touches the view frustum (point_light_score / spot_light_score)
	};

	struct Selected
	{
		u32 index;          // the light's position in the candidates of the last update
		float fade;         // 0..1, multiplies its intensity
		bool shadow;        // holds one of the shadow maps
		float shadow_fade;  // 0..1, multiplies its shadow strength (1 within the budget)
	};

	class LightSelector
	{
	public:
		LightSelector() = default;
		// the slot table points into the state map: a copy would point into the original's
		LightSelector(const LightSelector&) = delete;
		LightSelector& operator=(const LightSelector&) = delete;
		LightSelector(LightSelector&&) = default;              // a moved map keeps its nodes, the pointers stay valid
		LightSelector& operator=(LightSelector&&) = default;

		// One update per rendered frame of the view. candidates = ALL lights of one type submitted for this frame (any
		// count, in submission order); slots = the shader's slots for the type; shadow_maps = how many of the lights
		// holding a slot may cast. dt = seconds since the view's last update (SETTLE_SECONDS or more finishes every fade);
		// snap = start over (the view's first frame, a camera cut): the chosen lights show at full intensity at once.
		void update(const Candidate* candidates, u32 count, u32 slots, u32 shadow_maps, float dt, bool snap);

		// The lights holding a slot, in submission order (within the budget: every light, in the order it came).
		const std::vector<Selected>& selected() const { return m_selected; }
		u32 entered() const { return m_entered; }   // lights that got a slot in the last update
		u32 left() const { return m_left; }         // lights that gave one up (faded out, left the view, removed, displaced)
		// Nothing is fading and no chosen light waits for a slot or a shadow map: a view that renders only on demand can stop.
		bool settled() const { return m_settled; }
		void reset();

	private:
		struct State
		{
			float fade{ 0.0f };
			float shadow_fade{ 0.0f };
			float score{ 0.0f };
			int slot{ -1 };              // shader slot held, -1 = none
			u32 index{ 0 };              // position in this update's candidates
			u64 seen{ 0 };               // the update that last submitted the light (0 = never)
			bool chosen{ false };        // among the `slots` best by kept score in the last update
			bool shadow{ false };        // holds a shadow map
			bool shadow_wanted{ false }; // over budget: among the casters that should hold one
			bool wants_shadow{ false };
			bool in_view{ false };       // its reach touches the view frustum in this update
			bool was_in_view{ false };   // ... in the previous one
			bool fresh{ false };         // first submitted in this update
		};
		int free_slot() const;
		int dimmest_leaving_slot() const;
		void release(size_t slot);

		std::unordered_map<u64, State> m_states;   // node-based: the State pointers below stay valid until erased
		std::vector<State*> m_slots;               // per slot: the light holding it, nullptr = free
		std::vector<State*> m_by_index;            // candidate index -> its state (this update)
		std::vector<u32> m_order;                  // scratch: candidate indices by kept score
		std::vector<State*> m_casters;             // scratch: the casters ranked for the shadow maps
		std::unordered_map<u64, u32> m_dupes;      // scratch: occurrences of an id in this update
		std::vector<Selected> m_selected;
		u64 m_update{ 0 };
		u32 m_entered{ 0 }, m_left{ 0 };
		bool m_settled{ true };
	};
}
