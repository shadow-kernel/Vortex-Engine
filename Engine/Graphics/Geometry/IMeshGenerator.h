#pragma once

#include "../../Common/CommonHeaders.h"
#include <DirectXMath.h>
#include <vector>

namespace vortex::graphics
{
	struct VertexPosNormalUV
	{
		DirectX::XMFLOAT3 position;
		DirectX::XMFLOAT3 normal;
		DirectX::XMFLOAT2 uv;
	};

	struct MeshData
	{
		std::vector<VertexPosNormalUV> vertices;
		std::vector<u32> indices;

		bool is_valid() const { return !vertices.empty(); }
		void clear() { vertices.clear(); indices.clear(); }
	};

	/// <summary>
	/// Abstract base class for all mesh generators.
	/// Implements the Template Method pattern for mesh generation.
	/// </summary>
	class IMeshGenerator
	{
	public:
		virtual ~IMeshGenerator() = default;

		/// <summary>
		/// Generate mesh data. Template method that calls protected virtual methods.
		/// </summary>
		MeshData generate() const
		{
			MeshData data;
			generate_vertices(data.vertices);
			generate_indices(data.indices);
			return data;
		}

		/// <summary>
		/// Get the type name of this generator for serialization/UI.
		/// </summary>
		virtual const char* type_name() const = 0;

	protected:
		virtual void generate_vertices(std::vector<VertexPosNormalUV>& out_vertices) const = 0;
		virtual void generate_indices(std::vector<u32>& out_indices) const = 0;
	};
}
