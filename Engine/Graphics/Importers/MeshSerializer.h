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

		/// <summary>
		/// The render-side import cache (#364 C): write every submesh of an imported model as its own single-submesh
		/// file <dir>/submesh_N.vmesh, so a later start loads the model with load_vmesh instead of Assimp. Returns the
		/// number of files written; 0 when the model is skinned (the format carries no bone weights) or a write fails.
		/// The vertex / index data is moved out and back, not copied.
		/// </summary>
		static u32 save_submeshes_to_dir(ImportedModelData& data, const std::string& dir);

		/// <summary>
		/// Read a folder written by save_submeshes_to_dir back into one model: the geometry from the submesh_N.vmesh
		/// files, the material records from materials.vmc. False when the folder is incomplete or from another version.
		/// </summary>
		static bool load_submeshes_from_dir(const std::string& dir, ImportedModelData& out);

	private:
		static bool write_string(std::ofstream& file, const std::string& str, size_t max_length);
		static std::string read_string(std::ifstream& file, size_t max_length);
	};
}
