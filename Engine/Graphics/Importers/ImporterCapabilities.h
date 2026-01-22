#pragma once

namespace vortex::graphics
{
	/// <summary>
	/// Import capabilities and feature checks.
	/// </summary>
	class ImporterCapabilities
	{
	public:
		/// <summary>
		/// Check if Assimp model import is available.
		/// </summary>
		static constexpr bool has_assimp_support()
		{
#ifdef VORTEX_USE_ASSIMP
			return true;
#else
			return false;
#endif
		}

		/// <summary>
		/// Check if texture import is available (always true - uses stb_image).
		/// </summary>
		static constexpr bool has_texture_import_support()
		{
			return true;
		}

		/// <summary>
		/// Check if binary mesh serialization is available (always true).
		/// </summary>
		static constexpr bool has_vmesh_support()
		{
			return true;
		}

		/// <summary>
		/// Get a string describing available import features.
		/// </summary>
		static const char* get_feature_string()
		{
#ifdef VORTEX_USE_ASSIMP
			return "Assimp (FBX/OBJ/GLTF), stb_image (PNG/JPG/TGA), .vmesh";
#else
			return "stb_image (PNG/JPG/TGA), .vmesh (Model import disabled - install Assimp)";
#endif
		}
	};
}
