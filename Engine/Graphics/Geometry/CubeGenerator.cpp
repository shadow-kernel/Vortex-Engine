#include "CubeGenerator.h"

namespace vortex::graphics
{
	void CubeGenerator::generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const
	{
		const float h = m_size * 0.5f;

		out_vertices = {
			// Front face (+Z)
			{ {-h, -h,  h}, { 0,  0,  1}, {0, 1} },
			{ { h, -h,  h}, { 0,  0,  1}, {1, 1} },
			{ { h,  h,  h}, { 0,  0,  1}, {1, 0} },
			{ {-h,  h,  h}, { 0,  0,  1}, {0, 0} },
			// Back face (-Z)
			{ { h, -h, -h}, { 0,  0, -1}, {0, 1} },
			{ {-h, -h, -h}, { 0,  0, -1}, {1, 1} },
			{ {-h,  h, -h}, { 0,  0, -1}, {1, 0} },
			{ { h,  h, -h}, { 0,  0, -1}, {0, 0} },
			// Top face (+Y)
			{ {-h,  h,  h}, { 0,  1,  0}, {0, 1} },
			{ { h,  h,  h}, { 0,  1,  0}, {1, 1} },
			{ { h,  h, -h}, { 0,  1,  0}, {1, 0} },
			{ {-h,  h, -h}, { 0,  1,  0}, {0, 0} },
			// Bottom face (-Y)
			{ {-h, -h, -h}, { 0, -1,  0}, {0, 1} },
			{ { h, -h, -h}, { 0, -1,  0}, {1, 1} },
			{ { h, -h,  h}, { 0, -1,  0}, {1, 0} },
			{ {-h, -h,  h}, { 0, -1,  0}, {0, 0} },
			// Right face (+X)
			{ { h, -h,  h}, { 1,  0,  0}, {0, 1} },
			{ { h, -h, -h}, { 1,  0,  0}, {1, 1} },
			{ { h,  h, -h}, { 1,  0,  0}, {1, 0} },
			{ { h,  h,  h}, { 1,  0,  0}, {0, 0} },
			// Left face (-X)
			{ {-h, -h, -h}, {-1,  0,  0}, {0, 1} },
			{ {-h, -h,  h}, {-1,  0,  0}, {1, 1} },
			{ {-h,  h,  h}, {-1,  0,  0}, {1, 0} },
			{ {-h,  h, -h}, {-1,  0,  0}, {0, 0} },
		};
	}

	void CubeGenerator::generate_indices(std::vector<u32>& out_indices) const
	{
		out_indices = {
			0,  1,  2,  0,  2,  3,   // front
			4,  5,  6,  4,  6,  7,   // back
			8,  9,  10, 8,  10, 11,  // top
			12, 13, 14, 12, 14, 15,  // bottom
			16, 17, 18, 16, 18, 19,  // right
			20, 21, 22, 20, 22, 23   // left
		};
	}
}
