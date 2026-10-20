using System;
using System.Diagnostics;
using System.IO;
    using IOPath = System.IO.Path;
    using IOFile = System.IO.File;
    using IODirectory = System.IO.Directory;
    using IOFileInfo = System.IO.FileInfo;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace InvisibleGorillaXRay.Services
{
    using Core;
    using Handlers;
    using Models;
    using Values;

    /// <summary>
    /// Downloads the Windows asset of the latest release and replaces the running installation
    /// through a small helper script, then restarts the app. No installer and no manual steps.
    /// </summary>
    public static class WindowsAutoUpdateService
    {
        private const string UpdatesFolderName = "updates";
        private const string MarkerFileName = "applied.txt";

        public static async void StartAutoUpdateFlow(Window parent, UpdateHandler updateHandler, bool silent = false)
        {
            try
            {
                if (updateHandler == null)
                {
                    if (!silent)
                        OpenReleasePageFallback();
                    return;
                }

                UpdateInfo info = updateHandler.LastUpdateInfo
                    ?? await updateHandler.CheckForUpdateAsync().ConfigureAwait(true);

                if (info == null || info.Assets.Count == 0)
                {
                    if (!silent)
                        OpenReleasePageFallback();
                    return;
                }

                ReleaseAsset installer = PickWindowsPackage(info);
                if (installer == null)
                {
                    if (!silent)
                    {
                        MessageBox.Show(
                            parent,
                            "No Windows package is attached to the latest release. Opening the release page instead.",
                            "Invisible Gorilla XRay - Update",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        OpenReleasePageFallback();
                    }
                    return;
                }

                if (IsAlreadyApplied(info.Version))
                {
                    DiagnosticLog.Write(
                        "WindowsAutoUpdateService",
                        $"Update {info.Version} was already applied once; skipping to avoid a restart loop");
                    return;
                }

                if (!silent)
                {
                    MessageBoxResult confirm = MessageBox.Show(
                        parent,
                        $"Update {info.Version} is available.\n\nFile: {installer.Name}\nSize: {FormatSize(installer.Size)}\n\nDownload, install and restart now?",
                        "Invisible Gorilla XRay - Update",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (confirm != MessageBoxResult.Yes)
                        return;
                }

                string package = await DownloadPackageAsync(updateHandler, installer, info.Version).ConfigureAwait(true);
                if (string.IsNullOrEmpty(package))
                {
                    if (!silent)
                    {
                        MessageBox.Show(
                            parent,
                            "Download failed. Check your connection and try again.",
                            "Invisible Gorilla XRay - Update",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    return;
                }

                string payload = ExtractPackage(package);
                if (string.IsNullOrEmpty(payload))
                {
                    if (!silent)
                    {
                        MessageBox.Show(
                            parent,
                            "The downloaded package could not be unpacked.",
                            "Invisible Gorilla XRay - Update",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    return;
                }

                LaunchUpdater(payload, info.Version);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("WindowsAutoUpdateService.StartAutoUpdateFlow", ex);
                if (!silent)
                    OpenReleasePageFallback();
            }
        }

        private static ReleaseAsset PickWindowsPackage(UpdateInfo info)
        {
            // The release ships a self-contained zip for Windows; an installer is accepted too.
            ReleaseAsset zip = info.Assets
                .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Name.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(a => a.Name.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault();
            if (zip != null)
                return zip;

            return info.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? info.Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsAlreadyApplied(string version)
        {
            try
            {
                string marker = IOPath.Combine(UpdatesRoot, Sanitize(version), MarkerFileName);
                return IOFile.Exists(marker);
            }
            catch
            {
                return false;
            }
        }

        private static string UpdatesRoot => IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InvisibleGorillaXRay",
            UpdatesFolderName);

        private static async Task<string> DownloadPackageAsync(UpdateHandler updateHandler, ReleaseAsset asset, string version)
        {
            try
            {
                string dir = IOPath.Combine(UpdatesRoot, Sanitize(version));
                IODirectory.CreateDirectory(dir);

                string safeName = IOPath.GetFileName(asset.Name ?? string.Empty);
                if (string.IsNullOrWhiteSpace(safeName))
                    safeName = "update.zip";

                string destination = IOPath.Combine(dir, safeName);

                bool ok = await updateHandler.ReleaseService
                    .DownloadAssetAsync(asset, destination)
                    .ConfigureAwait(true);

                if (!ok)
                    return string.Empty;

                long actual = new IOFileInfo(destination).Length;
                if (actual == 0 || (asset.Size > 0 && actual != asset.Size))
                {
                    DiagnosticLog.Write(
                        "WindowsAutoUpdateService",
                        $"Downloaded asset rejected: expected={asset.Size}, actual={actual}, name={safeName}");
                    try { IOFile.Delete(destination); } catch { }
                    return string.Empty;
                }

                return destination;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("WindowsAutoUpdateService.DownloadPackageAsync", ex);
                return string.Empty;
            }
        }

        private static string ExtractPackage(string package)
        {
            try
            {
                string dir = IOPath.Combine(IOPath.GetDirectoryName(package) ?? UpdatesRoot, "payload");
                if (IODirectory.Exists(dir))
                    IODirectory.Delete(dir, recursive: true);

                System.IO.Compression.ZipFile.ExtractToDirectory(package, dir);
                return dir;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("WindowsAutoUpdateService.ExtractPackage", ex);
                return string.Empty;
            }
        }

        private static void LaunchUpdater(string payload, string version)
        {
            string target = AppContext.BaseDirectory.TrimEnd(IOPath.DirectorySeparatorChar);
            string exe = Environment.ProcessPath ?? IOPath.Combine(target, "Invisible Gorilla XRay.exe");
            int pid = Environment.ProcessId;

            string workDir = IOPath.GetDirectoryName(payload) ?? UpdatesRoot;
            string scriptPath = IOPath.Combine(workDir, "apply-update.ps1");
            IOFile.WriteAllText(scriptPath, UpdaterScript);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = string.Join(" ",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File",
                    Quote(scriptPath),
                    "-AppPid", pid.ToString(),
                    "-Source", Quote(payload),
                    "-Target", Quote(target),
                    "-Exe", Quote(exe),
                    "-Marker", Quote(IOPath.Combine(workDir, MarkerFileName))),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };

            DiagnosticLog.Write(
                "WindowsAutoUpdateService",
                $"Starting updater: version={version}, source={payload}, target={target}, exe={exe}");

            Process.Start(startInfo);
            Application.Current?.Shutdown();
        }

        private const string UpdaterScript = @"param(
    [int]$AppPid,
    [string]$Source,
    [string]$Target,
    [string]$Exe,
    [string]$Marker
)
$ErrorActionPreference = 'SilentlyContinue'

# The app has to release its files before they can be replaced.
$deadline = (Get-Date).AddMinutes(3)
while ((Get-Process -Id $AppPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
    Start-Sleep -Milliseconds 400
}
Start-Sleep -Milliseconds 700

# The tunnel helpers keep their own binaries locked (TUN folder).
foreach ($name in @('InvisibleGorilla-TUN', 'sing-box', 'tun2socks', 'tor', 'openflux')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 700

# Keep user data: settings, profiles, logs and the Tor data directory.
robocopy $Source $Target /E /R:5 /W:2 /NFL /NDL /NJH /NJS /XF Settings.json *.log /XD Configs Logs TOR_DATA

if ($Marker) { Set-Content -LiteralPath $Marker -Value ((Get-Date).ToString('o')) -Encoding ASCII }

Start-Process -FilePath $Exe -WorkingDirectory $Target
Start-Sleep -Seconds 2
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
";

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static string Sanitize(string value)
        {
            foreach (char invalid in IOPath.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "latest" : value;
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
