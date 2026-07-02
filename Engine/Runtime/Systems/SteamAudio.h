#pragma once

#include "../../Common/CommonHeaders.h"

// Steam Audio (Valve phonon, v4.8.1) integration (issue #21): HRTF binaural rendering + ray-traced
// occlusion, layered on top of the v1 miniaudio spatializer (#9). This is the "audio v2" path — it is
// OPT-IN (a project master switch AND a per-AudioSource flag) and degrades safely to v1 at every step:
// phonon.dll missing / init fails / master off / per-voice off -> the voice uses the v1 spatializer
// unchanged. phonon.dll is loaded DYNAMICALLY (LoadLibrary), so it is a true optional runtime dependency:
// the engine builds and boots without it.
//
// Design: docs/wiki/Design-Steam-Audio-Integration.md.

typedef void ma_node;   // matches miniaudio's `typedef void ma_node;` (identical redefinition is allowed)

namespace vortex::runtime::audio::steam {

	// One opaque per-voice Steam Audio object (its IPLSource + IPLBinauralEffect + the ma_node spliced into
	// the graph between the voice's ma_sound and its splitter/bus).
	struct source;

	// Global lifecycle. initialize() loads phonon.dll, creates the context + shared HRTF; returns false and
	// leaves the module disabled (is_available() == false) if the DLL is absent or any step fails.
	bool initialize(u32 sample_rate, u32 frame_size);
	void shutdown();
	bool is_available();

	// Project master switch. Off (default) => is_available() stays false even if the DLL loaded, so nothing
	// initializes the per-voice path. Set BEFORE initialize().
	void set_enabled(bool enabled);
	bool is_enabled();

	// Occlusion geometry: flat world-space triangle soup from the collision system, published once per scene.
	// verts = xyz*vertex_count floats; indices = 3*triangle_count ints into verts. Rebuilds the IPLScene.
	void set_geometry(const float* verts, u32 vertex_count, const s32* indices, u32 index_count);

	// Listener transform in GAME space (left-handed, +z forward) — mirrored to Steam Audio's RH space inside.
	void set_listener(f32 px, f32 py, f32 pz, f32 fx, f32 fy, f32 fz, f32 ux, f32 uy, f32 uz);

	// Per-voice source lifecycle. create_source returns nullptr if the module is unavailable.
	source* create_source();
	void destroy_source(source* s);

	// The ma_node to splice between the voice's ma_sound and its downstream node (splitter/bus). Never null
	// for a valid source. onProcess applies HRTF binaural + the latest occlusion gain.
	ma_node* node_of(source* s);

	// Per-frame source params (called from voices_update, game space). occlusion toggles the ray simulation
	// for this source; when false the occlusion gain is pinned to 1.0 (HRTF direction only).
	void set_source(source* s, f32 px, f32 py, f32 pz, bool occlusion);

	// Runs the occlusion ray simulation for all active sources. Called from a dedicated simulation thread
	// (NOT the audio thread), which this module owns and drives internally after initialize().
}
