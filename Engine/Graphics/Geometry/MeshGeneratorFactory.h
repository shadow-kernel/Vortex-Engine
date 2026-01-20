#pragma once

#include "IMeshGenerator.h"
#include "CubeGenerator.h"
#include "SphereGenerator.h"
#include "PlaneGenerator.h"
#include "CylinderGenerator.h"
#include "ConeGenerator.h"
#include <memory>
#include <string>
#include <unordered_map>
#include <functional>

namespace vortex::graphics
{
	/// <summary>
	/// Factory for creating mesh generators.
	/// Supports registration of custom generators.
	/// </summary>
	class MeshGeneratorFactory
	{
	public:
		using GeneratorCreator = std::function<std::unique_ptr<IMeshGenerator>()>;

		static MeshGeneratorFactory& instance();

		/// <summary>
		/// Register a custom mesh generator type.
		/// </summary>
		template<typename T>
		void register_generator(const std::string& type_name)
		{
			m_creators[type_name] = []() { return std::make_unique<T>(); };
		}

		/// <summary>
		/// Create a generator by type name.
		/// </summary>
		std::unique_ptr<IMeshGenerator> create(const std::string& type_name) const;

		/// <summary>
		/// Get all registered type names.
		/// </summary>
		std::vector<std::string> get_registered_types() const;

		// Convenience methods for built-in types
		static std::unique_ptr<CubeGenerator> create_cube(float size = 1.0f);
		static std::unique_ptr<SphereGenerator> create_sphere(float radius = 0.5f, u32 slices = 32, u32 stacks = 16);
		static std::unique_ptr<PlaneGenerator> create_plane(float width = 1.0f, float depth = 1.0f);
		static std::unique_ptr<CylinderGenerator> create_cylinder(float radius = 0.5f, float height = 1.0f, u32 slices = 32);
		static std::unique_ptr<ConeGenerator> create_cone(float radius = 0.5f, float height = 1.0f, u32 slices = 32);

	private:
		MeshGeneratorFactory();

		std::unordered_map<std::string, GeneratorCreator> m_creators;
	};
}
