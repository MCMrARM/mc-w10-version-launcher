#pragma comment(lib, "user32.lib")

#include <windows.h>
#include <string>
#include <iostream>

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, PWSTR lpCmdLine, int nCmdShow)
{
    // 1. Get full path of this EXE
    wchar_t modulePath[MAX_PATH] = { 0 };

    if (!GetModuleFileNameW(NULL, modulePath, MAX_PATH))
    {
        MessageBoxW(NULL, L"Failed to get module path", L"Error", MB_ICONERROR);
        return 1;
    }

    // 2. Extract directory
    std::wstring fullPath(modulePath);
    size_t pos = fullPath.find_last_of(L"\\/");
    if (pos == std::wstring::npos)
    {
        MessageBoxW(NULL, L"Invalid module path", L"Error", MB_ICONERROR);
        return 1;
    }

    std::wstring dir = fullPath.substr(0, pos);

    // 3. Build path to Main.exe
    std::wstring mainExe = dir + L"\\Minecraft.Windows.exe";

    // 4. Set working directory explicitly (important in MSIX)
    SetCurrentDirectoryW(dir.c_str());

    // 5. Launch Main.exe
    STARTUPINFOW si = { };
    PROCESS_INFORMATION pi = { };

    //Wtf Windows. We need a space before the command line to make CreateProcess pass the args properly
    wchar_t cmdLine[1024] = { 0 };
    swprintf_s(cmdLine, L" %s", lpCmdLine);

    BOOL result = CreateProcessW(
        mainExe.c_str(),   // application name
        cmdLine,              // command line
        NULL,              // process security
        NULL,              // thread security
        FALSE,             // inherit handles
        0,                 // creation flags
        NULL,              // environment
        dir.c_str(),       // working directory (critical for MSIX)
        &si,
        &pi
    );

    if (!result)
    {
        DWORD err = GetLastError();

        wchar_t msg[256];
        swprintf_s(msg, L"CreateProcess failed. Error: %lu", err);

        MessageBoxW(NULL, msg, L"Launch Error", MB_ICONERROR);
        return 1;
    }

    // 6. Clean up handles
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    return 0;
}