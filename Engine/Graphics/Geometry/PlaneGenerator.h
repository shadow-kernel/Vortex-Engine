#pragma once

#include "IMeshGenerator.h"

namespace vortex::graphics
{
	/// <summary>
	/// Generates a flat plane mesh on the XZ plane.
	/// </summary>
	class PlaneGenerator final : public IMeshGenerator
	{
	public:
		explicit PlaneGenerator(float width = 1.0f, float depth = 1.0f, u32 subdivisions_x = 1, u32 subdivisions_z = 1)
			: m_width(width), m_depth(depth), m_subdivisions_x(subdivisions_x), m_subdivisions_z(subdivisions_z) {}

		const char* type_name() const override { return "Plane"; }

		float width() const { return m_width; }
		float depth() const { return m_depth; }
		u32 subdivisions_x() const { return m_subdivisions_x; }
		u32 subdivisions_z() const { return m_subdivisions_z; }

		void set_width(float width) { m_width = width; }
		void set_depth(float depth) { m_depth = depth; }
		void set_subdivisions(u32 x, u32 z) { m_subdivisions_x = x; m_subdivisions_z = z; }

	protected:
		void generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const override;
		void generate_indices(std::vector<u32>& out_indices) const override;

	private:
		float m_width{ 1.0f };
		float m_depth{ 1.0f };
		u32 m_subdivisions_x{ 1 };
		u32 m_subdivisions_z{ 1 };
	};
}
