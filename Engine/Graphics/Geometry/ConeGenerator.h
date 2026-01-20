#pragma once

#include "IMeshGenerator.h"

namespace vortex::graphics
{
	/// <summary>
	/// Generates a cone mesh with optional base cap.
	/// </summary>
	class ConeGenerator final : public IMeshGenerator
	{
	public:
		explicit ConeGenerator(float radius = 0.5f, float height = 1.0f, u32 slices = 32, bool cap = true)
			: m_radius(radius), m_height(height), m_slices(slices), m_cap(cap) {}

		const char* type_name() const override { return "Cone"; }

		float radius() const { return m_radius; }
		float height() const { return m_height; }
		u32 slices() const { return m_slices; }
		bool has_cap() const { return m_cap; }

		void set_radius(float radius) { m_radius = radius; }
		void set_height(float height) { m_height = height; }
		void set_slices(u32 slices) { m_slices = slices; }
		void set_cap(bool cap) { m_cap = cap; }

	protected:
		void generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const override;
		void generate_indices(std::vector<u32>& out_indices) const override;

	private:
		float m_radius{ 0.5f };
		float m_height{ 1.0f };
		u32 m_slices{ 32 };
		bool m_cap{ true };
	};
}
