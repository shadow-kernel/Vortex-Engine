#include "Platform.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#if VORTEX_PLATFORM_WINDOWS
	#ifndef NOMINMAX
		#define NOMINMAX
	#endif
	#ifndef WIN32_LEAN_AND_MEAN
		#define WIN32_LEAN_AND_MEAN
	#endif
	#include <Windows.h>
	#include <psapi.h>
	#pragma comment(lib, "psapi.lib")
#else
	#include <dlfcn.h>
	#include <unistd.h>
	#include <climits>
	#if VORTEX_PLATFORM_APPLE
		#include <mach/mach.h>
		#include <mach-o/dyld.h>
	#endif
#endif

namespace vortex::platform
{
	void debug_output(const char* text)
	{
		if (!text) return;
#if VORTEX_PLATFORM_WINDOWS
		OutputDebugStringA(text);
#else
		std::fputs(text, stderr);
#endif
	}

	std::string env_string(const char* name)
	{
		if (!name) return {};
#if VORTEX_PLATFORM_WINDOWS
		char buffer[4096];
		const DWORD n = GetEnvironmentVariableA(name, buffer, sizeof(buffer));
		if (n == 0 || n >= sizeof(buffer)) return {};
		return std::string(buffer, n);
#else
		const char* value = std::getenv(name);
		return value ? std::string(value) : std::string();
#endif
	}

	bool env_flag(const char* name)
	{
		const std::string value = env_string(name);
		return !value.empty() && value[0] == '1';
	}

	library_handle load_library(const char* name)
	{
		if (!name || !*name) return nullptr;
#if VORTEX_PLATFORM_WINDOWS
		return reinterpret_cast<library_handle>(LoadLibraryA(name));
#else
		return dlopen(name, RTLD_NOW | RTLD_LOCAL);
#endif
	}

	void* library_symbol(library_handle library, const char* symbol)
	{
		if (!library || !symbol) return nullptr;
#if VORTEX_PLATFORM_WINDOWS
		return reinterpret_cast<void*>(GetProcAddress(reinterpret_cast<HMODULE>(library), symbol));
#else
		return dlsym(library, symbol);
#endif
	}

	void unload_library(library_handle library)
	{
		if (!library) return;
#if VORTEX_PLATFORM_WINDOWS
		FreeLibrary(reinterpret_cast<HMODULE>(library));
#else
		dlclose(library);
#endif
	}

	std::string shared_library_name(const char* base_name)
	{
		const std::string base = base_name ? base_name : "";
#if VORTEX_PLATFORM_WINDOWS
		return base + ".dll";
#elif VORTEX_PLATFORM_APPLE
		return "lib" + base + ".dylib";
#else
		return "lib" + base + ".so";
#endif
	}

	std::string temp_directory()
	{
#if VORTEX_PLATFORM_WINDOWS
		char buffer[MAX_PATH + 1]{};
		const DWORD n = GetTempPathA(MAX_PATH, buffer);   // always ends with a backslash
		if (n == 0 || n > MAX_PATH) return ".\\";
		return std::string(buffer, n);
#else
		const char* tmp = std::getenv("TMPDIR");
		std::string dir = (tmp && *tmp) ? tmp : "/tmp";
		if (dir.back() != '/') dir += '/';
		return dir;
#endif
	}

	std::string executable_directory()
	{
		std::string path;
#if VORTEX_PLATFORM_WINDOWS
		char buffer[MAX_PATH]{};
		const DWORD n = GetModuleFileNameA(nullptr, buffer, MAX_PATH);
		if (n > 0 && n < MAX_PATH) path.assign(buffer, n);
#elif VORTEX_PLATFORM_APPLE
		char buffer[PATH_MAX]{};
		uint32_t size = sizeof(buffer);
		if (_NSGetExecutablePath(buffer, &size) == 0)
		{
			char resolved[PATH_MAX]{};
			path = realpath(buffer, resolved) ? resolved : buffer;
		}
#else
		char buffer[PATH_MAX]{};
		const ssize_t n = readlink("/proc/self/exe", buffer, sizeof(buffer) - 1);
		if (n > 0) path.assign(buffer, static_cast<size_t>(n));
#endif
		const size_t slash = path.find_last_of("/\\");
		return slash == std::string::npos ? std::string("./") : path.substr(0, slash + 1);
	}

	uint64_t tick_count_ms()
	{
#if VORTEX_PLATFORM_WINDOWS
		return static_cast<uint64_t>(GetTickCount64());
#else
		using namespace std::chrono;
		return static_cast<uint64_t>(duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count());
#endif
	}

	size_t process_resident_bytes()
	{
#if VORTEX_PLATFORM_WINDOWS
		PROCESS_MEMORY_COUNTERS pmc{};
		if (GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc))) return static_cast<size_t>(pmc.WorkingSetSize);
		return 0;
#elif VORTEX_PLATFORM_APPLE
		mach_task_basic_info info{};
		mach_msg_type_number_t count = MACH_TASK_BASIC_INFO_COUNT;
		if (task_info(mach_task_self(), MACH_TASK_BASIC_INFO, reinterpret_cast<task_info_t>(&info), &count) == KERN_SUCCESS)
			return static_cast<size_t>(info.resident_size);
		return 0;
#else
		FILE* f = std::fopen("/proc/self/statm", "r");
		if (!f) return 0;
		long pages = 0, resident = 0;
		const int fields = std::fscanf(f, "%ld %ld", &pages, &resident);
		std::fclose(f);
		return fields == 2 ? static_cast<size_t>(resident) * static_cast<size_t>(sysconf(_SC_PAGESIZE)) : 0;
#endif
	}
}
