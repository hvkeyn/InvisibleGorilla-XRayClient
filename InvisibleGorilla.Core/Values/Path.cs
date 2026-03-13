using System.IO;
using System.Runtime.InteropServices;

namespace InvisibleGorillaXRay.Values
{
    public static class Path
    {
        public const string USER_SETTINGS = $"{Directory.ROOT}/Settings.json";

        public static readonly string XRAY_CORE_LIB = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{Directory.LIBRARIES}/XRayCore.dll"
            : $"{Directory.LIBRARIES}/XRayCore.dylib";

        public const string INVISIBLEGORILLA_TUN_EXE = $"{Directory.TUN}/InvisibleGorilla-TUN.exe";
        public const string INVISIBLEMAN_TUN_EXE = $"{Directory.TUN}/InvisibleMan-TUN.exe";

        public static readonly string TUN_EXE = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (File.Exists(INVISIBLEGORILLA_TUN_EXE) ? INVISIBLEGORILLA_TUN_EXE : INVISIBLEMAN_TUN_EXE)
            : $"{Directory.TUN}/tun2socks";

        // Keep the old constant for backward compatibility with Windows project
        public const string XRAY_CORE_DLL = $"{Directory.LIBRARIES}/XRayCore.dll";
    }
}
