#include "../ApiCommon.h"

EDITOR_INTERFACE void InitializeAssetDatabase(const char* project_path)
{
	if (!project_path) return;
	runtime::AssetDatabase::instance().initialize_with_project_path(project_path);
}

EDITOR_INTERFACE void InitializeAssetDatabaseWithManifest(const char* manifest_path)
{
	if (!manifest_path) return;
	runtime::AssetDatabase::instance().initialize_with_manifest(manifest_path);
}

EDITOR_INTERFACE void ShutdownAssetDatabase()
{
	runtime::AssetDatabase::instance().shutdown();
}

EDITOR_INTERFACE const char* GetAssetPathByGuid(const char* guid)
{
	if (!guid) return nullptr;
	return runtime::AssetDatabase::instance().get_asset_path_by_guid(guid);
}

EDITOR_INTERFACE bool HasAsset(const char* guid)
{
	if (!guid) return false;
	return runtime::AssetDatabase::instance().has_asset(guid);
}

// GUID-based resource loading
EDITOR_INTERFACE long LoadMeshByGuid(const char* guid)
{
	if (!guid) return 0;
	auto handle = runtime::resource_manager::load_mesh_by_guid(guid);
	return static_cast<long>(handle.value);
}

EDITOR_INTERFACE long LoadTextureByGuid(const char* guid)
{
	if (!guid) return 0;
	auto handle = runtime::resource_manager::load_texture_by_guid(guid);
	return static_cast<long>(handle.value);
}

EDITOR_INTERFACE long LoadMaterialByGuid(const char* guid)
{
	if (!guid) return 0;
	auto handle = runtime::resource_manager::load_material_by_guid(guid);
	return static_cast<long>(handle.value);
}

// MeshRenderer Component API
