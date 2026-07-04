#include "DX12ShaderCompiler.h"
#include <d3dcompiler.h>
#include <windows.h>
#include <fstream>
#include <vector>

namespace vortex::graphics::dx12
{
	namespace
	{
		using PFN_D3DCompile = HRESULT(WINAPI*)(LPCVOID, SIZE_T, LPCSTR, const D3D_SHADER_MACRO*, ID3DInclude*,
												LPCSTR, LPCSTR, UINT, UINT, ID3DBlob**, ID3DBlob**);
		using PFN_D3DCreateBlob = HRESULT(WINAPI*)(SIZE_T, ID3DBlob**);

		HMODULE compiler_module()
		{
			static HMODULE m = []() {
				HMODULE h = LoadLibraryW(L"d3dcompiler_47.dll");
				if (!h) h = LoadLibraryW(L"d3dcompiler_43.dll");
				return h;
			}();
			return m;
		}

		PFN_D3DCompile get_d3d_compile()
		{
			HMODULE m = compiler_module();
			if (!m) return nullptr;
			static auto fn = reinterpret_cast<PFN_D3DCompile>(GetProcAddress(m, "D3DCompile"));
			return fn;
		}

		PFN_D3DCreateBlob get_d3d_create_blob()
		{
			HMODULE m = compiler_module();
			if (!m) return nullptr;
			static auto fn = reinterpret_cast<PFN_D3DCreateBlob>(GetProcAddress(m, "D3DCreateBlob"));
			return fn;
		}

		void sh_log(const std::string& s) { OutputDebugStringA(("[shaders] " + s + "\n").c_str()); }

		std::string narrow(const std::wstring& w)
		{
			if (w.empty()) return std::string();
			int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
			std::string s(n, '\0');
			WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
			return s;
		}

		std::wstring exe_dir()
		{
			wchar_t buf[MAX_PATH]{};
			GetModuleFileNameW(nullptr, buf, MAX_PATH);
			std::wstring p(buf);
			auto slash = p.find_last_of(L"\\/");
			return slash == std::wstring::npos ? L"." : p.substr(0, slash);
		}

		bool dir_exists(const std::wstring& d)
		{
			DWORD a = GetFileAttributesW(d.c_str());
			return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
		}

		// ID3DInclude rooted at the shaders dir so `#include "Common.hlsli"` resolves during dev compiles.
		class ShaderInclude : public ID3DInclude
		{
		public:
			explicit ShaderInclude(std::wstring root) : m_root(std::move(root)) {}
			HRESULT __stdcall Open(D3D_INCLUDE_TYPE, LPCSTR fileName, LPCVOID, LPCVOID* ppData, UINT* pBytes) override
			{
				std::wstring file = m_root + L"\\" + std::wstring(fileName, fileName + strlen(fileName));
				std::ifstream f(file, std::ios::binary | std::ios::ate);
				if (!f) return E_FAIL;
				size_t n = (size_t)f.tellg(); f.seekg(0);
				char* data = new (std::nothrow) char[n];
				if (!data) return E_OUTOFMEMORY;
				f.read(data, n);
				*ppData = data; *pBytes = (UINT)n;
				return S_OK;
			}
			HRESULT __stdcall Close(LPCVOID pData) override { delete[] static_cast<const char*>(pData); return S_OK; }
		private:
			std::wstring m_root;
		};

		bool read_file(const std::wstring& path, std::vector<char>& out)
		{
			std::ifstream f(path, std::ios::binary | std::ios::ate);
			if (!f) return false;
			size_t n = (size_t)f.tellg(); f.seekg(0);
			out.resize(n);
			if (n) f.read(out.data(), n);
			return true;
		}

		bool write_blob(const std::wstring& path, ID3DBlob* blob)
		{
			if (!blob) return false;
			std::ofstream f(path, std::ios::binary);
			if (!f) return false;
			f.write(static_cast<const char*>(blob->GetBufferPointer()), (std::streamsize)blob->GetBufferSize());
			return f.good();
		}
	}

	const std::wstring& DX12ShaderCompiler::shaders_dir()
	{
		// Resolved once. Make-or-break for dev: the editor exe lives in <repo>/x64/Debug, so we walk up to find
		// <repo>/Engine/Shaders. Shipped games get a flat <exe>/Shaders (copied by GameExporter).
		static std::wstring dir = []() -> std::wstring {
			std::wstring exe = exe_dir();
			if (dir_exists(exe + L"\\Shaders")) return exe + L"\\Shaders";           // shipped layout
			std::wstring p = exe;
			for (int i = 0; i < 7; ++i)                                              // dev: walk up to the repo
			{
				if (dir_exists(p + L"\\Engine\\Shaders")) return p + L"\\Engine\\Shaders";
				auto slash = p.find_last_of(L"\\/");
				if (slash == std::wstring::npos) break;
				p = p.substr(0, slash);
			}
			sh_log("WARNING: could not resolve a shaders directory");
			return std::wstring();
		}();
		return dir;
	}

	ComPtr<ID3DBlob> DX12ShaderCompiler::load_cso(const std::wstring& path)
	{
		std::vector<char> bytes;
		if (!read_file(path, bytes) || bytes.empty()) return nullptr;
		auto createBlob = get_d3d_create_blob();
		if (!createBlob) return nullptr;
		ComPtr<ID3DBlob> blob;
		if (FAILED(createBlob(bytes.size(), &blob)) || !blob) return nullptr;
		memcpy(blob->GetBufferPointer(), bytes.data(), bytes.size());
		return blob;
	}

	ComPtr<ID3DBlob> DX12ShaderCompiler::compile_from_file(const std::wstring& path, const std::string& entry, const std::string& target)
	{
		auto compile = get_d3d_compile();
		if (!compile) { sh_log("d3dcompiler not available"); return nullptr; }

		std::vector<char> src;
		if (!read_file(path, src) || src.empty()) { sh_log("missing/empty shader file: " + narrow(path)); return nullptr; }

		UINT flags = 0;
#ifdef _DEBUG
		flags |= D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#else
		flags |= D3DCOMPILE_OPTIMIZATION_LEVEL3;
#endif
		ShaderInclude inc(shaders_dir());
		std::string srcName = narrow(path);
		ComPtr<ID3DBlob> blob, error;
		HRESULT hr = compile(src.data(), src.size(), srcName.c_str(), nullptr, &inc,
							  entry.c_str(), target.c_str(), flags, 0, &blob, &error);
		if (FAILED(hr))
		{
			if (error) sh_log(std::string("compile failed: ") + static_cast<const char*>(error->GetBufferPointer()));
			else sh_log("compile failed (no error blob): " + srcName);
			return nullptr;
		}
		return blob;
	}

	ComPtr<ID3DBlob> DX12ShaderCompiler::load_shader(const std::string& name, const std::string& stage,
													const std::string& entry, const std::string& target)
	{
		const std::wstring& dir = shaders_dir();
		if (dir.empty()) { sh_log("shaders dir not found; cannot load '" + name + "'"); return nullptr; }
		std::wstring wname(name.begin(), name.end());
		std::wstring wstage(stage.begin(), stage.end());

		// 1) precompiled blob (ship fast-path)
		if (auto blob = load_cso(dir + L"\\bin\\" + wname + L"." + wstage + L".cso"))
			return blob;

		// 2) compile from source (dev + hot-reload)
		return compile_from_file(dir + L"\\" + wname + L".hlsl", entry, target);
	}

	int DX12ShaderCompiler::precompile_builtins(const std::wstring& out_bin_dir)
	{
		// The canonical ship-list: every (shader, stage) the engine's pipelines ask load_shader() for. Keep in
		// sync with the load_shader() call sites (DX12Pipeline3D/Skybox/Grid/MotionVector/Upscale/Pipeline).
		// skinned has NO ps of its own — the skinned PSO reuses standard's pixel shader.
		struct BuiltinShader { const char* name; const char* stage; const char* entry; const char* target; };
		static const BuiltinShader kBuiltins[] =
		{
			{ "standard",     "vs", "VSMain",  "vs_5_0" }, { "standard",     "ps", "PSMain",  "ps_5_0" },
			{ "skinned",      "vs", "VSMain",  "vs_5_0" },
			{ "skybox",       "vs", "SkyVS",   "vs_5_0" }, { "skybox",       "ps", "SkyPS",   "ps_5_0" },
			{ "grid",         "vs", "GridVS",  "vs_5_0" }, { "grid",         "ps", "GridPS",  "ps_5_0" },
			{ "motionvector", "vs", "MvecVS",  "vs_5_0" }, { "motionvector", "ps", "MvecPS",  "ps_5_0" },
			{ "upscale",      "vs", "VSMain",  "vs_5_0" }, { "upscale",      "ps", "PSMain",  "ps_5_0" },
			{ "basic",        "vs", "BasicVS", "vs_5_0" }, { "basic",        "ps", "BasicPS", "ps_5_0" },
		};

		const std::wstring& dir = shaders_dir();
		if (dir.empty()) { sh_log("precompile: shaders dir not found"); return 0; }

		CreateDirectoryW(out_bin_dir.c_str(), nullptr); // no-op if it already exists (GameExporter also creates it)

		int written = 0;
		constexpr int expected = (int)(sizeof(kBuiltins) / sizeof(kBuiltins[0]));
		for (const auto& s : kBuiltins)
		{
			std::string nm(s.name), st(s.stage);
			std::wstring wname(nm.begin(), nm.end()), wstage(st.begin(), st.end());
			auto blob = compile_from_file(dir + L"\\" + wname + L".hlsl", s.entry, s.target);
			if (!blob) { sh_log(std::string("precompile FAILED: ") + s.name + "." + s.stage); continue; }
			if (write_blob(out_bin_dir + L"\\" + wname + L"." + wstage + L".cso", blob.Get())) ++written;
			else sh_log(std::string("precompile: could not write ") + s.name + "." + s.stage + ".cso");
		}
		sh_log("precompiled " + std::to_string(written) + "/" + std::to_string(expected) + " built-in shader blobs -> " + narrow(out_bin_dir));
		// ALL-OR-NOTHING contract: a partial set would ship a game with some pipelines shaderless (no .hlsl source
		// is packaged when precompile "succeeds"), so report 0 unless EVERY blob landed — the exporter then falls
		// back to shipping loose .hlsl and the build always renders.
		return written == expected ? written : 0;
	}
}
