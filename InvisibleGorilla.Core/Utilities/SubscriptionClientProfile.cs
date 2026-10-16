using System;
using System.IO;
using System.Text.RegularExpressions;

namespace InvisibleGorillaXRay.Utilities
{
    /// <summary>
    /// Headers a subscription panel expects from a Happ-compatible client.
    /// The device id is created once and reused, so a refresh does not look like a new device.
    /// </summary>
    public static class SubscriptionClientProfile
    {
        private static readonly Regex HwidPattern = new Regex("^[a-zA-Z0-9=-]{10,64}$", RegexOptions.Compiled);

        public static string OperatingSystem { get; private set; } = "Windows";
        public static string OsVersion { get; private set; } = "10";
        public static string Model { get; private set; } = "Desktop";

        public static void Configure(string operatingSystem, string osVersion, string model)
        {
            if (!string.IsNullOrWhiteSpace(operatingSystem))
                OperatingSystem = operatingSystem.Trim();
            if (!string.IsNullOrWhiteSpace(osVersion))
                OsVersion = osVersion.Trim();
            if (!string.IsNullOrWhiteSpace(model))
                Model = model.Trim();
        }

        public static string GetOrCreateHwid()
        {
            Values.Directory.EnsureWritableDirectories();
            string path = Path.Combine(Values.Directory.DATA_ROOT, "subscription-hwid.txt");
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    if (HwidPattern.IsMatch(existing))
                        return existing;
                }
            }
            catch
            {
            }

            string created = Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(path, created);
            }
            catch
            {
            }

            return created;
        }
    }
}
