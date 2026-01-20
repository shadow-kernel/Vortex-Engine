#include "SphereGenerator.h"
#include <cmath>

namespace vortex::graphics
{
	namespace
	{
		constexpr float PI = 3.14159265358979323846f;
	}

	void SphereGenerator::generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const
	{
		out_vertices.clear();
		out_vertices.reserve((m_stacks + 1) * (m_slices + 1));

		for (u32 i = 0; i <= m_stacks; ++i)
		{
			const float phi = PI * static_cast<float>(i) / static_cast<float>(m_stacks);
			const float y = m_radius * std::cos(phi);
			const float r = m_radius * std::sin(phi);

			for (u32 j = 0; j <= m_slices; ++j)
			{
				const float theta = 2.0f * PI * static_cast<float>(j) / static_cast<float>(m_slices);
				const float x = r * std::cos(theta);
				const float z = r * std::sin(theta);

				VertexPosNormalUV vertex;
				vertex.position = { x, y, z };
				vertex.normal = { x / m_radius, y / m_radius, z / m_radius };
				vertex.uv = { 
					static_cast<float>(j) / static_cast<float>(m_slices), 
					static_cast<float>(i) / static_cast<float>(m_stacks) 
				};

				out_vertices.push_back(vertex);
			}
		}
	}

	void SphereGenerator::generate_indices(std::vector<u32>& out_indices) const
	{
		out_indices.clear();
		out_indices.reserve(m_stacks * m_slices * 6);

		for (u32 i = 0; i < m_stacks; ++i)
		{
			for (u32 j = 0; j < m_slices; ++j)
			{
				const u32 a = i * (m_slices + 1) + j;
				const u32 b = a + m_slices + 1;

				out_indices.push_back(a);
				out_indices.push_back(b);
				out_indices.push_back(a + 1);

				out_indices.push_back(a + 1);
				out_indices.push_back(b);
				out_indices.push_back(b + 1);
			}
		}
	}
}
