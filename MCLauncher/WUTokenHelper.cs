using System;
using System.Runtime.InteropServices;
using Windows.Security.Authentication.Web.Core;

namespace MCLauncher {
    class WUTokenHelper {

        public static string GetWUToken() {
            try {
                string token;
                int status = GetWUToken(out token);
                if (status >= WU_ERRORS_START && status <= WU_ERRORS_END)
                    throw new WUTokenException(status);
                else if (status != 0)
                    Marshal.ThrowExceptionForHR(status);
                return token;
            } catch (SEHException e) {
                Marshal.ThrowExceptionForHR(e.HResult);
                return ""; //ghey
            }
        }

        private const int WU_ERRORS_START = unchecked((int) 0x80040200);
        private const int WU_NO_ACCOUNT = unchecked((int) 0x80040200);

        private const int WU_TOKEN_FETCH_ERROR_BASE = unchecked((int) 0x80040300);
        private const int WU_TOKEN_FETCH_ERROR_END = unchecked((int) 0x80040400);

        private const int WU_ERRORS_END = unchecked((int) 0x80040400);

        [DllImport("WUTokenHelper.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int GetWUToken([MarshalAs(UnmanagedType.LPWStr)] out string token);

        public class WUTokenException : Exception {
            public WUTokenException(int exception) : base(GetExceptionText(exception)) {
                HResult = exception;
            }
            private static String GetExceptionText(int e) {
                if (e >= WU_TOKEN_FETCH_ERROR_BASE && e < WU_TOKEN_FETCH_ERROR_END)
                {
                    var actualCode = (byte) e & 0xff;

                    if(!Enum.IsDefined(typeof(WebTokenRequestStatus), e))
                    {
                        return $"WUTokenHelper returned bogus HRESULT: {e} (THIS IS A BUG)";
                    }
                    var status = (WebTokenRequestStatus) Enum.ToObject(typeof(WebTokenRequestStatus), actualCode);
                    switch (status)
                    {
                        case WebTokenRequestStatus.Success:
                            return "Success (THIS IS A BUG)";
                        case WebTokenRequestStatus.UserCancel:
                            return "User cancelled token request (THIS IS A BUG)"; //TODO: should never happen?
                        case WebTokenRequestStatus.AccountSwitch:
                            return "User requested account switch (THIS IS A BUG)"; //TODO: should never happen?
                        case WebTokenRequestStatus.UserInteractionRequired:
                            return "User interaction required to complete token request (THIS IS A BUG)";
                        case WebTokenRequestStatus.AccountProviderNotAvailable:
                            return "Xbox Live account services are currently unavailable";
                        case WebTokenRequestStatus.ProviderError:
                            return "Unknown Xbox Live error";
                    }
                }
                switch (e) {
                    case WU_NO_ACCOUNT: return "No Microsoft account found";
                    default: return "Unknown " + e;
                }
            }
        }

    }
}
