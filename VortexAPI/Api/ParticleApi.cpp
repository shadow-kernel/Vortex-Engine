#include "../ApiCommon.h"
#include "../../Engine/Graphics/Particles/ParticleSystem.h"

#include <algorithm>
#include <cstring>
#include <string>
#include <type_traits>
#include <vector>

// ============================================================================================
// VFX interop (epic #116) — the extern "C" surface over Engine/Graphics/Particles/ParticleSystem.{h,cpp}.
//
// The signatures below ARE the native ABI contract shared with the managed side
// (Editor/DllWrapper/VortexAPI.Particles.cs + Editor/Core/Services/Particles/ParticleService.cs); see
// Engine/Graphics/Particles/README.md. Thin pass-throughs: every function forwards to vortex::particles.
//
// Conventions: handles u32 (0 = invalid; emitters and beams have separate handle spaces), worlds u32
// (0 = the scene, create more with ParticleCreateWorld for previews), world matrices = 16 floats row-major
// (row 3 = translation, the SubmitRenderItem layout), texture ids = engine texture ids (u64, ~0 = none),
// descriptors as JSON (the .vfx schema) or as ParticleEmitterDesc / ParticleBeamDesc structs.
// Backend: both renderers draw particles — SDL GPU (Metal / Vulkan) and, since #117, DirectX 12 (ParticleStats.rendererDraws == 1).
// ============================================================================================

namespace pfx = vortex::particles;

typedef pfx::EmitterDesc ParticleEmitterDesc;   // plain C-compatible struct (see ParticleSystem.h)
typedef pfx::BeamDesc ParticleBeamDesc;
static_assert(std::is_standard_layout_v<ParticleEmitterDesc> && std::is_trivially_copyable_v<ParticleEmitterDesc>);
static_assert(std::is_standard_layout_v<ParticleBeamDesc> && std::is_trivially_copyable_v<ParticleBeamDesc>);

typedef struct ParticleStats
{
	int32_t emitters, beams, aliveParticles, capacity;
	int32_t drawnParticles, drawBatches, ribbonVertices, workerThreads;
	float simulateMs, gatherMs;
	int32_t depthSnapshots, rendererDraws;
	uint64_t spawnedTotal;
} ParticleStats;
static_assert(sizeof(ParticleStats) == 56, "ParticleStats layout (VortexAPI.Particles.cs)");

typedef void (*ParticleFrameCallback)(float dt);

namespace
{
	std::string g_last_error;
	void set_error(const std::string& e) { g_last_error = e; }
}

// ---- lifecycle ----

EDITOR_INTERFACE int32_t  ParticleInit(void)                        { return pfx::init() ? 1 : 0; }
EDITOR_INTERFACE void     ParticleShutdown(void)                    { pfx::shutdown(); }
EDITOR_INTERFACE void     ParticleSetWorkerThreads(int32_t count)   { pfx::set_worker_threads(count < 0 ? 0u : (uint32_t)count); }
EDITOR_INTERFACE uint32_t ParticleCreateWorld(void)                 { return pfx::create_world(); }
EDITOR_INTERFACE void     ParticleClear(uint32_t world)             { pfx::clear(world); }           // 0xFFFFFFFF = every world
EDITOR_INTERFACE void     ParticleUpdate(uint32_t world, float dt)  { pfx::update(world, dt); }     // one step (dt clamped to 0.1 s)
// Called at the start of every main-surface frame with the real frame time; while one is registered the
// automatic world-0 update is off (the callback drives ParticleUpdate itself). null = unregister.
EDITOR_INTERFACE void     ParticleSetFrameCallback(ParticleFrameCallback cb) { pfx::set_frame_callback(cb); }
EDITOR_INTERFACE void     ParticleSetAutoUpdate(int32_t enabled)    { pfx::set_auto_update(enabled != 0); }

// The next RenderToSecondaryTarget draws the particles of this world (one-shot; -1 = none, the default for
// offscreen renders — thumbnails and previews stay particle-free unless they ask). The scene view always draws world 0.
EDITOR_INTERFACE void ParticleSetNextTargetWorld(int32_t world)
{
	graphics::Renderer::instance().particles().set_next_target_world(world);   // SDL GPU and DX12 (#117)
}

// Last JSON / creation error (UTF-8, truncated to size). Returns the full length.
EDITOR_INTERFACE int32_t ParticleGetLastError(char* buffer, int32_t size)
{
	if (buffer && size > 0)
	{
		size_t n = std::min((size_t)(size - 1), g_last_error.size());
		std::memcpy(buffer, g_last_error.data(), n);
		buffer[n] = 0;
	}
	return (int32_t)g_last_error.size();
}

// ---- emitters ----

EDITOR_INTERFACE void ParticleGetDefaultEmitterDesc(ParticleEmitterDesc* out) { if (out) pfx::default_emitter_desc(*out); }

EDITOR_INTERFACE uint32_t ParticleCreateEmitter(const ParticleEmitterDesc* desc, uint32_t world)
{
	if (!desc) { set_error("null descriptor"); return 0; }
	return pfx::create_emitter(*desc, world);
}

// One emitter object of the .vfx schema ({"rate":..., "shape":{...}, "render":{...}, ...}).
EDITOR_INTERFACE uint32_t ParticleCreateEmitterJson(const char* json, uint32_t world)
{
	std::string err;
	uint32_t h = pfx::create_emitter_json(json, world, &err);
	if (!h) set_error(err.empty() ? "emitter creation failed" : err);
	return h;
}

// A whole effect ({"emitters":[...]}): creates every enabled emitter, writes up to maxHandles handles and returns
// how many were created (-1 = parse error, see ParticleGetLastError). A "beam" section is not instanced here
// (it needs endpoints — ParticleCreateBeamJson accepts the same file).
EDITOR_INTERFACE int32_t ParticleCreateEffectJson(const char* json, uint32_t world, uint32_t* outHandles, int32_t maxHandles)
{
	std::vector<pfx::EmitterJson> emitters;
	std::string err;
	if (!pfx::parse_effect_json(json, emitters, nullptr, nullptr, &err)) { set_error(err); return -1; }
	int32_t n = 0;
	for (auto& e : emitters)
	{
		if (!e.enabled) continue;
		if (outHandles && n >= maxHandles) break;
		if (!e.texture.empty()) e.desc.texture = pfx::texture_from_path(e.texture);
		if (!e.trail_texture.empty()) e.desc.trail_texture = pfx::texture_from_path(e.trail_texture);
		uint32_t h = pfx::create_emitter(e.desc, world);
		if (!h) continue;
		if (outHandles) outHandles[n] = h;
		++n;
	}
	return n;
}

// Live edit (keeps the particles): the same JSON as ParticleCreateEmitterJson. Returns 1 on success.
EDITOR_INTERFACE int32_t ParticleSetEmitterJson(uint32_t h, const char* json)
{
	std::string err;
	bool ok = pfx::set_emitter_json(h, json, &err);
	if (!ok && !err.empty()) set_error(err);
	return ok ? 1 : 0;
}
EDITOR_INTERFACE int32_t ParticleSetEmitterDesc(uint32_t h, const ParticleEmitterDesc* desc) { return desc && pfx::set_emitter_desc(h, *desc) ? 1 : 0; }
EDITOR_INTERFACE int32_t ParticleGetEmitterDesc(uint32_t h, ParticleEmitterDesc* out)        { return out && pfx::get_emitter_desc(h, *out) ? 1 : 0; }
EDITOR_INTERFACE void    ParticleDestroyEmitter(uint32_t h)                                  { pfx::destroy_emitter(h); }
EDITOR_INTERFACE int32_t ParticleEmitterValid(uint32_t h)                                    { return pfx::emitter_valid(h) ? 1 : 0; }
EDITOR_INTERFACE void    ParticleSetTransform(uint32_t h, const float* world4x4)             { pfx::set_transform(h, world4x4); }
EDITOR_INTERFACE void    ParticleResetMotion(uint32_t h)                                     { pfx::reset_motion(h); }   // after a teleport
EDITOR_INTERFACE void    ParticlePlay(uint32_t h)                                            { pfx::play(h); }
EDITOR_INTERFACE void    ParticleStop(uint32_t h, int32_t clearParticles)                    { pfx::stop(h, clearParticles != 0); }
EDITOR_INTERFACE void    ParticlePause(uint32_t h, int32_t paused)                           { pfx::pause(h, paused != 0); }
EDITOR_INTERFACE void    ParticleRestart(uint32_t h)                                         { pfx::restart(h); }
EDITOR_INTERFACE void    ParticleBurst(uint32_t h, int32_t count)                            { if (count > 0) pfx::burst(h, (uint32_t)count); }
EDITOR_INTERFACE void    ParticleClearParticles(uint32_t h)                                  { pfx::clear_particles(h); }
EDITOR_INTERFACE void    ParticleSetSeed(uint32_t h, uint32_t seed)                          { pfx::set_seed(h, seed); }
EDITOR_INTERFACE void    ParticleSetLayer(uint32_t h, int32_t layer)                         { pfx::set_layer(h, layer > 0 ? 1u : 0u); }
EDITOR_INTERFACE void    ParticleSetVisible(uint32_t h, int32_t visible)                     { pfx::set_visible(h, visible != 0); }
EDITOR_INTERFACE void    ParticleSetSimulationSpeed(uint32_t h, float speed)                 { pfx::set_simulation_speed(h, speed); }
EDITOR_INTERFACE void    ParticleSetTexture(uint32_t h, uint64_t textureId)                  { pfx::set_texture(h, textureId); }
EDITOR_INTERFACE void    ParticleSetTrailTexture(uint32_t h, uint64_t textureId)             { pfx::set_trail_texture(h, textureId); }
EDITOR_INTERFACE void    ParticleSetAutoDestroy(uint32_t h, int32_t enabled)                 { pfx::set_auto_destroy(h, enabled != 0); }
EDITOR_INTERFACE int32_t ParticleGetAliveCount(uint32_t h)                                   { return (int32_t)pfx::alive_count(h); }
EDITOR_INTERFACE int32_t ParticleIsPlaying(uint32_t h)                                       { return pfx::is_playing(h) ? 1 : 0; }
EDITOR_INTERFACE int32_t ParticleIsFinished(uint32_t h)                                      { return pfx::is_finished(h) ? 1 : 0; }
EDITOR_INTERFACE float   ParticleGetTime(uint32_t h)                                         { return pfx::emitter_time(h); }
EDITOR_INTERFACE int32_t ParticleGetPositions(uint32_t h, float* outXyz, int32_t maxCount)   { return maxCount > 0 ? (int32_t)pfx::get_particle_positions(h, outXyz, (uint32_t)maxCount) : 0; }
EDITOR_INTERFACE int32_t ParticleGetEmitterCount(uint32_t world)                             { return (int32_t)pfx::emitter_count(world); }

// ---- beams (tracers, lasers) ----

EDITOR_INTERFACE void ParticleGetDefaultBeamDesc(ParticleBeamDesc* out) { if (out) pfx::default_beam_desc(*out); }

EDITOR_INTERFACE uint32_t ParticleCreateBeam(const ParticleBeamDesc* desc, uint32_t world)
{
	if (!desc) { set_error("null descriptor"); return 0; }
	return pfx::create_beam(*desc, world);
}

// A beam object, or a whole .vfx with a "beam" section.
EDITOR_INTERFACE uint32_t ParticleCreateBeamJson(const char* json, uint32_t world)
{
	std::string err;
	uint32_t h = pfx::create_beam_json(json, world, &err);
	if (!h) set_error(err.empty() ? "beam creation failed" : err);
	return h;
}

EDITOR_INTERFACE int32_t ParticleSetBeamDesc(uint32_t h, const ParticleBeamDesc* desc)            { return desc && pfx::set_beam_desc(h, *desc) ? 1 : 0; }
EDITOR_INTERFACE void    ParticleSetBeamPoints(uint32_t h, const float* from3, const float* to3) { pfx::set_beam_points(h, from3, to3); }
EDITOR_INTERFACE void    ParticleSetBeamTexture(uint32_t h, uint64_t textureId)                  { pfx::set_beam_texture(h, textureId); }
EDITOR_INTERFACE void    ParticleDestroyBeam(uint32_t h)                                         { pfx::destroy_beam(h); }
EDITOR_INTERFACE int32_t ParticleBeamValid(uint32_t h)                                           { return pfx::beam_valid(h) ? 1 : 0; }
EDITOR_INTERFACE int32_t ParticleGetBeamCount(uint32_t world)                                    { return (int32_t)pfx::beam_count(world); }

// ---- stats ----

EDITOR_INTERFACE void ParticleGetStats(uint32_t world, ParticleStats* out)
{
	if (!out) return;
	pfx::Stats s{};
	pfx::get_stats(world, s);
	out->emitters = (int32_t)s.emitters; out->beams = (int32_t)s.beams;
	out->aliveParticles = (int32_t)s.alive_particles; out->capacity = (int32_t)s.capacity;
	out->drawnParticles = (int32_t)s.drawn_particles; out->drawBatches = (int32_t)s.draw_batches;
	out->ribbonVertices = (int32_t)s.ribbon_vertices; out->workerThreads = (int32_t)s.worker_threads;
	out->simulateMs = s.simulate_ms; out->gatherMs = s.gather_ms;
	out->depthSnapshots = (int32_t)s.depth_snapshots; out->rendererDraws = (int32_t)s.renderer_draws;
	out->spawnedTotal = s.spawned_total;
}
