using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace InvisibleGorillaXRay.Mac.Handlers.Tunnels
{
    using InvisibleGorillaXRay.Core;
    using InvisibleGorillaXRay.Mac.Handlers.Settings;
    using InvisibleGorillaXRay.Handlers.Tunnels;
    using InvisibleGorillaXRay.Handlers;
    using InvisibleGorillaXRay.Models;
    using CorePath = InvisibleGorillaXRay.Values.Path;

    /// <summary>
    /// macOS TUN implementation using tun2socks.
    /// Routes all traffic through a utun device to the SOCKS proxy.
    /// Requires the tun2socks binary in the TUN/ directory.
    /// May need elevated privileges (sudo) for route manipulation.
    /// </summary>
    public class MacTunnel : ITunnel
    {
        private Process? tun2socksProcess;
        private bool isCancelled;
        private string? originalGateway;
        private string? originalInterface;
        private readonly List<string> excludedServerRoutes = new();
        private const string TUN_DEVICE = "utun9";

        public Status Enable(string ip, int port, string address, string server, string dns, LocalProxyCredentials localProxyCredentials)
        {
            isCancelled = false;

            if (!File.Exists(CorePath.TUN_EXE))
            {
                return new Status(
                    Code.ERROR,
                    SubCode.CANT_TUNNEL,
                    $"tun2socks binary not found at {CorePath.TUN_EXE}. Please ensure the app bundle contains TUN/tun2socks.");
            }

            try
            {
                Status appRulesStatus = PrepareAppRulesBridge(port, address, dns, localProxyCredentials);
                if (appRulesStatus.Code == Code.ERROR)
                    return appRulesStatus;

                SaveOriginalRoutes();

                StartTun2Socks(ip, port, localProxyCredentials);

                if (isCancelled)
                    return new Status(Code.INFO, SubCode.CANCELED, null);

                Thread.Sleep(1500);

                if (!ConfigureRoutes(ip, server))
                {
                    Disable();
                    return new Status(
                        Code.ERROR,
                        SubCode.CANT_TUNNEL,
                        $"Could not pin a direct route to the VPN server ({server}). " +
                        "Refusing to enable TUN to avoid a routing loop that exhausts sockets.");
                }

                if (!string.IsNullOrEmpty(dns))
                    ConfigureDns(dns);

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
            try { StopTun2Socks(); } catch { }
            try { RestoreRoutes(); } catch { }
            try { RestoreDns(); } catch { }
            try { MacAppRulesBridge.Clear(); } catch { }
        }

        public void Cancel()
        {
            isCancelled = true;
            Disable();
        }

        private static Status PrepareAppRulesBridge(int socksPort, string tunnelAddress, string dns, LocalProxyCredentials localProxyCredentials)
        {
            SettingsHandler settingsHandler = new(() => new MacStartup());
            UserSettings settings = settingsHandler.UserSettings;
            return MacAppRulesBridge.Prepare(settings, socksPort, tunnelAddress, dns, localProxyCredentials);
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
                Arguments = $"-device {TUN_DEVICE} -proxy {proxyArgument} -interface 127.0.0.1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            tun2socksProcess.Start();

            RunCommand("ifconfig", $"{TUN_DEVICE} {tunIp} {tunIp} up");
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
            string output = RunCommandOutput("route", "-n get default");
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("gateway:"))
                    originalGateway = trimmed.Split(':')[1].Trim();
                else if (trimmed.StartsWith("interface:"))
                    originalInterface = trimmed.Split(':')[1].Trim();
            }
        }

        // The VPN server must keep a direct (non-TUN) route, otherwise xray's own outbound to the
        // server re-enters the utun device, loops back through tun2socks -> local SOCKS -> xray and
        // spawns endless sockets (socket storm -> out of memory). Returns false when no direct
        // route to the server could be installed, so the caller can refuse to bring up the TUN.
        private bool ConfigureRoutes(string tunIp, string serverAddress)
        {
            List<string> serverIps = ResolveServerIps(serverAddress);
            excludedServerRoutes.Clear();

            foreach (string serverIp in serverIps)
            {
                (string? gateway, string? iface) = ResolveDirectPath(serverIp);

                if (!string.IsNullOrEmpty(gateway))
                    RunCommand("route", $"add -host {serverIp} {gateway}");
                else if (!string.IsNullOrEmpty(iface))
                    RunCommand("route", $"add -host {serverIp} -interface {iface}");
                else
                    continue;

                excludedServerRoutes.Add(serverIp);
            }

            if (excludedServerRoutes.Count == 0)
                return false;

            RunCommand("route", $"add -net 0.0.0.0/1 {tunIp}");
            RunCommand("route", $"add -net 128.0.0.0/1 {tunIp}");
            return true;
        }

        private void RestoreRoutes()
        {
            RunCommand("route", "delete -net 0.0.0.0/1");
            RunCommand("route", "delete -net 128.0.0.0/1");

            foreach (string serverIp in excludedServerRoutes)
                RunCommand("route", $"delete -host {serverIp}");

            excludedServerRoutes.Clear();
        }

        private static List<string> ResolveServerIps(string server)
        {
            List<string> ips = new();
            if (string.IsNullOrWhiteSpace(server))
                return ips;

            string host = server.Trim();

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
                DiagnosticLog.WriteException("MacTunnel.ResolveServerIps", ex);
            }

            return ips.Distinct().ToList();
        }

        // Ask the routing table how it currently reaches the server (before the TUN default routes
        // hijack it) and reuse that gateway/interface for the bypass route.
        private (string? gateway, string? iface) ResolveDirectPath(string serverIp)
        {
            string output = RunCommandOutput("route", $"-n get {serverIp}");
            string? gateway = null;
            string? iface = null;

            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("gateway:"))
                    gateway = trimmed.Split(':')[1].Trim();
                else if (trimmed.StartsWith("interface:"))
                    iface = trimmed.Split(':')[1].Trim();
            }

            if (string.Equals(iface, TUN_DEVICE, StringComparison.Ordinal))
            {
                gateway = null;
                iface = null;
            }

            if (string.IsNullOrEmpty(gateway) && string.IsNullOrEmpty(iface))
            {
                gateway = originalGateway;
                iface = string.Equals(originalInterface, TUN_DEVICE, StringComparison.Ordinal)
                    ? null
                    : originalInterface;
            }

            return (gateway, iface);
        }

        private void ConfigureDns(string dns)
        {
            string service = GetActiveNetworkService();
            if (!string.IsNullOrEmpty(service))
                RunCommand("networksetup", $"-setdnsservers \"{service}\" {dns}");
        }

        private void RestoreDns()
        {
            string service = GetActiveNetworkService();
            if (!string.IsNullOrEmpty(service))
                RunCommand("networksetup", $"-setdnsservers \"{service}\" Empty");
        }

        private string GetActiveNetworkService()
        {
            string[] candidates = { "Wi-Fi", "Ethernet", "USB 10/100/1000 LAN" };
            foreach (var svc in candidates)
            {
                string info = RunCommandOutput("networksetup", $"-getinfo \"{svc}\"");
                if (info.Contains("IP address") && !info.Contains("none"))
                    return svc;
            }
            return "Wi-Fi";
        }

        private void RunCommand(string command, string args)
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            p?.WaitForExit(5000);
        }

        private string RunCommandOutput(string command, string args)
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
    }
}
