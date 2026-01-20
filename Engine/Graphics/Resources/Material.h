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

	struct MaterialProperties
	{
		DirectX::XMFLOAT4 base_color{ 1.0f, 1.0f, 1.0f, 1.0f };
		float metallic{ 0.0f };
		float roughness{ 0.5f };
		float ao{ 1.0f };
		float padding{ 0.0f };
	};

	class Material
	{
	public:
		Material() = default;
		~Material() = default;

		bool create(ID3D12Device* device);
		void destroy();

		void set_base_color(const DirectX::XMFLOAT4& color);
		void set_metallic(float value);
		void set_roughness(float value);

		void set_albedo_texture(Texture* texture) { m_albedo_texture = texture; }
		void set_normal_texture(Texture* texture) { m_normal_texture = texture; }
		void set_metallic_roughness_texture(Texture* texture) { m_metallic_roughness_texture = texture; }

		const MaterialProperties& properties() const { return m_properties; }
		ID3D12Resource* constant_buffer() const { return m_constant_buffer.Get(); }

		Texture* albedo_texture() const { return m_albedo_texture; }
		Texture* normal_texture() const { return m_normal_texture; }
		Texture* metallic_roughness_texture() const { return m_metallic_roughness_texture; }

		bool is_valid() const { return m_constant_buffer != nullptr; }

		void update_gpu_data();

	private:
		MaterialProperties m_properties;
		ComPtr<ID3D12Resource> m_constant_buffer;
		void* m_mapped_data{ nullptr };

		Texture* m_albedo_texture{ nullptr };
		Texture* m_normal_texture{ nullptr };
		Texture* m_metallic_roughness_texture{ nullptr };
	};
}
