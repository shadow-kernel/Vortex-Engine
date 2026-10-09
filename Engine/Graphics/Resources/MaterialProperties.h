#pragma once

#include "../../Common/CommonHeaders.h"

namespace vortex::graphics
{
	/// <summary>
	/// PBR Material properties for GPU constant buffer.
	/// Aligned to 16 bytes for GPU compatibility. Backend-neutral: the DX12 and the SDL GPU (Metal)
	/// backends upload this exact layout, and the .vmat serializer writes it byte for byte.
	/// </summary>
	struct MaterialProperties
	{
		DirectX::XMFLOAT4 base_color{ 1.0f, 1.0f, 1.0f, 1.0f };  // 16 bytes
		float metallic{ 0.0f };                                   // 4 bytes - dielectric default (metal=0 so a material with no PBR params pushed still reads as a normal lit surface, not a near-black metal under one light)
		float roughness{ 0.5f };                                   // 4 bytes - moderate roughness
		float ao{ 1.0f };                                         // 4 bytes
		float normal_strength{ 1.0f };                            // 4 bytes
		
		// Texture flags (which textures are bound)
		u32 has_albedo_texture{ 0 };                              // 4 bytes
		u32 has_normal_texture{ 0 };                              // 4 bytes
		u32 has_metallic_texture{ 0 };                            // 4 bytes
		u32 has_roughness_texture{ 0 };                           // 4 bytes
		u32 has_ao_texture{ 0 };                                  // 4 bytes
		u32 use_directx_normals{ 1 };                             // 4 bytes (1 = DirectX, 0 = OpenGL)
		u32 is_unlit{ 0 };                                        // 4 bytes (1 = unlit/emissive, ignores lighting)
		float emissive_strength{ 1.0f };                          // 4 bytes (brightness multiplier for unlit)
		DirectX::XMFLOAT2 uv_tiling{ 1.0f, 1.0f };                // 8 bytes (texture repeat scale; 1,1 = no tiling)
		float height_scale{ 0.05f };                              // 4 bytes (parallax/displacement depth)
		float alpha_cutoff{ 0.0f };                               // 4 bytes (#329: > 0 = AlphaTest, fragments below it are discarded; was padding)
	};
	static_assert(sizeof(MaterialProperties) == 80, "MaterialProperties is a GPU + .vmat ABI: keep it 80 bytes");
}
