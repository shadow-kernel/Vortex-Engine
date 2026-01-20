#include "CylinderGenerator.h"
#include <cmath>

namespace vortex::graphics
{
	namespace
	{
		constexpr float PI = 3.14159265358979323846f;
	}

	void CylinderGenerator::generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const
	{
		out_vertices.clear();
		generate_side_vertices(out_vertices);

		if (m_caps)
		{
			generate_cap_vertices(out_vertices, true);
			generate_cap_vertices(out_vertices, false);
		}
	}

	void CylinderGenerator::generate_indices(std::vector<u32>& out_indices) const
	{
		out_indices.clear();

		generate_side_indices(out_indices, 0);

		if (m_caps)
		{
			const u32 side_verts = (m_slices + 1) * 2;
			const u32 cap_verts = m_slices + 2;

			generate_cap_indices(out_indices, side_verts, true);
			generate_cap_indices(out_indices, side_verts + cap_verts, false);
		}
	}

	void CylinderGenerator::generate_side_vertices(std::vector<VertexPosNormalUV>& out_vertices) const
	{
		const float half_height = m_height * 0.5f;

		for (u32 i = 0; i <= m_slices; ++i)
		{
			const float theta = 2.0f * PI * static_cast<float>(i) / static_cast<float>(m_slices);
			const float x = m_radius * std::cos(theta);
			const float z = m_radius * std::sin(theta);
			const float u = static_cast<float>(i) / static_cast<float>(m_slices);

			// Normal pointing outward
			DirectX::XMFLOAT3 normal{ x / m_radius, 0.0f, z / m_radius };

			// Top ring vertex
			out_vertices.push_back({ {x, half_height, z}, normal, {u, 0.0f} });
			// Bottom ring vertex
			out_vertices.push_back({ {x, -half_height, z}, normal, {u, 1.0f} });
		}
	}

	void CylinderGenerator::generate_cap_vertices(std::vector<VertexPosNormalUV>& out_vertices, bool top) const
	{
		const float y = top ? m_height * 0.5f : -m_height * 0.5f;
		const DirectX::XMFLOAT3 normal = top ? DirectX::XMFLOAT3{0, 1, 0} : DirectX::XMFLOAT3{0, -1, 0};

		// Center vertex
		out_vertices.push_back({ {0, y, 0}, normal, {0.5f, 0.5f} });

		// Ring vertices
		for (u32 i = 0; i <= m_slices; ++i)
		{
			const float theta = 2.0f * PI * static_cast<float>(i) / static_cast<float>(m_slices);
			const float x = m_radius * std::cos(theta);
			const float z = m_radius * std::sin(theta);

			const float u = (x / m_radius + 1.0f) * 0.5f;
			const float v = (z / m_radius + 1.0f) * 0.5f;

			out_vertices.push_back({ {x, y, z}, normal, {u, v} });
		}
	}

	void CylinderGenerator::generate_side_indices(std::vector<u32>& out_indices, u32 base_index) const
	{
		for (u32 i = 0; i < m_slices; ++i)
		{
			const u32 a = base_index + i * 2;
			const u32 b = a + 1;
			const u32 c = a + 2;
			const u32 d = a + 3;

			out_indices.push_back(a);
			out_indices.push_back(b);
			out_indices.push_back(c);

			out_indices.push_back(c);
			out_indices.push_back(b);
			out_indices.push_back(d);
		}
	}

	void CylinderGenerator::generate_cap_indices(std::vector<u32>& out_indices, u32 center_index, bool top) const
	{
		for (u32 i = 0; i < m_slices; ++i)
		{
			if (top)
			{
				out_indices.push_back(center_index);
				out_indices.push_back(center_index + 1 + i);
				out_indices.push_back(center_index + 2 + i);
			}
			else
			{
				out_indices.push_back(center_index);
				out_indices.push_back(center_index + 2 + i);
				out_indices.push_back(center_index + 1 + i);
			}
		}
	}
}
