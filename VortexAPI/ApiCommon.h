#pragma once

#ifndef EDITOR_INTERFACE
#define EDITOR_INTERFACE extern "C" __declspec(dllexport)
#endif // !EDITOR_INTERFACE

#include "CommonHeaders.h"
#include "Id.h"
#include "..\Engine\Components\Entity.h"
#include "..\Engine\Components\Transform.h"
#include "..\Engine\Components\MeshRenderer.h"
#include "..\Engine\Components\Skybox.h"
#include "..\Engine\Runtime\SceneManager.h"
#include "..\Engine\Runtime\ResourceManager.h"
#include "..\Engine\Runtime\AssetDatabase.h"
#include "..\Engine\Runtime\PrefabService.h"
#include "..\Engine\Runtime\RenderLoop.h"
#include "..\Engine\Runtime\GameHost.h"
#include "..\Engine\Runtime\Systems\RenderSystem.h"
#include "..\Engine\Runtime\Systems\RenderSystemDX12.h"
#include "..\Engine\Runtime\Systems\PhysicsSystem.h"
#include "..\Engine\Runtime\Systems\AudioSystem.h"
#include "..\Engine\Graphics\Resources\ResourceRegistry.h"
#include "..\Engine\Graphics\Importers\ModelImporter.h"
#include "..\Engine\Graphics\DX12\DX12Renderer.h"
#include "..\Engine\Input\InputSystem.h"
#include "..\Engine\Components\Camera.h"

using namespace vortex;

namespace {

	struct transform_component
	{
		f32 position[3];
		f32 rotation[3];
		f32 scale[3];

		transform::init_info to_init_info()
		{
			using namespace DirectX;
			transform::init_info info{};

			// Finite-guard at the engine boundary: a NaN/Inf from ANY script (movement, etc.) must never
			// reach the quaternion/matrix math — that is what crashed the engine on bad key combos.
			// (v==v is false for NaN; the magnitude check rejects +/-Inf — no <cmath> needed.)
			auto fin = [](f32 v, f32 fallback) -> f32 { return (v == v && v <= 3.4e38f && v >= -3.4e38f) ? v : fallback; };
			f32 pos[3] = { fin(position[0], 0.0f), fin(position[1], 0.0f), fin(position[2], 0.0f) };
			f32 rot[3] = { fin(rotation[0], 0.0f), fin(rotation[1], 0.0f), fin(rotation[2], 0.0f) };
			f32 scl[3] = { fin(scale[0], 1.0f), fin(scale[1], 1.0f), fin(scale[2], 1.0f) };

			memcpy(&info.position[0], &pos[0], sizeof(f32) * _countof(info.position));
			memcpy(&info.scale[0], &scl[0], sizeof(f32) * _countof(info.scale));

			XMFLOAT3A rotf{ &rot[0] };
			XMVECTOR quat{ XMQuaternionRotationRollPitchYawFromVector(XMLoadFloat3A(&rotf)) };
			XMFLOAT4A rotation_quat{};
			XMStoreFloat4A(&rotation_quat, quat);
			if (!(rotation_quat.x == rotation_quat.x) || !(rotation_quat.y == rotation_quat.y) ||
				!(rotation_quat.z == rotation_quat.z) || !(rotation_quat.w == rotation_quat.w))
			{ rotation_quat.x = rotation_quat.y = rotation_quat.z = 0.0f; rotation_quat.w = 1.0f; } // identity fallback

			memcpy(&info.rotation[0], &rotation_quat.x, sizeof(f32) * _countof(info.rotation));

			return info;
		}
	};

	struct game_entity_descriptor
	{
		transform_component transform;
	};

struct prefab_descriptor
{
	const char* path;
};

struct resource_descriptor
{
	const char* path;
};

	game_entity::entity entity_from_id(id::id_type id)
	{
		return game_entity::entity{ game_entity::entity_id{ id } };
	}
}
