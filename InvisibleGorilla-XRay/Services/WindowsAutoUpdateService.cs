using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace InvisibleGorillaXRay.Services
{
    using Core;
    using Handlers;
    using Models;
    using Values;

    public static class WindowsAutoUpdateService
    {
        private const string DownloadFolderName = "Invisible Gorilla XRay";

        public static async void StartAutoUpdateFlow(Window parent, UpdateHandler updateHandler)
        {
            try
            {
                if (updateHandler == null)
                {
                    OpenReleasePageFallback();
                    return;
                }

                UpdateInfo? info = updateHandler.LastUpdateInfo
                    ?? await updateHandler.CheckForUpdateAsync().ConfigureAwait(true);

                if (info == null || info.Assets.Count == 0)
                {
                    OpenReleasePageFallback();
                    return;
                }

                ReleaseAsset? installer = PickWindowsInstallerAsset(info);
                if (installer == null)
                {
                    MessageBox.Show(
                        parent,
                        "No Windows installer is attached to the latest release. Opening the release page instead.",
                        "Invisible Gorilla XRay - Update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    OpenReleasePageFallback();
                    return;
                }

                MessageBoxResult confirm = MessageBox.Show(
                    parent,
                    $"Update {info.Version} is available.\n\nFile: {installer.Name}\nSize: {FormatSize(installer.Size)}\n\nDownload and run the installer now?",
                    "Invisible Gorilla XRay - Update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                    return;

                string destination = await DownloadInstallerAsync(updateHandler, installer).ConfigureAwait(true);
                if (string.IsNullOrEmpty(destination))
                {
                    MessageBox.Show(
                        parent,
                        "Download failed. Check your connection and try again.",
                        "Invisible Gorilla XRay - Update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                MessageBoxResult runConfirm = MessageBox.Show(
                    parent,
                    $"Update downloaded:\n{destination}\n\nLaunch the installer now? The current app will close.",
                    "Invisible Gorilla XRay - Update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (runConfirm != MessageBoxResult.Yes)
                    return;

                try
                {
                    Process.Start(new ProcessStartInfo(destination)
                    {
                        UseShellExecute = true
                    });
                    Application.Current?.Shutdown();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("WindowsAutoUpdateService.LaunchInstaller", ex);
                    MessageBox.Show(
                        parent,
                        "Could not launch the downloaded installer. Open the file manually:\n" + destination,
                        "Invisible Gorilla XRay - Update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("WindowsAutoUpdateService.StartAutoUpdateFlow", ex);
                OpenReleasePageFallback();
            }
        }

        private static ReleaseAsset? PickWindowsInstallerAsset(UpdateInfo info)
        {
            // Prefer self-contained Windows .exe asset, then .msi if any.
            ReleaseAsset? exe = info.Assets
                .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Name.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(a => a.Name.IndexOf("win", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault();
            if (exe != null)
                return exe;

            return info.Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string> DownloadInstallerAsync(UpdateHandler updateHandler, ReleaseAsset asset)
        {
            try
            {
                string downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloadDir = System.IO.Path.Combine(downloads, "Downloads", DownloadFolderName);
                System.IO.Directory.CreateDirectory(downloadDir);
                string destination = System.IO.Path.Combine(downloadDir, asset.Name);

                bool ok = await updateHandler.ReleaseService
                    .DownloadAssetAsync(asset, destination)
                    .ConfigureAwait(true);

                return ok ? destination : string.Empty;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("WindowsAutoUpdateService.DownloadInstallerAsync", ex);
                return string.Empty;
            }
        }

        private static void OpenReleasePageFallback()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Route.LATEST_RELEASE)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("WindowsAutoUpdateService.OpenReleasePageFallback", ex);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "?";
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}
