#pragma once

// Example: How to use the Vortex Model Import System
// This file demonstrates the import functionality.

#include "Graphics/Resources/ResourceRegistry.h"
#include "Graphics/Importers/ModelImporter.h"
#include "Graphics/Importers/TextureImporter.h"
#include "Graphics/Importers/MeshSerializer.h"
#include "Graphics/Importers/ImporterCapabilities.h"

namespace vortex::examples
{
	/// <summary>
	/// Example usage of the model import system.
	/// </summary>
	class ModelImportExample
	{
	public:
		static void example_import_model()
		{
			using namespace graphics;

			// Check if Assimp is available
			if (!ImporterCapabilities::has_assimp_support())
			{
				// Can't import FBX/OBJ without Assimp
				// But can still use .vmesh and textures
				return;
			}

			// Get the resource registry
			auto& registry = ResourceRegistry::instance();

			// Import a model from FBX
			id::id_type model_id = registry.import_model("assets/models/character.fbx");
			
			if (id::is_valid(model_id))
			{
				// Model imported successfully!
				// Now we can use it with a MeshRenderer
			}
		}

		static void example_import_texture()
		{
			using namespace graphics;

			auto& registry = ResourceRegistry::instance();

			// Import a texture (always works - uses stb_image)
			id::id_type texture_id = registry.import_texture("assets/textures/diffuse.png");
			
			if (id::is_valid(texture_id))
			{
				// Texture imported successfully!
				Texture* texture = registry.get_texture(texture_id);
				// Use texture with materials
			}
		}

		static void example_native_format()
		{
			using namespace graphics;

			auto& registry = ResourceRegistry::instance();

			// Load from native .vmesh format (fast loading)
			id::id_type mesh_id = registry.load_vmesh("assets/meshes/optimized.vmesh");
			
			if (id::is_valid(mesh_id))
			{
				// Mesh loaded from native format
				Mesh* mesh = registry.get_mesh(mesh_id);
			}
		}

		static void example_direct_import()
		{
			using namespace graphics;

			// Direct use of importers (without ResourceRegistry)
			
			// Import model data
			ImportedModelData model_data = ModelImporter::import_from_file("model.fbx");
			
			if (model_data.is_valid())
			{
				// Process the imported data
				for (const auto& submesh : model_data.submeshes)
				{
					// Each submesh has vertices, indices, material index
					auto vertex_count = submesh.vertices.size();
					auto index_count = submesh.indices.size();
					// ...
				}

				// Save to native format for fast loading later
				MeshSerializer::save_to_file(model_data, "output.vmesh");
			}

			// Import texture data
			ImageData image_data = TextureImporter::import_from_file("texture.png");
			
			if (image_data.is_valid())
			{
				// Use the image data
				auto width = image_data.width;
				auto height = image_data.height;
				auto* pixels = image_data.pixels.data();
				// Create GPU texture from this data
			}
		}

		static void example_check_capabilities()
		{
			using namespace graphics;

			// Check what features are available
			bool has_assimp = ImporterCapabilities::has_assimp_support();
			bool has_textures = ImporterCapabilities::has_texture_import_support();
			bool has_vmesh = ImporterCapabilities::has_vmesh_support();

			// Get feature description
			const char* features = ImporterCapabilities::get_feature_string();
			
			// Log or display the features
			// Example output: "Assimp (FBX/OBJ/GLTF), stb_image (PNG/JPG/TGA), .vmesh"
		}

		static void example_format_checking()
		{
			using namespace graphics;

			// Check if a format is supported
			bool fbx_supported = ModelImporter::is_format_supported(".fbx");
			bool png_supported = TextureImporter::is_format_supported(".png");
			
			// Note: Even if format is "supported", actual import may fail
			// if Assimp is not available (for model formats)
		}
	};
}

/*
 * Usage from Editor (C#):
 * 
 * using Editor.DllWrapper;
 * 
 * // Check if Assimp is available
 * bool canImport = VortexAPI.IsAssimpAvailable();
 * 
 * // Import model
 * long modelId = VortexAPI.ImportModelFromFile("path/to/model.fbx");
 * 
 * // Import texture
 * long textureId = VortexAPI.ImportTextureFromFile("path/to/texture.png");
 * 
 * // Load native format
 * long meshId = VortexAPI.LoadVMeshFromFile("path/to/mesh.vmesh");
 * 
 * // Export to native format
 * bool success = VortexAPI.SaveMeshToVMesh(modelId, "output.vmesh");
 */
