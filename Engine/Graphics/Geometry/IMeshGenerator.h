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

	// Per-vertex skinning influences (kept PARALLEL to a submesh's VertexPosNormalUV array so every
	// existing consumer — decimator, triangle queries, serializer — stays untouched). Max 4 influences,
	// u8 indices into the model's bone palette (<=255 bones), weights normalized at import.
	struct VertexSkin
	{
		u8 bone_indices[4]{ 0, 0, 0, 0 };
		float bone_weights[4]{ 0.0f, 0.0f, 0.0f, 0.0f };
	};

	// The INTERLEAVED GPU vertex for skinned meshes (52 bytes). pos/normal/uv sit at the same offsets as
	// VertexPosNormalUV, so the rigid PSOs (input layout reads offsets 0/12/24; stride comes from the VBV)
	// can still draw a skinned mesh in bind pose — only the skinned PSO additionally reads offsets 32/36.
	struct SkinnedVertexPosNormalUV
	{
		DirectX::XMFLOAT3 position;   // offset 0
		DirectX::XMFLOAT3 normal;     // offset 12
		DirectX::XMFLOAT2 uv;         // offset 24
		u8 bone_indices[4];           // offset 32 (R8G8B8A8_UINT)
		float bone_weights[4];        // offset 36 (R32G32B32A32_FLOAT)
	};
	static_assert(sizeof(SkinnedVertexPosNormalUV) == 52, "skinned vertex must stay 52 bytes (GPU input layout)");

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
		/// Generate mesh data directly into output vectors.
		/// </summary>
		void generate(std::vector<VertexPosNormalUV>& out_vertices, std::vector<u32>& out_indices) const
		{
			generate_vertices(out_vertices);
			generate_indices(out_indices);
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
