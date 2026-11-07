#define WIN32_LEAN_AND_MEAN
#include <windows.h>
static BOOL ChatForeground(void) {
    DWORD pid = 0; GetWindowThreadProcessId(GetForegroundWindow(), &pid);
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return FALSE;
    WCHAR path[32768]; DWORD size = 32768;
    BOOL found = QueryFullProcessImageNameW(process, 0, path, &size);
    CloseHandle(process);
    if (!found) return FALSE;
    WCHAR *name = path;
    for (WCHAR *p = path; *p; ++p) if (*p == L'\\') name = p + 1;
    return lstrcmpiW(name, L"ChatGPT.exe") == 0;
}
// WH_SHELL executes inside the process about to handle the unclaimed app command.
__declspec(dllexport) LRESULT CALLBACK ShellProc(int code, WPARAM wparam, LPARAM lparam) {
    if (code == HSHELL_APPCOMMAND && (GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_BROWSER_HOME || GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_BROWSER_BACKWARD || GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_DICTATE_OR_COMMAND_CONTROL_TOGGLE)) {
        if (GET_APPCOMMAND_LPARAM(lparam) != APPCOMMAND_DICTATE_OR_COMMAND_CONTROL_TOGGLE && !ChatForeground())
            return CallNextHookEx(NULL, code, wparam, lparam);
        HWND bridge = FindWindowW(NULL, L"T1 语音快捷键");
        if (bridge) {
            PostMessageW(bridge, RegisterWindowMessageW(L"T1Vibe.NativeHome"), GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_DICTATE_OR_COMMAND_CONTROL_TOGGLE ? 2 : GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_BROWSER_BACKWARD ? 1 : 0, 0);
            return 1;
        }
    }
    return CallNextHookEx(NULL, code, wparam, lparam);
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) { return TRUE; }


