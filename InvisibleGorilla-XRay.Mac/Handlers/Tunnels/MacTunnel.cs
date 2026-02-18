using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace InvisibleGorillaXRay.Mac.Handlers.Tunnels
{
    using InvisibleGorillaXRay.Handlers.Tunnels;
    using InvisibleGorillaXRay.Models;

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
        private const string TUN_DEVICE = "utun9";
        private const string TUN2SOCKS_PATH = "./TUN/tun2socks";

        public Status Enable(string ip, int port, string address, string server, string dns)
        {
            isCancelled = false;

            if (!File.Exists(TUN2SOCKS_PATH))
            {
                return new Status(
                    Code.ERROR,
                    SubCode.CANT_TUNNEL,
                    "tun2socks binary not found. Please ensure TUN/tun2socks exists.");
            }

            try
            {
                SaveOriginalRoutes();

                StartTun2Socks(ip, port);

                if (isCancelled)
                    return new Status(Code.INFO, SubCode.CANCELED, null);

                Thread.Sleep(1500);

                ConfigureRoutes(ip, server);

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
        }

        public void Cancel()
        {
            isCancelled = true;
            Disable();
        }

        private void StartTun2Socks(string tunIp, int socksPort)
        {
            tun2socksProcess = new Process();
            tun2socksProcess.StartInfo = new ProcessStartInfo
            {
                FileName = TUN2SOCKS_PATH,
                Arguments = $"-device {TUN_DEVICE} -proxy socks5://127.0.0.1:{socksPort} -interface 127.0.0.1",
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

        private void ConfigureRoutes(string tunIp, string serverAddress)
        {
            if (!string.IsNullOrEmpty(originalGateway))
                RunCommand("route", $"add -host {serverAddress} {originalGateway}");

            RunCommand("route", $"add -net 0.0.0.0/1 {tunIp}");
            RunCommand("route", $"add -net 128.0.0.0/1 {tunIp}");
        }

        private void RestoreRoutes()
        {
            RunCommand("route", "delete -net 0.0.0.0/1");
            RunCommand("route", "delete -net 128.0.0.0/1");
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
