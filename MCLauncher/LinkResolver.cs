using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MCLauncher
{

    public static class LinkResolver
    {
        private const int fileFlagBackupSemantics = 0x02000000;
        private const int fileShareAll = 0x07;
        private const int openExisting = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string path,
            int desiredAccess,
            int shareMode,
            IntPtr securityAttributes,
            int creationDisposition,
            int flags,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetFinalPathNameByHandle(
            SafeFileHandle handle,
            System.Text.StringBuilder buffer,
            int bufferSize,
            int flags);

        public static string Resolve(string directory)
        {
            SafeFileHandle handle = CreateFile(
                directory,
                0,
                fileShareAll,
                IntPtr.Zero,
                openExisting,
                fileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                throw new IOException("Could not open directory");
            }

            var buffer = new System.Text.StringBuilder(1024);
            int result = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);

            if (result <= 0)
            {
                throw new IOException("Could not resolve final path");
            }

            string raw = buffer.ToString();

            return raw.StartsWith(@"\\?\") ? raw.Substring(4) : raw;
        }
    }
}