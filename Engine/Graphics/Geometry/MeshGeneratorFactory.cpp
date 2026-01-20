#include "MeshGeneratorFactory.h"

namespace vortex::graphics
{
	MeshGeneratorFactory& MeshGeneratorFactory::instance()
	{
		static MeshGeneratorFactory inst;
		return inst;
	}

	MeshGeneratorFactory::MeshGeneratorFactory()
	{
		// Register built-in generators
		register_generator<CubeGenerator>("Cube");
		register_generator<SphereGenerator>("Sphere");
		register_generator<PlaneGenerator>("Plane");
		register_generator<CylinderGenerator>("Cylinder");
		register_generator<ConeGenerator>("Cone");
	}

	std::unique_ptr<IMeshGenerator> MeshGeneratorFactory::create(const std::string& type_name) const
	{
		auto it = m_creators.find(type_name);
		if (it != m_creators.end())
		{
			return it->second();
		}
		return nullptr;
	}

	std::vector<std::string> MeshGeneratorFactory::get_registered_types() const
	{
		std::vector<std::string> types;
		types.reserve(m_creators.size());
		for (const auto& [name, _] : m_creators)
		{
			types.push_back(name);
		}
		return types;
	}

	std::unique_ptr<CubeGenerator> MeshGeneratorFactory::create_cube(float size)
	{
		return std::make_unique<CubeGenerator>(size);
	}

	std::unique_ptr<SphereGenerator> MeshGeneratorFactory::create_sphere(float radius, u32 slices, u32 stacks)
	{
		return std::make_unique<SphereGenerator>(radius, slices, stacks);
	}

	std::unique_ptr<PlaneGenerator> MeshGeneratorFactory::create_plane(float width, float depth)
	{
		return std::make_unique<PlaneGenerator>(width, depth);
	}

	std::unique_ptr<CylinderGenerator> MeshGeneratorFactory::create_cylinder(float radius, float height, u32 slices)
	{
		return std::make_unique<CylinderGenerator>(radius, height, slices);
	}

	std::unique_ptr<ConeGenerator> MeshGeneratorFactory::create_cone(float radius, float height, u32 slices)
	{
		return std::make_unique<ConeGenerator>(radius, height, slices);
	}
}
