#pragma once

#include "../../Common/CommonHeaders.h"
#include "ModelImporter.h"
#include <string>
#include <vector>

namespace vortex::graphics
{
	/// <summary>
	/// Binary mesh file format (.vmesh) for Vortex Engine.
	/// Designed for fast loading and minimal parsing.
	/// </summary>
	class MeshSerializer
	{
	public:
		static constexpr u32 VMESH_MAGIC = 0x4853454D; // "MESH"
		static constexpr u32 VMESH_VERSION = 1;

		struct VMeshHeader
		{
			u32 magic{ VMESH_MAGIC };
			u32 version{ VMESH_VERSION };
			u32 submesh_count{ 0 };
			DirectX::XMFLOAT3 bounds_min{ 0.0f, 0.0f, 0.0f };
			DirectX::XMFLOAT3 bounds_max{ 0.0f, 0.0f, 0.0f };
			char name[64]{ 0 };
		};

		struct VMeshSubMesh
		{
			u32 vertex_count{ 0 };
			u32 index_count{ 0 };
			u32 material_index{ 0 };
			char name[64]{ 0 };
		};

		/// <summary>
		/// Save model data to binary .vmesh file.
		/// </summary>
		static bool save_to_file(const ImportedModelData& data, const std::string& filepath);

		/// <summary>
		/// Load model data from binary .vmesh file.
		/// </summary>
		static ImportedModelData load_from_file(const std::string& filepath);

	private:
		static bool write_string(std::ofstream& file, const std::string& str, size_t max_length);
		static std::string read_string(std::ifstream& file, size_t max_length);
	};
}
