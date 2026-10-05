#pragma once

#include "../Engine/Graphics/Geometry/MeshGeneratorFactory.h"

#include "Test.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <vector>

using namespace vortex::graphics;

// Primitive geometry (VortexGeometryTest): every triangle of every built-in primitive must face outward — wound so
// its geometric normal agrees with the vertex normals, the same way as the cube. Sphere, cylinder and cone used to be
// wound the other way: back-face culling then dropped their visible side and the inner far wall was drawn, lit
// inside-out (the dark material-preview sphere). Headless; prints "ALL PASSED" or "FAILURES!" and exits non-zero.
class engine_test : public test
{
public:
	bool initialize() override { return true; }

	void run() override
	{
		int failures = 0;
		auto check = [&](const char* name, const IMeshGenerator& generator)
		{
			std::vector<VertexPosNormalUV> v;
			std::vector<u32> idx;
			generator.generate(v, idx);
			int total = 0, inward = 0;
			for (size_t i = 0; i + 2 < idx.size(); i += 3)
			{
				const auto& a = v[idx[i]]; const auto& b = v[idx[i + 1]]; const auto& c = v[idx[i + 2]];
				const float e1[3] = { b.position.x - a.position.x, b.position.y - a.position.y, b.position.z - a.position.z };
				const float e2[3] = { c.position.x - a.position.x, c.position.y - a.position.y, c.position.z - a.position.z };
				const float n[3] = { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
				// zero-area slivers at the sphere poles (sinf(pi) is -8.7e-8, not 0) have no meaningful orientation
				const float area2 = std::sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
				const float l1 = e1[0] * e1[0] + e1[1] * e1[1] + e1[2] * e1[2], l2 = e2[0] * e2[0] + e2[1] * e2[1] + e2[2] * e2[2];
				if (area2 < 1e-4f * (l1 > l2 ? l1 : l2)) continue;
				const float vn[3] = { a.normal.x + b.normal.x + c.normal.x, a.normal.y + b.normal.y + c.normal.y, a.normal.z + b.normal.z + c.normal.z };
				++total;
				if (n[0] * vn[0] + n[1] * vn[1] + n[2] * vn[2] <= 0.0f) ++inward;
			}
			const bool ok = total > 0 && inward == 0;
			std::printf("%-9s %4d triangles, %d facing inward  %s\n", name, total, inward, ok ? "ok" : "FAIL");
			if (!ok) ++failures;
		};
		check("cube", *MeshGeneratorFactory::create_cube(1.0f));
		check("plane", *MeshGeneratorFactory::create_plane(1.0f, 1.0f));
		check("sphere", *MeshGeneratorFactory::create_sphere(0.5f, 32, 16));
		check("cylinder", *MeshGeneratorFactory::create_cylinder(0.5f, 1.0f, 32));
		check("cone", *MeshGeneratorFactory::create_cone(0.5f, 1.0f, 32));
		std::printf(failures ? "FAILURES!\n" : "ALL PASSED\n");
		m_failed = failures != 0;
	}

	void shutdown() override { if (m_failed) std::exit(1); }

private:
	bool m_failed{ false };
};
