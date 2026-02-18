using System.Runtime.InteropServices;

namespace InvisibleGorillaXRay.Values
{
    public static class Path
    {
        public const string USER_SETTINGS = $"{Directory.ROOT}/Settings.json";

        public static readonly string XRAY_CORE_LIB = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{Directory.LIBRARIES}/XRayCore.dll"
            : $"{Directory.LIBRARIES}/XRayCore.dylib";

        public static readonly string TUN_EXE = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{Directory.TUN}/InvisibleMan-TUN.exe"
            : $"{Directory.TUN}/tun2socks";

        // Keep the old constant for backward compatibility with Windows project
        public const string XRAY_CORE_DLL = $"{Directory.LIBRARIES}/XRayCore.dll";
        public const string INVISIBLEMAN_TUN_EXE = $"{Directory.TUN}/InvisibleMan-TUN.exe";
    }
}
