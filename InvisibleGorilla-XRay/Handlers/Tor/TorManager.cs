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
        public BridgeType UsedType;
        public List<string> UsedLines;
        public string LinkLabel;

        public static BridgeCheckResult Ok(int latencyMs) =>
            Ok(latencyMs, BridgeType.NONE, null);

        public static BridgeCheckResult Ok(int latencyMs, BridgeType type, IList<string> lines)
        {
            var copy = new List<string>();
            if (lines != null)
            {
                foreach (string line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        copy.Add(line.Trim());
                }
            }

            return new BridgeCheckResult
            {
                Success = true,
                LatencyMs = latencyMs,
                Message = "OK",
                UsedType = type,
                UsedLines = copy
            };
        }

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
        /// <summary>Transport and short bridge ids for the live session. No addresses.</summary>
        public string ActiveLinkLabel { get; private set; } = "";
        public static string LatestLinkLabel { get; private set; } = "";
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

        public Status Start(TorSettings settings, int maxWaitMs = 180000)
        {
            if (settings == null)
                return new Status(Code.ERROR, SubCode.CANT_CONNECT, "Tor settings are missing.");

            if (!IsAvailable)
                return new Status(Code.ERROR, SubCode.CANT_CONNECT,
                    $"Tor binary not found at {Path.TOR_EXE}. Reinstall the app or run the build with the Tor step.");

            settings = PrepareSettings(settings);
            Stop();

            int socksPort = settings.GetSocksPort();
            int controlPort = settings.GetControlPort();
            // tor resolves relative paths against its WorkingDirectory (the Tor folder), so
            // "./Tor/Data/torrc" became "<app>\Tor\Tor\Data\torrc" and tor refused to start.
            string torDataRoot = System.IO.Path.GetFullPath(Directory.TOR_DATA);
            string dataDir = System.IO.Path.Combine(torDataRoot, "session");
            string cookieFile = System.IO.Path.Combine(dataDir, "control_auth_cookie");
            string torrcPath = System.IO.Path.Combine(torDataRoot, "torrc");
            string logFile = System.IO.Path.Combine(torDataRoot, "tor.log");

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
            ActiveLinkLabel = "";
            LatestLinkLabel = "";
        }

        private bool StartProcess(string torrcPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = System.IO.Path.GetFullPath(Path.TOR_EXE),
                Arguments = $"-f \"{torrcPath}\"",
                WorkingDirectory = System.IO.Path.GetFullPath(Directory.TOR_DATA),
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

            List<BridgeAttempt> chain = BuildChain(settings?.GetBridgeType() ?? BridgeType.NONE, settings?.GetBridgeLines());
            int activeIndex = 0;
            BridgeType active = chain.Count > 0 ? chain[0].Type : (settings?.GetBridgeType() ?? BridgeType.NONE);
            int lastPercent = -1;
            int stallMs = 0;
            bool chainExhausted = chain.Count <= 1;

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
                                IList<string> activeLines = activeIndex >= 0 && activeIndex < chain.Count
                                    ? chain[activeIndex].Lines
                                    : null;
                                ActiveLinkLabel = DescribeLink(control, active, activeLines);
                                LatestLinkLabel = ActiveLinkLabel;
                                StatusSummary = string.IsNullOrEmpty(ActiveLinkLabel)
                                    ? $"Connected via {active}"
                                    : ActiveLinkLabel;
                                DiagnosticLog.Write(Tag, "Tor bootstrap complete via " + active + ".");
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

                // Below 15% the bridge itself has not answered, so 30s is enough.
                // After the relay answers, directory downloads often sit still longer than 30s.
                int stallLimit = BootstrapPercent >= 15 ? 120000 : stallLimitMs;
                if (!chainExhausted && stallMs >= stallLimit && BootstrapPercent < 100)
                {
                    int nextIndex = activeIndex + 1;
                    if (nextIndex >= chain.Count)
                    {
                        chainExhausted = true;
                        DiagnosticLog.Write(Tag, $"Smart connect: {active} stalled at {BootstrapPercent}% and no further transport is available.");
                    }
                    else
                    {
                        BridgeAttempt next = chain[nextIndex];
                        activeIndex = nextIndex;
                        if (SwitchTransport(controlPort, cookieFile, next.Type, next.Lines))
                        {
                            DiagnosticLog.Write(Tag, $"Smart connect: {active} stalled at {BootstrapPercent}%, switching to {next.Type}.");
                            active = next.Type;
                            lastPercent = -1;
                            stallMs = 0;
                            BootstrapPercent = 0;
                            StatusSummary = $"Switching to {active}";
                        }
                        else
                        {
                            stallMs = stallLimitMs;
                        }
                    }
                }

                Thread.Sleep(interval);
                elapsed += interval;
            }

            string hint = active == BridgeType.NONE
                ? "Direct Tor looks blocked on this network: pick a bridge in Settings -> Tor (Snowflake or obfs4)."
                : "Try different bridges.";

            return new Status(Code.ERROR, SubCode.CANT_CONNECT,
                $"Tor did not finish bootstrapping within {maxWaitMs / 1000}s (reached {BootstrapPercent}% on {active}). {hint}");
        }

        private sealed class BridgeAttempt
        {
            public BridgeType Type;
            public List<string> Lines;
        }

        /// <summary>
        /// The user's lines first, then every fresh Moat group, then snowflake, obfs4, and meek.
        /// Direct is never a fallback once a bridge was requested.
        /// </summary>
        private List<BridgeAttempt> BuildChain(BridgeType type, IList<string> lines)
        {
            bool requested = type != BridgeType.NONE || HasLines(lines);
            if (type == BridgeType.NONE)
                type = InferBridgeType(lines);

            var chain = new List<BridgeAttempt>();
            var seen = new HashSet<BridgeType>();
            if (!requested || type == BridgeType.NONE && !HasLines(lines))
                return chain;

            AddAttempt(chain, seen, type, lines);
            foreach (BridgeCatalog.BridgeCandidate candidate in BridgeCatalog.ReadPlan())
                AddAttempt(chain, seen, MapTransport(candidate.Transport), candidate.Lines);
            AddAttempt(chain, seen, BridgeType.SNOWFLAKE, null);
            AddAttempt(chain, seen, BridgeType.OBFS4, null);
            AddAttempt(chain, seen, BridgeType.MEEK_AZURE, null);
            return chain;
        }

        private static void AddAttempt(List<BridgeAttempt> chain, HashSet<BridgeType> seen, BridgeType type, IList<string> lines)
        {
            if (type == BridgeType.NONE || seen.Contains(type))
                return;

            var copy = new List<string>();
            if (lines != null)
            {
                foreach (string line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        copy.Add(line.Trim());
                }
            }

            if (copy.Count == 0)
                copy = DefaultBridges.ForType(type) ?? new List<string>();
            if (copy.Count == 0)
                return;

            seen.Add(type);
            chain.Add(new BridgeAttempt { Type = type, Lines = copy });
        }

        private TorSettings PrepareSettings(TorSettings settings)
        {
            TorSettings copy = settings.Clone();
            List<string> lines = copy.GetBridgeLines();
            BridgeType type = copy.GetBridgeType();
            if (type == BridgeType.NONE && lines.Count > 0)
                type = InferBridgeType(lines);

            if (type != BridgeType.NONE && BridgeCatalog.ReadPlan().Count == 0)
                TryRefreshPlan();

            if (type != BridgeType.NONE && lines.Count == 0)
            {
                foreach (BridgeCatalog.BridgeCandidate candidate in BridgeCatalog.ReadPlan())
                {
                    if (MapTransport(candidate.Transport) != type)
                        continue;
                    lines = candidate.Lines;
                    break;
                }
            }

            // Bare "ip:port cert=…" lines (and lines prefixed with "Bridge ") used to leave
            // the type at NONE, so torrc was written without UseBridges and tor connected
            // directly while the UI still showed bridges. Such lines are obfs4-style.
            if (type == BridgeType.NONE && lines.Count > 0)
                type = BridgeType.OBFS4;

            copy.BridgeType = type;
            copy.BridgeLines = lines ?? new List<string>();
            return copy;
        }

        private static void TryRefreshPlan()
        {
            try
            {
                new MoatClient().GetCircumventionSettingsAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(Tag, "Moat refresh failed: " + ex.GetType().Name);
            }
        }

        private static bool HasLines(IList<string> lines)
        {
            if (lines == null)
                return false;
            foreach (string line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    return true;
            }
            return false;
        }

        private static BridgeType InferBridgeType(IList<string> lines)
        {
            if (lines == null)
                return BridgeType.NONE;
            foreach (string line in lines)
            {
                string trimmed = (line ?? string.Empty).Trim();
                // Users and panels often paste the torrc form "Bridge obfs4 ...".
                if (trimmed.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring("Bridge ".Length).TrimStart();

                if (trimmed.StartsWith("snowflake ", StringComparison.OrdinalIgnoreCase))
                    return BridgeType.SNOWFLAKE;
                if (trimmed.StartsWith("meek", StringComparison.OrdinalIgnoreCase))
                    return BridgeType.MEEK_AZURE;
                if (trimmed.StartsWith("webtunnel ", StringComparison.OrdinalIgnoreCase))
                    return BridgeType.WEBTUNNEL;
                if (trimmed.StartsWith("obfs4 ", StringComparison.OrdinalIgnoreCase))
                    return BridgeType.OBFS4;
            }
            return BridgeType.NONE;
        }

        private static BridgeType MapTransport(string transport)
        {
            switch ((transport ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "snowflake":
                    return BridgeType.SNOWFLAKE;
                case "meek":
                case "meek-azure":
                case "meek_lite":
                    return BridgeType.MEEK_AZURE;
                case "webtunnel":
                    return BridgeType.WEBTUNNEL;
                case "obfs4":
                    return BridgeType.OBFS4;
                default:
                    return BridgeType.NONE;
            }
        }

        private static string DescribeLink(TorControlClient control, BridgeType type, IList<string> lines)
        {
            string pretty = PrettyTransport(type);
            var shown = new List<string>();
            if (control != null)
            {
                foreach (string fingerprint in control.ReadFirstHopFingerprints())
                {
                    string tail = FingerprintTail(fingerprint);
                    if (tail.Length == 0)
                        continue;
                    string piece = "…" + tail;
                    if (shown.Contains(piece))
                        continue;
                    shown.Add(piece);
                    if (shown.Count == 3)
                        break;
                }
            }

            if (shown.Count == 0)
            {
                int count = 0;
                if (lines != null)
                {
                    foreach (string line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            count++;
                    }
                }
                return count > 1 ? pretty + " · " + count : pretty;
            }

            string label = pretty;
            foreach (string piece in shown)
                label += " · " + piece;
            return label;
        }

        private static string PrettyTransport(BridgeType type)
        {
            switch (type)
            {
                case BridgeType.SNOWFLAKE: return "Snowflake";
                case BridgeType.OBFS4: return "obfs4";
                case BridgeType.MEEK_AZURE: return "meek";
                case BridgeType.WEBTUNNEL: return "WebTunnel";
                default: return "Tor";
            }
        }

        private static string FingerprintTail(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
                return string.Empty;
            var hex = new System.Text.StringBuilder();
            foreach (char c in fingerprint)
            {
                if (Uri.IsHexDigit(c))
                    hex.Append(char.ToUpperInvariant(c));
            }
            if (hex.Length < 8)
                return string.Empty;
            return hex.ToString(hex.Length - 8, 8);
        }

        private bool SwitchTransport(int controlPort, string cookieFile, BridgeType type, IList<string> lines)
        {
            if (!TorrcBuilder.TryGetRuntimeBridge(type, lines, out string plugin, out List<string> bridges, out string error))
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
                    // RESETCONF already dropped the old bridges: without this guard tor would
                    // bootstrap directly and the caller would report a successful connection.
                    control.SetConf(out _, "DisableNetwork=1");
                    DiagnosticLog.Write(Tag, "Smart connect SETCONF failed: " + setReply);
                    return false;
                }

                // A transport change has to drop the old circuits or bootstrap stays on the stalled percent.
                control.SetConf(out _, "DisableNetwork=1");
                control.SetConf(out _, "DisableNetwork=0");
            }

            return true;
        }

        /// <summary>
        /// Validates a single bridge by booting a throwaway tor instance with only that bridge,
        /// on isolated ports, and timing how long it takes to reach 100% bootstrap.
        /// </summary>
        public BridgeCheckResult CheckBridge(BridgeType bridgeType, string bridgeLine, int timeoutMs = 150000)
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
        public BridgeCheckResult CheckBridge(BridgeType bridgeType, IList<string> bridgeLines, int timeoutMs = 150000)
        {
            if (!IsAvailable)
                return BridgeCheckResult.Fail("Tor binary not bundled.");

            int socksPort = FindFreePort(19250);
            int controlPort = FindFreePort(socksPort + 1);
            string torDataRoot = System.IO.Path.GetFullPath(Directory.TOR_DATA);
            // A shared "check" directory made two concurrent checks fight over the same
            // torrc/cookie; every check now gets its own scratch folder.
            string checkId = Guid.NewGuid().ToString("N").Substring(0, 8);
            string dataDir = System.IO.Path.Combine(torDataRoot, "check", checkId);
            string cookieFile = System.IO.Path.Combine(dataDir, "control_auth_cookie");
            string torrcPath = System.IO.Path.Combine(dataDir, "torrc.check");
            string logFile = System.IO.Path.Combine(dataDir, "tor-check.log");

            Process check = null;
            try
            {
                try { if (System.IO.Directory.Exists(dataDir)) System.IO.Directory.Delete(dataDir, true); } catch { }
                System.IO.Directory.CreateDirectory(dataDir);

                var supplied = new List<string>();
                if (bridgeLines != null)
                {
                    foreach (string line in bridgeLines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            supplied.Add(line.Trim());
                    }
                }

                if (bridgeType != BridgeType.NONE && BridgeCatalog.ReadPlan().Count == 0)
                    TryRefreshPlan();

                List<BridgeAttempt> chain = BuildChain(bridgeType, supplied);
                if (chain.Count == 0)
                    return BridgeCheckResult.Fail("No bridge lines for " + bridgeType + ". Refusing a direct connection.");

                int attempt = 0;
                bridgeType = chain[0].Type;
                List<string> lines = chain[0].Lines;
                int stallMs = 0;
                int lastPercent = -1;
                int checkPercent = 0;

                string torrc = TorrcBuilder.Build(socksPort, controlPort, dataDir, cookieFile, logFile, bridgeType, lines);
                File.WriteAllText(torrcPath, torrc);

                var startInfo = new ProcessStartInfo
                {
                    FileName = System.IO.Path.GetFullPath(Path.TOR_EXE),
                    Arguments = $"-f \"{torrcPath}\"",
                    WorkingDirectory = torDataRoot,
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
                                    BridgeCheckResult ok = BridgeCheckResult.Ok((int)stopwatch.ElapsedMilliseconds, bridgeType, lines);
                                    ok.LinkLabel = DescribeLink(control, bridgeType, lines);
                                    return ok;
                                }

                                if (percent > lastPercent)
                                {
                                    lastPercent = percent;
                                    stallMs = 0;
                                    // Local state only: a bridge check must not overwrite the
                                    // live session's BootstrapPercent/StatusSummary.
                                    checkPercent = percent;
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
                    }
                    else
                    {
                        stallMs += interval;
                    }

                    int checkStallLimit = checkPercent >= 15 ? 120000 : 30000;
                    if (stallMs >= checkStallLimit && attempt + 1 < chain.Count)
                    {
                        BridgeAttempt next = chain[attempt + 1];
                        if (SwitchTransport(controlPort, cookieFile, next.Type, next.Lines))
                        {
                            attempt++;
                            bridgeType = next.Type;
                            lines = next.Lines;
                            lastPercent = -1;
                            stallMs = 0;
                            checkPercent = 0;
                            DiagnosticLog.Write(Tag, "Bridge check switching to " + bridgeType);
                        }
                        else
                        {
                            attempt++;
                            stallMs = checkStallLimit;
                        }
                    }

                    Thread.Sleep(interval);
                    elapsed += interval;
                }

                return BridgeCheckResult.Fail($"Bridge did not connect within {timeoutMs / 1000}s ({checkPercent}% {bridgeType}).");
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

                try
                {
                    if (System.IO.Directory.Exists(dataDir))
                        System.IO.Directory.Delete(dataDir, true);
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
