#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>

// Runs inside Proton so the Windows path and layer name belong to the guest
// OpenXR loader. Exporting them in the host shell also affects Linux OpenXR.
#define CAP 32768
static wchar_t directory[CAP], game[CAP], layer[CAP], line[CAP];

int WINAPI WinMain(HINSTANCE instance, HINSTANCE previous, LPSTR unused, int show)
{
    (void)instance; (void)previous; (void)unused; (void)show;
    DWORD length = GetModuleFileNameW(NULL, directory, CAP);
    if (!length || length >= CAP) return ERROR_BAD_PATHNAME;
    wchar_t *last = wcsrchr(directory, L'\\');
    if (!last) return ERROR_BAD_PATHNAME;
    *last = L'\0';
    if (swprintf(game, CAP, L"%ls\\Cairn.exe", directory) < 0 ||
        swprintf(layer, CAP, L"%ls\\BepInEx\\plugins\\CairnVR\\openxr", directory) < 0)
        return ERROR_BUFFER_OVERFLOW;
    if (!SetEnvironmentVariableW(L"XR_API_LAYER_PATH", layer) ||
        !SetEnvironmentVariableW(L"XR_ENABLE_API_LAYERS", L"XR_APILAYER_CAIRN_controls"))
        return (int)GetLastError();

    // Steam passed the original game arguments after argv[0]. Keep their raw
    // escaping, including quoted empty strings and trailing backslashes.
    const wchar_t *tail = GetCommandLineW();
    while (*tail == ' ' || *tail == '\t') ++tail;
    int quoted = 0;
    while (*tail && (quoted || (*tail != ' ' && *tail != '\t'))) {
        if (*tail == '"') quoted = !quoted;
        ++tail;
    }
    if (swprintf(line, CAP, L"\"%ls\"%ls", game, tail) < 0) return ERROR_BUFFER_OVERFLOW;
    STARTUPINFOW startup = {0};
    PROCESS_INFORMATION process = {0};
    startup.cb = sizeof(startup);
    if (!CreateProcessW(game, line, NULL, NULL, FALSE, 0, NULL, directory, &startup, &process)) {
        DWORD error = GetLastError();
        MessageBoxW(NULL, L"Couldn't start the adjacent Cairn.exe.", L"CAIRN VR", MB_OK | MB_ICONERROR);
        return (int)error;
    }
    CloseHandle(process.hThread);
    DWORD exit_code = 1;
    WaitForSingleObject(process.hProcess, INFINITE);
    GetExitCodeProcess(process.hProcess, &exit_code);
    CloseHandle(process.hProcess);
    return (int)exit_code;
}
