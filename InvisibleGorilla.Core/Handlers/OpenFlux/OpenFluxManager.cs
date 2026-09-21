using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace InvisibleGorillaXRay.Handlers.OpenFlux
{
    using Models;
    using InvisibleGorillaXRay.Core;
    using InvisibleGorillaXRay.Services;
    using IoPath = System.IO.Path;
    using AppPath = InvisibleGorillaXRay.Values.Path;
    using AppDir = InvisibleGorillaXRay.Values.Directory;

    public enum OpenFluxClientStatus
    {
        Stopped,
        Connecting,
        WaitingPeer,
        Connected,
        Error
    }

    /// <summary>
    /// Sidecar for bundled openflux.exe (SOCKS5 client over Yandex Documents).
    /// Hot-swap of --url is Stop+Start of this process only, not of the Gorilla UI.
    /// </summary>
    public sealed class OpenFluxManager
    {
        private const string Tag = "OpenFlux";
        private const int MinKeyLength = 16;
        private static readonly string DummyConfigJson =
            "{\"remarks\":\"OpenFlux\",\"inbounds\":[],\"outbounds\":[]}";

        private readonly object sync = new object();
        private Process process;
        private StreamWriter logWriter;
        private volatile bool sessionStop;
        private volatile bool restartFlag;
        private int boundSocksPort;
        private string lastUrl = "";
        private string lastExitIp = "";
        private OpenFluxClientStatus status = OpenFluxClientStatus.Stopped;
        private string statusDetail = "";

        public event Action<OpenFluxClientStatus, string> StatusChanged;

        public bool IsAvailable => File.Exists(AppPath.OPENFLUX_EXE);

        public bool IsRunning
        {
            get
            {
                lock (sync)
                {
                    return process != null && !process.HasExited;
                }
            }
        }

        public int BoundSocksPort
        {
            get { lock (sync) return boundSocksPort; }
        }

        public string LastExitIp
        {
            get { lock (sync) return lastExitIp ?? ""; }
        }

        public void RememberExitIp(string ip)
        {
            string trimmed = (ip ?? "").Trim();
            if (trimmed.Length < 7 || trimmed.IndexOf('.') < 0)
                return;
            lock (sync)
                lastExitIp = trimmed;
        }

        public OpenFluxClientStatus Status
        {
            get { lock (sync) return status; }
        }

        public string StatusDetail
        {
            get { lock (sync) return statusDetail; }
        }

        public static string DummyConfig => DummyConfigJson;

        public int ResolveListenPort(int preferred)
        {
            int start = preferred > 0 ? preferred : OpenFluxProfile.DefaultSocksPort;
            if (start == 10801 || start == 10802 || start == 10803)
                start = OpenFluxProfile.DefaultSocksPort;

            for (int port = start; port < start + 40; port++)
            {
                if (port == 10801 || port == 10802 || port == 10803)
                    continue;
                if (!IsPortInUse(port))
                    return port;
            }

            return start;
        }

        public Status Start(OpenFluxProfile profile, string logDirectory, int listenWaitMs = 15000)
        {
            if (profile == null)
                return Fail("OpenFlux profile is missing.");

            if (!IsAvailable)
                return Fail($"OpenFlux binary not found at {AppPath.OPENFLUX_EXE}.");

            string url = OpenFluxUrl.Trim(profile.DocUrl);
            if (!OpenFluxUrl.TryValidate(url, out string urlError))
                return Fail(urlError);

            string key = profile.EncryptionKey ?? "";
            string keyFile = PrepareKeyFile(key);
            if (string.IsNullOrEmpty(keyFile))
                return Fail("key-short");

            KillOrphanSidecars();
            int port = ResolveListenPort(profile.GetSocksPort());
            string transport = OpenFluxUrl.ResolveTransport(url, profile.Transport);
            string codec = profile.GetCodec();
            if (!string.Equals(codec, "batched", StringComparison.OrdinalIgnoreCase))
                codec = OpenFluxProfile.DefaultCodec;

            StopProcess(waitExitMs: 3000);
            WaitUntilPortFree(port, 2000);

            string logPath = IoPath.Combine(
                string.IsNullOrWhiteSpace(logDirectory) ? AppDir.LOGS : logDirectory,
                "openflux.log");

            try
            {
                System.IO.Directory.CreateDirectory(IoPath.GetDirectoryName(logPath) ?? AppDir.LOGS);
                System.IO.Directory.CreateDirectory(AppDir.OPENFLUX);

                SetStatus(OpenFluxClientStatus.Connecting, "");
                if (!StartProcess(url, transport, codec, port, keyFile, logPath))
                    return Fail("launch");

                lock (sync)
                {
                    boundSocksPort = port;
                    lastUrl = url;
                }

                if (!WaitForListen(port, listenWaitMs))
                {
                    int exitCode = -1;
                    bool exited = IsProcessExited(out exitCode);
                    string detail = Status == OpenFluxClientStatus.Error
                        ? statusDetail
                        : (exited ? "launch" : "listen");
                    if (exited)
                        DiagnosticLog.Write(Tag, $"OpenFlux process exited before SOCKS listen, code={exitCode}");
                    StopProcess(waitExitMs: 3000);
                    return Fail(detail);
                }

                if (Status != OpenFluxClientStatus.Error)
                    SetStatus(OpenFluxClientStatus.Connected, "listen");

                DiagnosticLog.Write(Tag, $"SOCKS5 listening on 127.0.0.1:{port} transport={transport}");
                return new Status(Code.SUCCESS, SubCode.SUCCESS, port);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException(Tag, ex);
                StopProcess(waitExitMs: 2000);
                return Fail(ex.Message);
            }
        }

        public void BeginSession()
        {
            sessionStop = false;
            restartFlag = false;
        }

        public void WaitSession()
        {
            // Yandex document login on the sidecar dies after about a day while the
            // process still looks healthy. Recycle it so the next start authorizes again.
            DateTime recycleAt = DateTime.UtcNow.AddHours(3);
            while (!sessionStop)
            {
                if (restartFlag)
                    return;

                if (DateTime.UtcNow >= recycleAt)
                {
                    DiagnosticLog.Write(Tag, "OpenFlux session reached 3h; restarting to refresh the Yandex login");
                    restartFlag = true;
                    StopProcess(waitExitMs: 3000);
                    return;
                }

                lock (sync)
                {
                    if (process == null || process.HasExited)
                        return;
                }

                Thread.Sleep(200);
            }
        }

        public bool ConsumeRestartRequest()
        {
            if (sessionStop)
                return false;
            if (!restartFlag)
                return false;
            restartFlag = false;
            return true;
        }

        public bool RequestRestart()
        {
            restartFlag = true;
            StopProcess(waitExitMs: 3000);
            return true;
        }

        public void Stop()
        {
            sessionStop = true;
            restartFlag = false;
            StopProcess(waitExitMs: 3000);
            lock (sync)
            {
                boundSocksPort = 0;
                lastUrl = "";
                lastExitIp = "";
            }
            SetStatus(OpenFluxClientStatus.Stopped, "");
        }

        public bool SameLiveUrl(string url)
        {
            lock (sync)
            {
                return IsRunning
                    && string.Equals(lastUrl, OpenFluxUrl.Trim(url), StringComparison.Ordinal);
            }
        }

        private bool StartProcess(string url, string transport, string codec, int port, string keyFile, string logPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = AppPath.OPENFLUX_EXE,
                WorkingDirectory = AppDir.OPENFLUX,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("--role=client");
            startInfo.ArgumentList.Add("--inbound=socks5");
            startInfo.ArgumentList.Add("--transport=" + transport);
            startInfo.ArgumentList.Add("--codec=" + codec);
            startInfo.ArgumentList.Add("--url=" + url);
            startInfo.ArgumentList.Add("--socks5=127.0.0.1:" + port);
            if (!string.IsNullOrEmpty(keyFile))
                startInfo.ArgumentList.Add("--encryption-key-file=" + keyFile);

            startInfo.Environment["NO_PROXY"] = "*";
            startInfo.Environment["no_proxy"] = "*";
            startInfo.Environment["HTTP_PROXY"] = "";
            startInfo.Environment["HTTPS_PROXY"] = "";
            startInfo.Environment["ALL_PROXY"] = "";
            startInfo.Environment["http_proxy"] = "";
            startInfo.Environment["https_proxy"] = "";
            startInfo.Environment["OPENFLUX_BATCH_BYTES"] = "32768";
            startInfo.Environment["OPENFLUX_BATCH_COUNT"] = "96";
            startInfo.Environment["OPENFLUX_BATCH_LINGER_MS"] = "4";

            var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            started.OutputDataReceived += (_, e) => HandleProcessLine(e.Data, logPath);
            started.ErrorDataReceived += (_, e) => HandleProcessLine(e.Data, logPath);

            if (!started.Start())
                return false;

            started.BeginOutputReadLine();
            started.BeginErrorReadLine();

            lock (sync)
            {
                process = started;
            }

            DiagnosticLog.Write(Tag, $"Started pid={started.Id} exe={AppPath.OPENFLUX_EXE} socks=127.0.0.1:{port} transport={transport}");
            return true;
        }

        private void HandleProcessLine(string line, string logPath)
        {
            if (string.IsNullOrEmpty(line))
                return;

            AppendLog(logPath, line);

            if (IsKeepAliveNoise(line))
                return;

            DiagnosticLog.Write(Tag + ".out", Sanitize(line));

            if (line.IndexOf("Running as CLIENT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (Status != OpenFluxClientStatus.Error)
                    SetStatus(OpenFluxClientStatus.WaitingPeer, "");
                return;
            }

            if (line.IndexOf("looks like a login page", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("config not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SetStatus(OpenFluxClientStatus.Error, "document");
                return;
            }

            if (line.IndexOf("officeActionData.balancer_url missing", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("balancer_url missing", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SetStatus(OpenFluxClientStatus.Error, "transport");
            }
        }

        private static bool IsKeepAliveNoise(string line)
        {
            return line.IndexOf("fail 0", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("req/s", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Sanitize(string line)
        {
            if (string.IsNullOrEmpty(line))
                return "";
            if (line.IndexOf("encryption-key", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[redacted]";
            return line;
        }

        private void AppendLog(string logPath, string line)
        {
            try
            {
                lock (sync)
                {
                    logWriter ??= new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                    logWriter.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(Tag, $"log write failed: {ex.Message}");
            }
        }

        private void StopProcess(int waitExitMs)
        {
            Process running;
            lock (sync)
            {
                running = process;
                process = null;
            }

            if (running == null)
            {
                CloseLog();
                return;
            }

            try
            {
                if (!running.HasExited)
                {
                    int pid = 0;
                    try { pid = running.Id; } catch { }
                    // entireProcessTree uses kill(-pid) on Android and shares the app
                    // process group — that freezes or kills Gorilla itself on STOP.
                    try
                    {
                        if (IsAndroidRuntime())
                            running.Kill();
                        else
                            running.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        try { running.Kill(); } catch { }
                    }
                    if (pid > 0)
                        TryNativeKill(pid);
                    running.WaitForExit(IsAndroidRuntime() ? Math.Min(waitExitMs, 1500) : waitExitMs);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(Tag, $"Kill: {ex.Message}");
            }

            try { running.Dispose(); } catch { }
            CloseLog();
        }

        private void CloseLog()
        {
            lock (sync)
            {
                try { logWriter?.Flush(); } catch { }
                try { logWriter?.Dispose(); } catch { }
                logWriter = null;
            }
        }

        private void SetStatus(OpenFluxClientStatus next, string detail)
        {
            lock (sync)
            {
                status = next;
                statusDetail = detail ?? "";
            }
            try { StatusChanged?.Invoke(next, detail ?? ""); } catch { }
        }

        private Status Fail(string detail)
        {
            SetStatus(OpenFluxClientStatus.Error, detail);
            return new Status(Code.ERROR, SubCode.CANT_CONNECT, detail);
        }

        private string PrepareKeyFile(string keyOrPath)
        {
            string trimmed = (keyOrPath ?? "").Trim();
            if (trimmed.Length == 0)
                return "";

            try
            {
                if ((trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
                    && File.Exists(trimmed))
                {
                    return IoPath.GetFullPath(trimmed);
                }
            }
            catch { }

            if (trimmed.Length < MinKeyLength)
                return "";

            System.IO.Directory.CreateDirectory(AppDir.OPENFLUX);
            string dest = AppPath.OPENFLUX_USER_KEY;
            File.WriteAllText(dest, trimmed, new UTF8Encoding(false));
            return IoPath.GetFullPath(dest);
        }

        private bool ProbePeer(int socksPort, int timeoutMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(Math.Max(8000, timeoutMs));
            string lastError = "";
            while (DateTime.UtcNow < until)
            {
                try
                {
                    string body = Socks5Http.GetHttpsBody("127.0.0.1", socksPort, "api.ipify.org", "/", 6000);
                    string ip = (body ?? "").Trim();
                    if (ip.Length >= 7 && ip.IndexOf('.') > 0)
                    {
                        lock (sync)
                            lastExitIp = ip;
                        DiagnosticLog.Write(Tag, $"peer probe ok ip={ip}");
                        return true;
                    }
                    lastError = "empty body";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    DiagnosticLog.Write(Tag, $"peer probe retry: {ex.Message}");
                }

                Thread.Sleep(400);
            }

            DiagnosticLog.Write(Tag, $"peer probe failed: {lastError}");
            return false;
        }

        private static bool IsAndroidRuntime()
        {
            try
            {
                if (OperatingSystem.IsAndroid())
                    return true;
            }
            catch
            {
            }

            try
            {
                return File.Exists("/system/build.prop");
            }
            catch
            {
                return false;
            }
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
        private static extern int NativeKillSignal(int pid, int signal);

        private static void TryNativeKill(int pid)
        {
            if (pid <= 1 || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;
            try { NativeKillSignal(pid, 9); } catch { }
        }

        private static void KillOrphanSidecars()
        {
            if (IsAndroidRuntime())
            {
                KillAndroidOpenFluxOrphans();
                return;
            }

            try
            {
                foreach (Process candidate in Process.GetProcesses())
                {
                    string name = "";
                    try { name = candidate.ProcessName ?? ""; } catch { continue; }
                    if (name.IndexOf("openflux", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    try
                    {
                        if (!candidate.HasExited)
                        {
                            try { candidate.Kill(); } catch { }
                            TryNativeKill(candidate.Id);
                        }
                    }
                    catch { }
                    try { candidate.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(Tag, $"orphan kill: {ex.Message}");
            }
        }

        private static void KillAndroidOpenFluxOrphans()
        {
            int self = Environment.ProcessId;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(250);
            int checkedPids = 0;
            try
            {
                foreach (string dir in System.IO.Directory.GetDirectories("/proc"))
                {
                    if (DateTime.UtcNow > deadline || checkedPids > 80)
                        break;
                    string baseName = IoPath.GetFileName(dir);
                    if (!int.TryParse(baseName, out int pid) || pid <= 1 || pid == self)
                        continue;
                    checkedPids++;
                    string cmd = "";
                    try
                    {
                        cmd = File.ReadAllText(IoPath.Combine(dir, "cmdline")).Replace('\0', ' ');
                    }
                    catch
                    {
                        continue;
                    }

                    if (cmd.IndexOf("openflux", StringComparison.OrdinalIgnoreCase) < 0
                        && cmd.IndexOf("libopenflux", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    DiagnosticLog.Write(Tag, $"orphan kill android pid={pid}");
                    TryNativeKill(pid);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(Tag, $"orphan kill android: {ex.Message}");
            }
        }

        private bool WaitForListen(int port, int maxWaitMs)
        {
            int elapsed = 0;
            const int interval = 100;
            while (elapsed < maxWaitMs)
            {
                if (IsProcessExited(out _))
                    return false;
                if (IsPortInUse(port))
                    return true;
                Thread.Sleep(interval);
                elapsed += interval;
            }
            return false;
        }

        private bool IsProcessExited(out int exitCode)
        {
            exitCode = -1;
            lock (sync)
            {
                if (process == null)
                    return true;
                try
                {
                    if (!process.HasExited)
                        return false;
                    exitCode = process.ExitCode;
                    return true;
                }
                catch
                {
                    return true;
                }
            }
        }

        private static bool WaitForPort(int port, int maxWaitMs)
        {
            int elapsed = 0;
            const int interval = 100;
            while (elapsed < maxWaitMs)
            {
                if (IsPortInUse(port))
                    return true;
                Thread.Sleep(interval);
                elapsed += interval;
            }
            return false;
        }

        private static void WaitUntilPortFree(int port, int maxWaitMs)
        {
            int elapsed = 0;
            const int interval = 50;
            while (elapsed < maxWaitMs && IsPortInUse(port))
            {
                Thread.Sleep(interval);
                elapsed += interval;
            }
        }

        private static bool IsPortInUse(int port)
        {
            try
            {
                foreach (IPEndPoint endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    if (endpoint != null && endpoint.Port == port)
                        return true;
                }
            }
            catch
            {
            }

            try
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
