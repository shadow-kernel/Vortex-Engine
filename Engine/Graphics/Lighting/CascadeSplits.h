#pragma once

// ============================================================================
// The sun's cascades (#24, #466) — the backend-neutral part both renderers (DX12, SDL GPU) share.
//
// The three cascades slice the view frustum between NEAR_Z and the light's shadow distance with the practical scheme: a
// blend of the logarithmic split (resolution near the camera) and the uniform one (no absurdly thin far cascades),
// weighted by lambda. Up to the classic 80 m the blend is the fixed 0.6 it always was. A longer distance (a road, a city,
// a flight) moves lambda towards the logarithmic split, so the first cascade grows only with the fourth root of the
// distance (11 m at 80 m, 16 m at 300 m, 21 m at 1 km) instead of in proportion to it: the shadows next to the camera
// keep their sharpness and the last cascade alone stretches out to the distance.
//
// Every cascade's ortho box reaches back towards the light by the whole shadow distance, so casters outside the slice
// (a roof over the corridor, a tower 130 m away throwing its shadow over the camera) still land in its map.
//
// The depth bias. The shaders' classic bias is a fixed fraction of a box's depth range: bias_y x (1 + c) x
// (0.75 + 0.25 x softness), about 7 texels (Hard) or 19 texels (Soft) of the cascade at 80 m. Kept as it is, a longer
// distance multiplies it — the boxes get deeper and the texels wider — and the shadows of anything thinner than the bias
// (a crate, a fence, a person) vanish: 7 m of bias in the last cascade at 300 m (Soft), 23 m at 1 km. So beyond 80 m the
// bias follows the texel and the receiver's slope instead (cascade_bias below):
//     bias = classic x scale[c]  +  slope x texel x tan(the receiver's angle to the sun)
// The slope term is what keeps acne away: a tilted receiver's depth changes by texel x tan per texel of the filter's
// reach, so the bias it needs is exactly that (two texels of reach: the tap offset plus the bilinear footprint; the tan
// capped at 5, 79 degrees). A receiver facing the sun needs almost none of it, and keeps the shadows of thin casters. The
// constant part shrinks to one texel. Between 80 m and 160 m the bias blends from the classic one to this, so dragging
// the distance past 80 m changes nothing at once; up to 80 m scale is exactly 1 and slope 0 — the classic bias, bit for bit.
// ============================================================================
#include <algorithm>
#include <cmath>

namespace vortex::graphics::lighting
{
	constexpr float CASCADE_NEAR_Z = 0.1f;            // the slicing starts here (the view's own near plane is the camera's)
	constexpr float CASCADE_CLASSIC_DISTANCE = 80.0f; // the default shadow distance: the scheme is the classic one up to it
	constexpr float CASCADE_CLASSIC_LAMBDA = 0.6f;
	constexpr float CASCADE_MAX_LAMBDA = 0.97f;
	constexpr float CASCADE_BLEND_DISTANCE = 80.0f;   // the bias blends to the texel-relative one over 80 .. 160 m
	constexpr float CASCADE_DEFAULT_BIAS = 0.0008f;   // the shaders' bias_y at the Light's default ShadowBias (0.05 x 0.016)
	constexpr float CASCADE_SLOPE_TEXELS = 2.0f;      // the slope term's reach: the filter's tap offset + the bilinear footprint
	constexpr float CASCADE_FLOOR_TEXELS = 1.0f;      // the constant part beyond the blend

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

	// The bounding sphere of the view-frustum slice [near_z, far_z] for a view whose half-angle tangents are tan_half_x /
	// tan_half_y: the renderers' circumsphere of the slice's eight corners (its far corners are the farthest from the
	// slice's centre on the view axis), rounded up to 1/16 m as they do.
	inline float cascade_radius(float near_z, float far_z, float tan_half_x, float tan_half_y)
	{
		const float h = 0.5f * (far_z - near_z);
		const float r = std::sqrt(h * h + far_z * far_z * (tan_half_x * tan_half_x + tan_half_y * tan_half_y));
		return std::ceil(r * 16.0f) / 16.0f;
	}

	// The depth bias for a shadow distance (see the top of this file). `radius` holds the renderer's cascade radii (m),
	// bias_y and softness are the light's (the shaders' DirShadowParams y and w). out_scale[c] multiplies the classic bias
	// of cascade c; out_slope is the slope term's texels (times the light's bias relative to the default, so the Light's
	// ShadowBias still scales everything). Up to 80 m: scale 1, slope 0.
	inline void cascade_bias(float shadow_distance, unsigned count, const float* radius, float tan_half_x, float tan_half_y,
		unsigned tile_size, float bias_y, float softness, float* out_scale, float& out_slope)
	{
		out_slope = 0.0f;
		for (unsigned c = 0; c < count; ++c) out_scale[c] = 1.0f;
		if (!(shadow_distance > CASCADE_CLASSIC_DISTANCE) || count == 0 || tile_size == 0 || !(bias_y > 0.0f)) return;
		const float blend = (std::min)(1.0f, (shadow_distance - CASCADE_CLASSIC_DISTANCE) / CASCADE_BLEND_DISTANCE);
		const float user = bias_y / CASCADE_DEFAULT_BIAS;
		const float soft = 0.75f + 0.25f * (std::max)(softness, 1.0f);
		float s80[8];
		const unsigned n = count < 8 ? count : 8;
		cascade_splits(CASCADE_CLASSIC_DISTANCE, n, s80);
		float near80 = CASCADE_NEAR_Z;
		for (unsigned c = 0; c < n; ++c)
		{
			const float r = radius[c] > 0.01f ? radius[c] : 0.01f;
			const float classic = bias_y * (1.0f + (float)c) * soft;                       // depth units
			const float range = 2.0f * r + shadow_distance;                                // the box's depth (m)
			const float r80 = cascade_radius(near80, s80[c], tan_half_x, tan_half_y);
			const float world80 = classic * (2.0f * r80 + CASCADE_CLASSIC_DISTANCE);       // the classic bias at 80 m (m)
			const float floor_m = CASCADE_FLOOR_TEXELS * (2.0f * r / (float)tile_size) * user;
			const float want = world80 + blend * (floor_m - world80);
			out_scale[c] = want / (classic * range);
			near80 = s80[c];
		}
		out_slope = blend * CASCADE_SLOPE_TEXELS * user;
	}
}
