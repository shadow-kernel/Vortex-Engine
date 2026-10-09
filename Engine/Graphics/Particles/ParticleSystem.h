#pragma once

// ============================================================================
// Vortex particle module (VFX epic #116: #117 core, #121 trails & beams, #122 soft particles + collision).
//
// Backend-neutral CPU simulation (SoA, multithreaded, deterministic per seed) that produces world-space
// billboard instances + ribbon strips; the render backend only uploads and draws what gather() hands it
// (SDL GPU / Metal: Graphics/SdlGpu/SdlGpuParticles.cpp; DX12: documented no-op, see README.md).
//
//   worlds   independent sets of emitters/beams (0 = the scene, >0 = editor previews); the scene renderer
//            draws world 0, a secondary render target draws the world named by set_next_target_world()
//   emitters one particle system each (spawn rate + bursts, shapes, curves, noise, flipbook, collision,
//            trails); several emitters make one effect (.vfx)
//   beams    camera-facing ribbons between two points (tracers, lasers), optional travelling streak
//
// Conventions: metres / seconds, engine left-handed Y-up, row-major 4x4 world matrices (row 3 = translation,
// the layout SubmitRenderItem uses), Euler degrees applied Z·X·Y (like Transform). Shapes emit along local +Z.
// Handles are u32, 0 = invalid, stale handles are rejected (generation check) and every call no-ops on them.
// Threading: all functions must be called from one thread (the engine/UI thread); the simulation itself
// fans out to an internal worker pool and joins before returning.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include <string>
#include <vector>

namespace vortex::particles
{
	constexpr u32 MAX_CURVE_KEYS = 8;
	constexpr u32 MAX_GRADIENT_KEYS = 8;
	constexpr u32 MAX_BURSTS = 8;
	constexpr u32 LUT_SIZE = 64;
	constexpr u32 MAX_PARTICLES_PER_EMITTER = 1u << 20;
	constexpr u32 MAX_TRAIL_POINTS = 64;
	constexpr u64 NO_TEXTURE = ~0ull;

	enum : u32 { SPACE_WORLD = 0, SPACE_LOCAL = 1 };
	enum : u32 { SHAPE_POINT = 0, SHAPE_SPHERE, SHAPE_HEMISPHERE, SHAPE_CONE, SHAPE_BOX, SHAPE_CIRCLE, SHAPE_EDGE };
	enum : u32 { EMIT_VOLUME = 0, EMIT_SHELL = 1, EMIT_EDGE = 2 };   // box only (sphere/cone/circle use radius_thickness)
	enum : u32 { RENDER_BILLBOARD = 0, RENDER_STRETCHED, RENDER_HORIZONTAL, RENDER_VERTICAL };
	enum : u32 { BLEND_ALPHA = 0, BLEND_ADDITIVE = 1, BLEND_PREMULTIPLIED = 2 };
	enum : u32 { SORT_AUTO = 0, SORT_NONE = 1, SORT_DEPTH = 2 };
	enum : u32 { FLIPBOOK_LIFETIME = 0, FLIPBOOK_FPS = 1, FLIPBOOK_RANDOM = 2 };
	enum : u32 { COLLIDE_BOUNCE = 0, COLLIDE_KILL = 1 };
	enum : u32 { TEXMODE_STRETCH = 0, TEXMODE_TILE = 1 };

	// Piecewise-linear curve over [0,1] (t sorted ascending). count 0 = constant 1.
	struct Curve { u32 count; float t[MAX_CURVE_KEYS]; float v[MAX_CURVE_KEYS]; };
	// Colour ramp over [0,1], linear RGBA 0..1 per key (multiplies the start colour). count 0 = white.
	struct Gradient { u32 count; float t[MAX_GRADIENT_KEYS]; float rgba[MAX_GRADIENT_KEYS][4]; };
	// Burst: count_min..count_max particles at `time` (s into the cycle), repeated `cycles` times every
	// `interval` seconds (cycles 0 = repeat for the whole cycle), each with `probability`.
	struct Burst { float time; u32 count_min; u32 count_max; u32 cycles; float interval; float probability; };

	// One emitter. Plain data (C-compatible): the C ABI hands it through unchanged (ParticleEmitterDesc).
	// Fill it with default_emitter_desc() and change what you need.
	struct EmitterDesc
	{
		// main
		u32 max_particles;          // capacity (1 .. MAX_PARTICLES_PER_EMITTER)
		float duration;             // seconds of one emission cycle
		u32 looping;
		u32 prewarm;                // looping only: start as if it had run one full cycle
		float start_delay;
		u32 simulation_space;       // SPACE_WORLD / SPACE_LOCAL (particles move with the emitter)
		float simulation_speed;
		u32 seed;                   // 0 = a fresh seed per emitter
		// emission
		float rate;                 // particles / second
		float rate_over_distance;   // particles / metre the emitter moved
		u32 burst_count;
		Burst bursts[MAX_BURSTS];
		// start values (min / max, uniformly random)
		float lifetime[2];
		float speed[2];
		float size[2];
		float rotation[2];          // degrees
		float rotation_speed[2];    // degrees / second
		float color[4];             // start colour (sRGB 0..1), random blend towards color2
		float color2[4];
		float gravity;              // multiplier of (0, -9.81, 0)
		float drag;                 // 1/s (velocity *= exp(-drag dt))
		float inherit_velocity;     // world space: fraction of the emitter's velocity added at spawn
		// shape (emits along local +Z, then shape_rotation / shape_offset, then the emitter transform)
		u32 shape;
		u32 emit_from;              // box: EMIT_VOLUME / EMIT_SHELL / EMIT_EDGE
		float radius;
		float radius_thickness;     // 0 = surface / rim only, 1 = whole volume / area
		float angle;                // cone half angle (degrees)
		float arc;                  // cone / circle arc (degrees)
		float length;               // edge length
		float box[3];               // box size (full extents)
		float shape_offset[3];
		float shape_rotation[3];    // Euler degrees
		float random_direction;     // 0..1 blend of the start direction towards a random one
		// over lifetime
		float velocity[3];          // constant extra velocity (m/s), scaled by velocity_curve
		u32 velocity_space;         // SPACE_WORLD / SPACE_LOCAL (emitter axes)
		Curve velocity_curve;
		Curve speed_curve;          // multiplies the particle's own velocity
		Curve size_curve;           // multiplies the start size
		Gradient color_gradient;    // multiplies the start colour
		// noise (turbulence: displacement velocity from a 3D value-noise field)
		float noise_strength;       // m/s
		float noise_frequency;      // cycles / metre
		float noise_scroll;         // field scroll (units / second)
		u32 noise_octaves;          // 1..4
		// texture sheet (flipbook)
		u32 tiles_x, tiles_y;
		u32 flipbook_mode;          // FLIPBOOK_LIFETIME / FLIPBOOK_FPS / FLIPBOOK_RANDOM
		float flipbook_cycles;      // lifetime mode: loops over the lifetime
		float flipbook_fps;         // fps mode
		float start_frame[2];
		u32 frame_blend;            // cross-fade between frames
		// rendering
		u32 render_mode;            // RENDER_*
		u32 blend;                  // BLEND_*
		u32 lit;                    // scene lights shade the particle (ambient + sun + point/spot)
		float soft_distance;        // soft particles: fade over this depth distance to the scene (0 = hard)
		float length_scale;         // stretched: length = size * length_scale + speed * velocity_scale
		float velocity_scale;
		u32 sort_mode;              // SORT_* (auto = back-to-front for alpha / premultiplied)
		float emissive;             // colour multiplier (HDR-ish boost for additive glows)
		float aspect;               // billboard width / height
		u32 layer;                  // 0 world, 1 first-person viewmodel layer
		u64 texture;                // engine texture id (NO_TEXTURE = procedural soft disc)
		// depth-buffer collision (world-0 scene emitters, against the last frames' scene depth)
		u32 collision;
		u32 collision_mode;         // COLLIDE_BOUNCE / COLLIDE_KILL
		float bounce;               // restitution 0..1
		float dampen;               // tangential velocity loss per hit 0..1
		float lifetime_loss;        // fraction of the lifetime lost per hit
		float collision_radius;
		float collision_thickness;  // how far behind a surface still counts as a hit (m)
		// trails (per-particle ribbons)
		u32 trails;
		float trail_lifetime;       // seconds a trail point lives
		float trail_min_distance;   // new point after the particle moved this far
		u32 trail_max_points;       // 2..MAX_TRAIL_POINTS
		float trail_width;          // multiplier of the particle size
		Curve trail_width_curve;    // head (0) -> tail (1)
		Gradient trail_gradient;    // head (0) -> tail (1)
		u32 trail_inherit_color;
		u32 trail_texture_mode;     // TEXMODE_STRETCH / TEXMODE_TILE
		u64 trail_texture;
	};

	// A beam between two points (bullet tracer, laser, lightning). Plain data like EmitterDesc.
	struct BeamDesc
	{
		float width;
		Curve width_curve;          // start (0) -> end (1)
		Gradient gradient;          // start (0) -> end (1)
		float color[4];
		u32 blend;
		float emissive;
		float uv_tiling;            // 0 = texture stretched once, > 0 = repeats per metre
		float uv_scroll;            // U per second
		u32 segments;               // 1..256 (more = smoother noise)
		float noise;                // jitter amplitude (m)
		float noise_frequency;
		float noise_speed;
		float duration;             // 0 = lives until destroyed
		float fade_in, fade_out;    // seconds
		float speed;                // > 0: a streak of `length` metres travels start -> end at this speed (tracer)
		float length;
		float soft_distance;
		u32 layer;
		u64 texture;
	};

	void default_emitter_desc(EmitterDesc& d);
	void default_beam_desc(BeamDesc& d);
	Curve curve_constant(float v);
	Curve curve_linear(float v0, float v1);
	Gradient gradient_fade(float r, float g, float b, float a0, float a1);
	float eval_curve(const Curve& c, float t);
	void eval_gradient(const Gradient& g, float t, float out[4]);

	// ---- JSON (the .vfx schema, see README.md) ------------------------------------------------------------
	// Keys are case-insensitive; enums accept names ("additive") or numbers; ranges [min, max] or a number.
	struct EmitterJson { EmitterDesc desc; std::string name; std::string texture; std::string trail_texture; bool enabled{ true }; };
	struct BeamJson { BeamDesc desc; std::string texture; };
	bool parse_emitter_json(const char* json, EmitterJson& out, std::string* error = nullptr);
	bool parse_beam_json(const char* json, BeamJson& out, std::string* error = nullptr);
	// A whole effect: {"emitters":[...], "beam":{...}} (a bare emitter object is accepted as a 1-emitter effect).
	bool parse_effect_json(const char* json, std::vector<EmitterJson>& emitters, BeamJson* beam, bool* has_beam, std::string* error = nullptr);

	// ---- lifecycle ------------------------------------------------------------------------------------------
	bool init();                    // idempotent; starts the worker pool
	void shutdown();                // destroys everything, joins the workers
	bool initialized();
	void set_worker_threads(u32 count);   // 0 = simulate on the calling thread (tests); default cores-1 (max 7)
	u32 worker_threads();
	u32 create_world();             // a fresh world id (> 0) for previews
	void clear(u32 world);          // destroy every emitter + beam of a world (ALL_WORLDS = everything)
	constexpr u32 ALL_WORLDS = 0xFFFFFFFFu;

	// Step one world by dt seconds (spawn, simulate, collide, kill, trails). dt is clamped to [0, 0.1].
	void update(u32 world, float dt);

	// Frame driver: the renderer calls begin_frame() at the start of every main-surface frame. A registered
	// callback (the managed ParticleService) is invoked with the real frame time and drives the updates
	// itself; without one, world 0 is updated automatically with that time (C++ games, render tests).
	using FrameCallback = void (*)(float dt);
	void set_frame_callback(FrameCallback cb);
	void begin_frame();
	void set_auto_update(bool enabled);

	// ---- emitters -------------------------------------------------------------------------------------------
	u32 create_emitter(const EmitterDesc& desc, u32 world);
	u32 create_emitter_json(const char* json, u32 world, std::string* error = nullptr);
	bool set_emitter_desc(u32 h, const EmitterDesc& desc);          // live edit: particles are kept
	bool set_emitter_json(u32 h, const char* json, std::string* error = nullptr);
	bool get_emitter_desc(u32 h, EmitterDesc& out);
	void destroy_emitter(u32 h);
	bool emitter_valid(u32 h);
	void set_transform(u32 h, const float* world4x4);               // row-major; teleport = reset_motion()
	void reset_motion(u32 h);                                        // no interpolation / inherited velocity from the last pose
	void play(u32 h);                                                // start (or resume); restarts a finished emitter
	void stop(u32 h, bool clear_particles);                          // stop emitting (particles live on unless cleared)
	void pause(u32 h, bool paused);
	void restart(u32 h);                                             // clear + play from t = 0
	void burst(u32 h, u32 count);                                    // spawn now (works while stopped)
	void clear_particles(u32 h);
	void set_seed(u32 h, u32 seed);                                  // takes effect on the next restart
	void set_layer(u32 h, u32 layer);
	void set_visible(u32 h, bool visible);
	void set_simulation_speed(u32 h, float speed);
	void set_texture(u32 h, u64 texture_id);
	void set_trail_texture(u32 h, u64 texture_id);
	void set_auto_destroy(u32 h, bool enabled);                      // destroyed once finished (one-shot effects)
	u32 alive_count(u32 h);
	bool is_playing(u32 h);                                          // emitting (or waiting for its start delay)
	bool is_finished(u32 h);                                         // not emitting and no particle alive
	float emitter_time(u32 h);
	u32 get_particle_positions(u32 h, float* out_xyz, u32 max_count);   // world space (tests / debugging)
	u32 emitter_count(u32 world);

	// ---- beams ----------------------------------------------------------------------------------------------
	u32 create_beam(const BeamDesc& desc, u32 world);
	u32 create_beam_json(const char* json, u32 world, std::string* error = nullptr);
	bool set_beam_desc(u32 h, const BeamDesc& desc);
	void set_beam_points(u32 h, const float* from3, const float* to3);
	void set_beam_texture(u32 h, u64 texture_id);
	void destroy_beam(u32 h);
	bool beam_valid(u32 h);
	u32 beam_count(u32 world);

	// ---- textures by path (native convenience; the managed side passes texture ids) -------------------------
	u64 texture_from_path(const std::string& path);   // cached per path; NO_TEXTURE when it cannot be loaded
	void on_renderer_shutdown();                        // texture ids die with the renderer: forget them

	// ---- rendering (called by the backend) ------------------------------------------------------------------
	// 48-byte billboard instance; byte-matched to GpuParticle in Shaders/msl/particles.metal.
	struct GpuParticle
	{
		float pos[3]; float size;        // world centre, height in metres (width = size * aspect)
		float axis[3]; float rot;        // stretched: world streak vector (length included); rot: radians
		u32 color; float frame; float aspect; u32 flags;   // color RGBA8 (R in the low byte)
	};
	static_assert(sizeof(GpuParticle) == 48, "GpuParticle must stay 48 bytes (particles.metal)");

	// 24-byte ribbon vertex (trails + beams), camera-facing already.
	struct RibbonVertex { float pos[3]; float u, v; u32 color; };
	static_assert(sizeof(RibbonVertex) == 24, "RibbonVertex must stay 24 bytes (particles.metal)");

	struct DrawBatch
	{
		u32 kind;           // 0 = billboard instances [first, first+count), 1 = ribbon indices [first, first+count)
		u32 layer;
		u32 blend;
		u32 render_mode;
		u64 texture;
		u32 first, count;
		u32 tiles_x, tiles_y, frame_blend, lit;
		float soft_distance, emissive;
		float sort_depth;   // view depth of the batch centre (batches come back-to-front)
		u32 texture_mode;
	};

	struct ViewInfo
	{
		float view_proj[16];               // row-major (row vectors), for frustum culling
		float eye[3];
		float right[3], up[3], forward[3];
		bool ortho;
	};

	struct DrawList
	{
		std::vector<DrawBatch> batches;
		std::vector<GpuParticle> instances;
		std::vector<RibbonVertex> ribbon_vertices;
		std::vector<u32> ribbon_indices;
		void clear() { batches.clear(); instances.clear(); ribbon_vertices.clear(); ribbon_indices.clear(); }
	};

	// Collect everything of `world` that is visible from `view` (culled, alpha emitters sorted back-to-front,
	// trails + beams built camera-facing). Returns false when there is nothing to draw.
	bool gather(u32 world, const ViewInfo& view, DrawList& out);

	// The main view's scene depth for collision: linear view depth (metres) per texel, row 0 = top.
	struct DepthView
	{
		float eye[3], right[3], up[3], forward[3];
		float tan_half_x, tan_half_y;      // perspective: tan(fov/2) * aspect, tan(fov/2); ortho: half width / height
		float near_clip, far_clip;
		bool ortho;
	};
	bool wants_depth_snapshot();      // any world-0 emitter with collision that is alive
	void submit_depth_snapshot(const float* linear_depth, u32 width, u32 height, const DepthView& view);

	// ---- stats ----------------------------------------------------------------------------------------------
	struct Stats
	{
		u32 emitters, beams, alive_particles, capacity;
		u32 drawn_particles, draw_batches, ribbon_vertices, worker_threads;
		float simulate_ms, gather_ms;
		u64 spawned_total;
		u32 depth_snapshots;
		u32 renderer_draws;   // 1 when the active render backend draws particles (SDL GPU and, since #117, DX12)
	};
	void get_stats(u32 world, Stats& out);   // ALL_WORLDS for everything
	void note_gather_time(float ms, u32 drawn, u32 batches, u32 ribbon_vertices);
}
