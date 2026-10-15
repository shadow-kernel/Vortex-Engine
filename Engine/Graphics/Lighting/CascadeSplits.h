#pragma once

// ============================================================================
// The sun's cascade split scheme (#24, #466) — the backend-neutral part both renderers (DX12, SDL GPU) share.
//
// The three cascades slice the view frustum between NEAR_Z and the light's shadow distance with the practical scheme: a
// blend of the logarithmic split (resolution near the camera) and the uniform one (no absurdly thin far cascades),
// weighted by lambda. Up to the classic 80 m the blend is the fixed 0.6 it always was. A longer distance (a road, a city,
// a flight) moves lambda towards the logarithmic split, so the first cascade grows only with the fourth root of the
// distance (11 m at 80 m, 16 m at 300 m, 21 m at 1 km) instead of in proportion to it: the shadows next to the camera
// keep their sharpness and the last cascade alone stretches out to the distance.
//
// Each cascade's ortho box also reaches back towards the light so casters outside the slice (a roof over the corridor,
// a tower off-screen) still land in its map. That pull-back used to be the whole shadow distance for every cascade —
// harmless at 80 m, but the shaders' depth bias is a fixed fraction of the box's depth range, so at 300 m the near
// cascade's bias would have grown three-fold (shadows detaching from their casters). It is the distance up to 80 m, as
// before, and beyond that the larger of 80 m and the cascade's own diameter.
// ============================================================================
#include <algorithm>
#include <cmath>

namespace vortex::graphics::lighting
{
	constexpr float CASCADE_NEAR_Z = 0.1f;            // the slicing starts here (the view's own near plane is the camera's)
	constexpr float CASCADE_CLASSIC_DISTANCE = 80.0f; // the default shadow distance: the scheme is the classic one up to it
	constexpr float CASCADE_CLASSIC_LAMBDA = 0.6f;
	constexpr float CASCADE_MAX_LAMBDA = 0.97f;

	// The practical split's far end of cascade `index` (0-based) of `count`, for the blend weight `lambda`.
	inline float practical_split(float near_z, float far_z, unsigned index, unsigned count, float lambda)
	{
		const float f = (float)(index + 1) / (float)count;
		const float log_split = near_z * std::pow(far_z / near_z, f);
		const float uni_split = near_z + (far_z - near_z) * f;
		return uni_split + lambda * (log_split - uni_split);
	}

	// The blend weight for a shadow distance: 0.6 up to 80 m; beyond it the weight that puts the first split at the 80 m
	// scheme's first split times (distance / 80)^(1/4).
	inline float cascade_lambda(float shadow_distance, unsigned count)
	{
		if (shadow_distance <= CASCADE_CLASSIC_DISTANCE || count < 2) return CASCADE_CLASSIC_LAMBDA;
		const float classic_first = practical_split(CASCADE_NEAR_Z, CASCADE_CLASSIC_DISTANCE, 0, count, CASCADE_CLASSIC_LAMBDA);
		const float want = classic_first * std::pow(shadow_distance / CASCADE_CLASSIC_DISTANCE, 0.25f);
		const float f = 1.0f / (float)count;
		const float log_split = CASCADE_NEAR_Z * std::pow(shadow_distance / CASCADE_NEAR_Z, f);
		const float uni_split = CASCADE_NEAR_Z + (shadow_distance - CASCADE_NEAR_Z) * f;
		if (uni_split - log_split < 1e-4f) return CASCADE_CLASSIC_LAMBDA;
		const float lambda = (uni_split - want) / (uni_split - log_split);
		return std::clamp(lambda, CASCADE_CLASSIC_LAMBDA, CASCADE_MAX_LAMBDA);
	}

	// The far end of every cascade (view distance, metres) for a shadow distance; the last one is the distance itself.
	inline void cascade_splits(float shadow_distance, unsigned count, float* out_far)
	{
		const float lambda = cascade_lambda(shadow_distance, count);
		for (unsigned c = 0; c < count; ++c)
			out_far[c] = c + 1 == count ? shadow_distance : practical_split(CASCADE_NEAR_Z, shadow_distance, c, count, lambda);
	}

	// How far a cascade's box reaches back towards the light beyond its own bounding sphere (radius in metres).
	inline float cascade_pull_back(float shadow_distance, float radius)
	{
		return (std::min)(shadow_distance, (std::max)(CASCADE_CLASSIC_DISTANCE, 2.0f * radius));
	}
}
