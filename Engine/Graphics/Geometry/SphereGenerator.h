#pragma once

#include "IMeshGenerator.h"

namespace vortex::graphics
{
	/// <summary>
	/// Generates a sphere mesh using latitude/longitude subdivision.
	/// </summary>
	class SphereGenerator final : public IMeshGenerator
	{
	public:
		explicit SphereGenerator(float radius = 0.5f, u32 slices = 32, u32 stacks = 16)
			: m_radius(radius), m_slices(slices), m_stacks(stacks) {}

		const char* type_name() const override { return "Sphere"; }

		float radius() const { return m_radius; }
		u32 slices() const { return m_slices; }
		u32 stacks() const { return m_stacks; }

		void set_radius(float radius) { m_radius = radius; }
		void set_slices(u32 slices) { m_slices = slices; }
		void set_stacks(u32 stacks) { m_stacks = stacks; }

	protected:
		void generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const override;
		void generate_indices(std::vector<u32>& out_indices) const override;

	private:
		float m_radius{ 0.5f };
		u32 m_slices{ 32 };
		u32 m_stacks{ 16 };
	};
}
