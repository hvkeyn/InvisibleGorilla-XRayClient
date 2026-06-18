using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CorePath = InvisibleGorillaXRay.Values.Path;

namespace InvisibleGorillaXRay.Linux.Handlers.Tunnels
{
    using InvisibleGorillaXRay.Linux.Handlers.Settings;
    using InvisibleGorillaXRay.Handlers.Tunnels;
    using InvisibleGorillaXRay.Handlers;
    using InvisibleGorillaXRay.Models;

    /// <summary>
    /// Linux TUN implementation using xjasonlyu/tun2socks.
    /// Routes all traffic through a tun device to the local SOCKS5 listener.
    /// Requires CAP_NET_ADMIN — privileged commands are batched into one pkexec/sudo
    /// invocation per setup/teardown phase.
    /// </summary>
    public class LinuxTunnel : ITunnel
    {
        private Process? tun2socksProcess;
        private bool isCancelled;
        private string? originalGateway;
        private string? originalInterface;
        private const string TUN_DEVICE = "tun-igxray";

        public Status Enable(string ip, int port, string address, string server, string dns, LocalProxyCredentials localProxyCredentials)
        {
            isCancelled = false;

            if (!File.Exists(CorePath.TUN_EXE))
            {
                return new Status(
                    Code.ERROR,
                    SubCode.CANT_TUNNEL,
                    $"tun2socks binary not found at {CorePath.TUN_EXE}. Run ./build.sh to fetch and bundle it.");
            }

            string privileged = LinuxPrivilegedRunner.ResolvePrivilegedFront();
            if (string.IsNullOrEmpty(privileged))
            {
                return new Status(
                    Code.ERROR,
                    SubCode.CANT_TUNNEL,
                    "Neither pkexec nor sudo is available. Install policykit-1 (pkexec) for GNOME prompts, or sudo.");
            }

            try
            {
                Status appRulesStatus = PrepareAppRulesBridge(port, address, dns, localProxyCredentials);
                if (appRulesStatus.Code == Code.ERROR)
                    return appRulesStatus;

                SaveOriginalRoutes();

                string user = Environment.UserName;
                LinuxPrivilegedRunner.RunBatch(new[]
                {
                    $"ip tuntap add dev {TUN_DEVICE} mode tun user {user}",
                    $"ip addr add {ip}/24 dev {TUN_DEVICE}",
                    $"ip link set dev {TUN_DEVICE} up"
                }, privileged);

                StartTun2Socks(ip, port, localProxyCredentials);

                if (isCancelled)
                    return new Status(Code.INFO, SubCode.CANCELED, null);

                Thread.Sleep(1500);

                List<string> routeCommands = new();
                if (!string.IsNullOrEmpty(originalGateway) && !string.IsNullOrEmpty(server))
                    routeCommands.Add($"ip route add {server}/32 via {originalGateway}");

                routeCommands.Add($"ip route add 0.0.0.0/1 dev {TUN_DEVICE}");
                routeCommands.Add($"ip route add 128.0.0.0/1 dev {TUN_DEVICE}");
                LinuxPrivilegedRunner.RunBatch(routeCommands, privileged);

                if (!string.IsNullOrEmpty(dns) && CommandExists("resolvectl"))
                {
                    LinuxPrivilegedRunner.RunBatch(new[]
                    {
                        $"resolvectl dns {TUN_DEVICE} {dns}"
                    }, privileged);
                }

                return new Status(Code.SUCCESS, SubCode.SUCCESS, null);
            }
            catch (Exception ex)
            {
                Disable();
                return new Status(Code.ERROR, SubCode.CANT_TUNNEL, $"TUN error: {ex.Message}");
            }
        }

        public void Disable()
        {
            string privileged = LinuxPrivilegedRunner.ResolvePrivilegedFront();

            try { StopTun2Socks(); } catch { }

            if (!string.IsNullOrEmpty(privileged))
            {
                List<string> cleanupCommands = new()
                {
                    "ip route del 0.0.0.0/1",
                    "ip route del 128.0.0.0/1",
                    $"ip link set dev {TUN_DEVICE} down",
                    $"ip tuntap del dev {TUN_DEVICE} mode tun"
                };

                if (CommandExists("resolvectl"))
                    cleanupCommands.Insert(2, $"resolvectl revert {TUN_DEVICE}");

                try
                {
                    LinuxPrivilegedRunner.RunBatch(cleanupCommands, privileged, continueOnError: true);
                }
                catch { }
            }

            try { LinuxAppRulesBridge.Clear(); } catch { }
        }

        public void Cancel()
        {
            isCancelled = true;
            Disable();
        }

        private static Status PrepareAppRulesBridge(int socksPort, string tunnelAddress, string dns, LocalProxyCredentials localProxyCredentials)
        {
            SettingsHandler settingsHandler = new(() => new LinuxStartup());
            UserSettings settings = settingsHandler.UserSettings;
            return LinuxAppRulesBridge.Prepare(settings, socksPort, tunnelAddress, dns, localProxyCredentials);
        }

        private void StartTun2Socks(string tunIp, int socksPort, LocalProxyCredentials localProxyCredentials)
        {
            string proxyArgument = localProxyCredentials?.HasValue == true
                ? localProxyCredentials.BuildSocks5Uri("127.0.0.1", socksPort)
                : $"socks5://127.0.0.1:{socksPort}";

            tun2socksProcess = new Process();
            tun2socksProcess.StartInfo = new ProcessStartInfo
            {
                FileName = CorePath.TUN_EXE,
                Arguments = $"-device {TUN_DEVICE} -proxy {proxyArgument} -interface lo",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            tun2socksProcess.Start();
        }

        private void StopTun2Socks()
        {
            if (tun2socksProcess != null && !tun2socksProcess.HasExited)
            {
                tun2socksProcess.Kill();
                tun2socksProcess.WaitForExit(3000);
                tun2socksProcess.Dispose();
                tun2socksProcess = null;
            }
        }

        private void SaveOriginalRoutes()
        {
            string output = RunCommandOutput("ip", "route show default");
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("default")) continue;

                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i] == "via") originalGateway = parts[i + 1];
                    if (parts[i] == "dev") originalInterface = parts[i + 1];
                }
                break;
            }
        }

        private static bool CommandExists(string name)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/env",
                    Arguments = $"which {name}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                })!;
                p.WaitForExit(2000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private string RunCommandOutput(string command, string args)
        {
            try
            {
                var p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return output;
            }
            catch { return string.Empty; }
        }
    }
}
