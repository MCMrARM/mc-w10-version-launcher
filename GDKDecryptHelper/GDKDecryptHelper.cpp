// GDKDecryptHelper.cpp : Defines the entry point for the application.
//

#include "framework.h"
#include <fstream>
#include <filesystem>

#define MAX_LOADSTRING 100

int error(std::ofstream& log, const char* message, const wchar_t* doneFile) {
    log << message << " (error code: " << std::to_string(GetLastError()) << ")" << std::endl;
    log.close();

    std::wofstream done(doneFile);
    done.close();
    return 1;
}

int APIENTRY wWinMain(_In_ HINSTANCE hInstance,
                     _In_opt_ HINSTANCE hPrevInstance,
                     _In_ LPWSTR    lpCmdLine,
                     _In_ int       nCmdShow)
{
    UNREFERENCED_PARAMETER(hPrevInstance);
    UNREFERENCED_PARAMETER(lpCmdLine);

    if (__argc != 5) {
        MessageBoxW(NULL, L"Expected 2 parameters: exeSrcPath, exeDstPath, logFile, doneFile", L"GDKDecryptHelper error", MB_ICONERROR);
        return 1;
    }

    wchar_t* exeSrcPath = __wargv[1];
    wchar_t* exeDstPath = __wargv[2];
    wchar_t* logFile = __wargv[3];
    wchar_t* doneFile = __wargv[4];

    std::filesystem::path logPath = logFile;
    std::ofstream log(logPath, std::ios::app);

    WCHAR tempPath[MAX_PATH];
    WCHAR tempFile[MAX_PATH];

    DWORD length = GetTempPathW(MAX_PATH, tempPath);
    if (length == 0 || length > MAX_PATH) {
        return error(log, "Failed to get system temp path", doneFile);
    }

    if (GetTempFileNameW(tempPath, L"MCLauncher", 0, tempFile) == 0) {
        return error(log, "Failed to allocate temporary file for copying exe", doneFile);
    }

    if (CopyFileW(exeSrcPath, exeDstPath, 0) == 0) {
        return error(log, "Failed to copy exe to temporary file", doneFile);
    }

    log << "Successfully copied exe to specified destination path" << std::endl;
    log.close();

    std::wofstream done(doneFile);
    done.close();

    return 0;
}
