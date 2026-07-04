#pragma comment(lib, "Engine.lib")

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <crtdbg.h>
#include <cstdlib>

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

        // Route debug-CRT assert/error reports to stderr instead of the default modal Abort/Retry/Ignore WINDOW.
        //
        // Why this matters: engine init runs on the editor's WPF UI thread, and the startup splash is Topmost. Any
        // modal debug dialog shown here therefore appears BEHIND the splash — invisible — and the UI thread blocks in
        // that dialog's message loop forever. The editor freezes at "Starting…", and launching from Visual Studio
        // (F5) lands in break mode with "only native code is running". The same binary runs fine standalone because
        // the tripping check is timing-dependent and doesn't fire without the debugger's altered scheduling.
        //
        // The primary offender — the standard assert() macro — is compiled out entirely by NDEBUG (defined for the
        // Debug native build); this additionally routes the _ASSERTE / iterator-debug / invalid-parameter reports so
        // none of THEM can pop a hidden modal dialog either. _set_error_mode covers the plain runtime-error message.
        _set_error_mode(_OUT_TO_STDERR);
        _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
        _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
        _CrtSetReportMode(_CRT_ERROR,  _CRTDBG_MODE_FILE);
        _CrtSetReportFile(_CRT_ERROR,  _CRTDBG_FILE_STDERR);
#endif
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}
