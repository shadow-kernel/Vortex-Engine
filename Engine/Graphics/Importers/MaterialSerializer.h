#pragma once

#include "../../Common/CommonHeaders.h"
#include "../Resources/Material.h"
#include <string>

namespace vortex::graphics
{
	/// <summary>
	/// Material data for serialization.
	/// </summary>
	struct MaterialData
	{
		std::string name;
		MaterialProperties properties;
		std::string albedo_texture_path;
		std::string normal_texture_path;
		std::string metallic_roughness_texture_path;

		bool is_valid() const { return !name.empty(); }
	};

	/// <summary>
	/// Binary material file format (.vmat) for Vortex Engine.
	/// </summary>
	class MaterialSerializer
	{
	public:
		static constexpr u32 VMAT_MAGIC = 0x54414D56; // "VMAT"
		static constexpr u32 VMAT_VERSION = 1;

		struct VMaterialHeader
		{
			u32 magic{ VMAT_MAGIC };
			u32 version{ VMAT_VERSION };
			MaterialProperties properties;
			char name[64]{ 0 };
			char albedo_texture[256]{ 0 };
			char normal_texture[256]{ 0 };
			char metallic_roughness_texture[256]{ 0 };
		};

		/// <summary>
		/// Save material data to binary .vmat file.
		/// </summary>
		static bool save_to_file(const MaterialData& data, const std::string& filepath);

		/// <summary>
		/// Load material data from binary .vmat file.
		/// </summary>
		static MaterialData load_from_file(const std::string& filepath);
	};
}
