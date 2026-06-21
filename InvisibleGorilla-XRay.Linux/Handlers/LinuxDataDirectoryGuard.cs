using System;
using System.Diagnostics;
using System.IO;
using InvisibleGorillaXRay.Core;

namespace InvisibleGorillaXRay.Linux.Handlers
{
    internal static class LinuxDataDirectoryGuard
    {
        public static void ValidateAtStartup()
        {
            string dataRoot = Values.Directory.DATA_ROOT;
            string probePath = Path.Combine(dataRoot, ".write-probe");

            if (CanWriteProbe(probePath))
                return;

            DiagnosticLog.Write(
                "LinuxDataDirectoryGuard",
                $"Data directory is not writable (likely created by a prior sudo run): {dataRoot}");

            if (TryRepairOwnership(dataRoot) && CanWriteProbe(probePath))
            {
                DiagnosticLog.Write("LinuxDataDirectoryGuard", "Ownership repair succeeded.");
                return;
            }

            string fixCommand = $"sudo chown -R \"{Environment.UserName}\" \"{dataRoot}\"";
            LinuxNotifyHandler.TrySendNotification(
                "Invisible Gorilla XRay",
                "Data folder is not writable (root-owned after sudo). " +
                $"Run once in a terminal: {fixCommand}");
        }

        public static bool CanWriteProbe(string probePath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(probePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(probePath, "ok");
                File.Delete(probePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRepairOwnership(string dataRoot)
        {
            if (!OperatingSystem.IsLinux())
                return false;

            try
            {
                if (!CommandExists("pkexec"))
                    return false;

                string escapedRoot = dataRoot.Replace("'", "'\\''");
                string user = Environment.UserName.Replace("'", "'\\''");
                string script = $"chown -R '{user}' '{escapedRoot}'";

                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkexec",
                    Arguments = $"/bin/sh -c \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;

                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("LinuxDataDirectoryGuard.TryRepairOwnership", ex);
                return false;
            }
        }

        private static bool CommandExists(string name)
        {
            try
            {
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/env",
                    Arguments = $"which {name}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                })!;

                process.WaitForExit(2000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
