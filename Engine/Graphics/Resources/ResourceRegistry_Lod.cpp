#include "ResourceRegistry_Internal.h"

namespace vortex::graphics
{
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


}
