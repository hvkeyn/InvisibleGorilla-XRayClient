using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Android.App;

namespace InvisibleGorillaXRay.Android.Platforms
{
    using InvisibleGorillaXRay.Core;

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

            CopyAssetIfPresent("Runtime/geoip.dat", Path.Combine(InvisibleGorillaXRay.Values.Directory.ROOT, "geoip.dat"));
            CopyAssetIfPresent("Runtime/geosite.dat", Path.Combine(InvisibleGorillaXRay.Values.Directory.ROOT, "geosite.dat"));

            foreach (string assetPath in GetNativeRuntimeAssetCandidates())
            {
                if (CopyAssetIfPresent(
                    assetPath,
                    InvisibleGorillaXRay.Values.Path.XRAY_CORE_LIB,
                    assetIdentity: assetPath))
                {
                    DiagnosticLog.Write(
                        "AndroidAppStorage",
                        $"Prepared native runtime asset '{assetPath}' for architecture {RuntimeInformation.ProcessArchitecture}");
                    return;
                }
            }

            DiagnosticLog.Write(
                "AndroidAppStorage",
                $"Native runtime asset was not found for architecture {RuntimeInformation.ProcessArchitecture}");
        }

        private static IEnumerable<string> GetNativeRuntimeAssetCandidates()
        {
            string? abiFolder = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x86_64",
                Architecture.Arm64 => "arm64-v8a",
                Architecture.X86 => "x86",
                Architecture.Arm => "armeabi-v7a",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(abiFolder))
                yield return $"Runtime/{abiFolder}/libXRayCore.bin";

            yield return "Runtime/libXRayCore.bin";
        }

        private static bool CopyAssetIfPresent(string assetPath, string destinationPath, string? assetIdentity = null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? InvisibleGorillaXRay.Values.Directory.ROOT);

                using Stream assetStream = Application.Context.Assets!.Open(assetPath);
                string? identityPath = string.IsNullOrWhiteSpace(assetIdentity)
                    ? null
                    : destinationPath + ".asset-id";

                if (File.Exists(destinationPath))
                {
                    try
                    {
                        FileInfo destinationInfo = new FileInfo(destinationPath);
                        string currentIdentity = identityPath != null && File.Exists(identityPath)
                            ? File.ReadAllText(identityPath)
                            : string.Empty;

                        if (destinationInfo.Length == assetStream.Length &&
                            (identityPath == null || string.Equals(currentIdentity, assetIdentity, StringComparison.Ordinal)))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }

                using FileStream destinationStream = File.Create(destinationPath);
                assetStream.CopyTo(destinationStream);

                if (identityPath != null)
                    File.WriteAllText(identityPath, assetIdentity);

                return true;
            }
            catch
            {
                // Asset is optional during development; build scripts populate it for APK packaging.
                return false;
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
