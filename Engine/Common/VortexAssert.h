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

		// LOG-AND-CONTINUE — never a modal box, never a break (unless explicitly requested).
		//
		// Engine init runs on the WPF UI thread while the STARTUP SPLASH is Topmost. A modal MessageBox
		// shown here appears BEHIND the splash (invisible) and the UI thread blocks in the dialog's message
		// loop forever -> the editor "hangs" at the splash under Visual Studio F5, and VS reports "only
		// native code is running" (the thread sitting inside the native modal loop). A __debugbreak() likewise
		// halts F5 in native code with nothing runnable. Standalone/Release always logged-and-continued, which
		// is exactly why it worked there. So: log to the debugger output AND to %TEMP%\vortex_asserts.log, then
		// CONTINUE. Set VORTEX_ASSERT_BREAK=1 to break into an attached debugger at the failing assert instead.
#ifdef _WIN32
		OutputDebugStringA(buffer);
		OutputDebugStringA("\n");

		char logPath[MAX_PATH]{};
		DWORD n = GetTempPathA(MAX_PATH, logPath);
		if (n > 0 && n <= MAX_PATH)
		{
			strncat_s(logPath, "vortex_asserts.log", _TRUNCATE);
			FILE* f = nullptr;
			if (fopen_s(&f, logPath, "a") == 0 && f) { fputs(buffer, f); fputc('\n', f); fclose(f); }
		}

		char envBuf[8];
		DWORD en = GetEnvironmentVariableA("VORTEX_ASSERT_BREAK", envBuf, sizeof(envBuf));
		if (en > 0 && en < sizeof(envBuf) && envBuf[0] == '1' && IsDebuggerPresent())
			__debugbreak();
#else
		fprintf(stderr, "%s\n", buffer);
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
