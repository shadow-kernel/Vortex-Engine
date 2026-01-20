#pragma once

#include "IMeshGenerator.h"

namespace vortex::graphics
{
	/// <summary>
	/// Generates a cube mesh with proper normals for each face.
	/// </summary>
	class CubeGenerator final : public IMeshGenerator
	{
	public:
		explicit CubeGenerator(float size = 1.0f) : m_size(size) {}

		const char* type_name() const override { return "Cube"; }

		float size() const { return m_size; }
		void set_size(float size) { m_size = size; }

	protected:
		void generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const override;
		void generate_indices(std::vector<u32>& out_indices) const override;

	private:
		float m_size{ 1.0f };
	};
}
