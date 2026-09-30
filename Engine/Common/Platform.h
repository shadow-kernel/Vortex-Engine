#pragma once

// ============================================================================
// Vortex platform layer — the ONLY engine header that knows which OS it is on.
//
// Everything outside Graphics/DX12 and the Win32 GameHost goes through this
// header instead of <Windows.h>: debug output, environment variables, dynamic
// libraries, temp / executable directories, timers and process memory. On
// Windows every function maps 1:1 onto the Win32 call the engine used before;
// on macOS / Linux it maps onto POSIX / Mach. Non-MSVC compilers additionally
// get the MSVC "secure CRT" functions the code base uses (fopen_s, strncpy_s,
// getenv_s, ...) so existing call sites compile unchanged.
//
// Included first by CommonHeaders.h, so it is available everywhere.
// ============================================================================

// ---- Platform detection ----------------------------------------------------
#if defined(_WIN32)
	#define VORTEX_PLATFORM_WINDOWS 1
#elif defined(__APPLE__)
	#include <TargetConditionals.h>
	#define VORTEX_PLATFORM_APPLE 1
	#if TARGET_OS_OSX
		#define VORTEX_PLATFORM_MACOS 1
	#endif
#elif defined(__linux__)
	#define VORTEX_PLATFORM_LINUX 1
#endif

#ifndef VORTEX_PLATFORM_WINDOWS
	#define VORTEX_PLATFORM_WINDOWS 0
#endif
#ifndef VORTEX_PLATFORM_APPLE
	#define VORTEX_PLATFORM_APPLE 0
#endif
#ifndef VORTEX_PLATFORM_MACOS
	#define VORTEX_PLATFORM_MACOS 0
#endif
#ifndef VORTEX_PLATFORM_LINUX
	#define VORTEX_PLATFORM_LINUX 0
#endif
#define VORTEX_PLATFORM_POSIX (VORTEX_PLATFORM_APPLE || VORTEX_PLATFORM_LINUX)

// ---- Render backend availability ------------------------------------------
// DirectX 12 is the Windows backend. The CMake build defines VORTEX_NO_DX12 on
// every other platform (and can define it on Windows to build the portable core
// alone); the Visual Studio projects never define it, so they behave as before.
#if VORTEX_PLATFORM_WINDOWS && !defined(VORTEX_NO_DX12)
	#define VORTEX_HAS_DX12 1
#else
	#define VORTEX_HAS_DX12 0
#endif
// The SDL GPU backend (Metal on macOS, Vulkan on Linux). Defined to 1 by the CMake build on those
// platforms; never on the Visual Studio projects.
#ifndef VORTEX_HAS_SDLGPU
	#define VORTEX_HAS_SDLGPU 0
#endif

// ---- Compiler helpers ------------------------------------------------------
#if defined(_MSC_VER)
	#define VORTEX_FORCEINLINE __forceinline
	#define VORTEX_DEBUGBREAK() __debugbreak()
#else
	#define VORTEX_FORCEINLINE inline __attribute__((always_inline))
	#define VORTEX_DEBUGBREAK() __builtin_trap()
#endif

// Exported C entry point of the VortexAPI shared library
// (VortexAPI.dll on Windows, libVortexAPI.dylib on macOS, libVortexAPI.so on Linux).
#if VORTEX_PLATFORM_WINDOWS
	#define VORTEX_API_EXPORT extern "C" __declspec(dllexport)
#else
	#define VORTEX_API_EXPORT extern "C" __attribute__((visibility("default")))
#endif

// ---- MSVC secure-CRT compatibility for clang / gcc --------------------------
// The engine was written against MSVC's *_s functions. Instead of touching every
// call site, non-MSVC builds get equivalent inline definitions here. Semantics
// follow MSVC except that an over-long source string is truncated (MSVC fails
// with ERANGE and empties the buffer); the engine only ever passes
// count == destsz - 1, where both behaviours are identical.
#if !defined(_MSC_VER)
#include <cerrno>
#include <climits>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>

using errno_t = int;

#ifndef _TRUNCATE
	#define _TRUNCATE (static_cast<size_t>(-1))
#endif
#ifndef MAX_PATH
	#ifdef PATH_MAX
		#define MAX_PATH PATH_MAX
	#else
		#define MAX_PATH 4096
	#endif
#endif
#ifndef _countof
	#define _countof(array) (sizeof(array) / sizeof((array)[0]))
#endif

inline errno_t fopen_s(FILE** file, const char* filename, const char* mode)
{
	if (!file) return EINVAL;
	*file = std::fopen(filename, mode);
	return *file ? 0 : (errno ? errno : EINVAL);
}

inline errno_t strncpy_s(char* dest, size_t destsz, const char* src, size_t count)
{
	if (!dest || destsz == 0) return EINVAL;
	if (!src) { dest[0] = '\0'; return EINVAL; }
	size_t n = (count == _TRUNCATE) ? std::strlen(src) : strnlen(src, count);
	if (n >= destsz) n = destsz - 1;
	std::memcpy(dest, src, n);
	dest[n] = '\0';
	return 0;
}

template<size_t N>
inline errno_t strncpy_s(char (&dest)[N], const char* src, size_t count)
{
	return strncpy_s(dest, N, src, count);
}

inline errno_t strncat_s(char* dest, size_t destsz, const char* src, size_t count)
{
	if (!dest || destsz == 0 || !src) return EINVAL;
	const size_t used = strnlen(dest, destsz);
	if (used >= destsz) return EINVAL;   // dest is not NUL-terminated
	size_t n = (count == _TRUNCATE) ? std::strlen(src) : strnlen(src, count);
	const size_t room = destsz - used - 1;
	if (n > room) n = room;
	std::memcpy(dest + used, src, n);
	dest[used + n] = '\0';
	return 0;
}

template<size_t N>
inline errno_t strncat_s(char (&dest)[N], const char* src, size_t count)
{
	return strncat_s(dest, N, src, count);
}

// MSVC semantics: *len receives the value size INCLUDING the terminating NUL, 0 when unset.
inline errno_t getenv_s(size_t* len, char* buffer, size_t bufsz, const char* name)
{
	const char* value = name ? std::getenv(name) : nullptr;
	if (!value)
	{
		if (len) *len = 0;
		if (buffer && bufsz) buffer[0] = '\0';
		return 0;
	}
	const size_t needed = std::strlen(value) + 1;
	if (len) *len = needed;
	if (buffer && bufsz)
	{
		if (needed > bufsz) { buffer[0] = '\0'; return ERANGE; }
		std::memcpy(buffer, value, needed);
	}
	return 0;
}

// The returned buffer is malloc'ed and must be released with free(), exactly like MSVC.
inline errno_t _dupenv_s(char** buffer, size_t* len, const char* name)
{
	if (!buffer) return EINVAL;
	const char* value = name ? std::getenv(name) : nullptr;
	if (!value)
	{
		*buffer = nullptr;
		if (len) *len = 0;
		return 0;
	}
	const size_t needed = std::strlen(value) + 1;
	*buffer = static_cast<char*>(std::malloc(needed));
	if (!*buffer) { if (len) *len = 0; return ENOMEM; }
	std::memcpy(*buffer, value, needed);
	if (len) *len = needed;
	return 0;
}

inline long long _ftelli64(FILE* file) { return static_cast<long long>(ftello(file)); }
inline int _fseeki64(FILE* file, long long offset, int origin) { return fseeko(file, static_cast<off_t>(offset), origin); }
#endif // !_MSC_VER

// ---- Platform services (implemented in Platform.cpp) -----------------------
#include <cstddef>
#include <cstdint>
#include <string>

namespace vortex::platform
{
	// Debugger / console diagnostics: OutputDebugStringA on Windows, stderr elsewhere. No newline is appended.
	void debug_output(const char* text);

	// Environment variables: "" when unset. env_flag() is true when the value starts with '1'.
	std::string env_string(const char* name);
	bool env_flag(const char* name);

	// Dynamic libraries (LoadLibrary / dlopen). `name` is used verbatim; build the platform spelling with
	// shared_library_name("phonon") -> "phonon.dll" / "libphonon.dylib" / "libphonon.so".
	using library_handle = void*;
	library_handle load_library(const char* name);
	void* library_symbol(library_handle library, const char* symbol);
	void unload_library(library_handle library);
	std::string shared_library_name(const char* base_name);

	// Directories, always WITH a trailing separator: the temp directory and the running executable's directory.
	std::string temp_directory();
	std::string executable_directory();
	inline constexpr char path_separator = VORTEX_PLATFORM_WINDOWS ? '\\' : '/';

	// Monotonic milliseconds since an arbitrary origin (GetTickCount64 / steady_clock).
	uint64_t tick_count_ms();

	// Resident memory of this process in bytes (Windows working set / Mach resident size / Linux RSS); 0 if unknown.
	size_t process_resident_bytes();
}
