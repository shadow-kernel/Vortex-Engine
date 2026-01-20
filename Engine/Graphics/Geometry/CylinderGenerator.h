#pragma once

#include "IMeshGenerator.h"

namespace vortex::graphics
{
	/// <summary>
	/// Generates a cylinder mesh with caps.
	/// </summary>
	class CylinderGenerator final : public IMeshGenerator
	{
	public:
		explicit CylinderGenerator(float radius = 0.5f, float height = 1.0f, u32 slices = 32, bool caps = true)
			: m_radius(radius), m_height(height), m_slices(slices), m_caps(caps) {}

		const char* type_name() const override { return "Cylinder"; }

		float radius() const { return m_radius; }
		float height() const { return m_height; }
		u32 slices() const { return m_slices; }
		bool has_caps() const { return m_caps; }

		void set_radius(float radius) { m_radius = radius; }
		void set_height(float height) { m_height = height; }
		void set_slices(u32 slices) { m_slices = slices; }
		void set_caps(bool caps) { m_caps = caps; }

	protected:
		void generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const override;
		void generate_indices(std::vector<u32>& out_indices) const override;

	private:
		void generate_side_vertices(std::vector<VertexPosNormalUV>& out_vertices) const;
		void generate_cap_vertices(std::vector<VertexPosNormalUV>& out_vertices, bool top) const;
		void generate_side_indices(std::vector<u32>& out_indices, u32 base_index) const;
		void generate_cap_indices(std::vector<u32>& out_indices, u32 center_index, bool top) const;

		float m_radius{ 0.5f };
		float m_height{ 1.0f };
		u32 m_slices{ 32 };
		bool m_caps{ true };
	};
}
