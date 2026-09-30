#pragma once

// Verbose diagnostics gate. Under a native/mixed debugger EVERY OutputDebugString is a debug event
// that suspends the whole process and round-trips to the IDE (~0.5-2ms each) — per-asset import
// logging alone added seconds of F5-only load stall while the same build loaded instantly
// standalone. Chatty per-item logs go through VORTEX_VLOG and are opt-in via VORTEX_VERBOSE_LOG=1
// (the same switch the editor's managed asset logs use). One-off init/error lines may stay direct.

#include "Platform.h"

#if VORTEX_PLATFORM_WINDOWS
// Windows-only translation units rely on this header being included FIRST and bringing in the Win32
// API with the min/max macros suppressed — keep that contract on Windows.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace vortex
{
	inline bool verbose_log()
	{
		static bool v = platform::env_flag("VORTEX_VERBOSE_LOG");
		return v;
	}
}

#define VORTEX_VLOG(s) do { if (::vortex::verbose_log()) ::vortex::platform::debug_output(s); } while (0)
