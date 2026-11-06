#define WIN32_LEAN_AND_MEAN
#include <windows.h>
// WH_SHELL executes inside the process about to handle the unclaimed app command.
__declspec(dllexport) LRESULT CALLBACK ShellProc(int code, WPARAM wparam, LPARAM lparam) {
    if (code == HSHELL_APPCOMMAND && (GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_BROWSER_HOME || GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_BROWSER_BACKWARD || GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_DICTATE_OR_COMMAND_CONTROL_TOGGLE)) {
        HWND bridge = FindWindowW(NULL, L"T1 语音快捷键");
        if (bridge) {
            PostMessageW(bridge, RegisterWindowMessageW(L"T1Vibe.NativeHome"), GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_DICTATE_OR_COMMAND_CONTROL_TOGGLE ? 2 : GET_APPCOMMAND_LPARAM(lparam) == APPCOMMAND_BROWSER_BACKWARD ? 1 : 0, 0);
            return 1;
        }
    }
    return CallNextHookEx(NULL, code, wparam, lparam);
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) { return TRUE; }


