namespace InvisibleGorillaXRay.Values
{
    internal static class Directory
    {
        public const string ROOT = ".";
        public const string LIBRARIES = $"{ROOT}/Libraries";
        public const string TUN = $"{ROOT}/TUN";
        public const string CONFIGS = $"{ROOT}/Configs";
        public const string ASSETS = $"{ROOT}/Assets";
        public const string LOCALIZATION = $"{ASSETS}/Localization";
        // Bundled Tor daemon + pluggable transports (ships with the app).
        public const string TOR = $"{ROOT}/Tor";
        // Writable Tor runtime: generated torrc, tor DataDirectory, control cookie.
        public const string TOR_DATA = $"{ROOT}/Tor/Data";
    }
}