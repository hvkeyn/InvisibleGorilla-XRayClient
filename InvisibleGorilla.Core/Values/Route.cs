namespace InvisibleGorillaXRay.Values
{
    public static class Route
    {
        public const string EMAIL = "mailto:invisiblegorilla@gmail.com";
        public const string REPOSITORY = "https://github.com/hvkeyn/InvisibleGorilla-XRayClient";
        public const string WEBSITE = REPOSITORY;
        public const string ISSUES = $"{REPOSITORY}/issues";
        public const string LATEST_RELEASE = $"{REPOSITORY}/releases/latest";
        public const string BROADCAST =
            "https://raw.githubusercontent.com/hvkeyn/InvisibleGorilla-XRayClient/master/docs/data/Broadcast.dat";
        public const string GOOGLE_ANALYTICS = "https://www.google-analytics.com/mp/collect?measurement_id={0}&api_secret={1}";
    }
}