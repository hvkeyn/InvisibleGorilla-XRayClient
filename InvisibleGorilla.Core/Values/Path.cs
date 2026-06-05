using System.IO;
using System.Runtime.InteropServices;

namespace InvisibleGorillaXRay.Values
{
    public static class Path
    {
        public static string USER_SETTINGS => System.IO.Path.Combine(Directory.ROOT, "Settings.json");
        public static string DIAGNOSTIC_LOG => System.IO.Path.Combine(Directory.ROOT, "diagnostic.log");

        public static string XRAY_CORE_LIB => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? System.IO.Path.Combine(Directory.LIBRARIES, "XRayCore.dll")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? System.IO.Path.Combine(Directory.LIBRARIES, "XRayCore.dylib")
                : System.IO.Path.Combine(Directory.LIBRARIES, "libXRayCore.so");

        public static string INVISIBLEGORILLA_TUN_EXE => System.IO.Path.Combine(Directory.TUN, "InvisibleGorilla-TUN.exe");
        public static string INVISIBLEMAN_TUN_EXE => System.IO.Path.Combine(Directory.TUN, "InvisibleMan-TUN.exe");

        public static string TUN_EXE => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (File.Exists(INVISIBLEGORILLA_TUN_EXE) ? INVISIBLEGORILLA_TUN_EXE : INVISIBLEMAN_TUN_EXE)
            : System.IO.Path.Combine(Directory.TUN, "tun2socks");

        // Tor daemon + pluggable transport binaries (bundled from the Tor Expert Bundle).
        private static string ExeName(string name) =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{name}.exe" : name;

        // Platform overrides. On Android, executables ship as lib*.so inside the read-only
        // nativeLibraryDir (the only place Android allows executing binaries), and the geoip
        // data files are extracted to a writable directory. The platform layer sets these.
        private static string? torExeOverride;
        private static string? pluggableTransportOverride;
        private static string? geoIpOverride;
        private static string? geoIp6Override;

        /// <summary>
        /// Overrides Tor binary/data locations for platforms that cannot use the default
        /// bundled-next-to-the-app layout (notably Android, where binaries live in the
        /// native library dir as lib*.so and geoip files are extracted to app storage).
        /// </summary>
        public static void OverrideTorBinaries(string? torExe, string? pluggableTransportExe, string? geoip, string? geoip6)
        {
            torExeOverride = torExe;
            pluggableTransportOverride = pluggableTransportExe;
            geoIpOverride = geoip;
            geoIp6Override = geoip6;
        }

        public static string TOR_EXE => torExeOverride ?? System.IO.Path.Combine(Directory.TOR, ExeName("tor"));
        // lyrebird is the modern obfs4/meek pluggable transport binary (was obfs4proxy).
        public static string LYREBIRD_EXE => System.IO.Path.Combine(Directory.TOR, ExeName("lyrebird"));
        public static string OBFS4_EXE => System.IO.Path.Combine(Directory.TOR, ExeName("obfs4proxy"));
        public static string SNOWFLAKE_EXE => System.IO.Path.Combine(Directory.TOR, ExeName("snowflake-client"));
        public static string CONJURE_EXE => System.IO.Path.Combine(Directory.TOR, ExeName("conjure-client"));
        // GeoIP files tor expects (ships with the expert bundle alongside the binaries).
        public static string TOR_GEOIP => geoIpOverride ?? System.IO.Path.Combine(Directory.TOR, "geoip");
        public static string TOR_GEOIP6 => geoIp6Override ?? System.IO.Path.Combine(Directory.TOR, "geoip6");

        // Generated torrc + writable Tor state (per session).
        public static string TORRC => System.IO.Path.Combine(Directory.TOR_DATA, "torrc");
        public static string TOR_CONTROL_COOKIE => System.IO.Path.Combine(Directory.TOR_DATA, "control_auth_cookie");

        // The pluggable transport binary that actually exists on disk (lyrebird preferred).
        public static string PLUGGABLE_TRANSPORT_EXE =>
            pluggableTransportOverride ?? (File.Exists(LYREBIRD_EXE) ? LYREBIRD_EXE : OBFS4_EXE);

        // Keep the old constant for backward compatibility with Windows project
        public static string XRAY_CORE_DLL => System.IO.Path.Combine(Directory.LIBRARIES, "XRayCore.dll");
    }
}
