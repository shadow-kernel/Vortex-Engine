#include "ConeGenerator.h"
#include <cmath>

namespace vortex::graphics
{
	namespace
	{
		constexpr float PI = 3.14159265358979323846f;
	}

	void ConeGenerator::generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const
	{
		out_vertices.clear();
		const float half_height = m_height * 0.5f;
		const float slope = m_radius / m_height;

		// Apex vertex (repeated for each slice for proper normals)
		// Side vertices
		for (u32 i = 0; i <= m_slices; ++i)
		{
			const float theta = 2.0f * PI * static_cast<float>(i) / static_cast<float>(m_slices);
			const float x = std::cos(theta);
			const float z = std::sin(theta);

			// Calculate normal for cone surface
			const float nx = x;
			const float ny = slope;
			const float nz = z;
			const float len = std::sqrt(nx * nx + ny * ny + nz * nz);

			DirectX::XMFLOAT3 normal{ nx / len, ny / len, nz / len };
			const float u = static_cast<float>(i) / static_cast<float>(m_slices);

			// Apex
			out_vertices.push_back({ {0, half_height, 0}, normal, {u, 0.0f} });
			// Base
			out_vertices.push_back({ {m_radius * x, -half_height, m_radius * z}, normal, {u, 1.0f} });
		}

		// Base cap
		if (m_cap)
		{
			// Center
			out_vertices.push_back({ {0, -half_height, 0}, {0, -1, 0}, {0.5f, 0.5f} });

			for (u32 i = 0; i <= m_slices; ++i)
			{
				const float theta = 2.0f * PI * static_cast<float>(i) / static_cast<float>(m_slices);
				const float x = m_radius * std::cos(theta);
				const float z = m_radius * std::sin(theta);

				const float u = (std::cos(theta) + 1.0f) * 0.5f;
				const float v = (std::sin(theta) + 1.0f) * 0.5f;

				out_vertices.push_back({ {x, -half_height, z}, {0, -1, 0}, {u, v} });
			}
		}
	}

	void ConeGenerator::generate_indices(std::vector<u32>& out_indices) const
	{
		out_indices.clear();

		// Side triangles
		for (u32 i = 0; i < m_slices; ++i)
		{
			const u32 apex = i * 2;
			const u32 base1 = apex + 1;
			const u32 base2 = apex + 3;

			out_indices.push_back(apex);
			out_indices.push_back(base1);
			out_indices.push_back(base2);
		}

		// Base cap
		if (m_cap)
		{
			const u32 center = (m_slices + 1) * 2;

			for (u32 i = 0; i < m_slices; ++i)
			{
				out_indices.push_back(center);
				out_indices.push_back(center + 2 + i);
				out_indices.push_back(center + 1 + i);
			}
		}
	}
}
