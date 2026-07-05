#pragma once

// Verbose diagnostics gate. Under a native/mixed debugger EVERY OutputDebugString is a debug event
// that suspends the whole process and round-trips to the IDE (~0.5-2ms each) — per-asset import
// logging alone added seconds of F5-only load stall while the same build loaded instantly
// standalone. Chatty per-item logs go through VORTEX_VLOG and are opt-in via VORTEX_VERBOSE_LOG=1
// (the same switch the editor's managed asset logs use). One-off init/error lines may stay direct.

#ifndef NOMINMAX
#define NOMINMAX          // this header is often included FIRST — never leak the min/max macros
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace vortex
{
	inline bool verbose_log()
	{
		static bool v = []
		{
			char b[8];
			DWORD n = GetEnvironmentVariableA("VORTEX_VERBOSE_LOG", b, sizeof(b));
			return n > 0 && n < sizeof(b) && b[0] == '1';
		}();
		return v;
	}
}

#define VORTEX_VLOG(s) do { if (::vortex::verbose_log()) OutputDebugStringA(s); } while (0)
