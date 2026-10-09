// Vortex particle module — CPU simulation, emission, collision, trails, beams and render gathering.
// See ParticleSystem.h for the contract and README.md for the design notes.
//
// Frame of one world (update()):
//   1. pre-update (serial, cheap): emission clock, rate / distance / burst spawn counts, emitter velocity
//   2. particle update (parallel, chunks of CHUNK particles over every emitter): age, forces, noise,
//      depth collision, trail points, then the world-space render instance of each particle
//   3. per emitter (parallel across emitters): compact the dead (swap-remove, serial per emitter => the
//      result does not depend on the thread count), spawn this frame's particles (per-emitter PCG stream),
//      bounds for culling
// Everything a particle does is a pure function of its state + the emitter + dt, and spawning draws from the
// emitter's own random stream, so the same seed and dt sequence give bit-identical results on any core count.
#include "ParticleSystem.h"
#include "../Backend.h"
#include "../../Common/Platform.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <functional>
#include <mutex>
#include <thread>
#include <unordered_map>

// restrict-qualified members of the hot-loop views (clang/gcc); MSVC keeps plain pointers there
#if defined(_MSC_VER) && !defined(__clang__)
	#define VX_RESTRICT
#else
	#define VX_RESTRICT __restrict
#endif

namespace vortex::particles
{
	// =====================================================================================================
	// Defaults, curves, gradients
	// =====================================================================================================
	Curve curve_constant(float v) { Curve c{}; c.count = 1; c.t[0] = 0.0f; c.v[0] = v; return c; }
	Curve curve_linear(float v0, float v1) { Curve c{}; c.count = 2; c.t[0] = 0.0f; c.v[0] = v0; c.t[1] = 1.0f; c.v[1] = v1; return c; }
	Gradient gradient_fade(float r, float g, float b, float a0, float a1)
	{
		Gradient gr{};
		gr.count = 2;
		gr.t[0] = 0.0f; gr.rgba[0][0] = r; gr.rgba[0][1] = g; gr.rgba[0][2] = b; gr.rgba[0][3] = a0;
		gr.t[1] = 1.0f; gr.rgba[1][0] = r; gr.rgba[1][1] = g; gr.rgba[1][2] = b; gr.rgba[1][3] = a1;
		return gr;
	}

	float eval_curve(const Curve& c, float t)
	{
		if (c.count == 0) return 1.0f;
		if (c.count == 1 || t <= c.t[0]) return c.v[0];
		const u32 n = std::min(c.count, MAX_CURVE_KEYS);
		for (u32 i = 1; i < n; ++i)
		{
			if (t <= c.t[i])
			{
				float span = c.t[i] - c.t[i - 1];
				float k = span > 1e-6f ? (t - c.t[i - 1]) / span : 1.0f;
				return c.v[i - 1] + (c.v[i] - c.v[i - 1]) * k;
			}
		}
		return c.v[n - 1];
	}

	void eval_gradient(const Gradient& g, float t, float out[4])
	{
		if (g.count == 0) { out[0] = out[1] = out[2] = out[3] = 1.0f; return; }
		const u32 n = std::min(g.count, MAX_GRADIENT_KEYS);
		if (n == 1 || t <= g.t[0]) { std::memcpy(out, g.rgba[0], 16); return; }
		for (u32 i = 1; i < n; ++i)
		{
			if (t <= g.t[i])
			{
				float span = g.t[i] - g.t[i - 1];
				float k = span > 1e-6f ? (t - g.t[i - 1]) / span : 1.0f;
				for (int q = 0; q < 4; ++q) out[q] = g.rgba[i - 1][q] + (g.rgba[i][q] - g.rgba[i - 1][q]) * k;
				return;
			}
		}
		std::memcpy(out, g.rgba[n - 1], 16);
	}

	void default_emitter_desc(EmitterDesc& d)
	{
		std::memset(&d, 0, sizeof(d));
		d.max_particles = 1000;
		d.duration = 5.0f;
		d.looping = 1;
		d.simulation_speed = 1.0f;
		d.rate = 10.0f;
		d.lifetime[0] = d.lifetime[1] = 2.0f;
		d.speed[0] = d.speed[1] = 1.0f;
		d.size[0] = d.size[1] = 0.25f;
		for (int i = 0; i < 4; ++i) d.color[i] = d.color2[i] = 1.0f;
		d.shape = SHAPE_CONE;
		d.radius = 0.1f;
		d.radius_thickness = 1.0f;
		d.angle = 25.0f;
		d.arc = 360.0f;
		d.length = 1.0f;
		d.box[0] = d.box[1] = d.box[2] = 1.0f;
		d.noise_frequency = 1.0f;
		d.noise_octaves = 1;
		d.tiles_x = d.tiles_y = 1;
		d.flipbook_cycles = 1.0f;
		d.flipbook_fps = 15.0f;
		d.render_mode = RENDER_BILLBOARD;
		d.blend = BLEND_ALPHA;
		d.soft_distance = 0.25f;
		d.length_scale = 2.0f;
		d.emissive = 1.0f;
		d.aspect = 1.0f;
		d.texture = NO_TEXTURE;
		d.bounce = 0.4f;
		d.dampen = 0.2f;
		d.collision_radius = 0.02f;
		d.collision_thickness = 0.5f;
		d.trail_lifetime = 0.3f;
		d.trail_min_distance = 0.05f;
		d.trail_max_points = 16;
		d.trail_width = 1.0f;
		d.trail_inherit_color = 1;
		d.trail_texture = NO_TEXTURE;
	}

	void default_beam_desc(BeamDesc& d)
	{
		std::memset(&d, 0, sizeof(d));
		d.width = 0.05f;
		for (int i = 0; i < 4; ++i) d.color[i] = 1.0f;
		d.blend = BLEND_ADDITIVE;
		d.emissive = 1.0f;
		d.segments = 1;
		d.noise_frequency = 1.0f;
		d.noise_speed = 5.0f;
		d.fade_out = 0.05f;
		d.length = 2.0f;
		d.texture = NO_TEXTURE;
	}

	namespace
	{
		// =================================================================================================
		// Small math (row-major 4x4, row vectors: p' = p * M, translation in m[12..14])
		// =================================================================================================
		constexpr float PI = 3.14159265358979f;
		constexpr float DEG = PI / 180.0f;
		constexpr u32 CHUNK = 4096;

		inline float clampf(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
		inline float sat(float v) { return clampf(v, 0.0f, 1.0f); }
		inline float lerpf(float a, float b, float t) { return a + (b - a) * t; }
		inline bool finite3(const float* v) { return std::isfinite(v[0]) && std::isfinite(v[1]) && std::isfinite(v[2]); }

		inline void xform_point(const float* m, float x, float y, float z, float* o)
		{
			o[0] = x * m[0] + y * m[4] + z * m[8] + m[12];
			o[1] = x * m[1] + y * m[5] + z * m[9] + m[13];
			o[2] = x * m[2] + y * m[6] + z * m[10] + m[14];
		}
		inline void xform_dir(const float* m, float x, float y, float z, float* o)
		{
			o[0] = x * m[0] + y * m[4] + z * m[8];
			o[1] = x * m[1] + y * m[5] + z * m[9];
			o[2] = x * m[2] + y * m[6] + z * m[10];
		}
		inline float len3(float x, float y, float z) { return std::sqrt(x * x + y * y + z * z); }
		inline void normalize3(float* v)
		{
			float l = len3(v[0], v[1], v[2]);
			if (l > 1e-12f) { v[0] /= l; v[1] /= l; v[2] /= l; }
		}
		inline void cross3(const float* a, const float* b, float* o)
		{
			o[0] = a[1] * b[2] - a[2] * b[1];
			o[1] = a[2] * b[0] - a[0] * b[2];
			o[2] = a[0] * b[1] - a[1] * b[0];
		}
		inline float dot3(const float* a, const float* b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

		// Engine Euler (degrees, applied Z·X·Y — Transform / SceneRenderService convention) -> 3x3 rows.
		void euler_rows(const float* deg, float* r /*9*/)
		{
			float cx = std::cos(deg[0] * DEG), sx = std::sin(deg[0] * DEG);
			float cy = std::cos(deg[1] * DEG), sy = std::sin(deg[1] * DEG);
			float cz = std::cos(deg[2] * DEG), sz = std::sin(deg[2] * DEG);
			r[0] = cz * cy + sz * sx * sy;  r[1] = sz * cx; r[2] = -cz * sy + sz * sx * cy;
			r[3] = -sz * cy + cz * sx * sy; r[4] = cz * cx; r[5] = sz * sy + cz * sx * cy;
			r[6] = cx * sy;                 r[7] = -sx;     r[8] = cx * cy;
		}

		// =================================================================================================
		// Random: PCG32 (one stream per emitter)
		// =================================================================================================
		struct Pcg
		{
			u64 state{ 0x853c49e6748fea9bull }, inc{ 0xda3e39cb94b95bdbull };
			void seed(u64 s, u64 seq = 54u)
			{
				state = 0u; inc = (seq << 1u) | 1u;
				next(); state += s; next();
			}
			u32 next()
			{
				u64 old = state;
				state = old * 6364136223846793005ull + inc;
				u32 xorshifted = (u32)(((old >> 18u) ^ old) >> 27u);
				u32 rot = (u32)(old >> 59u);
				return (xorshifted >> rot) | (xorshifted << ((32u - rot) & 31u));
			}
			float unit() { return (float)(next() >> 8) * (1.0f / 16777216.0f); }        // [0, 1)
			float range(const float* r) { return r[0] + (r[1] - r[0]) * unit(); }
			float sym() { return unit() * 2.0f - 1.0f; }
			void unit_vector(float* v)
			{
				float z = sym();
				float a = unit() * 2.0f * PI;
				float s = std::sqrt(std::max(0.0f, 1.0f - z * z));
				v[0] = s * std::cos(a); v[1] = s * std::sin(a); v[2] = z;
			}
		};

		// =================================================================================================
		// Value noise (3D, smooth, deterministic) for turbulence
		// =================================================================================================
		inline u32 hash3(int x, int y, int z, u32 seed)
		{
			u32 h = (u32)x * 0x8da6b343u ^ (u32)y * 0xd8163841u ^ (u32)z * 0xcb1ab31fu ^ seed * 0x9e3779b9u;
			h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
			return h;
		}
		inline float hval(u32 h) { return (float)(h & 0xFFFFFFu) * (2.0f / 16777215.0f) - 1.0f; }
		inline float smooth(float f) { return f * f * (3.0f - 2.0f * f); }

		float value_noise(float x, float y, float z, u32 seed)
		{
			float fx = std::floor(x), fy = std::floor(y), fz = std::floor(z);
			int ix = (int)fx, iy = (int)fy, iz = (int)fz;
			float ux = smooth(x - fx), uy = smooth(y - fy), uz = smooth(z - fz);
			float c000 = hval(hash3(ix, iy, iz, seed)), c100 = hval(hash3(ix + 1, iy, iz, seed));
			float c010 = hval(hash3(ix, iy + 1, iz, seed)), c110 = hval(hash3(ix + 1, iy + 1, iz, seed));
			float c001 = hval(hash3(ix, iy, iz + 1, seed)), c101 = hval(hash3(ix + 1, iy, iz + 1, seed));
			float c011 = hval(hash3(ix, iy + 1, iz + 1, seed)), c111 = hval(hash3(ix + 1, iy + 1, iz + 1, seed));
			float x00 = lerpf(c000, c100, ux), x10 = lerpf(c010, c110, ux);
			float x01 = lerpf(c001, c101, ux), x11 = lerpf(c011, c111, ux);
			return lerpf(lerpf(x00, x10, uy), lerpf(x01, x11, uy), uz);
		}

		// Vector value noise: ONE lattice walk, each corner hash supplies three 10-bit components (x, y, z).
		inline void value_noise3(float x, float y, float z, u32 seed, float* out)
		{
			float fx = std::floor(x), fy = std::floor(y), fz = std::floor(z);
			int ix = (int)fx, iy = (int)fy, iz = (int)fz;
			float ux = smooth(x - fx), uy = smooth(y - fy), uz = smooth(z - fz);
			constexpr float k = 2.0f / 1023.0f;
			float c[8][3];
			for (int n = 0; n < 8; ++n)
			{
				u32 h = hash3(ix + (n & 1), iy + ((n >> 1) & 1), iz + ((n >> 2) & 1), seed);
				c[n][0] = (float)(h & 1023u) * k - 1.0f;
				c[n][1] = (float)((h >> 10) & 1023u) * k - 1.0f;
				c[n][2] = (float)((h >> 20) & 1023u) * k - 1.0f;
			}
			for (int q = 0; q < 3; ++q)
			{
				float x00 = lerpf(c[0][q], c[1][q], ux), x10 = lerpf(c[2][q], c[3][q], ux);
				float x01 = lerpf(c[4][q], c[5][q], ux), x11 = lerpf(c[6][q], c[7][q], ux);
				out[q] = lerpf(lerpf(x00, x10, uy), lerpf(x01, x11, uy), uz);
			}
		}

		inline void noise_vec(float x, float y, float z, u32 octaves, float* out)
		{
			value_noise3(x, y, z, 11u, out);
			if (octaves <= 1) return;
			float amp = 0.5f, norm = 1.0f;
			for (u32 o = 1; o < octaves; ++o)
			{
				x *= 2.03f; y *= 2.03f; z *= 2.03f;
				float n[3];
				value_noise3(x, y, z, 11u + o * 7u, n);
				out[0] += n[0] * amp; out[1] += n[1] * amp; out[2] += n[2] * amp;
				norm += amp;
				amp *= 0.5f;
			}
			float inv = 1.0f / norm;
			out[0] *= inv; out[1] *= inv; out[2] *= inv;
		}

		inline u32 pack_rgba(float r, float g, float b, float a)
		{
			auto q = [](float v) -> u32 { v = v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v); return (u32)(v * 255.0f + 0.5f); };
			return q(r) | (q(g) << 8) | (q(b) << 16) | (q(a) << 24);
		}
		inline void unpack_rgba(u32 c, float* o)
		{
			constexpr float k = 1.0f / 255.0f;
			o[0] = (float)(c & 255) * k; o[1] = (float)((c >> 8) & 255) * k; o[2] = (float)((c >> 16) & 255) * k; o[3] = (float)(c >> 24) * k;
		}

		// =================================================================================================
		// Worker pool: parallel_for(count, fn) — the calling thread participates; blocks until done.
		// =================================================================================================
		class JobPool
		{
		public:
			~JobPool() { stop(); }
			void start(u32 workers)
			{
				stop();
				m_quit = false;
				for (u32 i = 0; i < workers; ++i) m_threads.emplace_back([this] { worker(); });
			}
			void stop()
			{
				{
					std::lock_guard<std::mutex> lock(m_mutex);
					m_quit = true;
				}
				m_cv.notify_all();
				for (auto& t : m_threads) if (t.joinable()) t.join();
				m_threads.clear();
			}
			u32 workers() const { return (u32)m_threads.size(); }

			void parallel_for(u32 count, const std::function<void(u32)>& fn)
			{
				if (count == 0) return;
				if (m_threads.empty() || count == 1)
				{
					for (u32 i = 0; i < count; ++i) fn(i);
					return;
				}
				{
					std::lock_guard<std::mutex> lock(m_mutex);
					m_job = &fn;
					m_total = count;
					m_next.store(0);
					m_done.store(0);
					++m_generation;
				}
				m_cv.notify_all();
				run_items();
				std::unique_lock<std::mutex> lock(m_mutex);
				m_done_cv.wait(lock, [this] { return m_done.load() >= m_total && m_busy == 0; });
				m_job = nullptr;
			}

		private:
			void run_items()
			{
				const std::function<void(u32)>* job = m_job;
				for (;;)
				{
					u32 i = m_next.fetch_add(1);
					if (i >= m_total) break;
					(*job)(i);
					if (m_done.fetch_add(1) + 1 == m_total)
					{
						std::lock_guard<std::mutex> lock(m_mutex);
						m_done_cv.notify_all();
					}
				}
			}
			void worker()
			{
				u64 seen = 0;
				for (;;)
				{
					{
						std::unique_lock<std::mutex> lock(m_mutex);
						m_cv.wait(lock, [&] { return m_quit || (m_generation != seen && m_job != nullptr); });
						if (m_quit) return;
						seen = m_generation;
						++m_busy;
					}
					run_items();
					{
						std::lock_guard<std::mutex> lock(m_mutex);
						--m_busy;
					}
					m_done_cv.notify_all();
				}
			}

			std::vector<std::thread> m_threads;
			std::mutex m_mutex;
			std::condition_variable m_cv, m_done_cv;
			const std::function<void(u32)>* m_job{ nullptr };
			std::atomic<u32> m_next{ 0 }, m_done{ 0 };
			u32 m_total{ 0 };
			u32 m_busy{ 0 };
			u64 m_generation{ 0 };
			bool m_quit{ false };
		};

		// =================================================================================================
		// State
		// =================================================================================================
		struct Emitter
		{
			u32 gen{ 0 };
			bool used{ false };
			u32 world{ 0 };
			EmitterDesc d{};
			// baked
			float size_lut[LUT_SIZE], speed_lut[LUT_SIZE], vel_lut[LUT_SIZE], twidth_lut[LUT_SIZE];
			float color_lut[LUT_SIZE][4], tcolor_lut[LUT_SIZE][4];
			bool has_size{ false }, has_speed{ false }, has_vel{ false }, has_color{ false };
			float shape_rot[9]{ 1, 0, 0, 0, 1, 0, 0, 0, 1 };
			// pose
			float m[16]{ 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
			float m_last[16]{ 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };   // pose at the previous update
			float scale_uniform{ 1.0f };
			float vel_w[3]{ 0, 0, 0 };
			bool posed{ false };             // set_transform() was called at least once
			// state
			bool playing{ false }, paused{ false }, visible{ true }, auto_destroy{ false };
			float time{ 0.0f };              // emission clock (sim seconds since play)
			u32 cycle{ 0 };
			float spawn_accum{ 0.0f }, dist_accum{ 0.0f };
			u32 burst_done[MAX_BURSTS]{};
			Pcg rng;
			u32 seed_used{ 0 };
			u64 spawned_total{ 0 };
			float sim_clock{ 0.0f };         // total simulated time (noise scroll)
			// per-frame plan
			float step_dt{ 0.0f };
			u32 plan_rate{ 0 }, plan_burst{ 0 };
			// particles (SoA, grown on demand up to max_particles)
			u32 count{ 0 }, allocated{ 0 };
			std::vector<float> px, py, pz, vx, vy, vz, age, life, size0, rot, rotv, frame0;
			std::vector<u32> col0, pseed;
			std::vector<u8> dead;
			std::vector<GpuParticle> inst;
			// trails: per particle a ring of tmax points (x, y, z, birth time)
			u32 tmax{ 0 };
			std::vector<float> trail;
			std::vector<u8> tcount, thead;
			std::vector<float> tlast;   // sim time of the last recorded point
			// bounds (world, of the instances)
			float bmin[3]{ 0, 0, 0 }, bmax[3]{ 0, 0, 0 };
		};

		struct Beam
		{
			u32 gen{ 0 };
			bool used{ false };
			u32 world{ 0 };
			BeamDesc d{};
			float from[3]{ 0, 0, 0 }, to[3]{ 0, 0, 1 };
			float age{ 0.0f };
		};

		struct DepthSnap
		{
			std::vector<float> z;
			u32 w{ 0 }, h{ 0 };
			DepthView v{};
			bool valid{ false };
		};

		std::recursive_mutex g_mutex;
		bool g_initialized = false;
		JobPool g_pool;
		u32 g_workers_wanted = 0xFFFFFFFFu;
		std::vector<Emitter> g_emitters;
		std::vector<Beam> g_beams;
		std::vector<u32> g_free_emitters, g_free_beams;
		u32 g_next_world = 1;
		u32 g_seed_counter = 0x1234567u;
		DepthSnap g_depth;
		u32 g_depth_snapshots = 0;
		FrameCallback g_callback = nullptr;
		bool g_auto_update = true;
		std::chrono::steady_clock::time_point g_last_frame{};
		bool g_have_last_frame = false;
		std::unordered_map<std::string, u64> g_texture_cache;
		// stats
		float g_sim_ms = 0.0f, g_gather_ms = 0.0f;
		u32 g_drawn = 0, g_batches = 0, g_ribbon_verts = 0;

		constexpr u32 BEAM_BIT = 0x80000000u;

		u32 make_handle(u32 slot, u32 gen, bool beam) { return (beam ? BEAM_BIT : 0u) | ((gen & 0x7FFFu) << 16) | ((slot + 1) & 0xFFFFu); }

		Emitter* get_emitter(u32 h)
		{
			if (h == 0 || (h & BEAM_BIT)) return nullptr;
			u32 slot = (h & 0xFFFFu);
			if (slot == 0 || slot > g_emitters.size()) return nullptr;
			Emitter& e = g_emitters[slot - 1];
			if (!e.used || (e.gen & 0x7FFFu) != ((h >> 16) & 0x7FFFu)) return nullptr;
			return &e;
		}

		Beam* get_beam(u32 h)
		{
			if (h == 0 || !(h & BEAM_BIT)) return nullptr;
			u32 slot = (h & 0xFFFFu);
			if (slot == 0 || slot > g_beams.size()) return nullptr;
			Beam& b = g_beams[slot - 1];
			if (!b.used || (b.gen & 0x7FFFu) != ((h >> 16) & 0x7FFFu)) return nullptr;
			return &b;
		}

		u32 default_workers()
		{
			const std::string env = platform::env_string("VORTEX_PARTICLE_THREADS");
			if (!env.empty()) return (u32)std::max(0, std::min(64, std::atoi(env.c_str())));
			u32 hw = std::thread::hardware_concurrency();
			if (hw <= 1) return 0;
			return std::min(7u, hw - 1);
		}

		void ensure_init()
		{
			if (g_initialized) return;
			g_initialized = true;
			u32 n = g_workers_wanted == 0xFFFFFFFFu ? default_workers() : g_workers_wanted;
			g_pool.start(n);
		}

		// ------------------------------------------------------------------------------------------ desc
		void sanitize(EmitterDesc& d)
		{
			auto fixr = [](float* r, float lo, float hi)
			{
				for (int i = 0; i < 2; ++i) { if (!std::isfinite(r[i])) r[i] = lo; r[i] = clampf(r[i], lo, hi); }
				if (r[1] < r[0]) std::swap(r[0], r[1]);
			};
			auto fixf = [](float& f, float lo, float hi, float def) { if (!std::isfinite(f)) f = def; f = clampf(f, lo, hi); };
			d.max_particles = std::max(1u, std::min(d.max_particles, MAX_PARTICLES_PER_EMITTER));
			fixf(d.duration, 0.01f, 3600.0f, 5.0f);
			fixf(d.start_delay, 0.0f, 3600.0f, 0.0f);
			fixf(d.simulation_speed, 0.0f, 100.0f, 1.0f);
			fixf(d.rate, 0.0f, 1e7f, 0.0f);
			fixf(d.rate_over_distance, 0.0f, 1e6f, 0.0f);
			d.burst_count = std::min(d.burst_count, MAX_BURSTS);
			for (u32 i = 0; i < d.burst_count; ++i)
			{
				Burst& b = d.bursts[i];
				fixf(b.time, 0.0f, 3600.0f, 0.0f);
				fixf(b.interval, 0.001f, 3600.0f, 0.1f);
				fixf(b.probability, 0.0f, 1.0f, 1.0f);
				b.count_min = std::min(b.count_min, MAX_PARTICLES_PER_EMITTER);
				b.count_max = std::max(b.count_min, std::min(b.count_max, MAX_PARTICLES_PER_EMITTER));
			}
			fixr(d.lifetime, 0.001f, 3600.0f);
			fixr(d.speed, -1e4f, 1e4f);
			fixr(d.size, 0.0f, 1e4f);
			fixr(d.rotation, -1e5f, 1e5f);
			fixr(d.rotation_speed, -1e5f, 1e5f);
			for (int i = 0; i < 4; ++i) { fixf(d.color[i], 0.0f, 1.0f, 1.0f); fixf(d.color2[i], 0.0f, 1.0f, 1.0f); }
			fixf(d.gravity, -1000.0f, 1000.0f, 0.0f);
			fixf(d.drag, 0.0f, 1000.0f, 0.0f);
			fixf(d.inherit_velocity, -10.0f, 10.0f, 0.0f);
			d.shape = std::min(d.shape, (u32)SHAPE_EDGE);
			d.emit_from = std::min(d.emit_from, (u32)EMIT_EDGE);
			fixf(d.radius, 0.0f, 1e4f, 0.1f);
			fixf(d.radius_thickness, 0.0f, 1.0f, 1.0f);
			fixf(d.angle, 0.0f, 90.0f, 25.0f);
			fixf(d.arc, 0.0f, 360.0f, 360.0f);
			fixf(d.length, 0.0f, 1e4f, 1.0f);
			for (int i = 0; i < 3; ++i)
			{
				fixf(d.box[i], 0.0f, 1e4f, 1.0f);
				fixf(d.shape_offset[i], -1e4f, 1e4f, 0.0f);
				fixf(d.shape_rotation[i], -1e5f, 1e5f, 0.0f);
				fixf(d.velocity[i], -1e4f, 1e4f, 0.0f);
			}
			fixf(d.random_direction, 0.0f, 1.0f, 0.0f);
			d.simulation_space = d.simulation_space ? SPACE_LOCAL : SPACE_WORLD;
			d.velocity_space = d.velocity_space ? SPACE_LOCAL : SPACE_WORLD;
			d.velocity_curve.count = std::min(d.velocity_curve.count, MAX_CURVE_KEYS);
			d.speed_curve.count = std::min(d.speed_curve.count, MAX_CURVE_KEYS);
			d.size_curve.count = std::min(d.size_curve.count, MAX_CURVE_KEYS);
			d.trail_width_curve.count = std::min(d.trail_width_curve.count, MAX_CURVE_KEYS);
			d.color_gradient.count = std::min(d.color_gradient.count, MAX_GRADIENT_KEYS);
			d.trail_gradient.count = std::min(d.trail_gradient.count, MAX_GRADIENT_KEYS);
			fixf(d.noise_strength, 0.0f, 1e4f, 0.0f);
			fixf(d.noise_frequency, 0.0001f, 1e3f, 1.0f);
			fixf(d.noise_scroll, -1e3f, 1e3f, 0.0f);
			d.noise_octaves = std::max(1u, std::min(d.noise_octaves, 4u));
			d.tiles_x = std::max(1u, std::min(d.tiles_x, 64u));
			d.tiles_y = std::max(1u, std::min(d.tiles_y, 64u));
			d.flipbook_mode = std::min(d.flipbook_mode, (u32)FLIPBOOK_RANDOM);
			fixf(d.flipbook_cycles, 0.0f, 1000.0f, 1.0f);
			fixf(d.flipbook_fps, 0.0f, 1000.0f, 15.0f);
			fixr(d.start_frame, 0.0f, 4096.0f);
			d.render_mode = std::min(d.render_mode, (u32)RENDER_VERTICAL);
			d.blend = std::min(d.blend, (u32)BLEND_PREMULTIPLIED);
			fixf(d.soft_distance, 0.0f, 100.0f, 0.0f);
			fixf(d.length_scale, 0.0f, 1000.0f, 2.0f);
			fixf(d.velocity_scale, 0.0f, 100.0f, 0.0f);
			d.sort_mode = std::min(d.sort_mode, (u32)SORT_DEPTH);
			fixf(d.emissive, 0.0f, 100.0f, 1.0f);
			fixf(d.aspect, 0.01f, 100.0f, 1.0f);
			d.layer = d.layer ? 1u : 0u;
			d.collision_mode = std::min(d.collision_mode, (u32)COLLIDE_KILL);
			fixf(d.bounce, 0.0f, 2.0f, 0.4f);
			fixf(d.dampen, 0.0f, 1.0f, 0.2f);
			fixf(d.lifetime_loss, 0.0f, 1.0f, 0.0f);
			fixf(d.collision_radius, 0.0f, 100.0f, 0.02f);
			fixf(d.collision_thickness, 0.001f, 1000.0f, 0.5f);
			fixf(d.trail_lifetime, 0.01f, 60.0f, 0.3f);
			fixf(d.trail_min_distance, 0.0f, 100.0f, 0.05f);
			d.trail_max_points = std::max(2u, std::min(d.trail_max_points, MAX_TRAIL_POINTS));
			fixf(d.trail_width, 0.0f, 1000.0f, 1.0f);
			d.trail_texture_mode = d.trail_texture_mode ? TEXMODE_TILE : TEXMODE_STRETCH;
		}

		void sanitize(BeamDesc& d)
		{
			auto fixf = [](float& f, float lo, float hi, float def) { if (!std::isfinite(f)) f = def; f = clampf(f, lo, hi); };
			fixf(d.width, 0.0f, 1e4f, 0.05f);
			for (int i = 0; i < 4; ++i) fixf(d.color[i], 0.0f, 1.0f, 1.0f);
			d.blend = std::min(d.blend, (u32)BLEND_PREMULTIPLIED);
			fixf(d.emissive, 0.0f, 100.0f, 1.0f);
			fixf(d.uv_tiling, 0.0f, 1e4f, 0.0f);
			fixf(d.uv_scroll, -1e4f, 1e4f, 0.0f);
			d.segments = std::max(1u, std::min(d.segments, 256u));
			fixf(d.noise, 0.0f, 1e3f, 0.0f);
			fixf(d.noise_frequency, 0.0f, 1e3f, 1.0f);
			fixf(d.noise_speed, 0.0f, 1e3f, 5.0f);
			fixf(d.duration, 0.0f, 3600.0f, 0.0f);
			fixf(d.fade_in, 0.0f, 3600.0f, 0.0f);
			fixf(d.fade_out, 0.0f, 3600.0f, 0.0f);
			fixf(d.speed, 0.0f, 1e6f, 0.0f);
			fixf(d.length, 0.0f, 1e5f, 2.0f);
			fixf(d.soft_distance, 0.0f, 100.0f, 0.0f);
			d.width_curve.count = std::min(d.width_curve.count, MAX_CURVE_KEYS);
			d.gradient.count = std::min(d.gradient.count, MAX_GRADIENT_KEYS);
			d.layer = d.layer ? 1u : 0u;
		}

		void bake(Emitter& e)
		{
			const EmitterDesc& d = e.d;
			for (u32 i = 0; i < LUT_SIZE; ++i)
			{
				float t = (float)i / (float)(LUT_SIZE - 1);
				e.size_lut[i] = eval_curve(d.size_curve, t);
				e.speed_lut[i] = eval_curve(d.speed_curve, t);
				e.vel_lut[i] = eval_curve(d.velocity_curve, t);
				e.twidth_lut[i] = eval_curve(d.trail_width_curve, t);
				eval_gradient(d.color_gradient, t, e.color_lut[i]);
				eval_gradient(d.trail_gradient, t, e.tcolor_lut[i]);
			}
			e.has_size = d.size_curve.count > 0;
			e.has_speed = d.speed_curve.count > 0;
			e.has_vel = (d.velocity[0] != 0.0f || d.velocity[1] != 0.0f || d.velocity[2] != 0.0f);
			e.has_color = d.color_gradient.count > 0;
			euler_rows(d.shape_rotation, e.shape_rot);
		}

		inline float lut(const float* l, float t)
		{
			float f = sat(t) * (float)(LUT_SIZE - 1);
			u32 i = (u32)f;
			if (i >= LUT_SIZE - 1) return l[LUT_SIZE - 1];
			float k = f - (float)i;
			return l[i] + (l[i + 1] - l[i]) * k;
		}
		inline void lut4(const float (*l)[4], float t, float* o)
		{
			float f = sat(t) * (float)(LUT_SIZE - 1);
			u32 i = (u32)f;
			if (i >= LUT_SIZE - 1) { std::memcpy(o, l[LUT_SIZE - 1], 16); return; }
			float k = f - (float)i;
			for (int q = 0; q < 4; ++q) o[q] = l[i][q] + (l[i + 1][q] - l[i][q]) * k;
		}

		void update_scale(Emitter& e)
		{
			float sx = len3(e.m[0], e.m[1], e.m[2]), sy = len3(e.m[4], e.m[5], e.m[6]), sz = len3(e.m[8], e.m[9], e.m[10]);
			float s = (sx + sy + sz) / 3.0f;
			e.scale_uniform = std::isfinite(s) && s > 1e-6f ? s : 1.0f;
		}

		void ensure_capacity(Emitter& e, u32 needed)
		{
			needed = std::min(needed, e.d.max_particles);
			if (needed <= e.allocated) return;
			u32 n = std::max(needed, std::min(e.d.max_particles, std::max(64u, e.allocated * 2u)));
			for (auto* v : { &e.px, &e.py, &e.pz, &e.vx, &e.vy, &e.vz, &e.age, &e.life, &e.size0, &e.rot, &e.rotv, &e.frame0, &e.tlast }) v->resize(n);
			e.col0.resize(n); e.pseed.resize(n); e.dead.resize(n); e.inst.resize(n);
			if (e.d.trails)
			{
				e.tmax = e.d.trail_max_points;
				e.trail.resize((size_t)n * e.tmax * 4);
				e.tcount.resize(n); e.thead.resize(n);
			}
			e.allocated = n;
		}

		void reset_trails_layout(Emitter& e)
		{
			// trail settings changed: drop the history (layout depends on trail_max_points)
			e.tmax = e.d.trails ? e.d.trail_max_points : 0;
			if (e.d.trails)
			{
				e.trail.assign((size_t)e.allocated * e.tmax * 4, 0.0f);
				e.tcount.assign(e.allocated, 0);
				e.thead.assign(e.allocated, 0);
			}
			else { e.trail.clear(); e.tcount.clear(); e.thead.clear(); }
		}

		void restart_state(Emitter& e, bool reseed)
		{
			e.time = 0.0f;
			e.cycle = 0;
			e.spawn_accum = 0.0f;
			e.dist_accum = 0.0f;
			std::memset(e.burst_done, 0, sizeof(e.burst_done));
			if (reseed)
			{
				u32 s = e.d.seed ? e.d.seed : (g_seed_counter = g_seed_counter * 747796405u + 2891336453u);
				e.seed_used = s;
				e.rng.seed(s, 0x5851f42dull ^ (u64)s);
			}
		}

		// ------------------------------------------------------------------------------------------ instances
		// Raw views of the particle arrays, hoisted out of the hot loops: a byte store to `dead` may alias any
		// vector's internal pointer, which would otherwise force the compiler to reload them every iteration.
		struct Soa
		{
			float* VX_RESTRICT px; float* VX_RESTRICT py; float* VX_RESTRICT pz;
			float* VX_RESTRICT vx; float* VX_RESTRICT vy; float* VX_RESTRICT vz;
			float* VX_RESTRICT age; float* VX_RESTRICT life; float* VX_RESTRICT size0;
			float* VX_RESTRICT rot; float* VX_RESTRICT rotv; float* VX_RESTRICT frame0;
			u32* VX_RESTRICT col0; u8* VX_RESTRICT dead; GpuParticle* VX_RESTRICT inst;
		};
		inline Soa soa(Emitter& e)
		{
			return Soa{ e.px.data(), e.py.data(), e.pz.data(), e.vx.data(), e.vy.data(), e.vz.data(), e.age.data(), e.life.data(),
				e.size0.data(), e.rot.data(), e.rotv.data(), e.frame0.data(), e.col0.data(), e.dead.data(), e.inst.data() };
		}

		// Per-emitter constants of the render-instance write.
		struct InstCtx
		{
			const float* m; bool local; float scale;
			const float* size_lut; bool has_size;
			const float (*color_lut)[4]; bool has_color;
			u32 frames, fmode; float cycles, fps;
			bool stretched; float length_scale, velocity_scale, aspect;
		};
		inline InstCtx inst_ctx(const Emitter& e)
		{
			const EmitterDesc& d = e.d;
			return InstCtx{ e.m, d.simulation_space == SPACE_LOCAL, e.scale_uniform, e.size_lut, e.has_size, e.color_lut, e.has_color,
				d.tiles_x * d.tiles_y, d.flipbook_mode, d.flipbook_cycles, d.flipbook_fps,
				d.render_mode == RENDER_STRETCHED, d.length_scale, d.velocity_scale, d.aspect };
		}

		inline void write_instance(const Soa& s, const InstCtx& c, u32 i)
		{
			GpuParticle& o = s.inst[i];
			const float t = s.age[i] / s.life[i];
			float wp[3], wv[3];
			if (c.local)
			{
				xform_point(c.m, s.px[i], s.py[i], s.pz[i], wp);
				xform_dir(c.m, s.vx[i], s.vy[i], s.vz[i], wv);
			}
			else { wp[0] = s.px[i]; wp[1] = s.py[i]; wp[2] = s.pz[i]; wv[0] = s.vx[i]; wv[1] = s.vy[i]; wv[2] = s.vz[i]; }
			o.pos[0] = wp[0]; o.pos[1] = wp[1]; o.pos[2] = wp[2];
			float size = s.size0[i] * (c.has_size ? lut(c.size_lut, t) : 1.0f);
			if (c.local) size *= c.scale;
			o.size = size;
			o.rot = s.rot[i];
			o.aspect = c.aspect;
			o.flags = 0;
			if (c.stretched)
			{
				float sp = len3(wv[0], wv[1], wv[2]);
				float len = size * c.length_scale + sp * c.velocity_scale;
				if (len < size) len = size;
				if (sp > 1e-5f) { float k = len / sp; o.axis[0] = wv[0] * k; o.axis[1] = wv[1] * k; o.axis[2] = wv[2] * k; }
				else { o.axis[0] = 0.0f; o.axis[1] = len; o.axis[2] = 0.0f; }
			}
			else { o.axis[0] = o.axis[1] = o.axis[2] = 0.0f; }
			if (c.has_color)
			{
				float c0[4], g[4];
				unpack_rgba(s.col0[i], c0);
				lut4(c.color_lut, t, g);
				o.color = pack_rgba(c0[0] * g[0], c0[1] * g[1], c0[2] * g[2], c0[3] * g[3]);
			}
			else o.color = s.col0[i];
			if (c.frames > 1)
			{
				float f = s.frame0[i];
				if (c.fmode == FLIPBOOK_LIFETIME) f += t * (float)c.frames * c.cycles;
				else if (c.fmode == FLIPBOOK_FPS) f += s.age[i] * c.fps;
				f = std::fmod(f, (float)c.frames);
				if (f < 0.0f) f += (float)c.frames;
				o.frame = f;
			}
			else o.frame = 0.0f;
		}

		inline void write_instance(Emitter& e, u32 i) { write_instance(soa(e), inst_ctx(e), i); }

		// ------------------------------------------------------------------------------------------ collision
		inline void recon(const DepthSnap& s, int u, int v, float* out)
		{
			u = std::max(0, std::min((int)s.w - 1, u));
			v = std::max(0, std::min((int)s.h - 1, v));
			float z = s.z[(size_t)v * s.w + u];
			float nx = ((float)u + 0.5f) / (float)s.w * 2.0f - 1.0f;
			float ny = 1.0f - ((float)v + 0.5f) / (float)s.h * 2.0f;
			float kx = s.v.ortho ? nx * s.v.tan_half_x : nx * z * s.v.tan_half_x;
			float ky = s.v.ortho ? ny * s.v.tan_half_y : ny * z * s.v.tan_half_y;
			for (int i = 0; i < 3; ++i) out[i] = s.v.eye[i] + s.v.forward[i] * z + s.v.right[i] * kx + s.v.up[i] * ky;
		}

		// World-space particle vs the scene depth. Returns 0 = no hit, 1 = bounced (p / v modified), 2 = kill.
		inline int collide(const DepthSnap& s, const EmitterDesc& d, float* p, float* v, float& age, float life)
		{
			float dv[3] = { p[0] - s.v.eye[0], p[1] - s.v.eye[1], p[2] - s.v.eye[2] };
			float zv = dot3(dv, s.v.forward);
			if (zv <= s.v.near_clip) return 0;
			float xv = dot3(dv, s.v.right), yv = dot3(dv, s.v.up);
			float nx = s.v.ortho ? xv / s.v.tan_half_x : xv / (zv * s.v.tan_half_x);
			float ny = s.v.ortho ? yv / s.v.tan_half_y : yv / (zv * s.v.tan_half_y);
			if (nx <= -1.0f || nx >= 1.0f || ny <= -1.0f || ny >= 1.0f) return 0;
			int u = (int)((nx * 0.5f + 0.5f) * (float)s.w), vv = (int)((0.5f - ny * 0.5f) * (float)s.h);
			u = std::max(0, std::min((int)s.w - 1, u));
			vv = std::max(0, std::min((int)s.h - 1, vv));
			float zs = s.z[(size_t)vv * s.w + u];
			if (!(zs < s.v.far_clip * 0.999f)) return 0;           // sky / nothing there
			float pen = zv + d.collision_radius - zs;
			if (pen <= 0.0f || pen > d.collision_thickness) return 0;
			if (d.collision_mode == COLLIDE_KILL) return 2;

			// surface normal from the neighbouring texels (the side facing the camera)
			float c[3], a[3], b[3];
			recon(s, u, vv, c);
			const int du = u + 1 < (int)s.w ? 1 : -1, dvv = vv + 1 < (int)s.h ? 1 : -1;
			recon(s, u + du, vv, a);
			recon(s, u, vv + dvv, b);
			float ea[3] = { (a[0] - c[0]) * du, (a[1] - c[1]) * du, (a[2] - c[2]) * du };
			float eb[3] = { (b[0] - c[0]) * dvv, (b[1] - c[1]) * dvv, (b[2] - c[2]) * dvv };
			float n[3];
			cross3(ea, eb, n);
			float nl = len3(n[0], n[1], n[2]);
			float toeye[3] = { s.v.eye[0] - c[0], s.v.eye[1] - c[1], s.v.eye[2] - c[2] };
			if (nl < 1e-9f) { n[0] = -s.v.forward[0]; n[1] = -s.v.forward[1]; n[2] = -s.v.forward[2]; }
			else
			{
				n[0] /= nl; n[1] /= nl; n[2] /= nl;
				if (dot3(n, toeye) < 0.0f) { n[0] = -n[0]; n[1] = -n[1]; n[2] = -n[2]; }
			}
			// back onto the surface along the view ray (same pixel), then reflect
			if (s.v.ortho) { for (int i = 0; i < 3; ++i) p[i] -= s.v.forward[i] * pen; }
			else { float k = (zs - d.collision_radius) / zv; for (int i = 0; i < 3; ++i) p[i] = s.v.eye[i] + dv[i] * k; }
			float vn = dot3(v, n);
			if (vn < 0.0f)
			{
				float vt[3] = { v[0] - n[0] * vn, v[1] - n[1] * vn, v[2] - n[2] * vn };
				float keep = 1.0f - d.dampen;
				for (int i = 0; i < 3; ++i) v[i] = vt[i] * keep - n[i] * vn * d.bounce;
			}
			age += d.lifetime_loss * life;
			return 1;
		}

		// ------------------------------------------------------------------------------------------ update
		struct Ctx
		{
			float dt;
			const DepthSnap* depth;   // null = no collision this frame
		};

		void update_range(Emitter& e, u32 begin, u32 end, const Ctx& ctx)
		{
			const EmitterDesc& d = e.d;
			const float dt = e.step_dt;
			const bool local = d.simulation_space == SPACE_LOCAL;
			// gravity in the simulation space
			float g[3] = { 0.0f, -9.81f * d.gravity, 0.0f };
			if (local && d.gravity != 0.0f)
			{
				float gw[3] = { g[0], g[1], g[2] };
				for (int r = 0; r < 3; ++r)
				{
					const float* row = e.m + r * 4;
					float l2 = row[0] * row[0] + row[1] * row[1] + row[2] * row[2];
					g[r] = l2 > 1e-12f ? (gw[0] * row[0] + gw[1] * row[1] + gw[2] * row[2]) / l2 : 0.0f;
				}
			}
			const float drag_k = d.drag > 0.0f ? std::exp(-d.drag * dt) : 1.0f;
			// constant extra velocity in the simulation space
			float xv[3] = { d.velocity[0], d.velocity[1], d.velocity[2] };
			if (e.has_vel)
			{
				if (!local && d.velocity_space == SPACE_LOCAL)
				{
					float r[3];
					xform_dir(e.m, xv[0], xv[1], xv[2], r);
					float s = e.scale_uniform > 1e-6f ? 1.0f / e.scale_uniform : 1.0f;
					xv[0] = r[0] * s; xv[1] = r[1] * s; xv[2] = r[2] * s;
				}
				else if (local && d.velocity_space == SPACE_WORLD)
				{
					float w[3] = { xv[0], xv[1], xv[2] };
					for (int r = 0; r < 3; ++r)
					{
						const float* row = e.m + r * 4;
						float l2 = row[0] * row[0] + row[1] * row[1] + row[2] * row[2];
						xv[r] = l2 > 1e-12f ? (w[0] * row[0] + w[1] * row[1] + w[2] * row[2]) / l2 : 0.0f;
					}
				}
			}
			const bool noise = d.noise_strength > 0.0f;
			const float nf = d.noise_frequency, nscroll = e.sim_clock * d.noise_scroll, nstr = d.noise_strength;
			const u32 octaves = d.noise_octaves;
			const bool collide_now = ctx.depth && d.collision && d.layer == 0 && e.world == 0 && dt > 0.0f;
			const bool trails = d.trails && e.tmax >= 2;
			const bool has_speed = e.has_speed, has_vel = e.has_vel;
			const float* speed_lut = e.speed_lut;
			const float* vel_lut = e.vel_lut;
			const Soa s = soa(e);
			const InstCtx ic = inst_ctx(e);

			for (u32 i = begin; i < end; ++i)
			{
				float a = s.age[i] + dt;
				if (a >= s.life[i]) { s.dead[i] = 1; continue; }
				s.dead[i] = 0;
				s.age[i] = a;
				if (dt > 0.0f)
				{
					float t = a / s.life[i];
					float vx = (s.vx[i] + g[0] * dt) * drag_k;
					float vy = (s.vy[i] + g[1] * dt) * drag_k;
					float vz = (s.vz[i] + g[2] * dt) * drag_k;
					s.vx[i] = vx; s.vy[i] = vy; s.vz[i] = vz;
					float sm = has_speed ? lut(speed_lut, t) : 1.0f;
					float mx = vx * sm, my = vy * sm, mz = vz * sm;
					if (has_vel)
					{
						float k = lut(vel_lut, t);
						mx += xv[0] * k; my += xv[1] * k; mz += xv[2] * k;
					}
					if (noise)
					{
						float n[3];
						noise_vec(s.px[i] * nf + nscroll, s.py[i] * nf + nscroll * 0.7f, s.pz[i] * nf - nscroll * 0.4f, octaves, n);
						mx += n[0] * nstr; my += n[1] * nstr; mz += n[2] * nstr;
					}
					s.px[i] += mx * dt; s.py[i] += my * dt; s.pz[i] += mz * dt;
					s.rot[i] += s.rotv[i] * dt;

					if (collide_now)
					{
						float p[3], v[3];
						if (local)
						{
							xform_point(e.m, s.px[i], s.py[i], s.pz[i], p);
							xform_dir(e.m, s.vx[i], s.vy[i], s.vz[i], v);
						}
						else { p[0] = s.px[i]; p[1] = s.py[i]; p[2] = s.pz[i]; v[0] = s.vx[i]; v[1] = s.vy[i]; v[2] = s.vz[i]; }
						float age = s.age[i];
						int r = collide(*ctx.depth, d, p, v, age, s.life[i]);
						if (r == 2 || age >= s.life[i]) { s.dead[i] = 1; continue; }
						if (r == 1)
						{
							s.age[i] = age;
							if (local)
							{
								// back into emitter space (rows are orthogonal: inverse = scaled transpose)
								float q[3] = { p[0] - e.m[12], p[1] - e.m[13], p[2] - e.m[14] };
								float lp[3], lv[3];
								for (int rr = 0; rr < 3; ++rr)
								{
									const float* row = e.m + rr * 4;
									float l2 = row[0] * row[0] + row[1] * row[1] + row[2] * row[2];
									float il = l2 > 1e-12f ? 1.0f / l2 : 0.0f;
									lp[rr] = (q[0] * row[0] + q[1] * row[1] + q[2] * row[2]) * il;
									lv[rr] = (v[0] * row[0] + v[1] * row[1] + v[2] * row[2]) * il;
								}
								s.px[i] = lp[0]; s.py[i] = lp[1]; s.pz[i] = lp[2];
								s.vx[i] = lv[0]; s.vy[i] = lv[1]; s.vz[i] = lv[2];
							}
							else { s.px[i] = p[0]; s.py[i] = p[1]; s.pz[i] = p[2]; s.vx[i] = v[0]; s.vy[i] = v[1]; s.vz[i] = v[2]; }
						}
					}

					if (trails)
					{
						// record a world-space point when the particle moved far enough (the head always follows it)
						float wp[3];
						if (local) xform_point(e.m, s.px[i], s.py[i], s.pz[i], wp);
						else { wp[0] = s.px[i]; wp[1] = s.py[i]; wp[2] = s.pz[i]; }
						float* ring = &e.trail[(size_t)i * e.tmax * 4];
						u32 cnt = e.tcount[i], head = e.thead[i];
						bool add = cnt == 0;
						if (!add)
						{
							const float* last = ring + head * 4;
							float dx = wp[0] - last[0], dy = wp[1] - last[1], dz = wp[2] - last[2];
							add = dx * dx + dy * dy + dz * dz >= d.trail_min_distance * d.trail_min_distance;
						}
						if (add)
						{
							head = cnt == 0 ? 0 : (head + 1) % e.tmax;
							float* pt = ring + head * 4;
							pt[0] = wp[0]; pt[1] = wp[1]; pt[2] = wp[2]; pt[3] = e.sim_clock;
							e.thead[i] = (u8)head;
							if (cnt < e.tmax) e.tcount[i] = (u8)(cnt + 1);
						}
					}
				}
				write_instance(s, ic, i);
			}
		}

		void move_particle(Emitter& e, u32 dst, u32 src)
		{
			e.px[dst] = e.px[src]; e.py[dst] = e.py[src]; e.pz[dst] = e.pz[src];
			e.vx[dst] = e.vx[src]; e.vy[dst] = e.vy[src]; e.vz[dst] = e.vz[src];
			e.age[dst] = e.age[src]; e.life[dst] = e.life[src]; e.size0[dst] = e.size0[src];
			e.rot[dst] = e.rot[src]; e.rotv[dst] = e.rotv[src]; e.frame0[dst] = e.frame0[src];
			e.col0[dst] = e.col0[src]; e.pseed[dst] = e.pseed[src]; e.dead[dst] = e.dead[src];
			e.inst[dst] = e.inst[src];
			e.tlast[dst] = e.tlast[src];
			if (e.tmax)
			{
				std::memcpy(&e.trail[(size_t)dst * e.tmax * 4], &e.trail[(size_t)src * e.tmax * 4], sizeof(float) * 4 * e.tmax);
				e.tcount[dst] = e.tcount[src]; e.thead[dst] = e.thead[src];
			}
		}

		void compact(Emitter& e)
		{
			u32 i = 0;
			while (i < e.count)
			{
				if (e.dead[i])
				{
					u32 last = e.count - 1;
					while (last > i && e.dead[last]) --last;
					if (last > i) move_particle(e, i, last);
					e.count = last;   // [last] is dead now or was moved into i
					if (last == i) break;
				}
				++i;
			}
		}

		// One shape sample in shape space: position + unit direction (+Z forward).
		void sample_shape(Emitter& e, float* pos, float* dir)
		{
			const EmitterDesc& d = e.d;
			Pcg& r = e.rng;
			pos[0] = pos[1] = pos[2] = 0.0f;
			dir[0] = 0.0f; dir[1] = 0.0f; dir[2] = 1.0f;
			switch (d.shape)
			{
			case SHAPE_SPHERE:
			case SHAPE_HEMISPHERE:
			{
				r.unit_vector(dir);
				if (d.shape == SHAPE_HEMISPHERE && dir[2] < 0.0f) dir[2] = -dir[2];
				float rr = d.radius * lerpf(1.0f, std::cbrt(r.unit()), d.radius_thickness);
				pos[0] = dir[0] * rr; pos[1] = dir[1] * rr; pos[2] = dir[2] * rr;
				break;
			}
			case SHAPE_CONE:
			{
				float phi = r.unit() * d.arc * DEG;
				float s = std::sqrt(lerpf(1.0f, r.unit(), d.radius_thickness));   // 0..1 across the base
				float half = d.angle * DEG;
				if (d.radius < 1e-5f)
				{
					// point cone: uniform over the spherical cap
					float cz = lerpf(1.0f, std::cos(half), r.unit());
					float sz = std::sqrt(std::max(0.0f, 1.0f - cz * cz));
					dir[0] = std::cos(phi) * sz; dir[1] = std::sin(phi) * sz; dir[2] = cz;
				}
				else
				{
					pos[0] = std::cos(phi) * s * d.radius; pos[1] = std::sin(phi) * s * d.radius;
					float a = half * s;
					dir[0] = std::cos(phi) * std::sin(a); dir[1] = std::sin(phi) * std::sin(a); dir[2] = std::cos(a);
				}
				break;
			}
			case SHAPE_BOX:
			{
				float hx = d.box[0] * 0.5f, hy = d.box[1] * 0.5f, hz = d.box[2] * 0.5f;
				if (d.emit_from == EMIT_VOLUME)
				{
					pos[0] = r.sym() * hx; pos[1] = r.sym() * hy; pos[2] = r.sym() * hz;
				}
				else if (d.emit_from == EMIT_SHELL)
				{
					// face chosen by area
					float ax = d.box[1] * d.box[2], ay = d.box[0] * d.box[2], az = d.box[0] * d.box[1];
					float pick = r.unit() * (ax + ay + az);
					float sgn = r.unit() < 0.5f ? -1.0f : 1.0f;
					pos[0] = r.sym() * hx; pos[1] = r.sym() * hy; pos[2] = r.sym() * hz;
					if (pick < ax) pos[0] = sgn * hx; else if (pick < ax + ay) pos[1] = sgn * hy; else pos[2] = sgn * hz;
				}
				else
				{
					// one of the 12 edges, by length
					float ex = d.box[0], ey = d.box[1], ez = d.box[2];
					float pick = r.unit() * 4.0f * (ex + ey + ez);
					float s1 = r.unit() < 0.5f ? -1.0f : 1.0f, s2 = r.unit() < 0.5f ? -1.0f : 1.0f;
					if (pick < 4.0f * ex) { pos[0] = r.sym() * hx; pos[1] = s1 * hy; pos[2] = s2 * hz; }
					else if (pick < 4.0f * (ex + ey)) { pos[0] = s1 * hx; pos[1] = r.sym() * hy; pos[2] = s2 * hz; }
					else { pos[0] = s1 * hx; pos[1] = s2 * hy; pos[2] = r.sym() * hz; }
				}
				break;
			}
			case SHAPE_CIRCLE:
			{
				float phi = r.unit() * d.arc * DEG;
				float s = std::sqrt(lerpf(1.0f, r.unit(), d.radius_thickness));
				dir[0] = std::cos(phi); dir[1] = std::sin(phi); dir[2] = 0.0f;
				pos[0] = dir[0] * s * d.radius; pos[1] = dir[1] * s * d.radius;
				break;
			}
			case SHAPE_EDGE:
				pos[0] = r.sym() * d.length * 0.5f;
				break;
			default:   // point
				break;
			}
			if (d.random_direction > 0.0f)
			{
				float rv[3];
				r.unit_vector(rv);
				for (int i = 0; i < 3; ++i) dir[i] = lerpf(dir[i], rv[i], d.random_direction);
				normalize3(dir);
			}
			// shape rotation + offset
			const float* R = e.shape_rot;
			float p2[3] = { pos[0] * R[0] + pos[1] * R[3] + pos[2] * R[6], pos[0] * R[1] + pos[1] * R[4] + pos[2] * R[7], pos[0] * R[2] + pos[1] * R[5] + pos[2] * R[8] };
			float d2[3] = { dir[0] * R[0] + dir[1] * R[3] + dir[2] * R[6], dir[0] * R[1] + dir[1] * R[4] + dir[2] * R[7], dir[0] * R[2] + dir[1] * R[5] + dir[2] * R[8] };
			for (int i = 0; i < 3; ++i) { pos[i] = p2[i] + d.shape_offset[i]; dir[i] = d2[i]; }
		}

		// Spawn one particle; f = where in the frame it was emitted (0 = start, 1 = now) for pose
		// interpolation, leftover = simulated time it already had this frame.
		void spawn_one(Emitter& e, float f, float leftover)
		{
			if (e.count >= e.d.max_particles) return;
			ensure_capacity(e, e.count + 1);
			if (e.count >= e.allocated) return;
			const EmitterDesc& d = e.d;
			Pcg& r = e.rng;
			const u32 i = e.count++;
			float pos[3], dir[3];
			sample_shape(e, pos, dir);
			float speed = r.range(d.speed);
			float p[3], v[3];
			if (d.simulation_space == SPACE_LOCAL)
			{
				p[0] = pos[0]; p[1] = pos[1]; p[2] = pos[2];
				v[0] = dir[0] * speed; v[1] = dir[1] * speed; v[2] = dir[2] * speed;
			}
			else
			{
				float pa[3], pb[3];
				xform_point(e.m_last, pos[0], pos[1], pos[2], pa);
				xform_point(e.m, pos[0], pos[1], pos[2], pb);
				for (int k = 0; k < 3; ++k) p[k] = lerpf(pa[k], pb[k], f);
				float wd[3];
				xform_dir(e.m, dir[0], dir[1], dir[2], wd);
				normalize3(wd);
				for (int k = 0; k < 3; ++k) v[k] = wd[k] * speed + e.vel_w[k] * d.inherit_velocity;
			}
			float life = r.range(d.lifetime);
			e.life[i] = life;
			e.size0[i] = r.range(d.size);
			e.rot[i] = r.range(d.rotation) * DEG;
			e.rotv[i] = r.range(d.rotation_speed) * DEG;
			float k = r.unit();
			e.col0[i] = pack_rgba(lerpf(d.color[0], d.color2[0], k), lerpf(d.color[1], d.color2[1], k), lerpf(d.color[2], d.color2[2], k), lerpf(d.color[3], d.color2[3], k));
			e.pseed[i] = r.next();
			const u32 frames = d.tiles_x * d.tiles_y;
			if (frames > 1)
			{
				if (d.flipbook_mode == FLIPBOOK_RANDOM) e.frame0[i] = std::floor(r.unit() * (float)frames);
				else e.frame0[i] = std::floor(r.range(d.start_frame));
			}
			else e.frame0[i] = 0.0f;
			// pre-integrate the part of the frame it already lived
			leftover = std::max(0.0f, std::min(leftover, life * 0.999f));
			e.age[i] = leftover;
			if (leftover > 0.0f)
			{
				float g = -9.81f * d.gravity;
				if (d.simulation_space == SPACE_WORLD) { v[1] += g * leftover; }
				for (int q = 0; q < 3; ++q) p[q] += v[q] * leftover;
			}
			e.px[i] = p[0]; e.py[i] = p[1]; e.pz[i] = p[2];
			e.vx[i] = v[0]; e.vy[i] = v[1]; e.vz[i] = v[2];
			e.dead[i] = 0;
			e.tlast[i] = e.sim_clock;
			if (e.tmax)
			{
				e.tcount[i] = 0; e.thead[i] = 0;
				float wp[3];
				if (d.simulation_space == SPACE_LOCAL) xform_point(e.m, p[0], p[1], p[2], wp);
				else { wp[0] = p[0]; wp[1] = p[1]; wp[2] = p[2]; }
				float* pt = &e.trail[(size_t)i * e.tmax * 4];
				pt[0] = wp[0]; pt[1] = wp[1]; pt[2] = wp[2]; pt[3] = e.sim_clock;
				e.tcount[i] = 1;
			}
			++e.spawned_total;
			write_instance(e, i);
		}

		// Emission clock + spawn plan for this frame (serial, cheap).
		void pre_update(Emitter& e, float dt)
		{
			const EmitterDesc& d = e.d;
			e.plan_rate = 0;
			e.plan_burst = 0;
			e.step_dt = e.paused ? 0.0f : dt * d.simulation_speed;
			// emitter velocity from the pose change since the last update
			if (dt > 1e-6f)
			{
				for (int k = 0; k < 3; ++k) e.vel_w[k] = (e.m[12 + k] - e.m_last[12 + k]) / dt;
				if (!finite3(e.vel_w)) e.vel_w[0] = e.vel_w[1] = e.vel_w[2] = 0.0f;
			}
			if (!e.playing || e.step_dt <= 0.0f) return;
			const float sdt = e.step_dt;
			float t0 = e.time - d.start_delay, t1 = t0 + sdt;
			e.time += sdt;
			if (t1 <= 0.0f) return;              // still waiting for the start delay
			if (t0 < 0.0f) t0 = 0.0f;
			const float dur = d.duration;

			auto fire_bursts = [&](float local_b)
			{
				for (u32 b = 0; b < d.burst_count; ++b)
				{
					const Burst& br = d.bursts[b];
					for (;;)
					{
						u32 k = e.burst_done[b];
						if (br.cycles != 0 && k >= br.cycles) break;
						float bt = br.time + (float)k * br.interval;
						if (bt >= dur && !(k == 0 && br.time == 0.0f)) break;
						if (bt > local_b) break;
						e.burst_done[b] = k + 1;
						if (br.probability < 1.0f && e.rng.unit() >= br.probability) continue;
						u32 n = br.count_min;
						if (br.count_max > br.count_min) n += (u32)(e.rng.unit() * (float)(br.count_max - br.count_min + 1));
						e.plan_burst += std::min(n, br.count_max);
					}
				}
			};

			float emit_span = 0.0f;
			if (!d.looping)
			{
				if (t0 < dur)
				{
					float b = std::min(t1, dur);
					emit_span = b - t0;
					fire_bursts(b);
				}
				if (t1 >= dur)
				{
					e.playing = false;   // emission cycle over; the particles live on
				}
			}
			else
			{
				emit_span = t1 - t0;
				// the cycle index is carried explicitly: (k * dur) / dur may round below k at a boundary
				u32 cyc = std::max(e.cycle, (u32)std::floor(t0 / dur));
				if (cyc != e.cycle) { e.cycle = cyc; std::memset(e.burst_done, 0, sizeof(e.burst_done)); }
				for (int guard = 0; guard < 64; ++guard)
				{
					float cycle_start = (float)cyc * dur;
					float local_b = std::min(t1 - cycle_start, dur);
					fire_bursts(local_b);
					if (t1 < cycle_start + dur) break;
					++cyc;
					e.cycle = cyc;
					std::memset(e.burst_done, 0, sizeof(e.burst_done));
				}
			}
			if (emit_span > 0.0f && d.rate > 0.0f)
			{
				e.spawn_accum += d.rate * emit_span;
				float n = std::floor(e.spawn_accum);
				e.spawn_accum -= n;
				e.plan_rate = (u32)std::min(n, (float)d.max_particles);
			}
			if (emit_span > 0.0f && d.rate_over_distance > 0.0f)
			{
				float moved = len3(e.m[12] - e.m_last[12], e.m[13] - e.m_last[13], e.m[14] - e.m_last[14]);
				if (std::isfinite(moved) && moved < 1000.0f)
				{
					e.dist_accum += moved * d.rate_over_distance;
					float n = std::floor(e.dist_accum);
					e.dist_accum -= n;
					e.plan_rate += (u32)std::min(n, (float)d.max_particles);
				}
			}
		}

		void compute_bounds(Emitter& e)
		{
			if (e.count == 0) { std::memset(e.bmin, 0, sizeof(e.bmin)); std::memset(e.bmax, 0, sizeof(e.bmax)); return; }
			float mn[3] = { 1e30f, 1e30f, 1e30f }, mx[3] = { -1e30f, -1e30f, -1e30f };
			float maxsize = 0.0f;
			for (u32 i = 0; i < e.count; ++i)
			{
				const GpuParticle& o = e.inst[i];
				for (int k = 0; k < 3; ++k) { mn[k] = std::min(mn[k], o.pos[k]); mx[k] = std::max(mx[k], o.pos[k]); }
				float s = o.size * std::max(1.0f, o.aspect);
				float al = len3(o.axis[0], o.axis[1], o.axis[2]);
				maxsize = std::max(maxsize, std::max(s, al));
			}
			if (e.tmax)
			{
				for (u32 i = 0; i < e.count; ++i)
				{
					const float* ring = &e.trail[(size_t)i * e.tmax * 4];
					for (u32 k = 0; k < e.tcount[i]; ++k)
						for (int q = 0; q < 3; ++q) { mn[q] = std::min(mn[q], ring[k * 4 + q]); mx[q] = std::max(mx[q], ring[k * 4 + q]); }
				}
			}
			for (int k = 0; k < 3; ++k) { e.bmin[k] = mn[k] - maxsize; e.bmax[k] = mx[k] + maxsize; }
		}

		void finish_emitter(Emitter& e)
		{
			compact(e);
			// rate spawns spread across the frame, bursts at its end
			const u32 n = e.plan_rate;
			const float sdt = e.step_dt;
			for (u32 k = 0; k < n; ++k)
			{
				float f = (float)(k + 1) / (float)n;
				spawn_one(e, f, (1.0f - f) * sdt);
			}
			for (u32 k = 0; k < e.plan_burst; ++k) spawn_one(e, 1.0f, 0.0f);
			e.plan_rate = e.plan_burst = 0;
			compute_bounds(e);
			std::memcpy(e.m_last, e.m, sizeof(e.m));
		}

		void destroy_emitter_slot(u32 slot)
		{
			Emitter& e = g_emitters[slot];
			u32 gen = e.gen;
			e = Emitter{};
			e.gen = gen + 1;
			e.used = false;
			g_free_emitters.push_back(slot);
		}

		void destroy_beam_slot(u32 slot)
		{
			Beam& b = g_beams[slot];
			u32 gen = b.gen;
			b = Beam{};
			b.gen = gen + 1;
			b.used = false;
			g_free_beams.push_back(slot);
		}

		void update_world(u32 world, float dt)
		{
			auto t0 = std::chrono::steady_clock::now();
			dt = std::isfinite(dt) ? clampf(dt, 0.0f, 0.1f) : 0.0f;
			std::vector<Emitter*> list;
			list.reserve(g_emitters.size());
			for (auto& e : g_emitters) if (e.used && (world == ALL_WORLDS || e.world == world)) list.push_back(&e);
			for (Emitter* e : list) { pre_update(*e, dt); e->sim_clock += e->step_dt; }

			Ctx ctx{ dt, g_depth.valid ? &g_depth : nullptr };
			struct Task { Emitter* e; u32 begin, end; };
			std::vector<Task> tasks;
			for (Emitter* e : list)
				for (u32 b = 0; b < e->count; b += CHUNK) tasks.push_back({ e, b, std::min(e->count, b + CHUNK) });
			g_pool.parallel_for((u32)tasks.size(), [&](u32 i) { update_range(*tasks[i].e, tasks[i].begin, tasks[i].end, ctx); });
			g_pool.parallel_for((u32)list.size(), [&](u32 i) { finish_emitter(*list[i]); });

			// one-shot effects clean themselves up
			for (u32 s = 0; s < g_emitters.size(); ++s)
			{
				Emitter& e = g_emitters[s];
				if (e.used && e.auto_destroy && (world == ALL_WORLDS || e.world == world) && !e.playing && e.count == 0)
					destroy_emitter_slot(s);
			}
			for (u32 s = 0; s < g_beams.size(); ++s)
			{
				Beam& b = g_beams[s];
				if (!b.used || (world != ALL_WORLDS && b.world != world)) continue;
				b.age += dt;
				if (b.d.duration > 0.0f && b.age >= b.d.duration) destroy_beam_slot(s);
			}
			g_sim_ms = std::chrono::duration<float, std::milli>(std::chrono::steady_clock::now() - t0).count();
		}

		// ------------------------------------------------------------------------------------------ gather
		struct Frustum { float p[6][4]; };
		Frustum extract_frustum(const float* m)
		{
			// row-major, row vectors (p' = p * M): the column j of M is (m[j], m[4+j], m[8+j], m[12+j])
			auto col = [&](int j, int r) { return m[r * 4 + j]; };
			Frustum f{};
			for (int r = 0; r < 4; ++r)
			{
				f.p[0][r] = col(3, r) + col(0, r);
				f.p[1][r] = col(3, r) - col(0, r);
				f.p[2][r] = col(3, r) + col(1, r);
				f.p[3][r] = col(3, r) - col(1, r);
				f.p[4][r] = col(2, r);
				f.p[5][r] = col(3, r) - col(2, r);
			}
			for (auto& pl : f.p)
			{
				float l = len3(pl[0], pl[1], pl[2]);
				if (l > 1e-6f) for (float& v : pl) v /= l;
			}
			return f;
		}
		bool aabb_visible(const Frustum& f, const float* mn, const float* mx)
		{
			for (const auto& pl : f.p)
			{
				float x = pl[0] >= 0.0f ? mx[0] : mn[0];
				float y = pl[1] >= 0.0f ? mx[1] : mn[1];
				float z = pl[2] >= 0.0f ? mx[2] : mn[2];
				if (pl[0] * x + pl[1] * y + pl[2] * z + pl[3] < 0.0f) return false;
			}
			return true;
		}

		// Back-to-front order of [0, n) by view depth: keys are the depth quantised to 16 bits over the emitter's
		// depth range (plenty for blending order), sorted with two 8-bit LSD radix passes (stable).
		std::vector<u16> g_keys, g_keys_tmp;
		std::vector<u32> g_idx, g_idx_tmp;
		std::vector<float> g_depths;
		void sort_back_to_front(const Emitter& e, const ViewInfo& view, std::vector<u32>& order)
		{
			const u32 n = e.count;
			g_depths.resize(n); g_keys.resize(n); g_idx.resize(n); g_keys_tmp.resize(n); g_idx_tmp.resize(n);
			float dmin = 1e30f, dmax = -1e30f;
			const float ex = view.eye[0], ey = view.eye[1], ez = view.eye[2];
			const float fx = view.forward[0], fy = view.forward[1], fz = view.forward[2];
			for (u32 i = 0; i < n; ++i)
			{
				const float* p = e.inst[i].pos;
				float d = (p[0] - ex) * fx + (p[1] - ey) * fy + (p[2] - ez) * fz;
				g_depths[i] = d;
				dmin = std::min(dmin, d); dmax = std::max(dmax, d);
			}
			const float scale = dmax > dmin ? 65535.0f / (dmax - dmin) : 0.0f;
			for (u32 i = 0; i < n; ++i)
			{
				g_keys[i] = (u16)(65535.0f - (g_depths[i] - dmin) * scale + 0.5f);   // far first = small key
				g_idx[i] = i;
			}
			u16* kin = g_keys.data(); u16* kout = g_keys_tmp.data();
			u32* iin = g_idx.data(); u32* iout = g_idx_tmp.data();
			for (int pass = 0; pass < 2; ++pass)
			{
				const int shift = pass * 8;
				u32 hist[256] = {};
				for (u32 i = 0; i < n; ++i) ++hist[(kin[i] >> shift) & 255u];
				u32 sum = 0;
				for (u32 b = 0; b < 256; ++b) { u32 c = hist[b]; hist[b] = sum; sum += c; }
				for (u32 i = 0; i < n; ++i)
				{
					u32 dst = hist[(kin[i] >> shift) & 255u]++;
					kout[dst] = kin[i]; iout[dst] = iin[i];
				}
				std::swap(kin, kout); std::swap(iin, iout);
			}
			order.assign(iin, iin + n);
		}

		inline void ribbon_side(const float* p, const float* tangent, const ViewInfo& view, float half_width, float* side)
		{
			float tocam[3];
			if (view.ortho) { tocam[0] = -view.forward[0]; tocam[1] = -view.forward[1]; tocam[2] = -view.forward[2]; }
			else { tocam[0] = view.eye[0] - p[0]; tocam[1] = view.eye[1] - p[1]; tocam[2] = view.eye[2] - p[2]; }
			cross3(tangent, tocam, side);
			float l = len3(side[0], side[1], side[2]);
			if (l < 1e-9f) { side[0] = view.up[0]; side[1] = view.up[1]; side[2] = view.up[2]; l = 1.0f; }
			float k = half_width / l;
			side[0] *= k; side[1] *= k; side[2] *= k;
		}

		// Append a camera-facing strip through `pts` (n >= 2, xyz each) to the ribbon buffers.
		void emit_strip(DrawList& out, const float* pts, u32 n, const float* widths, const u32* colors, const float* us, const ViewInfo& view)
		{
			const u32 base = (u32)out.ribbon_vertices.size();
			for (u32 j = 0; j < n; ++j)
			{
				const float* p = pts + j * 3;
				const float* pa = pts + (j > 0 ? j - 1 : 0) * 3;
				const float* pb = pts + (j + 1 < n ? j + 1 : n - 1) * 3;
				float tan[3] = { pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2] };
				float side[3];
				ribbon_side(p, tan, view, widths[j] * 0.5f, side);
				RibbonVertex a{ { p[0] - side[0], p[1] - side[1], p[2] - side[2] }, us[j], 0.0f, colors[j] };
				RibbonVertex b{ { p[0] + side[0], p[1] + side[1], p[2] + side[2] }, us[j], 1.0f, colors[j] };
				out.ribbon_vertices.push_back(a);
				out.ribbon_vertices.push_back(b);
			}
			for (u32 j = 0; j + 1 < n; ++j)
			{
				u32 i0 = base + j * 2;
				out.ribbon_indices.insert(out.ribbon_indices.end(), { i0, i0 + 1, i0 + 2, i0 + 2, i0 + 1, i0 + 3 });
			}
		}

		void gather_trails(const Emitter& e, const ViewInfo& view, DrawList& out)
		{
			const EmitterDesc& d = e.d;
			static thread_local std::vector<float> pts, widths, us, ages, dist;
			static thread_local std::vector<u32> cols;
			for (u32 i = 0; i < e.count; ++i)
			{
				const float* ring = &e.trail[(size_t)i * e.tmax * 4];
				u32 cnt = e.tcount[i];
				if (cnt == 0) continue;
				pts.clear(); widths.clear(); us.clear(); cols.clear(); ages.clear();
				// head = the particle itself, then the recorded points newest -> oldest while young enough
				const GpuParticle& o = e.inst[i];
				pts.insert(pts.end(), { o.pos[0], o.pos[1], o.pos[2] });
				ages.push_back(0.0f);
				u32 head = e.thead[i];
				for (u32 k = 0; k < cnt; ++k)
				{
					const float* pt = ring + ((head + e.tmax - k) % e.tmax) * 4;
					float a = e.sim_clock - pt[3];
					if (a > d.trail_lifetime) break;
					float dx = pt[0] - pts[pts.size() - 3], dy = pt[1] - pts[pts.size() - 2], dz = pt[2] - pts[pts.size() - 1];
					if (dx * dx + dy * dy + dz * dz < 1e-10f) continue;
					pts.insert(pts.end(), { pt[0], pt[1], pt[2] });
					ages.push_back(a);
				}
				const u32 n = (u32)(pts.size() / 3);
				if (n < 2) continue;
				float pc[4];
				unpack_rgba(o.color, pc);
				float total = 0.0f;
				dist.assign(n, 0.0f);
				for (u32 j = 1; j < n; ++j)
				{
					const float* a = &pts[(j - 1) * 3]; const float* b = &pts[j * 3];
					total += len3(b[0] - a[0], b[1] - a[1], b[2] - a[2]);
					dist[j] = total;
				}
				for (u32 j = 0; j < n; ++j)
				{
					float t = total > 1e-6f ? dist[j] / total : (float)j / (float)(n - 1);
					widths.push_back(o.size * d.trail_width * lut(e.twidth_lut, t));
					float g[4];
					lut4(e.tcolor_lut, t, g);
					float fade = 1.0f - sat(ages[j] / d.trail_lifetime);
					float c[4] = { g[0], g[1], g[2], g[3] * fade };
					if (d.trail_inherit_color) for (int q = 0; q < 4; ++q) c[q] *= pc[q];
					cols.push_back(pack_rgba(c[0], c[1], c[2], c[3]));
					us.push_back(d.trail_texture_mode == TEXMODE_TILE ? dist[j] : t);
				}
				emit_strip(out, pts.data(), n, widths.data(), cols.data(), us.data(), view);
			}
		}

		void gather_beam(const Beam& b, const ViewInfo& view, DrawList& out)
		{
			const BeamDesc& d = b.d;
			float dir[3] = { b.to[0] - b.from[0], b.to[1] - b.from[1], b.to[2] - b.from[2] };
			float total = len3(dir[0], dir[1], dir[2]);
			if (total < 1e-5f) return;
			// visible span along the beam (travelling streak for tracers)
			float s0 = 0.0f, s1 = total;
			if (d.speed > 0.0f)
			{
				float headp = b.age * d.speed;
				s1 = std::min(total, headp);
				s0 = std::max(0.0f, headp - std::max(0.01f, d.length));
				if (s0 >= total || s1 <= s0) return;
			}
			float alpha = 1.0f;
			if (d.fade_in > 0.0f) alpha *= sat(b.age / d.fade_in);
			if (d.duration > 0.0f && d.fade_out > 0.0f) alpha *= sat((d.duration - b.age) / d.fade_out);
			if (alpha <= 0.0f) return;
			const u32 seg = d.segments;
			const u32 n = seg + 1;
			std::vector<float> pts(n * 3), widths(n), us(n);
			std::vector<u32> cols(n);
			float ud[3] = { dir[0] / total, dir[1] / total, dir[2] / total };
			// jitter basis perpendicular to the beam
			float ax[3], ay[3];
			float ref[3] = { std::fabs(ud[1]) < 0.9f ? 0.0f : 1.0f, std::fabs(ud[1]) < 0.9f ? 1.0f : 0.0f, 0.0f };
			cross3(ud, ref, ax); normalize3(ax);
			cross3(ud, ax, ay); normalize3(ay);
			for (u32 j = 0; j < n; ++j)
			{
				float f = (float)j / (float)seg;              // along the visible span
				float s = lerpf(s0, s1, f);
				float t = s / total;                           // along the whole beam (curves)
				float p[3] = { b.from[0] + ud[0] * s, b.from[1] + ud[1] * s, b.from[2] + ud[2] * s };
				if (d.noise > 0.0f && j > 0 && j < seg)
				{
					float tt = b.age * d.noise_speed;
					float env = std::sin(PI * f);                  // pinned ends
					float nxv = value_noise(s * d.noise_frequency, tt, 0.5f, 101u) * d.noise * env;
					float nyv = value_noise(s * d.noise_frequency, tt, 7.5f, 202u) * d.noise * env;
					for (int q = 0; q < 3; ++q) p[q] += ax[q] * nxv + ay[q] * nyv;
				}
				std::memcpy(&pts[j * 3], p, 12);
				widths[j] = d.width * eval_curve(d.width_curve, t);
				float g[4];
				eval_gradient(d.gradient, t, g);
				cols[j] = pack_rgba(d.color[0] * g[0], d.color[1] * g[1], d.color[2] * g[2], d.color[3] * g[3] * alpha);
				us[j] = (d.uv_tiling > 0.0f ? s * d.uv_tiling : t) - b.age * d.uv_scroll;
			}
			emit_strip(out, pts.data(), n, widths.data(), cols.data(), us.data(), view);
		}

		float view_depth(const ViewInfo& v, const float* p)
		{
			return (p[0] - v.eye[0]) * v.forward[0] + (p[1] - v.eye[1]) * v.forward[1] + (p[2] - v.eye[2]) * v.forward[2];
		}
	}

	// =====================================================================================================
	// Public API
	// =====================================================================================================
	bool init()
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		ensure_init();
		return true;
	}

	void shutdown()
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		g_pool.stop();
		g_emitters.clear(); g_beams.clear(); g_free_emitters.clear(); g_free_beams.clear();
		g_depth = DepthSnap{};
		g_texture_cache.clear();
		g_callback = nullptr;
		g_initialized = false;
	}

	bool initialized() { return g_initialized; }

	void set_worker_threads(u32 count)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		g_workers_wanted = std::min(count, 64u);
		if (g_initialized) g_pool.start(g_workers_wanted);
	}

	u32 worker_threads() { return g_pool.workers(); }

	u32 create_world()
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		return g_next_world++;
	}

	void clear(u32 world)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		for (u32 s = 0; s < g_emitters.size(); ++s)
			if (g_emitters[s].used && (world == ALL_WORLDS || g_emitters[s].world == world)) destroy_emitter_slot(s);
		for (u32 s = 0; s < g_beams.size(); ++s)
			if (g_beams[s].used && (world == ALL_WORLDS || g_beams[s].world == world)) destroy_beam_slot(s);
		if (world == 0 || world == ALL_WORLDS) g_depth.valid = false;
	}

	void update(u32 world, float dt)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		ensure_init();
		update_world(world, dt);
	}

	void set_frame_callback(FrameCallback cb)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		g_callback = cb;
	}

	void set_auto_update(bool enabled) { g_auto_update = enabled; }

	void begin_frame()
	{
		auto now = std::chrono::steady_clock::now();
		float dt = g_have_last_frame ? std::chrono::duration<float>(now - g_last_frame).count() : (1.0f / 60.0f);
		g_last_frame = now;
		g_have_last_frame = true;
		if (!std::isfinite(dt) || dt < 0.0f) dt = 0.0f;
		if (dt > 0.1f) dt = 0.1f;
		FrameCallback cb;
		{
			std::lock_guard<std::recursive_mutex> lock(g_mutex);
			cb = g_callback;
		}
		if (cb) { cb(dt); return; }   // invoked without the lock: it calls back into this module
		if (g_auto_update && g_initialized) update(0, dt);
	}

	// ---- emitters -------------------------------------------------------------------------------------------
	u32 create_emitter(const EmitterDesc& desc, u32 world)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		ensure_init();
		u32 slot;
		if (!g_free_emitters.empty()) { slot = g_free_emitters.back(); g_free_emitters.pop_back(); }
		else
		{
			if (g_emitters.size() >= 0xFFFFu) return 0;
			slot = (u32)g_emitters.size();
			g_emitters.emplace_back();
		}
		Emitter& e = g_emitters[slot];
		u32 gen = e.gen;
		e = Emitter{};
		e.gen = gen;
		if ((e.gen & 0x7FFFu) == 0) e.gen += 1;   // generation 0 never appears in a handle
		e.used = true;
		e.world = world;
		e.d = desc;
		sanitize(e.d);
		bake(e);
		e.tmax = e.d.trails ? e.d.trail_max_points : 0;
		restart_state(e, true);
		return make_handle(slot, e.gen, false);
	}

	u32 create_emitter_json(const char* json, u32 world, std::string* error)
	{
		EmitterJson ej;
		if (!parse_emitter_json(json, ej, error)) return 0;
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (!ej.texture.empty()) ej.desc.texture = texture_from_path(ej.texture);
		if (!ej.trail_texture.empty()) ej.desc.trail_texture = texture_from_path(ej.trail_texture);
		return create_emitter(ej.desc, world);
	}

	bool set_emitter_desc(u32 h, const EmitterDesc& desc)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e) return false;
		EmitterDesc d = desc;
		sanitize(d);
		const bool relayout = d.trails != e->d.trails || d.trail_max_points != e->d.trail_max_points;
		const bool shrink = d.max_particles < e->count;
		const u32 old_seed = e->d.seed;
		e->d = d;
		bake(*e);
		if (shrink) e->count = d.max_particles;
		if (relayout) reset_trails_layout(*e);
		if (d.seed != old_seed && d.seed != 0) restart_state(*e, true);
		// burst indices past the new list must not fire stale counts
		for (u32 b = d.burst_count; b < MAX_BURSTS; ++b) e->burst_done[b] = 0;
		for (u32 i = 0; i < e->count; ++i) write_instance(*e, i);
		compute_bounds(*e);
		return true;
	}

	bool set_emitter_json(u32 h, const char* json, std::string* error)
	{
		EmitterJson ej;
		if (!parse_emitter_json(json, ej, error)) return false;
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e) return false;
		// textures: a path in the JSON wins, otherwise the texture the emitter already has stays
		ej.desc.texture = !ej.texture.empty() ? texture_from_path(ej.texture) : e->d.texture;
		ej.desc.trail_texture = !ej.trail_texture.empty() ? texture_from_path(ej.trail_texture) : e->d.trail_texture;
		ej.desc.layer = e->d.layer;   // the layer belongs to the owner (component / API), not the asset
		return set_emitter_desc(h, ej.desc);
	}

	bool get_emitter_desc(u32 h, EmitterDesc& out)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e) return false;
		out = e->d;
		return true;
	}

	void destroy_emitter(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) destroy_emitter_slot((u32)(e - g_emitters.data()));
	}

	bool emitter_valid(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		return get_emitter(h) != nullptr;
	}

	void set_transform(u32 h, const float* m)
	{
		if (!m) return;
		for (int i = 0; i < 16; ++i) if (!std::isfinite(m[i])) return;
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e) return;
		std::memcpy(e->m, m, sizeof(e->m));
		update_scale(*e);
		if (!e->posed) { std::memcpy(e->m_last, m, sizeof(e->m_last)); e->posed = true; }   // first pose: no motion
	}

	void reset_motion(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) { std::memcpy(e->m_last, e->m, sizeof(e->m)); e->vel_w[0] = e->vel_w[1] = e->vel_w[2] = 0.0f; }
	}

	void play(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e) return;
		if (e->paused) { e->paused = false; if (e->playing) return; }
		if (e->playing) return;
		restart_state(*e, e->spawned_total == 0 || e->d.seed == 0);
		e->playing = true;
		if (e->d.prewarm && e->d.looping)
		{
			// run one full cycle before the first frame (serially, this emitter only)
			const float step = 1.0f / 30.0f;
			int steps = (int)std::ceil(e->d.duration / step);
			steps = std::min(steps, 3600);
			Ctx ctx{ step, nullptr };
			for (int s = 0; s < steps; ++s)
			{
				pre_update(*e, step);
				e->sim_clock += e->step_dt;
				update_range(*e, 0, e->count, ctx);
				finish_emitter(*e);
			}
		}
	}

	void stop(u32 h, bool clear_particles)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e) return;
		e->playing = false;
		e->paused = false;
		if (clear_particles) { e->count = 0; compute_bounds(*e); }
	}

	void pause(u32 h, bool paused)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) e->paused = paused;
	}

	void restart(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e) return;
		e->count = 0;
		e->playing = false;
		e->paused = false;
		std::memcpy(e->m_last, e->m, sizeof(e->m));
		restart_state(*e, true);
		play(h);
	}

	void burst(u32 h, u32 count)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e) return;
		count = std::min(count, e->d.max_particles);
		for (u32 k = 0; k < count; ++k) spawn_one(*e, 1.0f, 0.0f);
		compute_bounds(*e);
	}

	void clear_particles(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) { e->count = 0; compute_bounds(*e); }
	}

	void set_seed(u32 h, u32 seed)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) { e->d.seed = seed; restart_state(*e, true); }
	}

	void set_layer(u32 h, u32 layer)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) e->d.layer = layer ? 1u : 0u;
	}

	void set_visible(u32 h, bool visible)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) e->visible = visible;
	}

	void set_simulation_speed(u32 h, float speed)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) e->d.simulation_speed = std::isfinite(speed) ? clampf(speed, 0.0f, 100.0f) : 1.0f;
	}

	void set_texture(u32 h, u64 texture_id)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) e->d.texture = texture_id;
	}

	void set_trail_texture(u32 h, u64 texture_id)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) e->d.trail_texture = texture_id;
	}

	void set_auto_destroy(u32 h, bool enabled)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Emitter* e = get_emitter(h)) e->auto_destroy = enabled;
	}

	u32 alive_count(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		return e ? e->count : 0;
	}

	bool is_playing(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		return e && e->playing && !e->paused;
	}

	bool is_finished(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		return !e || (!e->playing && e->count == 0);
	}

	float emitter_time(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		return e ? e->time : 0.0f;
	}

	u32 get_particle_positions(u32 h, float* out, u32 max_count)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Emitter* e = get_emitter(h);
		if (!e || !out) return 0;
		u32 n = std::min(max_count, e->count);
		for (u32 i = 0; i < n; ++i) std::memcpy(out + i * 3, e->inst[i].pos, 12);
		return n;
	}

	u32 emitter_count(u32 world)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		u32 n = 0;
		for (auto& e : g_emitters) if (e.used && (world == ALL_WORLDS || e.world == world)) ++n;
		return n;
	}

	// ---- beams ----------------------------------------------------------------------------------------------
	u32 create_beam(const BeamDesc& desc, u32 world)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		ensure_init();
		u32 slot;
		if (!g_free_beams.empty()) { slot = g_free_beams.back(); g_free_beams.pop_back(); }
		else
		{
			if (g_beams.size() >= 0xFFFFu) return 0;
			slot = (u32)g_beams.size();
			g_beams.emplace_back();
		}
		Beam& b = g_beams[slot];
		u32 gen = b.gen;
		b = Beam{};
		b.gen = gen;
		if ((b.gen & 0x7FFFu) == 0) b.gen += 1;
		b.used = true;
		b.world = world;
		b.d = desc;
		sanitize(b.d);
		return make_handle(slot, b.gen, true);
	}

	u32 create_beam_json(const char* json, u32 world, std::string* error)
	{
		BeamJson bj;
		if (!parse_beam_json(json, bj, error)) return 0;
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (!bj.texture.empty()) bj.desc.texture = texture_from_path(bj.texture);
		return create_beam(bj.desc, world);
	}

	bool set_beam_desc(u32 h, const BeamDesc& desc)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		Beam* b = get_beam(h);
		if (!b) return false;
		b->d = desc;
		sanitize(b->d);
		return true;
	}

	void set_beam_points(u32 h, const float* from3, const float* to3)
	{
		if (!from3 || !to3 || !finite3(from3) || !finite3(to3)) return;
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Beam* b = get_beam(h)) { std::memcpy(b->from, from3, 12); std::memcpy(b->to, to3, 12); }
	}

	void set_beam_texture(u32 h, u64 texture_id)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Beam* b = get_beam(h)) b->d.texture = texture_id;
	}

	void destroy_beam(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		if (Beam* b = get_beam(h)) destroy_beam_slot((u32)(b - g_beams.data()));
	}

	bool beam_valid(u32 h)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		return get_beam(h) != nullptr;
	}

	u32 beam_count(u32 world)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		u32 n = 0;
		for (auto& b : g_beams) if (b.used && (world == ALL_WORLDS || b.world == world)) ++n;
		return n;
	}

	// ---- textures -------------------------------------------------------------------------------------------
	u64 texture_from_path(const std::string& path)
	{
		if (path.empty()) return NO_TEXTURE;
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		auto it = g_texture_cache.find(path);
		if (it != g_texture_cache.end()) return it->second;
		u64 id = NO_TEXTURE;
		auto& reg = graphics::ResourceRegistry::instance();
		if (reg.is_initialized())
		{
			id::id_type t = reg.import_texture(path);
			if (id::is_valid(t)) id = (u64)t;
		}
		if (id == NO_TEXTURE) platform::debug_output(("[particles] texture not loaded: " + path + "\n").c_str());
		else g_texture_cache[path] = id;   // failures are retried next time (the device may come up later)
		return id;
	}

	void on_renderer_shutdown()
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		g_texture_cache.clear();
		for (auto& e : g_emitters) if (e.used) { e.d.texture = NO_TEXTURE; e.d.trail_texture = NO_TEXTURE; }
		for (auto& b : g_beams) if (b.used) b.d.texture = NO_TEXTURE;
		g_depth.valid = false;
	}

	// ---- rendering ------------------------------------------------------------------------------------------
	bool gather(u32 world, const ViewInfo& view, DrawList& out)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		auto t0 = std::chrono::steady_clock::now();
		out.clear();
		const Frustum fr = extract_frustum(view.view_proj);
		std::vector<u32> order;
		for (Emitter& e : g_emitters)
		{
			if (!e.used || e.world != world || !e.visible || e.count == 0) continue;
			if (!aabb_visible(fr, e.bmin, e.bmax)) continue;
			const EmitterDesc& d = e.d;
			float center[3] = { (e.bmin[0] + e.bmax[0]) * 0.5f, (e.bmin[1] + e.bmax[1]) * 0.5f, (e.bmin[2] + e.bmax[2]) * 0.5f };
			float depth = view_depth(view, center);

			DrawBatch b{};
			b.kind = 0; b.layer = d.layer; b.blend = d.blend; b.render_mode = d.render_mode; b.texture = d.texture;
			b.tiles_x = d.tiles_x; b.tiles_y = d.tiles_y; b.frame_blend = d.frame_blend; b.lit = d.lit;
			b.soft_distance = d.soft_distance; b.emissive = d.emissive; b.sort_depth = depth;
			b.first = (u32)out.instances.size();
			b.count = e.count;
			const bool sort = d.sort_mode == SORT_DEPTH || (d.sort_mode == SORT_AUTO && d.blend != BLEND_ADDITIVE);
			if (sort && e.count > 1)
			{
				sort_back_to_front(e, view, order);
				out.instances.resize(b.first + e.count);
				GpuParticle* dst = out.instances.data() + b.first;
				for (u32 i = 0; i < e.count; ++i) dst[i] = e.inst[order[i]];
			}
			else out.instances.insert(out.instances.end(), e.inst.begin(), e.inst.begin() + e.count);
			out.batches.push_back(b);

			if (d.trails && e.tmax >= 2)
			{
				u32 first_index = (u32)out.ribbon_indices.size();
				gather_trails(e, view, out);
				u32 n = (u32)out.ribbon_indices.size() - first_index;
				if (n > 0)
				{
					DrawBatch t = b;
					t.kind = 1; t.first = first_index; t.count = n; t.texture = d.trail_texture;
					t.render_mode = 0; t.tiles_x = t.tiles_y = 1; t.frame_blend = 0;
					t.texture_mode = d.trail_texture_mode;
					t.sort_depth = depth + 1e-3f;   // trails behind their particles
					out.batches.push_back(t);
				}
			}
		}
		for (Beam& bm : g_beams)
		{
			if (!bm.used || bm.world != world) continue;
			float mn[3], mx[3];
			float w = bm.d.width + bm.d.noise * 2.0f;
			for (int k = 0; k < 3; ++k) { mn[k] = std::min(bm.from[k], bm.to[k]) - w; mx[k] = std::max(bm.from[k], bm.to[k]) + w; }
			if (!aabb_visible(fr, mn, mx)) continue;
			u32 first_index = (u32)out.ribbon_indices.size();
			gather_beam(bm, view, out);
			u32 n = (u32)out.ribbon_indices.size() - first_index;
			if (n == 0) continue;
			float center[3] = { (bm.from[0] + bm.to[0]) * 0.5f, (bm.from[1] + bm.to[1]) * 0.5f, (bm.from[2] + bm.to[2]) * 0.5f };
			DrawBatch b{};
			b.kind = 1; b.layer = bm.d.layer; b.blend = bm.d.blend; b.texture = bm.d.texture;
			b.tiles_x = b.tiles_y = 1; b.soft_distance = bm.d.soft_distance; b.emissive = bm.d.emissive;
			b.first = first_index; b.count = n; b.sort_depth = view_depth(view, center);
			b.texture_mode = bm.d.uv_tiling > 0.0f ? TEXMODE_TILE : TEXMODE_STRETCH;
			out.batches.push_back(b);
		}
		std::stable_sort(out.batches.begin(), out.batches.end(), [](const DrawBatch& a, const DrawBatch& b) { return a.sort_depth > b.sort_depth; });
		g_gather_ms = std::chrono::duration<float, std::milli>(std::chrono::steady_clock::now() - t0).count();
		g_drawn = (u32)out.instances.size();
		g_batches = (u32)out.batches.size();
		g_ribbon_verts = (u32)out.ribbon_vertices.size();
		return !out.batches.empty();
	}

	bool wants_depth_snapshot()
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		for (auto& e : g_emitters)
			if (e.used && e.world == 0 && e.d.collision && e.d.layer == 0 && (e.count > 0 || e.playing)) return true;
		return false;
	}

	void submit_depth_snapshot(const float* z, u32 w, u32 h, const DepthView& view)
	{
		if (!z || w == 0 || h == 0) return;
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		g_depth.z.assign(z, z + (size_t)w * h);
		g_depth.w = w; g_depth.h = h;
		g_depth.v = view;
		g_depth.valid = view.tan_half_x > 0.0f && view.tan_half_y > 0.0f;
		++g_depth_snapshots;
	}

	// ---- stats ----------------------------------------------------------------------------------------------
	void get_stats(u32 world, Stats& out)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		out = Stats{};
		for (auto& e : g_emitters)
		{
			if (!e.used || (world != ALL_WORLDS && e.world != world)) continue;
			++out.emitters;
			out.alive_particles += e.count;
			out.capacity += e.d.max_particles;
			out.spawned_total += e.spawned_total;
		}
		for (auto& b : g_beams) if (b.used && (world == ALL_WORLDS || b.world == world)) ++out.beams;
		out.drawn_particles = g_drawn;
		out.draw_batches = g_batches;
		out.ribbon_vertices = g_ribbon_verts;
		out.worker_threads = g_pool.workers();
		out.simulate_ms = g_sim_ms;
		out.gather_ms = g_gather_ms;
		out.depth_snapshots = g_depth_snapshots;
		out.renderer_draws = 1;   // every backend draws particles: SDL GPU (Metal / Vulkan) and DX12 since #117
	}

	void note_gather_time(float ms, u32 drawn, u32 batches, u32 ribbon_vertices)
	{
		std::lock_guard<std::recursive_mutex> lock(g_mutex);
		g_gather_ms = ms; g_drawn = drawn; g_batches = batches; g_ribbon_verts = ribbon_vertices;
	}
}
