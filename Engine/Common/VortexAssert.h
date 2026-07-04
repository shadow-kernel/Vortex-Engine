#pragma once

#include <cstdlib>
#include <cstdio>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <Windows.h>
#include <debugapi.h>
#endif

namespace vortex
{
	/// <summary>
	/// Handles assertion failures with optional message and debug break.
	/// </summary>
	inline void assertion_failed(const char* expression, const char* file, int line, const char* message = nullptr)
	{
		char buffer[2048];
		if (message)
		{
			snprintf(buffer, sizeof(buffer), 
				"ASSERTION FAILED!\n\nExpression: %s\nMessage: %s\n\nFile: %s\nLine: %d", 
				expression, message, file, line);
		}
		else
		{
			snprintf(buffer, sizeof(buffer), 
				"ASSERTION FAILED!\n\nExpression: %s\n\nFile: %s\nLine: %d", 
				expression, file, line);
		}

#ifdef _WIN32
		OutputDebugStringA(buffer);
		OutputDebugStringA("\n");

		// Debug builds: LOG the failed assertion (debug output + a log file) and CONTINUE.
		//
		// This used to pop a MODAL MessageBoxA here. That is fatal for the editor: engine init runs on the WPF
		// UI thread, and the startup splash is Topmost — so the message box appears BEHIND the splash, invisible,
		// and the UI thread blocks in the dialog's modal message loop forever. The editor then "hangs at
		// Starting…", and under the VS debugger you land in break mode with "only native code is running" (the
		// thread sitting inside the native MessageBox loop). Standalone it works because the tripping assert is
		// timing-dependent. Logging + continue mirrors that working path and removes the deadlock.
		//
		// Devs who WANT to stop on an assert can set the env var VORTEX_ASSERT_BREAK=1 to break into an attached
		// debugger at the failing assert instead.
		#ifdef _DEBUG
		{
			char _logpath[MAX_PATH];
			char _tmp[MAX_PATH];
			DWORD _n = GetTempPathA(MAX_PATH, _tmp);
			if (_n > 0 && _n < MAX_PATH)
			{
				snprintf(_logpath, sizeof(_logpath), "%svortex_asserts.log", _tmp);
				FILE* _f = nullptr;
				if (fopen_s(&_f, _logpath, "a") == 0 && _f)
				{
					fprintf(_f, "%s\n\n", buffer);
					fclose(_f);
				}
			}
			char _brk[8];
			if (GetEnvironmentVariableA("VORTEX_ASSERT_BREAK", _brk, sizeof(_brk)) > 0 && _brk[0] == '1' && IsDebuggerPresent())
			{
				__debugbreak();
			}
		}
		#else
		// In release, just log and continue
		#endif
#else
		fprintf(stderr, "%s\n", buffer);
		#ifdef _DEBUG
		std::abort();
		#endif
#endif
	}

	/// <summary>
	/// Handles verification failures (always active).
	/// </summary>
	inline bool verification_failed(const char* expression, const char* file, int line, const char* message = nullptr)
	{
		char buffer[2048];
		if (message)
		{
			snprintf(buffer, sizeof(buffer), 
				"VERIFICATION FAILED!\n\nExpression: %s\nMessage: %s\n\nFile: %s\nLine: %d", 
				expression, message, file, line);
		}
		else
		{
			snprintf(buffer, sizeof(buffer), 
				"VERIFICATION FAILED!\n\nExpression: %s\n\nFile: %s\nLine: %d", 
				expression, file, line);
		}

#ifdef _WIN32
		OutputDebugStringA(buffer);
		OutputDebugStringA("\n");
#else
		fprintf(stderr, "%s\n", buffer);
#endif
		return false;
	}
}

// ============================================================================
// VORTEX_ASSERT - Debug-only assertions (removed in Release builds)
// ============================================================================

#ifdef _DEBUG

/// <summary>
/// Assert that an expression is true. Only active in Debug builds.
/// </summary>
#define VORTEX_ASSERT(expr) \
	do { \
		if (!(expr)) { \
			vortex::assertion_failed(#expr, __FILE__, __LINE__); \
		} \
	} while (0)

/// <summary>
/// Assert with a custom message. Only active in Debug builds.
/// </summary>
#define VORTEX_ASSERT_MSG(expr, msg) \
	do { \
		if (!(expr)) { \
			vortex::assertion_failed(#expr, __FILE__, __LINE__, msg); \
		} \
	} while (0)

/// <summary>
/// Assert that code should never be reached. Only active in Debug builds.
/// </summary>
#define VORTEX_ASSERT_UNREACHABLE() \
	do { \
		vortex::assertion_failed("UNREACHABLE CODE", __FILE__, __LINE__, "This code path should never be executed"); \
	} while (0)

/// <summary>
/// Assert that a pointer is not null. Only active in Debug builds.
/// </summary>
#define VORTEX_ASSERT_NOT_NULL(ptr) \
	VORTEX_ASSERT_MSG((ptr) != nullptr, "Pointer must not be null")

#else

#define VORTEX_ASSERT(expr) ((void)0)
#define VORTEX_ASSERT_MSG(expr, msg) ((void)0)
#define VORTEX_ASSERT_UNREACHABLE() ((void)0)
#define VORTEX_ASSERT_NOT_NULL(ptr) ((void)0)

#endif

// ============================================================================
// VORTEX_VERIFY - Always-active verification (evaluates expression in Release)
// ============================================================================

/// <summary>
/// Verify that an expression is true. Always evaluates the expression.
/// Reports failure but continues execution.
/// </summary>
#define VORTEX_VERIFY(expr) \
	((expr) ? true : vortex::verification_failed(#expr, __FILE__, __LINE__))

/// <summary>
/// Verify with a custom message. Always evaluates the expression.
/// </summary>
#define VORTEX_VERIFY_MSG(expr, msg) \
	((expr) ? true : vortex::verification_failed(#expr, __FILE__, __LINE__, msg))

// ============================================================================
// VORTEX_ENSURE - Critical verification that aborts on failure
// ============================================================================

/// <summary>
/// Ensure that an expression is true. Always evaluates and aborts on failure.
/// Use for critical invariants that must never fail.
/// </summary>
#define VORTEX_ENSURE(expr) \
	do { \
		if (!(expr)) { \
			vortex::assertion_failed(#expr, __FILE__, __LINE__, "CRITICAL: Invariant violated"); \
			std::abort(); \
		} \
	} while (0)

/// <summary>
/// Ensure with a custom message. Aborts on failure.
/// </summary>
#define VORTEX_ENSURE_MSG(expr, msg) \
	do { \
		if (!(expr)) { \
			vortex::assertion_failed(#expr, __FILE__, __LINE__, msg); \
			std::abort(); \
		} \
	} while (0)
