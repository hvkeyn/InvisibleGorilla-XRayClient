using System;
using System.IO;
using Android.App;

namespace InvisibleGorillaXRay.Android.Platforms
{
    internal static class AndroidAppStorage
    {
        private static readonly string AppRoot = ResolveAppRoot();

        public static void ConfigureAppRoot()
        {
            InvisibleGorillaXRay.Values.Directory.SetRoot(AppRoot);
            InvisibleGorillaXRay.Values.Directory.EnsureWritableDirectories();
        }

        public static void EnsureRuntimeAssets()
        {
            ConfigureAppRoot();

            CopyAssetIfPresent("runtime/geoip.dat", Path.Combine(InvisibleGorillaXRay.Values.Directory.ROOT, "geoip.dat"));
            CopyAssetIfPresent("runtime/geosite.dat", Path.Combine(InvisibleGorillaXRay.Values.Directory.ROOT, "geosite.dat"));
            CopyAssetIfPresent("runtime/libXRayCore.bin", InvisibleGorillaXRay.Values.Path.XRAY_CORE_LIB);
        }

        private static void CopyAssetIfPresent(string assetPath, string destinationPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? InvisibleGorillaXRay.Values.Directory.ROOT);

                using Stream assetStream = Application.Context.Assets!.Open(assetPath);
                using FileStream destinationStream = File.Create(destinationPath);
                assetStream.CopyTo(destinationStream);
            }
            catch
            {
                // Asset is optional during development; build scripts populate it for APK packaging.
            }
        }

        private static string ResolveAppRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = AppContext.BaseDirectory;

            return Path.Combine(localAppData, "InvisibleGorilla-XRay");
        }
    }
}
