#include "PlaneGenerator.h"

namespace vortex::graphics
{
	void PlaneGenerator::generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const
	{
		out_vertices.clear();

		const u32 verts_x = m_subdivisions_x + 1;
		const u32 verts_z = m_subdivisions_z + 1;
		out_vertices.reserve(verts_x * verts_z);

		const float half_width = m_width * 0.5f;
		const float half_depth = m_depth * 0.5f;

		for (u32 z = 0; z < verts_z; ++z)
		{
			const float tz = static_cast<float>(z) / static_cast<float>(m_subdivisions_z);
			const float pz = -half_depth + tz * m_depth;

			for (u32 x = 0; x < verts_x; ++x)
			{
				const float tx = static_cast<float>(x) / static_cast<float>(m_subdivisions_x);
				const float px = -half_width + tx * m_width;

				VertexPosNormalUV vertex;
				vertex.position = { px, 0.0f, pz };
				vertex.normal = { 0.0f, 1.0f, 0.0f };
				vertex.uv = { tx, tz };

				out_vertices.push_back(vertex);
			}
		}
	}

	void PlaneGenerator::generate_indices(std::vector<u32>& out_indices) const
	{
		out_indices.clear();
		out_indices.reserve(m_subdivisions_x * m_subdivisions_z * 6);

		const u32 verts_x = m_subdivisions_x + 1;

		for (u32 z = 0; z < m_subdivisions_z; ++z)
		{
			for (u32 x = 0; x < m_subdivisions_x; ++x)
			{
				const u32 a = z * verts_x + x;
				const u32 b = a + 1;
				const u32 c = a + verts_x;
				const u32 d = c + 1;

				out_indices.push_back(a);
				out_indices.push_back(c);
				out_indices.push_back(b);

				out_indices.push_back(b);
				out_indices.push_back(c);
				out_indices.push_back(d);
			}
		}
	}
}
