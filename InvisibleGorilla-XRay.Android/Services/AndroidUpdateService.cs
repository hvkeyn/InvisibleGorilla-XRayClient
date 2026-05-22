using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.Content;
using AndroidUri = Android.Net.Uri;

namespace InvisibleGorillaXRay.Android.Services
{
    using InvisibleGorillaXRay.Core;
    using InvisibleGorillaXRay.Models;
    using InvisibleGorillaXRay.Services;

    internal static class AndroidUpdateService
    {
        private const string FileProviderAuthority = "io.invisiblegorilla.xray.fileprovider";
        private const string DownloadFolderName = "update";
        private const string ApkMimeType = "application/vnd.android.package-archive";

        private static readonly GitHubReleaseService ReleaseService = new GitHubReleaseService();
        private static int downloadInProgress;

        public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken token = default)
        {
            try
            {
                UpdateInfo? info = await ReleaseService.GetLatestReleaseAsync(token).ConfigureAwait(false);
                if (info == null)
                    return null;

                info.IsNewerThanCurrent = IsNewerThanInstalled(info.Version);
                return info;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidUpdateService.CheckForUpdateAsync", ex);
                return null;
            }
        }

        public static ReleaseAsset? PickApkAssetForCurrentDevice(UpdateInfo info)
        {
            if (info == null || info.Assets.Count == 0)
                return null;

            string preferredAbi = (Build.SupportedAbis != null && Build.SupportedAbis.Count > 0)
                ? Build.SupportedAbis[0].ToLowerInvariant()
                : string.Empty;

            ReleaseAsset[] apks = info.Assets
                .Where(a => !string.IsNullOrEmpty(a.Name) && a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (apks.Length == 0)
                return null;

            // Prefer ABI match (arm64-v8a -> arm64), then a generic "android"/no-arch asset, otherwise first APK.
            string abiTag = preferredAbi switch
            {
                "arm64-v8a" => "arm64",
                "armeabi-v7a" => "arm",
                "x86_64" => "x64",
                "x86" => "x86",
                _ => preferredAbi
            };

            ReleaseAsset? exact = apks.FirstOrDefault(a => a.Name.ToLowerInvariant().Contains(abiTag));
            if (exact != null)
                return exact;

            ReleaseAsset? androidGeneric = apks.FirstOrDefault(a => a.Name.ToLowerInvariant().Contains("android"));
            if (androidGeneric != null)
                return androidGeneric;

            return apks[0];
        }

        public static async Task<string?> DownloadApkAsync(
            Activity activity,
            ReleaseAsset asset,
            IProgress<double>? progress,
            CancellationToken token)
        {
            if (activity == null || asset == null || string.IsNullOrEmpty(asset.DownloadUrl))
                return null;

            if (Interlocked.Exchange(ref downloadInProgress, 1) == 1)
            {
                DiagnosticLog.Write("AndroidUpdateService", "Download skipped: another download is already in progress");
                return null;
            }

            try
            {
                string? cacheRoot = activity.CacheDir?.AbsolutePath;
                if (string.IsNullOrEmpty(cacheRoot))
                {
                    DiagnosticLog.Write("AndroidUpdateService", "Cache directory is unavailable; cannot download update");
                    return null;
                }

                string downloadDir = System.IO.Path.Combine(cacheRoot, DownloadFolderName);
                System.IO.Directory.CreateDirectory(downloadDir);

                // Clean stale APKs from previous attempts to avoid filling the cache with old releases.
                foreach (string stale in System.IO.Directory.GetFiles(downloadDir, "*.apk"))
                {
                    try { File.Delete(stale); } catch { }
                }

                string fileName = string.IsNullOrEmpty(asset.Name) ? "update.apk" : asset.Name;
                string destination = System.IO.Path.Combine(downloadDir, fileName);

                DiagnosticLog.Write("AndroidUpdateService", $"Downloading {asset.DownloadUrl} -> {destination} ({asset.Size} bytes)");
                bool ok = await ReleaseService.DownloadAssetAsync(asset, destination, progress, token).ConfigureAwait(false);
                if (!ok)
                {
                    DiagnosticLog.Write("AndroidUpdateService", $"Download failed for {asset.Name}");
                    return null;
                }

                DiagnosticLog.Write("AndroidUpdateService", $"Download finished: {destination}");
                return destination;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidUpdateService.DownloadApkAsync", ex);
                return null;
            }
            finally
            {
                Interlocked.Exchange(ref downloadInProgress, 0);
            }
        }

        public static bool LaunchPackageInstaller(Activity activity, string apkPath)
        {
            if (activity == null || string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath))
                return false;

            try
            {
                global::Java.IO.File apkFile = new global::Java.IO.File(apkPath);
                AndroidUri uri = FileProvider.GetUriForFile(activity, FileProviderAuthority, apkFile);

                Intent installIntent = new Intent(Intent.ActionView)
                    .SetDataAndType(uri, ApkMimeType)
                    .AddFlags(ActivityFlags.GrantReadUriPermission)
                    .AddFlags(ActivityFlags.NewTask);

                activity.StartActivity(installIntent);
                DiagnosticLog.Write("AndroidUpdateService", $"Launched package installer for {apkPath}");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidUpdateService.LaunchPackageInstaller", ex);
                return false;
            }
        }

        public static string GetInstalledVersion()
        {
            try
            {
                Context? context = global::Android.App.Application.Context;
                if (context == null)
                    return string.Empty;

                global::Android.Content.PM.PackageInfo? info = context.PackageManager?.GetPackageInfo(context.PackageName!, 0);
                return info?.VersionName ?? string.Empty;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidUpdateService.GetInstalledVersion", ex);
                return string.Empty;
            }
        }

        private static bool IsNewerThanInstalled(string remoteVersion)
        {
            if (string.IsNullOrWhiteSpace(remoteVersion))
                return false;

            string installed = GetInstalledVersion();
            return CompareSemver(remoteVersion, installed) > 0;
        }

        public static int CompareSemver(string left, string right)
        {
            int[] l = ParseVersionParts(left);
            int[] r = ParseVersionParts(right);
            int max = Math.Max(l.Length, r.Length);
            for (int i = 0; i < max; i++)
            {
                int li = i < l.Length ? l[i] : 0;
                int ri = i < r.Length ? r[i] : 0;
                if (li != ri)
                    return li > ri ? 1 : -1;
            }
            return 0;
        }

        private static int[] ParseVersionParts(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return Array.Empty<int>();
            string normalized = version.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(1);

            string[] split = normalized.Split('.', '-', '+');
            int[] parts = new int[split.Length];
            for (int i = 0; i < split.Length; i++)
            {
                parts[i] = int.TryParse(split[i], out int n) ? n : 0;
            }
            return parts;
        }
    }
}
