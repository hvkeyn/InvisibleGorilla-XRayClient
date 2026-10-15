using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    using Models;
    using Values;
    using InvisibleGorillaXRay.Core;

    public sealed class BridgeCheckResult
    {
        public bool Success;
        public int LatencyMs;
        public string Message;

        public static BridgeCheckResult Ok(int latencyMs) =>
            new BridgeCheckResult { Success = true, LatencyMs = latencyMs, Message = "OK" };

        public static BridgeCheckResult Fail(string message) =>
            new BridgeCheckResult { Success = false, LatencyMs = -1, Message = message };
    }

    /// <summary>
    /// Manages the bundled tor daemon as a sidecar process: generates the torrc,
    /// starts/stops tor, monitors bootstrap progress over the control port, and runs
    /// throwaway instances to validate individual bridges.
    /// </summary>
    public sealed class TorManager
    {
        private const string Tag = "Tor";

        private Process torProcess;
        private readonly object sync = new object();

        public int BootstrapPercent { get; private set; }
        public string StatusSummary { get; private set; } = "Off";
        public bool IsRunning
        {
            get
            {
                lock (sync)
                {
                    return torProcess != null && !torProcess.HasExited;
                }
            }
        }

        public bool IsAvailable => File.Exists(Path.TOR_EXE);

        public Status Start(TorSettings settings, int maxWaitMs = 90000)
        {
            if (settings == null)
                return new Status(Code.ERROR, SubCode.CANT_CONNECT, "Tor settings are missing.");

            if (!IsAvailable)
                return new Status(Code.ERROR, SubCode.CANT_CONNECT,
                    $"Tor binary not found at {Path.TOR_EXE}. Reinstall the app or run the build with the Tor step.");

            Stop();

            int socksPort = settings.GetSocksPort();
            int controlPort = settings.GetControlPort();
            string dataDir = System.IO.Path.Combine(Directory.TOR_DATA, "session");
            string cookieFile = System.IO.Path.Combine(dataDir, "control_auth_cookie");
            string torrcPath = System.IO.Path.Combine(Directory.TOR_DATA, "torrc");
            string logFile = System.IO.Path.Combine(Directory.TOR_DATA, "tor.log");

            try
            {
                System.IO.Directory.CreateDirectory(dataDir);
                if (File.Exists(cookieFile))
                    File.Delete(cookieFile);

                List<string> bridges = settings.GetBridgeLines();
                string torrc = TorrcBuilder.Build(
                    socksPort, controlPort, dataDir, cookieFile, logFile,
                    settings.GetBridgeType(), bridges);
                File.WriteAllText(torrcPath, torrc);

                DiagnosticLog.Write(Tag, $"Starting tor (socks={socksPort}, control={controlPort}, bridgeType={settings.GetBridgeType()})");

                if (!StartProcess(torrcPath))
                    return new Status(Code.ERROR, SubCode.CANT_CONNECT, "Failed to launch the tor process.");

                StatusSummary = "Starting";
                BootstrapPercent = 0;

                Status bootstrap = WaitForBootstrap(controlPort, cookieFile, maxWaitMs, settings);
                if (bootstrap.Code != Code.SUCCESS)
                {
                    Stop();
                    return bootstrap;
                }

                StatusSummary = "Connected";
                BootstrapPercent = 100;
                DiagnosticLog.Write(Tag, "Tor bootstrap complete (100%).");
                return new Status(Code.SUCCESS, SubCode.SUCCESS, socksPort);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException(Tag, ex);
                Stop();
                return new Status(Code.ERROR, SubCode.CANT_CONNECT, $"Tor error: {ex.Message}");
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                if (torProcess != null)
                {
                    try
                    {
                        if (!torProcess.HasExited)
                        {
                            torProcess.Kill();
                            torProcess.WaitForExit(4000);
                        }
                    }
                    catch { }
                    try { torProcess.Dispose(); } catch { }
                    torProcess = null;
                }
            }

            BootstrapPercent = 0;
            StatusSummary = "Off";
        }

        private bool StartProcess(string torrcPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.TOR_EXE,
                Arguments = $"-f \"{torrcPath}\"",
                WorkingDirectory = Directory.TOR,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) DiagnosticLog.Write(Tag + ".out", e.Data); };
            process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) DiagnosticLog.Write(Tag + ".err", e.Data); };

            if (!process.Start())
                return false;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (sync)
            {
                torProcess = process;
            }
            return true;
        }

        private Status WaitForBootstrap(int controlPort, string cookieFile, int maxWaitMs, TorSettings settings)
        {
            int elapsed = 0;
            const int interval = 1000;
            const int stallLimitMs = 30000;

            // Phase 1: wait for the control port + cookie file to appear.
            while (elapsed < maxWaitMs)
            {
                if (!IsRunning)
                    return new Status(Code.ERROR, SubCode.CANT_CONNECT, "Tor process exited during startup. Check tor.log.");

                if (IsPortActive(controlPort) && File.Exists(cookieFile))
                    break;

                Thread.Sleep(interval);
                elapsed += interval;
            }

            BridgeType active = settings?.GetBridgeType() ?? BridgeType.NONE;
            var tried = new HashSet<BridgeType> { active };
            int lastPercent = -1;
            int stallMs = 0;
            bool chainExhausted = false;

            // Phase 2: poll bootstrap. If progress stalls for 30s, switch transport
            // with SETCONF (Orbot Smart Connect) instead of killing tor.
            while (elapsed < maxWaitMs)
            {
                if (!IsRunning)
                    return new Status(Code.ERROR, SubCode.CANT_CONNECT, "Tor process exited during bootstrap. Check tor.log.");

                using (var control = new TorControlClient())
                {
                    if (control.Connect(controlPort, cookieFile))
                    {
                        int percent = control.GetBootstrapPercent(out string summary);
                        if (percent >= 0)
                        {
                            BootstrapPercent = percent;
                            StatusSummary = string.IsNullOrEmpty(summary)
                                ? $"{percent}% {active}"
                                : $"{percent}% {summary} ({active})";
                            if (percent >= 100)
                            {
                                StatusSummary = $"Connected via {active}";
                                DiagnosticLog.Write(Tag, $"Tor bootstrap complete via {active}.");
                                return new Status(Code.SUCCESS, SubCode.SUCCESS, null);
                            }

                            if (percent > lastPercent)
                            {
                                lastPercent = percent;
                                stallMs = 0;
                            }
                            else
                            {
                                stallMs += interval;
                            }
                        }
                        else
                        {
                            stallMs += interval;
                        }
                    }
                    else
                    {
                        stallMs += interval;
                    }
                }

                if (!chainExhausted && stallMs >= stallLimitMs && BootstrapPercent < 100)
                {
                    BridgeType? next = NextTransport(tried);
                    if (next == null)
                    {
                        chainExhausted = true;
                        DiagnosticLog.Write(Tag, $"Smart connect: {active} stalled at {BootstrapPercent}% and no further transport is available.");
                    }
                    else if (SwitchTransport(controlPort, cookieFile, next.Value))
                    {
                        DiagnosticLog.Write(Tag, $"Smart connect: {active} stalled at {BootstrapPercent}%, switching to {next.Value}.");
                        active = next.Value;
                        tried.Add(active);
                        lastPercent = -1;
                        stallMs = 0;
                        StatusSummary = $"Switching to {active}";
                    }
                    else
                    {
                        tried.Add(next.Value);
                        stallMs = 0;
                    }
                }

                Thread.Sleep(interval);
                elapsed += interval;
            }

            return new Status(Code.ERROR, SubCode.CANT_CONNECT,
                $"Tor did not finish bootstrapping within {maxWaitMs / 1000}s (reached {BootstrapPercent}% on {active}). Try different bridges.");
        }

        /// <summary>
        /// Orbot order after the user's current choice: snowflake, then obfs4, then meek.
        /// Direct is never a fallback once a bridge was requested.
        /// </summary>
        private static BridgeType? NextTransport(HashSet<BridgeType> tried)
        {
            BridgeType[] order = { BridgeType.SNOWFLAKE, BridgeType.OBFS4, BridgeType.MEEK_AZURE };
            foreach (BridgeType candidate in order)
            {
                if (tried.Contains(candidate))
                    continue;
                if (!TorrcBuilder.TryGetRuntimeBridge(candidate, null, out _, out _, out _))
                    continue;
                return candidate;
            }
            return null;
        }

        private bool SwitchTransport(int controlPort, string cookieFile, BridgeType type)
        {
            if (!TorrcBuilder.TryGetRuntimeBridge(type, null, out string plugin, out List<string> bridges, out string error))
            {
                DiagnosticLog.Write(Tag, $"Smart connect cannot use {type}: {error}");
                return false;
            }

            using (var control = new TorControlClient())
            {
                if (!control.Connect(controlPort, cookieFile))
                    return false;

                if (!control.ResetConf(out string resetReply, "UseBridges", "ClientTransportPlugin", "Bridge"))
                {
                    DiagnosticLog.Write(Tag, "Smart connect RESETCONF failed: " + resetReply);
                    return false;
                }

                if (type == BridgeType.NONE)
                    return true;

                var args = new List<string>
                {
                    "UseBridges=1",
                    "ClientTransportPlugin=" + TorrcBuilder.QuoteConfValue(plugin)
                };
                foreach (string bridge in bridges)
                    args.Add("Bridge=" + TorrcBuilder.QuoteConfValue(bridge));

                if (!control.SetConf(out string setReply, args.ToArray()))
                {
                    DiagnosticLog.Write(Tag, "Smart connect SETCONF failed: " + setReply);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Validates a single bridge by booting a throwaway tor instance with only that bridge,
        /// on isolated ports, and timing how long it takes to reach 100% bootstrap.
        /// </summary>
        public BridgeCheckResult CheckBridge(BridgeType bridgeType, string bridgeLine, int timeoutMs = 60000)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(bridgeLine))
                lines.Add(bridgeLine.Trim());
            return CheckBridge(bridgeType, lines, timeoutMs);
        }

        /// <summary>
        /// Boots one throwaway tor with every supplied line. Tor tries them together,
        /// so a dead first line no longer hides a working later line.
        /// </summary>
        public BridgeCheckResult CheckBridge(BridgeType bridgeType, IList<string> bridgeLines, int timeoutMs = 60000)
        {
            if (!IsAvailable)
                return BridgeCheckResult.Fail("Tor binary not bundled.");

            int socksPort = FindFreePort(19250);
            int controlPort = FindFreePort(socksPort + 1);
            string dataDir = System.IO.Path.Combine(Directory.TOR_DATA, "check");
            string cookieFile = System.IO.Path.Combine(dataDir, "control_auth_cookie");
            string torrcPath = System.IO.Path.Combine(Directory.TOR_DATA, "torrc.check");
            string logFile = System.IO.Path.Combine(Directory.TOR_DATA, "tor-check.log");

            Process check = null;
            try
            {
                try { if (System.IO.Directory.Exists(dataDir)) System.IO.Directory.Delete(dataDir, true); } catch { }
                System.IO.Directory.CreateDirectory(dataDir);

                var lines = new List<string>();
                if (bridgeLines != null)
                {
                    foreach (string line in bridgeLines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            lines.Add(line.Trim());
                    }
                }

                string torrc = TorrcBuilder.Build(socksPort, controlPort, dataDir, cookieFile, logFile, bridgeType, lines);
                File.WriteAllText(torrcPath, torrc);

                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.TOR_EXE,
                    Arguments = $"-f \"{torrcPath}\"",
                    WorkingDirectory = Directory.TOR,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                check = new Process { StartInfo = startInfo };
                check.OutputDataReceived += (s, e) => { };
                check.ErrorDataReceived += (s, e) => { };
                if (!check.Start())
                    return BridgeCheckResult.Fail("Failed to start tor for bridge check.");
                check.BeginOutputReadLine();
                check.BeginErrorReadLine();

                var stopwatch = Stopwatch.StartNew();
                int elapsed = 0;
                const int interval = 1000;

                while (elapsed < timeoutMs)
                {
                    if (check.HasExited)
                        return BridgeCheckResult.Fail("Tor exited before reaching the bridge.");

                    if (IsPortActive(controlPort) && File.Exists(cookieFile))
                    {
                        using (var control = new TorControlClient())
                        {
                            if (control.Connect(controlPort, cookieFile))
                            {
                                int percent = control.GetBootstrapPercent(out _);
                                if (percent >= 100)
                                {
                                    stopwatch.Stop();
                                    return BridgeCheckResult.Ok((int)stopwatch.ElapsedMilliseconds);
                                }
                            }
                        }
                    }

                    Thread.Sleep(interval);
                    elapsed += interval;
                }

                return BridgeCheckResult.Fail($"Bridge did not connect within {timeoutMs / 1000}s.");
            }
            catch (Exception ex)
            {
                return BridgeCheckResult.Fail(ex.Message);
            }
            finally
            {
                try
                {
                    if (check != null && !check.HasExited)
                    {
                        check.Kill();
                        check.WaitForExit(3000);
                    }
                    check?.Dispose();
                }
                catch { }
            }
        }

        private static bool IsPortActive(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(500))
                        return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static int FindFreePort(int start)
        {
            for (int port = start; port < start + 200; port++)
            {
                if (!IsPortActive(port))
                    return port;
            }
            return start;
        }
    }
}
