using System.IO;

namespace InvisibleGorillaXRay.Values
{
    public static class Path
    {
        public const string USER_SETTINGS = $"{Directory.ROOT}/Settings.json";
        public const string XRAY_CORE_DLL = $"{Directory.LIBRARIES}/XRayCore.dll";
        public const string INVISIBLEGORILLA_TUN_EXE = $"{Directory.TUN}/InvisibleGorilla-TUN.exe";
        public const string INVISIBLEMAN_TUN_EXE = $"{Directory.TUN}/InvisibleMan-TUN.exe";

        public static readonly string TUN_EXE = File.Exists(INVISIBLEGORILLA_TUN_EXE)
            ? INVISIBLEGORILLA_TUN_EXE
            : INVISIBLEMAN_TUN_EXE;

        // Tor daemon + pluggable transport binaries (bundled from the Tor Expert Bundle).
        public const string TOR_EXE = $"{Directory.TOR}/tor.exe";
        public const string LYREBIRD_EXE = $"{Directory.TOR}/lyrebird.exe";
        public const string OBFS4_EXE = $"{Directory.TOR}/obfs4proxy.exe";
        public const string SNOWFLAKE_EXE = $"{Directory.TOR}/snowflake-client.exe";
        public const string CONJURE_EXE = $"{Directory.TOR}/conjure-client.exe";
        public const string TOR_GEOIP = $"{Directory.TOR}/geoip";
        public const string TOR_GEOIP6 = $"{Directory.TOR}/geoip6";
        public const string TORRC = $"{Directory.TOR_DATA}/torrc";
        public const string TOR_CONTROL_COOKIE = $"{Directory.TOR_DATA}/control_auth_cookie";

        // The pluggable transport binary that actually exists on disk (lyrebird preferred).
        public static readonly string PLUGGABLE_TRANSPORT_EXE = File.Exists(LYREBIRD_EXE)
            ? LYREBIRD_EXE
            : OBFS4_EXE;
    }
}