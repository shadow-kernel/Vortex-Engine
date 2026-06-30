#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <string>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	// Centralized shader loader/compiler — the SINGLE place that owns the d3dcompiler_47 LoadLibrary dance, the
	// debug/release compile-flag policy, and #include resolution. Previously every pipeline copy-pasted its own
	// get_d3d_compile + an embedded HLSL string; now all engine HLSL lives in files under Engine/Shaders/ (dev) or
	// <exe>/Shaders/ (shipped), and pipelines just ask for them by name.
	//
	// Resolution: load_shader prefers a precompiled Shaders/bin/<name>.<stage>.cso next to the exe (ship fast-path —
	// no per-launch compile), else compiles Shaders/<name>.hlsl from disk (dev + hot-reload). Returns nullptr on any
	// failure so the caller decides whether the pass is fatal or has a fallback.
	class DX12ShaderCompiler
	{
	public:
		// name  = base file name (e.g. "upscale" -> Shaders/upscale.hlsl or Shaders/bin/upscale.vs.cso)
		// stage = "vs" / "ps" / "cs" (only used for the .cso file name)
		// entry = HLSL entry point (e.g. "VSMain"); target = shader model (e.g. "vs_5_0")
		static ComPtr<ID3DBlob> load_shader(const std::string& name, const std::string& stage,
											const std::string& entry, const std::string& target);

		// Compile an .hlsl file from disk, with #include rooted at the shaders dir. Logs + returns nullptr on failure.
		static ComPtr<ID3DBlob> compile_from_file(const std::wstring& path, const std::string& entry, const std::string& target);

		// Load a precompiled .cso blob from disk. Returns nullptr if missing/unreadable.
		static ComPtr<ID3DBlob> load_cso(const std::wstring& path);

		// The resolved shaders directory: <exe>/Shaders (shipped) or <repo>/Engine/Shaders (dev). Empty if not found.
		static const std::wstring& shaders_dir();
	};
}
