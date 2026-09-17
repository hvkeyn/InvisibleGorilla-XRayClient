using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace InvisibleGorillaXRay.Handlers.OpenFlux
{
    using Models;
    using InvisibleGorillaXRay.Core;
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

                if (!WaitForPort(port, listenWaitMs))
                {
                    string detail = Status == OpenFluxClientStatus.Error ? statusDetail : "listen";
                    StopProcess(waitExitMs: 3000);
                    return Fail(detail);
                }

                if (Status != OpenFluxClientStatus.Error)
                    SetStatus(OpenFluxClientStatus.WaitingPeer, "");

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
            while (!sessionStop)
            {
                if (restartFlag)
                    return;

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
                RedirectStandardInput = true,
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

            DiagnosticLog.Write(Tag, $"Started pid={started.Id} socks=127.0.0.1:{port} transport={transport}");
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
                || line.IndexOf("BATCH decode error", StringComparison.OrdinalIgnoreCase) >= 0
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
                    running.Kill(entireProcessTree: true);
                    running.WaitForExit(waitExitMs);
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
                using TcpClient client = new TcpClient();
                IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(80));
                if (!ok)
                    return false;
                client.EndConnect(ar);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
