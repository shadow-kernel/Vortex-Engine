#pragma once

#include "../../Common/CommonHeaders.h"
#include "Texture.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>
#include <string>

namespace vortex::graphics
{
	using Microsoft::WRL::ComPtr;

	/// <summary>
	/// PBR Material properties for GPU constant buffer.
	/// Aligned to 16 bytes for GPU compatibility.
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
		float _pad_h{ 0.0f };                                     // 4 bytes (keep the struct 16-byte aligned)
	};



	/// <summary>
	/// PBR Material class supporting full texture set:
	/// - Albedo (Base Color)
	/// - Normal Map (DirectX or OpenGL format)
	/// - Metallic
	/// - Roughness
	/// - Ambient Occlusion (AO)
	/// </summary>
	class Material
	{
	public:
		Material() = default;
		~Material() = default;

		bool create(ID3D12Device* device);
		void destroy();

		// Property setters
		void set_base_color(const DirectX::XMFLOAT4& color);
		void set_metallic(float value);
		void set_roughness(float value);
		void set_ao(float value);
		void set_normal_strength(float value);
		void set_use_directx_normals(bool use_directx);
		void set_unlit(bool is_unlit);
		void set_emissive_strength(float strength);
		void set_uv_tiling(float u, float v);
		void set_height_scale(float value);
		// Blend mode (#33): 0 = opaque, 1 = alpha blend, 2 = additive. CPU-side draw-routing state
		// only (the renderer picks the PSO + pass from it) — deliberately NOT in MaterialProperties,
		// whose layout is a GPU constant-buffer ABI.
		void set_blend_mode(u32 mode) { m_blend_mode = (mode <= 2) ? mode : 0; }
		u32 blend_mode() const { return m_blend_mode; }

		// Texture setters
		void set_albedo_texture(Texture* texture);
		void set_normal_texture(Texture* texture);
		void set_metallic_texture(Texture* texture);
		void set_roughness_texture(Texture* texture);
		void set_ao_texture(Texture* texture);
		void set_height_texture(Texture* texture);

		// Property getters
		const MaterialProperties& properties() const { return m_properties; }
		ID3D12Resource* constant_buffer() const { return m_constant_buffer.Get(); }
		bool is_unlit() const { return m_properties.is_unlit != 0; }

		// Texture getters
		Texture* albedo_texture() const { return m_albedo_texture; }
		Texture* normal_texture() const { return m_normal_texture; }
		Texture* metallic_texture() const { return m_metallic_texture; }
		Texture* roughness_texture() const { return m_roughness_texture; }
		Texture* ao_texture() const { return m_ao_texture; }
		Texture* height_texture() const { return m_height_texture; }

		bool is_valid() const { return m_constant_buffer != nullptr; }
		bool uses_directx_normals() const { return m_properties.use_directx_normals != 0; }

		void update_gpu_data();

		// Name for editor display
		const std::string& name() const { return m_name; }
		void set_name(const std::string& name) { m_name = name; }

	private:
		MaterialProperties m_properties;
		u32 m_blend_mode{ 0 };   // 0 opaque, 1 alpha blend, 2 additive (#33) — not part of the CB
		ComPtr<ID3D12Resource> m_constant_buffer;
		void* m_mapped_data{ nullptr };
		std::string m_name{ "New Material" };

		// PBR Texture slots
		Texture* m_albedo_texture{ nullptr };
		Texture* m_normal_texture{ nullptr };
		Texture* m_metallic_texture{ nullptr };
		Texture* m_roughness_texture{ nullptr };
		Texture* m_ao_texture{ nullptr };
		Texture* m_height_texture{ nullptr };
	};
}
