#pragma comment(lib, "Engine.lib")

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <crtdbg.h>
#include <cstdio>
#include <cstdlib>   // _set_invalid_parameter_handler

#if _DEBUG
// A failed Debug-CRT report (standard assert(), _ASSERTE, STL iterator-debug checks, invalid-parameter)
// normally pops the Abort/Retry/Ignore WINDOW or breaks into the debugger. Shown from engine init on the
// WPF UI thread it appears BEHIND the Topmost startup splash (the UI thread then deadlocks in the dialog's
// message loop) or halts F5 in native code — either way the editor "hangs" at the splash under Visual
// Studio. This hook routes every such report to the debugger output + %TEMP%\vortex_crt.log and returns
// 0 = "handled, do NOT break, continue" — matching Release/standalone, which never had this problem.
static int VortexCrtReportHook(int reportType, char* message, int* returnValue)
{
    if (message)
    {
        OutputDebugStringA(message);
        char logPath[MAX_PATH]{};
        DWORD n = GetTempPathA(MAX_PATH, logPath);
        if (n > 0 && n <= MAX_PATH)
        {
            strncat_s(logPath, "vortex_crt.log", _TRUNCATE);
            FILE* f = nullptr;
            if (fopen_s(&f, logPath, "a") == 0 && f) { fputs(message, f); fclose(f); }
        }
    }
    if (returnValue) *returnValue = 0;   // 0 = do not break into the debugger
    return TRUE;                          // TRUE = report fully handled, suppress the default dialog/break
}

// The Debug CRT's invalid-parameter path (e.g. a bad printf spec, an out-of-range STL index) also breaks/
// aborts by default. Swallow it to debug output so it can't freeze startup under F5 either.
static void VortexInvalidParameter(const wchar_t*, const wchar_t*, const wchar_t*, unsigned int, uintptr_t)
{
    OutputDebugStringA("[CRT] invalid parameter (ignored)\n");
}
#endif

BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
#if _DEBUG
        _CrtSetDbgFlag(_CRTDBG_ALLOC_MEM_DF | _CRTDBG_LEAK_CHECK_DF);
        // Send asserts/warnings/errors to our log-and-continue hook instead of a modal window that would
        // freeze the editor behind the startup splash under F5. (Release compiles all of this out.)
        _CrtSetReportHook(VortexCrtReportHook);
        _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE | _CRTDBG_MODE_DEBUG);
        _CrtSetReportMode(_CRT_ERROR,  _CRTDBG_MODE_FILE | _CRTDBG_MODE_DEBUG);
        _CrtSetReportMode(_CRT_WARN,   _CRTDBG_MODE_FILE | _CRTDBG_MODE_DEBUG);
        _set_invalid_parameter_handler(VortexInvalidParameter);
#endif
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

