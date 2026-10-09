#pragma once

// ============================================================================
// Volumetric fog (#119) — the backend-neutral part: the parameters both renderers' passes consume.
//
// The pass ray-marches every pixel of a half-resolution target from the camera to the scene depth (or the max
// distance in the sky), accumulating the light scattered towards the camera — the fog colour (ambient), the sun
// through the cascaded shadow maps (shafts), every point and spot light through their shadow maps (visible cones
// and glows) with a Henyey-Greenstein phase — against the transmittance of a density that follows the scene fog's
// height profile and a wind-animated 3D noise. The result composites over the scene as `scene * T + S` before the
// particles. Off = the untouched analytic fog path.
// ============================================================================
#include "../../Common/CommonHeaders.h"

namespace vortex::graphics::volumetrics
{
	struct Params
	{
		bool enabled{ false };
		float density{ 0.5f };         // scattering density (× the scene fog's height profile)
		float anisotropy{ 0.55f };     // Henyey-Greenstein g: 0 = isotropic, towards 1 = forward (looking into lights)
		float max_distance{ 60.0f };   // metres marched
		float noise_strength{ 0.5f };  // 0 = uniform, 1 = fully patchy
		float noise_scale{ 6.0f };     // metres per noise cell
		float noise_speed{ 0.35f };    // cells per second of wind
		float sun{ 1.0f };             // directional light contribution (shafts)
		float lights{ 1.0f };          // point / spot light contribution (cones, glows)
		float ambient{ 0.35f };        // fog-colour in-scatter (× the scene ambient strength)
		u32 steps{ 24 };               // ray-march steps (8 .. 48)
		bool shadows{ true };          // march through the shadow maps (no shafts through walls)
	};
}
