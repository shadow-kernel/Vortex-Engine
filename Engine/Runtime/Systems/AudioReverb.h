#pragma once

#include "../../Common/CommonHeaders.h"

// Global algorithmic reverb (issue #15): ONE Freeverb-style node (parallel combs +
// serial allpasses, public-domain tunings) on a send bus. Voices feed it through a
// per-voice splitter's second output whose gain = AudioSource.reverbZoneMix x the
// listener's blended zone weight (computed by the C# zone service). The node
// outputs WET ONLY — the dry path stays on the normal mixer buses.
namespace vortex::runtime::audio {

	// Lifetime driven by the mixer (after the bus tree exists).
	void reverb_initialize();
	void reverb_shutdown();

	// Blended zone parameters, pushed by the zone service whenever they change.
	// decay_seconds: RT60-ish tail length; wet_level 0..1; predelay_ms 0..200.
	void reverb_set_params(f32 decay_seconds, f32 wet_level, f32 predelay_ms);

	// The reverb node (ma_node* — typedef'd void in miniaudio) for the voice layer
	// to attach splitter sends to. nullptr while the reverb isn't up.
	void* reverb_node();
}
