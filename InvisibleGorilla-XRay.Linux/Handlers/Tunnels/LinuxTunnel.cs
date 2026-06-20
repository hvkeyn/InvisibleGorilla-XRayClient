using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CorePath = InvisibleGorillaXRay.Values.Path;

namespace InvisibleGorillaXRay.Linux.Handlers.Tunnels
{
    using InvisibleGorillaXRay.Core;
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
        private readonly List<string> excludedServerRoutes = new();
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

                // CRITICAL: the VPN server must keep a direct (non-TUN) route. Otherwise xray's
                // own outbound to the server re-enters the tun device, tun2socks forwards it back
                // to the local SOCKS, xray dials the server again — an infinite routing loop that
                // spawns thousands of sockets ("too many open files" -> Out of memory). The DNS
                // resolve happens here, before the TUN default routes are installed, so it still
                // uses the real uplink.
                List<string> serverIps = ResolveServerIps(server);
                if (serverIps.Count == 0)
                {
                    Disable();
                    return new Status(
                        Code.ERROR,
                        SubCode.CANT_TUNNEL,
                        $"Could not resolve the VPN server address '{server}' to an IP. " +
                        "Refusing to enable TUN to avoid a routing loop.");
                }

                List<string> routeCommands = new();
                excludedServerRoutes.Clear();
                foreach (string serverIp in serverIps)
                {
                    string via = ResolveDirectVia(serverIp);
                    if (string.IsNullOrEmpty(via))
                        continue;

                    routeCommands.Add($"ip route replace {serverIp}/32 {via}");
                    excludedServerRoutes.Add(serverIp);
                }

                if (excludedServerRoutes.Count == 0)
                {
                    Disable();
                    return new Status(
                        Code.ERROR,
                        SubCode.CANT_TUNNEL,
                        $"Could not pin a direct route to the VPN server ({server}). " +
                        "Refusing to enable TUN to avoid a routing loop that exhausts sockets.");
                }

                // set -e: if any server-bypass route fails to install, the TUN default routes
                // below are NOT applied, so traffic keeps flowing on the original uplink instead
                // of looping. A non-zero exit then aborts the whole enable.
                routeCommands.Add($"ip route replace 0.0.0.0/1 dev {TUN_DEVICE}");
                routeCommands.Add($"ip route replace 128.0.0.0/1 dev {TUN_DEVICE}");

                int routeExit = LinuxPrivilegedRunner.RunBatchChecked(routeCommands, privileged);
                if (routeExit != 0)
                {
                    DiagnosticLog.Write("LinuxTunnel", $"Route batch failed (exit={routeExit}); aborting to avoid a loop.");
                    Disable();
                    return new Status(
                        Code.ERROR,
                        SubCode.CANT_TUNNEL,
                        "Failed to install split-tunnel routes; aborted to avoid a routing loop.");
                }

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
                List<string> cleanupCommands = new();

                foreach (string serverIp in excludedServerRoutes)
                    cleanupCommands.Add($"ip route del {serverIp}/32");

                cleanupCommands.Add("ip route del 0.0.0.0/1");
                cleanupCommands.Add("ip route del 128.0.0.0/1");

                if (CommandExists("resolvectl"))
                    cleanupCommands.Add($"resolvectl revert {TUN_DEVICE}");

                cleanupCommands.Add($"ip link set dev {TUN_DEVICE} down");
                cleanupCommands.Add($"ip tuntap del dev {TUN_DEVICE} mode tun");

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

        // The config "address" may be a raw IPv4, an IPv6, or a hostname (Reality configs often
        // use a domain). TUN split routing is IPv4, so we resolve to IPv4 addresses and pin each.
        private static List<string> ResolveServerIps(string server)
        {
            List<string> ips = new();
            if (string.IsNullOrWhiteSpace(server))
                return ips;

            string host = server.Trim();

            // Tolerate "host:port" or a bare host.
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon && !host.Contains('/'))
                host = host.Substring(0, colon);

            if (IPAddress.TryParse(host, out IPAddress? direct)
                && direct.AddressFamily == AddressFamily.InterNetwork)
            {
                ips.Add(direct.ToString());
                return ips;
            }

            try
            {
                foreach (IPAddress addr in Dns.GetHostAddresses(host))
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                        ips.Add(addr.ToString());
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("LinuxTunnel.ResolveServerIps", ex);
            }

            return ips.Distinct().ToList();
        }

        // Ask the kernel how it currently reaches the server and reuse exactly that path. Done
        // before the TUN default routes are installed, so it reflects the real uplink. Falls back
        // to the saved default gateway/interface when "ip route get" is unavailable.
        private string ResolveDirectVia(string serverIp)
        {
            string? via = null;
            string? dev = null;

            string output = RunCommandOutput("ip", $"route get {serverIp}");
            string[] parts = output.Split(
                new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "via") via = parts[i + 1];
                else if (parts[i] == "dev") dev = parts[i + 1];
            }

            // Never pin the server to the tun device itself (that would be the loop we prevent).
            if (string.Equals(dev, TUN_DEVICE, StringComparison.Ordinal))
            {
                via = null;
                dev = null;
            }

            if (string.IsNullOrEmpty(via) && string.IsNullOrEmpty(dev))
            {
                via = originalGateway;
                dev = string.Equals(originalInterface, TUN_DEVICE, StringComparison.Ordinal)
                    ? null
                    : originalInterface;
            }

            if (!string.IsNullOrEmpty(via) && !string.IsNullOrEmpty(dev))
                return $"via {via} dev {dev}";
            if (!string.IsNullOrEmpty(via))
                return $"via {via}";
            if (!string.IsNullOrEmpty(dev))
                return $"dev {dev}";

            return string.Empty;
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
