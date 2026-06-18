// GDKDecryptHelper.cpp : Defines the entry point for the application.
//

#include "framework.h"
#include <fstream>
#include <filesystem>

#define MAX_LOADSTRING 100

int error(std::ofstream& log, const char* message) {
    log << message << " (error code: " << std::to_string(GetLastError()) << ")" << std::endl;
    log.close();
    return 1;
}

int APIENTRY wWinMain(_In_ HINSTANCE hInstance,
                     _In_opt_ HINSTANCE hPrevInstance,
                     _In_ LPWSTR    lpCmdLine,
                     _In_ int       nCmdShow)
{
    UNREFERENCED_PARAMETER(hPrevInstance);
    UNREFERENCED_PARAMETER(lpCmdLine);

    if (__argc != 4) {
        MessageBoxW(NULL, L"Expected 2 parameters: exeSrcPath, exeDstPath, logFile", L"GDKDecryptHelper error", MB_ICONERROR);
        return 1;
    }

    wchar_t* exeSrcPath = __wargv[1];
    wchar_t* exeDstPath = __wargv[2];
    wchar_t* logFile = __wargv[3];

    std::filesystem::path logPath = logFile;
    std::ofstream log(logPath, std::ios::app);


    //error(exeSrcPath);
    //error(exeDstPath);

    WCHAR tempPath[MAX_PATH];
    WCHAR tempFile[MAX_PATH];

    DWORD length = GetTempPathW(MAX_PATH, tempPath);
    if (length == 0 || length > MAX_PATH) {
        return error(log, "Failed to get system temp path");
    }

    if (GetTempFileNameW(tempPath, L"MCLauncher", 0, tempFile) == 0) {
        return error(log, "Failed to allocate temporary file for copying exe");
    }

    if (CopyFileW(exeSrcPath, tempFile, 0) == 0) {
        return error(log, "Failed to copy exe to temporary file");
    }

    if (MoveFileW(tempFile, exeDstPath) == 0) {
        return error(log, "Failed to move exe to destination path");
    }

    log << "Successfully copied exe to specified destination path" << std::endl;
    log.close();

    return 0;
}
