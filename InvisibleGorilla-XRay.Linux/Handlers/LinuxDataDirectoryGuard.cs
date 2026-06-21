using System;
using System.IO;
using InvisibleGorillaXRay.Core;

namespace InvisibleGorillaXRay.Linux.Handlers
{
    /// <summary>
    /// Detects data directories left root-owned after an accidental sudo ./run-igxray
    /// and surfaces a one-line fix before TUN setup fails with Access denied.
    /// </summary>
    internal static class LinuxDataDirectoryGuard
    {
        public static void ValidateAtStartup()
        {
            string dataRoot = Values.Directory.DATA_ROOT;
            string tunDir = Values.Directory.DATA_TUN;

            if (IsWritableDirectory(tunDir) && CanWriteProbe(Path.Combine(tunDir, ".write-probe")))
                return;

            string fixCommand = $"sudo chown -R \"{Environment.UserName}\" \"{dataRoot}\"";

            DiagnosticLog.Write(
                "LinuxDataDirectoryGuard",
                $"Data directory is not writable (likely created by a prior sudo run): {tunDir}");

            LinuxNotifyHandler.TrySendNotification(
                "Invisible Gorilla XRay",
                "Cannot write to the data folder (probably owned by root after sudo). " +
                $"Run in a terminal: {fixCommand}");
        }

        public static bool IsWritableDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0;
            }
            catch
            {
                return false;
            }
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
    }
}
