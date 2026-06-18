#pragma comment(lib, "user32.lib")
#pragma comment(lib, "RuntimeObject.lib")

#include <windows.h>
#include <string>
#include <iostream>

#include <combaseapi.h>
#include <hstring.h>
#include <winstring.h>
#include <tlhelp32.h>
#include <psapi.h>

#include "tinyxml/tinyxml2.h"

//All interfaces and their respective IIDs were recovered from xgameruntime.dll
//Microsoft provides a version with a pdb in the official GDK dev kit, which is
//a goldmine of information
//Also possible to find these IIDs by searching for their respective names in
//HKCR\Interface

//used by the game
/*__declspec(uuid("F2746100-46B0-45C1-8403-9BAFE4253FA9"))
class IGameProtocolActivationCallbacks_V1 : public IUnknown {
    virtual HRESULT OnGameProtocolActivation(HSTRING uri) = 0;
    virtual HRESULT OnIsCallerAlive(int* isAlive) = 0;
};

__declspec(uuid("2A4C9647-50BD-46AD-99D3-9DD1B543AA6B"))
class IGameProtocolActivationCallbacks_V2 : public IGameProtocolActivationCallbacks_V1 {
    virtual HRESULT OnGameFileActivation(HSTRING filePath) = 0;
};*/

//used by the game
/*
__declspec(uuid("36822329-19DB-4DB4-AF5C-1625476DE335"))
class IGameProtocolServer_V1 : public IUnknown {
    virtual HRESULT RegisterGameProtocolActivationCallbacks(IGameProtocolActivationCallbacks_V1* callbacks, IUnknown** protocolServerRegistrationToken) = 0;
};

__declspec(uuid("CDF164C6-F371-46B9-A01D-07609A17789D"))
class IGameProtocolServer_V2 : public IGameProtocolServer_V1 {
    virtual HRESULT RegisterGameActivationCallbacksV2(IGameProtocolActivationCallbacks_V2* callbacks, IUnknown** protocolServerRegistrationToken) = 0;
};*/

//these are used by GamePlatformServices to activate the game when it's already running
//normally GameLaunchHelper would call these, but GLH doesn't work in unpackaged installs for some reason,
//so we need to handle it manually
struct __declspec(uuid("F58E3884-1F75-4C66-9127-A66161818693"))
IGameProtocolProvider_V1 : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE NotifyGameProtocolActivation(unsigned int titleId, HSTRING uri, int* wasRegistered) = 0;
    virtual HRESULT STDMETHODCALLTYPE HandleStateShare(unsigned int titleId, HSTRING unk2, HSTRING unk3, int* unk4) = 0; //not used, but needs to be present for the vtable structure to be correct
};
struct __declspec(uuid("80BB78AD-711A-4BD6-A9FC-3C912AF1CB46"))
IGameProtocolProvider_V2 : public IGameProtocolProvider_V1 {
    virtual HRESULT STDMETHODCALLTYPE NotifyGameFileActivation(unsigned int titleId, HSTRING filePath, int* wasRegistered) = 0;
};

//recovered from gamingservices.dll by looking up IIDs for IGameProtocolProvider
const CLSID CLSID_GameProtocolService = { 0xDEA688F3, 0x0625, 0x45AB, { 0xAF, 0x1A, 0xEF, 0xCF, 0x9B, 0xB4, 0x40, 0xF6 } };

int showError(const wchar_t* name, HRESULT error) {
    wchar_t message[256] = { 0 };
    swprintf_s(message, L"%s (error code %X)", name, error);
    MessageBoxW(NULL, message, L"Error", MB_ICONERROR);
    return error;
}

int getTitleId(std::wstring& dir) {
    std::wstring gameConfigPath = dir + L"\\MicrosoftGame.Config";
    FILE* fp;
    errno_t err = _wfopen_s(&fp, gameConfigPath.c_str(), L"rb");
    if (err) {
        showError(L"Loading MicrosoftGame.Config", err);
        return 0;
    }
    tinyxml2::XMLDocument gameConfig;
    tinyxml2::XMLError xmlResult = gameConfig.LoadFile(fp);
    if (xmlResult != tinyxml2::XML_SUCCESS) {
        showError(L"Parsing MicrosoftGame.Config", xmlResult);
        return 0;
    }

    tinyxml2::XMLElement* root = gameConfig.FirstChildElement("Game");
    if (!root) {
        showError(L"Unable to find Game element in MicrosoftGame.Config", 0);
        return 0;
    }
    tinyxml2::XMLElement* titleIdElement = root->FirstChildElement("TitleId");
    if (!titleIdElement) {
        showError(L"Unable to find Game.TitleId element in MicrosoftGame.Config", 0);
        return 0;
    }

    return std::stoi(titleIdElement->GetText(), nullptr, 16);
}

DWORD findMinecraftProcess(std::wstring& fullExePath) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);

    DWORD result = 0;
    int scanned = 0;
    int checked = 0;
    if (Process32FirstW(snap, &pe)) {
        do {
            HANDLE hproc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pe.th32ProcessID);
            if (!hproc) {
                continue;
            }
            scanned++;
            DWORD pathSize = MAX_PATH;
            std::wstring path(pathSize, L'\0');
            QueryFullProcessImageNameW(hproc, 0, path.data(), &pathSize);
            if (pathSize == 0) {
                continue;
            }
            path.resize(pathSize);
            CloseHandle(hproc);

            //TODO: need to check this works with long paths?
            if (_wcsicmp(path.c_str(), fullExePath.c_str()) == 0) {
                result = pe.th32ProcessID;
                break;
            }
            checked++;
        } while (Process32NextW(snap, &pe));
    }

    CloseHandle(snap);
    return result;
}

int activateRunningInstance(PWSTR lpCmdLine, int titleId) {
    HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);

    IGameProtocolProvider_V1* iface = NULL;
    hr = CoCreateInstance(CLSID_GameProtocolService, NULL, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&iface));
    if (hr < 0) {
        return showError(L"CoCreateInstance failed", hr);
    }

    bool isUri = false;
    if (wcsstr(lpCmdLine, L"://")) {
        isUri = true;
    } else {
        //strip long path prefix
        if (wcsncmp(lpCmdLine, L"\\?\\\\", 4) == 0 || wcsncmp(lpCmdLine, L"\\??\\", 4) == 0) {
            lpCmdLine = &lpCmdLine[4];
        }
    }

    HSTRING hstring;
    hr = WindowsCreateString(lpCmdLine, wcslen(lpCmdLine), &hstring);
    if (hr < 0) {
        iface->Release();
        return showError(L"WindowsCreateString failed", hr);
    }

    int wasRegistered = 0;
    if (isUri) {
        hr = iface->NotifyGameProtocolActivation(titleId, hstring, &wasRegistered);
    } else {
        IGameProtocolProvider_V2* iface2;
        hr = iface->QueryInterface(&iface2);
        if (hr < 0) {
            iface->Release();
            return showError(L"Your version of GamingServices doesn't support importing files while the game is running. Try opening the file while the game is not running.", hr);
        } else {
            MessageBoxW(NULL, L"Importing files while the game is running may not work. This is a bug in Minecraft. If it doesn't work, try opening the file while the game is not running.", L"Warning", MB_ICONWARNING);
        }

        hr = iface2->NotifyGameFileActivation(titleId, hstring, &wasRegistered);
        iface2->Release();
    }

    iface->Release();
    return hr < 0 ? showError((std::wstring(isUri ? L"NotifyGameProtocolActivation failed" : L"NotifyGameFileActivation failed") + std::wstring(L" with args: ") + std::wstring(lpCmdLine)).data(), hr) : hr;
}

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, PWSTR lpCmdLine, int nCmdShow) {
    wchar_t modulePath[MAX_PATH] = { 0 };

    if (!GetModuleFileNameW(NULL, modulePath, MAX_PATH)) {
        MessageBoxW(NULL, L"Failed to get module path", L"Error", MB_ICONERROR);
        return 1;
    }

    std::wstring fullPath(modulePath);
    size_t pos = fullPath.find_last_of(L"\\/");
    if (pos == std::wstring::npos) {
        MessageBoxW(NULL, L"Invalid module path", L"Error", MB_ICONERROR);
        return 1;
    }

    std::wstring dir = fullPath.substr(0, pos);

    //TODO: we should probably fetch this from MicrosoftGame.Config for portability
    std::wstring mainExe = dir + L"\\Minecraft.Windows.exe";
    
    if (findMinecraftProcess(mainExe) > 0) {
        if (lpCmdLine != NULL && lpCmdLine[0] != '\0') {
            int titleId = getTitleId(dir);
            activateRunningInstance(lpCmdLine, titleId);
            return 0;
        } else {
            //TODO: this should bring it to the foreground when No is clicked
            int result = MessageBoxW(NULL, L"This version is already running. Do you want to launch another instance?", L"Version already running", MB_OKCANCEL | MB_DEFBUTTON2 | MB_ICONEXCLAMATION);
            if (result != IDOK) {
                return 0;
            }
        }
    }

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
        NULL,       // working directory
        &si,
        &pi
    );

    if (!result) {
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