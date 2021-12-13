﻿#include "pch.h"
#include "combaseapi.h"
#include <thread>

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Security::Authentication::Web::Core;
using namespace Windows::Internal::Security::Authentication::Web;
using namespace Windows::Security::Cryptography;

#define WU_NO_ACCOUNT MAKE_HRESULT(SEVERITY_ERROR, FACILITY_ITF, 0x200)
#define WU_TOKEN_FETCH_ERROR_BASE MAKE_HRESULT(SEVERITY_ERROR, FACILITY_ITF, 0x400)

extern "C" __declspec(dllexport) int  __stdcall GetWUToken(wchar_t** retToken) {
	auto tokenBrokerStatics = get_activation_factory<TokenBrokerInternal, Windows::Foundation::IUnknown>();
	auto statics = tokenBrokerStatics.as<ITokenBrokerInternalStatics>();
	auto accounts = statics.FindAllAccountsAsync().get();
	wprintf(L"Account count = %i\n", accounts.Size());
	if (accounts.Size() == 0)
		return WU_NO_ACCOUNT;

	hstring tokenBase64;

	// Loop the accounts on the system incase
	// the first doesn't have access to the token
	for (size_t i = 0; i < accounts.Size(); i++)
	{
		auto accountInfo = accounts.GetAt(i);
		wprintf(L"ID = %s\n", accountInfo.Id().c_str());
		wprintf(L"Name = %s\n", accountInfo.UserName().c_str());

		try {
			TryGetToken(accountInfo, tokenBase64);
		}
		catch (...) {
			if (i == accounts.Size() - 1) {
				throw std::current_exception();
			}
		}

		if (tokenBase64.size() >= 1) {
			break;
		}
	}

	*retToken = (wchar_t*)::CoTaskMemAlloc((tokenBase64.size() + 1) * sizeof(wchar_t));
	memcpy(*retToken, tokenBase64.data(), (tokenBase64.size() + 1) * sizeof(wchar_t));

	return S_OK;
}

void TryGetToken(winrt::Windows::Security::Credentials::WebAccount& accountInfo, winrt::hstring& tokenBase64)
{
	auto accountProvider = WebAuthenticationCoreManager::FindAccountProviderAsync(L"https://login.microsoft.com", L"consumers").get();
	WebTokenRequest request(accountProvider, L"service::dcat.update.microsoft.com::MBI_SSL", L"{28520974-CE92-4F36-A219-3F255AF7E61E}");
	auto result = WebAuthenticationCoreManager::GetTokenSilentlyAsync(request, accountInfo).get();
	if (result.ResponseStatus() != WebTokenRequestStatus::Success) {
		return WU_TOKEN_FETCH_ERROR_BASE | static_cast<int32_t>(result.ResponseStatus());
	}
	auto token = result.ResponseData().GetAt(0).Token();
	wprintf(L"Token = %s\n", token.c_str());
	auto tokenBinary = CryptographicBuffer::ConvertStringToBinary(token, BinaryStringEncoding::Utf16LE);
	tokenBase64 = CryptographicBuffer::EncodeToBase64String(tokenBinary);
	wprintf(L"Encoded token = %s\n", tokenBase64.c_str());
}
