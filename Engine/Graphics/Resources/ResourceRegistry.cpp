#include "ResourceRegistry.h"
#include "../Geometry/MeshGeneratorFactory.h"
#include "../Geometry/MeshDecimator.h"
#include "../Importers/ModelImporter.h"
#include "../Importers/TextureImporter.h"
#include "../Importers/MeshSerializer.h"
#include <algorithm>
#include <cmath>
#include <string>
#include <cfloat>
#include <Windows.h>

namespace vortex::graphics
{
	ResourceRegistry& ResourceRegistry::instance()
	{
		static ResourceRegistry inst;
		return inst;
	}

	void ResourceRegistry::initialize(ID3D12Device* device)
	{
		if (!device) return;
		m_device = device;

		create_srv_heap();

		m_default_cube = create_primitive_cube(1.0f);
		m_default_sphere = create_primitive_sphere(0.5f);
		m_default_plane = create_primitive_plane(10.0f, 10.0f);
		m_default_cylinder = create_primitive_cylinder(0.5f, 1.0f);
		m_default_cone = create_primitive_cone(0.5f, 1.0f);

		m_default_white_texture = create_solid_color_texture(0xFFFFFFFF, "White");
		m_default_material = create_material("Default");
	}

	void ResourceRegistry::shutdown()
	{
		m_meshes.clear();
		m_textures.clear();
		m_materials.clear();

		m_default_cube = id::invalid_id;
		m_default_sphere = id::invalid_id;
		m_default_plane = id::invalid_id;
		m_default_cylinder = id::invalid_id;
		m_default_cone = id::invalid_id;
		m_default_white_texture = id::invalid_id;
		m_default_material = id::invalid_id;

		m_device = nullptr;
	}

	id::id_type ResourceRegistry::create_mesh(const MeshData& data, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		auto mesh = std::make_unique<Mesh>();
		if (!mesh->create(m_device, data))
		{
			return id::invalid_id;
		}
		mesh->set_name(name);

		id::id_type id = m_next_mesh_id++;
		m_meshes[id] = std::move(mesh);
		return id;
	}

	id::id_type ResourceRegistry::create_mesh_from_generator(const IMeshGenerator& generator)
	{
		if (!m_device) return id::invalid_id;

		auto mesh = std::make_unique<Mesh>();
		if (!mesh->create_from_generator(m_device, generator))
		{
			return id::invalid_id;
		}

		id::id_type id = m_next_mesh_id++;
		m_meshes[id] = std::move(mesh);
		return id;
	}

	id::id_type ResourceRegistry::create_primitive_cube(float size)
	{
		auto generator = MeshGeneratorFactory::create_cube(size);
		return create_mesh_from_generator(*generator);
	}

	id::id_type ResourceRegistry::create_primitive_sphere(float radius, u32 slices, u32 stacks)
	{
		auto generator = MeshGeneratorFactory::create_sphere(radius, slices, stacks);
		return create_mesh_from_generator(*generator);
	}

	id::id_type ResourceRegistry::create_inverted_sphere(float radius, u32 slices, u32 stacks)
	{
		// Create sphere and then invert the winding order for skybox rendering
		auto generator = MeshGeneratorFactory::create_sphere(radius, slices, stacks);
		
		// Generate the mesh data
		std::vector<VertexPosNormalUV> vertices;
		std::vector<u32> indices;
		generator->generate(vertices, indices);
		
		// Invert normals (point inward for skybox)
		for (auto& v : vertices)
		{
			v.normal.x = -v.normal.x;
			v.normal.y = -v.normal.y;
			v.normal.z = -v.normal.z;
		}
		
		// Reverse winding order (swap indices in each triangle)
		for (size_t i = 0; i + 2 < indices.size(); i += 3)
		{
			std::swap(indices[i + 1], indices[i + 2]);
		}
		
		// Create mesh from modified data
		MeshData mesh_data;
		mesh_data.vertices.reserve(vertices.size());
		for (const auto& v : vertices)
		{
			mesh_data.vertices.push_back({
				{ v.position.x, v.position.y, v.position.z },
				{ v.normal.x, v.normal.y, v.normal.z },
				{ v.uv.x, v.uv.y }
			});
		}
		mesh_data.indices = std::move(indices);
		
		return create_mesh(mesh_data, "InvertedSphere");
	}

	id::id_type ResourceRegistry::create_primitive_plane(float width, float depth)
	{
		auto generator = MeshGeneratorFactory::create_plane(width, depth);
		return create_mesh_from_generator(*generator);
	}

	id::id_type ResourceRegistry::create_primitive_cylinder(float radius, float height, u32 slices)
	{
		auto generator = MeshGeneratorFactory::create_cylinder(radius, height, slices);
		return create_mesh_from_generator(*generator);
	}

	id::id_type ResourceRegistry::create_primitive_cone(float radius, float height, u32 slices)
	{
		auto generator = MeshGeneratorFactory::create_cone(radius, height, slices);
		return create_mesh_from_generator(*generator);
	}

	Mesh* ResourceRegistry::get_mesh(id::id_type id)
	{
		auto it = m_meshes.find(id);
		return it != m_meshes.end() ? it->second.get() : nullptr;
	}

	void ResourceRegistry::destroy_mesh(id::id_type id)
	{
		auto it = m_meshes.find(id);
		if (it != m_meshes.end())
		{
			m_meshes.erase(it);
		}
	}

	std::vector<id::id_type> ResourceRegistry::get_all_mesh_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_meshes.size());
		for (const auto& [id, _] : m_meshes)
		{
			ids.push_back(id);
		}
		return ids;
	}

	id::id_type ResourceRegistry::create_texture(const TextureDesc& desc, const void* data)
	{
		if (!m_device) return id::invalid_id;

		auto texture = std::make_unique<Texture>();
		if (!texture->create(m_device, desc, data))
		{
			return id::invalid_id;
		}

		id::id_type id = m_next_texture_id++;
		
		assign_srv_to_texture(texture.get());
		
		m_textures[id] = std::move(texture);
		return id;
	}

	id::id_type ResourceRegistry::create_solid_color_texture(u32 color, const std::string& name)
	{
		TextureDesc desc;
		desc.width = 1;
		desc.height = 1;
		desc.format = TextureFormat::RGBA8_UNORM;
		return create_texture(desc, &color);
	}

	Texture* ResourceRegistry::get_texture(id::id_type id)
	{
		auto it = m_textures.find(id);
		return it != m_textures.end() ? it->second.get() : nullptr;
	}

	void ResourceRegistry::destroy_texture(id::id_type id)
	{
		auto it = m_textures.find(id);
		if (it != m_textures.end())
		{
			m_textures.erase(it);
		}
	}

	std::vector<id::id_type> ResourceRegistry::get_all_texture_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_textures.size());
		for (const auto& [id, _] : m_textures)
		{
			ids.push_back(id);
		}
		return ids;
	}

	id::id_type ResourceRegistry::create_material(const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		auto material = std::make_unique<Material>();

		id::id_type id = m_next_material_id++;
		m_materials[id] = std::move(material);
		return id;
	}

	Material* ResourceRegistry::get_material(id::id_type id)
	{
		auto it = m_materials.find(id);
		return it != m_materials.end() ? it->second.get() : nullptr;
	}

	void ResourceRegistry::destroy_material(id::id_type id)
	{
		auto it = m_materials.find(id);
		if (it != m_materials.end())
		{
			m_materials.erase(it);
		}
	}

	std::vector<id::id_type> ResourceRegistry::get_all_material_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_materials.size());
		for (const auto& [id, _] : m_materials)
		{
			ids.push_back(id);
		}
		return ids;
	}

	id::id_type ResourceRegistry::import_model(const std::string& filepath)
	{
		if (!m_device) 
		{
			OutputDebugStringA("ResourceRegistry::import_model - Device not initialized!\n");
			return id::invalid_id;
		}

		OutputDebugStringA(("ResourceRegistry: Importing model: " + filepath + "\n").c_str());

		ImportedModelData data = ModelImporter::import_from_file(filepath);
		if (!data.is_valid())
		{
			OutputDebugStringA("ResourceRegistry: ModelImporter returned invalid data!\n");
			return id::invalid_id;
		}

		OutputDebugStringA(("ResourceRegistry: Model has " + std::to_string(data.submeshes.size()) + " submeshes\n").c_str());

		MeshData combined_data;
		u32 index_offset = 0;

		for (const auto& submesh : data.submeshes)
		{
			for (const auto& vertex : submesh.vertices)
			{
				combined_data.vertices.push_back(vertex);
			}
			for (auto idx : submesh.indices)
			{
				combined_data.indices.push_back(idx + index_offset);
			}
			index_offset += static_cast<u32>(submesh.vertices.size());
		}

		return create_mesh(combined_data, data.name);
	}

	id::id_type ResourceRegistry::import_texture(const std::string& filepath, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		ImageData image_data = TextureImporter::import_from_file(filepath);
		if (!image_data.is_valid())
		{
			OutputDebugStringA(("Failed to load texture: " + filepath + "\n").c_str());
			return id::invalid_id;
		}
		return create_texture_from_image(image_data, filepath);
	}

	id::id_type ResourceRegistry::import_texture_from_memory(const u8* data, u64 length, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		ImageData image_data = TextureImporter::import_from_memory(data, length);
		if (!image_data.is_valid())
		{
			OutputDebugStringA("Failed to load texture from memory\n");
			return id::invalid_id;
		}
		return create_texture_from_image(image_data, name.empty() ? std::string("memtex") : name);
	}

	id::id_type ResourceRegistry::create_texture_from_image(ImageData& image_data, const std::string& label)
	{
		OutputDebugStringA(("Loaded texture: " + label + " (" +
			std::to_string(image_data.width) + "x" + std::to_string(image_data.height) + ")\n").c_str());

		std::vector<u8> rgba_pixels;
		const u8* pixel_data = image_data.pixels.data();
		
		if (image_data.format == ImageFormat::RGB8 || image_data.channels == 3)
		{
			rgba_pixels.resize(image_data.width * image_data.height * 4);
			const u8* src = image_data.pixels.data();
			for (size_t i = 0; i < image_data.width * image_data.height; i++)
			{
				rgba_pixels[i * 4 + 0] = src[i * 3 + 0];
				rgba_pixels[i * 4 + 1] = src[i * 3 + 1];
				rgba_pixels[i * 4 + 2] = src[i * 3 + 2];
				rgba_pixels[i * 4 + 3] = 255;
			}
			pixel_data = rgba_pixels.data();
			image_data.format = ImageFormat::RGBA8;
			image_data.channels = 4;
		}

		TextureDesc desc;
		desc.width = image_data.width;
		desc.height = image_data.height;
		
		switch (image_data.format)
		{
		case ImageFormat::R8:
			desc.format = TextureFormat::R8_UNORM;
			break;
		case ImageFormat::RG8:
			desc.format = TextureFormat::RG8_UNORM;
			break;
		case ImageFormat::RGBA8:
		default:
			desc.format = TextureFormat::RGBA8_UNORM;
			break;
		}

		return create_texture(desc, pixel_data);
	}

	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials(const std::string& filepath)
	{
		MultiMaterialImportResult result;
		result.success = false;

		if (!m_device)
		{
			OutputDebugStringA("ResourceRegistry not initialized\n");
			return result;
		}

		OutputDebugStringA(("=== Multi-Material Import: " + filepath + " ===\n").c_str());

		// Import model - ModelImporter now handles texture assignment
		ImportedModelData model_data = ModelImporter::import_from_file(filepath);
		if (!model_data.is_valid())
		{
			OutputDebugStringA("Import failed - no valid data\n");
			return result;
		}
		return build_model_result(model_data);
	}

	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials_from_memory(
		const u8* data, u64 length, const std::string& ext_hint, const std::string& virtual_dir)
	{
		MultiMaterialImportResult result;
		result.success = false;
		if (!m_device)
		{
			OutputDebugStringA("ResourceRegistry not initialized\n");
			return result;
		}

		OutputDebugStringA(("=== Multi-Material Import (memory): ." + ext_hint + " ===\n").c_str());
		ImportedModelData model_data = ModelImporter::import_from_memory(data, length, ext_hint, virtual_dir);
		if (!model_data.is_valid())
		{
			OutputDebugStringA("Import from memory failed - no valid data\n");
			return result;
		}
		return build_model_result(model_data);
	}

	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::build_model_result(ImportedModelData& model_data)
	{
		MultiMaterialImportResult result;
		result.success = false;

		result.model_name = model_data.name;
		OutputDebugStringA(("Model: " + model_data.name + ", " +
			std::to_string(model_data.submeshes.size()) + " submeshes\n").c_str());

		for (size_t i = 0; i < model_data.submeshes.size(); i++)
		{
			const auto& submesh = model_data.submeshes[i];
			SubmeshImportResult sub_result;
			sub_result.material_index = submesh.material_index;
			sub_result.name = submesh.name.empty() ? ("Submesh_" + std::to_string(i)) : submesh.name;

			OutputDebugStringA(("Processing: " + sub_result.name + "\n").c_str());

			// Create mesh
			sub_result.mesh_id = create_mesh_from_submesh(submesh, sub_result.name);
			if (sub_result.mesh_id == id::invalid_id)
			{
				continue;
			}

			// Create material
			sub_result.material_id = create_material(sub_result.name + "_material");
			if (sub_result.material_id == id::invalid_id)
			{
				continue;
			}

			auto* mat = get_material(sub_result.material_id);
			if (mat)
			{
				mat->set_base_color({ submesh.base_color[0], submesh.base_color[1], submesh.base_color[2], submesh.base_color[3] });
			}

			// Load texture - ModelImporter already assigned the correct path
			if (!submesh.diffuse_texture.empty())
			{
				OutputDebugStringA(("  Texture: " + submesh.diffuse_texture + "\n").c_str());
				sub_result.texture_id = import_texture(submesh.diffuse_texture, sub_result.name + "_tex");
				if (sub_result.texture_id != id::invalid_id && mat)
				{
					auto* tex = get_texture(sub_result.texture_id);
					if (tex)
					{
						mat->set_albedo_texture(tex);
						OutputDebugStringA("  Texture bound OK\n");
					}
				}
			}
			else
			{
				OutputDebugStringA("  No texture assigned\n");
			}

			result.submeshes.push_back(sub_result);
		}

		result.success = !result.submeshes.empty();
		OutputDebugStringA(("=== Import Complete: " + std::to_string(result.submeshes.size()) + " submeshes ===\n").c_str());

		return result;
	}

	id::id_type ResourceRegistry::create_mesh_from_submesh(const SubMeshData& submesh, const std::string& name)
	{
		if (submesh.vertices.empty())
			return id::invalid_id;

		MeshData mesh_data;
		mesh_data.vertices = submesh.vertices;
		mesh_data.indices = submesh.indices;

		id::id_type mesh_id = create_mesh(mesh_data, name);
		
		if (mesh_id != id::invalid_id)
		{
			auto* mesh = get_mesh(mesh_id);
			if (mesh)
			{
				float minX = FLT_MAX, minY = FLT_MAX, minZ = FLT_MAX;
				float maxX = -FLT_MAX, maxY = -FLT_MAX, maxZ = -FLT_MAX;
				
				for (const auto& vertex : submesh.vertices)
				{
					minX = std::min(minX, vertex.position.x);
					minY = std::min(minY, vertex.position.y);
					minZ = std::min(minZ, vertex.position.z);
					maxX = std::max(maxX, vertex.position.x);
					maxY = std::max(maxY, vertex.position.y);
					maxZ = std::max(maxZ, vertex.position.z);
				}
				
				mesh->set_bounds(minX, minY, minZ, maxX, maxY, maxZ);
			}
			// Build decimated LOD meshes for this submesh while its CPU geometry is still available.
			register_lod_chain(mesh_id, submesh, name);
		}

		return mesh_id;
	}

	void ResourceRegistry::register_lod_chain(id::id_type base_mesh_id, const SubMeshData& submesh, const std::string& name)
	{
		if (base_mesh_id == id::invalid_id) return;
		// Tiny meshes aren't worth LODs (the overhead/quality tradeoff isn't favorable).
		if (submesh.indices.size() < 900 || submesh.vertices.size() < 300) return;

		DirectX::XMFLOAT3 bmin{ FLT_MAX, FLT_MAX, FLT_MAX }, bmax{ -FLT_MAX, -FLT_MAX, -FLT_MAX };
		for (const auto& v : submesh.vertices)
		{
			bmin.x = std::min(bmin.x, v.position.x); bmin.y = std::min(bmin.y, v.position.y); bmin.z = std::min(bmin.z, v.position.z);
			bmax.x = std::max(bmax.x, v.position.x); bmax.y = std::max(bmax.y, v.position.y); bmax.z = std::max(bmax.z, v.position.z);
		}
		const float dx = bmax.x - bmin.x, dy = bmax.y - bmin.y, dz = bmax.z - bmin.z;

		LodChain chain;
		chain.lods[0] = base_mesh_id;
		chain.lod_count = 1;
		chain.radius = 0.5f * std::sqrt(dx * dx + dy * dy + dz * dz);
		if (chain.radius < 0.0001f) chain.radius = 1.0f;

		const unsigned gridRes[3] = { 24u, 12u, 6u };
		double prevIdx = static_cast<double>(submesh.indices.size());
		for (int L = 0; L < 3 && chain.lod_count < 4; ++L)
		{
			MeshData dec = decimate_vertex_cluster(submesh.vertices, submesh.indices, bmin, bmax, gridRes[L]);
			// Accept only if it meaningfully reduced triangle count vs the previous level.
			if (!dec.is_valid() || dec.indices.size() < 3 || static_cast<double>(dec.indices.size()) > prevIdx * 0.7)
				continue;
			id::id_type lodId = create_mesh(dec, name + "_LOD" + std::to_string(L + 1));
			if (lodId == id::invalid_id) continue;
			Mesh* lm = get_mesh(lodId);
			if (lm) lm->set_bounds(bmin.x, bmin.y, bmin.z, bmax.x, bmax.y, bmax.z);
			chain.lods[chain.lod_count++] = lodId;
			prevIdx = static_cast<double>(dec.indices.size());
		}

		if (chain.lod_count > 1) m_lod_chains[base_mesh_id] = chain;
	}

	const ResourceRegistry::LodChain* ResourceRegistry::get_lod_chain(id::id_type base_mesh_id) const
	{
		auto it = m_lod_chains.find(base_mesh_id);
		return it != m_lod_chains.end() ? &it->second : nullptr;
	}

	bool ResourceRegistry::export_mesh_to_vmesh(id::id_type mesh_id, const std::string& filepath)
	{
		return false;
	}

	id::id_type ResourceRegistry::load_vmesh(const std::string& filepath)
	{
		if (!m_device) return id::invalid_id;

		ImportedModelData model_data = MeshSerializer::load_from_file(filepath);
		if (!model_data.is_valid())
		{
			return id::invalid_id;
		}

		return create_mesh_from_submesh(model_data.submeshes[0], model_data.name);
	}

	bool ResourceRegistry::create_srv_heap()
	{
		if (!m_device) return false;

		D3D12_DESCRIPTOR_HEAP_DESC heap_desc{};
		heap_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
		heap_desc.NumDescriptors = MAX_SRV_DESCRIPTORS;
		heap_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

		if (FAILED(m_device->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(&m_srv_heap))))
		{
			OutputDebugStringA("Failed to create SRV heap\n");
			return false;
		}

		m_srv_descriptor_size = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
		m_next_srv_index = 0;

		OutputDebugStringA("SRV heap created\n");
		return true;
	}

	void ResourceRegistry::assign_srv_to_texture(Texture* texture)
	{
		if (!texture || !m_srv_heap || !m_device) return;
		if (m_next_srv_index >= MAX_SRV_DESCRIPTORS)
		{
			OutputDebugStringA("SRV heap full\n");
			return;
		}

		D3D12_CPU_DESCRIPTOR_HANDLE cpu_handle = m_srv_heap->GetCPUDescriptorHandleForHeapStart();
		cpu_handle.ptr += m_next_srv_index * m_srv_descriptor_size;

		D3D12_GPU_DESCRIPTOR_HANDLE gpu_handle = m_srv_heap->GetGPUDescriptorHandleForHeapStart();
		gpu_handle.ptr += m_next_srv_index * m_srv_descriptor_size;

		D3D12_SHADER_RESOURCE_VIEW_DESC srv_desc{};
		srv_desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		srv_desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
		srv_desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		srv_desc.Texture2D.MipLevels = 1;
		srv_desc.Texture2D.MostDetailedMip = 0;

		m_device->CreateShaderResourceView(texture->resource(), &srv_desc, cpu_handle);
		texture->set_srv_handles(cpu_handle, gpu_handle);

		m_next_srv_index++;
	}
}
