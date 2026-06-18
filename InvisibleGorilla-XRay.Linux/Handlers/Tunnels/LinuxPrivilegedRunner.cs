using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace InvisibleGorillaXRay.Linux.Handlers.Tunnels
{
    /// <summary>
    /// Runs privileged shell commands in a single pkexec/sudo invocation so the user
    /// sees one polkit prompt instead of one per ip/resolvectl call.
    /// </summary>
    internal static class LinuxPrivilegedRunner
    {
        public static void RunBatch(IEnumerable<string> commands, string privilegedFront, bool continueOnError = false)
        {
            List<string> commandList = commands
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .ToList();

            if (commandList.Count == 0 || string.IsNullOrEmpty(privilegedFront))
                return;

            StringBuilder script = new();
            script.AppendLine("#!/bin/sh");
            script.AppendLine(continueOnError ? "set +e" : "set -e");
            foreach (string command in commandList)
            {
                script.AppendLine(continueOnError ? $"{command} || true" : command);
            }

            string scriptPath = Path.Combine(
                Path.GetTempPath(),
                $"igxray-priv-{Guid.NewGuid():N}.sh");

            File.WriteAllText(scriptPath, script.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));

            try
            {
                if (privilegedFront == "pkexec")
                    StartAndWait("pkexec", $"/bin/sh \"{scriptPath}\"");
                else
                    StartAndWait("sudo", $"--non-interactive /bin/sh \"{scriptPath}\"");
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }

        public static string ResolvePrivilegedFront()
        {
            if (CommandExists("pkexec")) return "pkexec";
            if (CommandExists("sudo")) return "sudo";
            return string.Empty;
        }

        private static void StartAndWait(string fileName, string arguments)
        {
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            process?.WaitForExit(30000);
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
