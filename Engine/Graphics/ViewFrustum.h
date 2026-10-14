#pragma once

// ============================================================================
// The six planes of a view's frustum, for the backend-neutral render budgets (the light selection #416 and the
// bone-palette budget #415). Gribb-Hartmann on a row-major view-projection (the XMFLOAT4X4 every renderer keeps),
// D3D depth range 0..1 — the same extraction both backends use for their own culling.
// ============================================================================
#include "../Common/CommonHeaders.h"
#include <cmath>

namespace vortex::graphics
{
	// Normalized planes (a, b, c, d): a point is inside a plane when a*x + b*y + c*z + d >= 0.
	struct ViewFrustum { float p[6][4]; };

	inline ViewFrustum view_frustum(const DirectX::XMFLOAT4X4& m)
	{
		ViewFrustum f = { {
			{ m._14 + m._11, m._24 + m._21, m._34 + m._31, m._44 + m._41 },   // left
			{ m._14 - m._11, m._24 - m._21, m._34 - m._31, m._44 - m._41 },   // right
			{ m._14 + m._12, m._24 + m._22, m._34 + m._32, m._44 + m._42 },   // bottom
			{ m._14 - m._12, m._24 - m._22, m._34 - m._32, m._44 - m._42 },   // top
			{ m._13,         m._23,         m._33,         m._43         },   // near
			{ m._14 - m._13, m._24 - m._23, m._34 - m._33, m._44 - m._43 },   // far
		} };
		for (int i = 0; i < 6; ++i)
		{
			const float a = f.p[i][0], b = f.p[i][1], c = f.p[i][2];
			const float len = sqrtf(a * a + b * b + c * c);
			if (len > 1e-6f) { f.p[i][0] /= len; f.p[i][1] /= len; f.p[i][2] /= len; f.p[i][3] /= len; }
		}
		return f;
	}

	// How far a sphere reaches into the frustum: >= 0 when it touches the inside of every plane (visible, or at least
	// not provably outside), otherwise minus the distance by which it misses the worst plane.
	inline float sphere_frustum_slack(const ViewFrustum& f, float x, float y, float z, float r)
	{
		float slack = 3.4e38f;
		for (int i = 0; i < 6; ++i)
		{
			const float s = f.p[i][0] * x + f.p[i][1] * y + f.p[i][2] * z + f.p[i][3] + r;
			if (s < slack) slack = s;
		}
		return slack;
	}
}
