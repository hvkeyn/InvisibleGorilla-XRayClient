using System;
using System.Runtime.InteropServices;

namespace InvisibleGorillaXRay.Services.Diagnostics
{
    /// <summary>
    /// Reads the integrity level of the current process.
    /// </summary>
    /// <remarks>
    /// Windows starts an executable with the integrity level of its file label, so a build
    /// copied out of a sandboxed workspace can run as Low and silently lose the system proxy,
    /// the tray icon and the Wintun adapter. The startup check reports that state instead of
    /// letting every feature fail with an unrelated error.
    /// </remarks>
    public static class ProcessIntegrity
    {
        private enum Level
        {
            Unknown = 0,
            Untrusted,
            Low,
            Medium,
            High,
            System
        }

        public static bool IsLow => GetLevel() == Level.Low || GetLevel() == Level.Untrusted;

        public static string Describe()
        {
            return GetLevel() switch
            {
                Level.Untrusted => "Untrusted",
                Level.Low => "Low",
                Level.Medium => "Medium",
                Level.High => "High",
                Level.System => "System",
                _ => "Unknown"
            };
        }

        private static Level GetLevel()
        {
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token))
                    return Level.Unknown;

                int length = 0;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                if (length <= 0)
                {
                    CloseHandle(token);
                    return Level.Unknown;
                }

                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
                        return Level.Unknown;

                    IntPtr sid = Marshal.ReadIntPtr(buffer, 0);
                    if (sid == IntPtr.Zero)
                        return Level.Unknown;

                    int subAuthorities = Marshal.ReadByte(sid, 1);
                    if (subAuthorities <= 0)
                        return Level.Unknown;

                    int rid = Marshal.ReadInt32(sid, 8 + (subAuthorities - 1) * 4);
                    return rid switch
                    {
                        0x0000 => Level.Untrusted,
                        0x1000 => Level.Low,
                        0x2000 => Level.Medium,
                        0x2100 => Level.Medium,
                        0x3000 => Level.High,
                        0x4000 => Level.System,
                        _ => Level.Unknown
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                    CloseHandle(token);
                }
            }
            catch
            {
                return Level.Unknown;
            }
        }

        private const int TokenIntegrityLevel = 25;
        private const uint TOKEN_QUERY = 0x0008;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr token,
            int informationClass,
            IntPtr information,
            int informationLength,
            out int returnLength);
    }
}
