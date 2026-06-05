using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;

namespace InvisibleGorillaXRay.Core
{
    using Models;
    using Handlers.Proxies;
    using Handlers.Tunnels;
    using Handlers.Tor;
    using Values;
    using Utilities;
    using Services;
    using Services.Analytics.Core;

    public class InvisibleGorillaXRayCore
    {
        private Func<Config> getConfig;
        private Func<Mode> getMode;
        private Func<Protocol> getProtocol;
        private Func<LogLevel> getLogLevel;
        private Func<string> getLogPath;
        private Func<int> getProxyPort;
        private Func<int> getTunPort;
        private Func<int> getTestPort;
        private Func<bool> getSystemProxyUsed;
        private Func<bool> getUdpEnabled;
        private Func<string> getTunIp;
        private Func<string> getDns;
        private Func<IProxy> getProxy;
        private Func<ITunnel> getTunnel;
        private Action<string> onFailLoadingConfig;
        private Func<TorSettings> getTorSettings;

        private readonly TorManager torManager = new TorManager();
        private string currentRuntimeConfig;

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();
        private AnalyticsService AnalyticsService => ServiceLocator.Get<AnalyticsService>();

        public void Setup(
            Func<Config> getConfig, 
            Func<Mode> getMode,
            Func<Protocol> getProtocol,
            Func<LogLevel> getLogLevel,
            Func<string> getLogPath,
            Func<int> getProxyPort,
            Func<int> getTunPort,
            Func<int> getTestPort,
            Func<bool> getSystemProxyUsed,
            Func<bool> getUdpEnabled,
            Func<string> getTunIp,
            Func<string> getDns,
            Func<IProxy> getProxy, 
            Func<ITunnel> getTunnel,
            Action<string> onFailLoadingConfig)
        {
            this.getConfig = getConfig;
            this.getMode = getMode;
            this.getProtocol = getProtocol;
            this.getLogLevel = getLogLevel;
            this.getLogPath = getLogPath;
            this.getProxyPort = getProxyPort;
            this.getTunPort = getTunPort;
            this.getTestPort = getTestPort;
            this.getSystemProxyUsed = getSystemProxyUsed;
            this.getUdpEnabled = getUdpEnabled;
            this.getTunIp = getTunIp;
            this.getDns = getDns;
            this.getProxy = getProxy;
            this.getTunnel = getTunnel;
            this.onFailLoadingConfig = onFailLoadingConfig;
        }

        /// <summary>
        /// Optional Tor integration. When wired and enabled in settings, the connection is
        /// routed through the bundled tor daemon (Tor-only or Xray-over-Tor chaining).
        /// </summary>
        public void SetupTor(Func<TorSettings> getTorSettings)
        {
            this.getTorSettings = getTorSettings;
        }

        public TorManager GetTorManager() => torManager;

        public Status LoadConfig()
        {
            Config config = getConfig.Invoke();

            if (config == null)
                return new Status(Code.ERROR, SubCode.NO_CONFIG, LocalizationService.GetTerm(Localization.NO_CONFIGS_FOUND));

            return LoadConfig(config.Path);
        }

        public Status LoadConfig(string path)
        {
            if (!XRayCoreWrapper.IsFileExists(path))
            {
                onFailLoadingConfig.Invoke(path);
                return new Status(Code.ERROR, SubCode.NO_CONFIG, LocalizationService.GetTerm(Localization.NO_CONFIGS_FOUND));
            }

            string format = XRayCoreWrapper.GetConfigFormat(path);
            string file = XRayCoreWrapper.LoadConfig(format, path);

            if (!JsonUtility.IsJsonValid(file))
                return new Status(Code.ERROR, SubCode.INVALID_CONFIG, LocalizationService.GetTerm(Localization.INVALID_CONFIG));

            return new Status(Code.SUCCESS, SubCode.SUCCESS, file);
        }

        public Status EnableMode()
        {
            Mode mode = getMode.Invoke();

            if (mode == Mode.PROXY || mode == Mode.TUN)
            {
                // For PROXY and TUN modes, defer system activation until xray-core is listening.
                // This prevents proxy clients or tunnel bridges from hitting a dead port.
                return new Status(
                    code: Code.SUCCESS,
                    subCode: SubCode.SUCCESS,
                    content: null
                );
            }
            else
            {
                return EnableTunnel(LocalProxyCredentials.None);
            }
        }

        public void DisableMode()
        {
            DisableProxy();
            DisableTunnel();
        }

        public void Run(string config)
        {
            DiagnosticLog.Clear();
            Mode mode = getMode.Invoke();
            int proxyPort = getProxyPort.Invoke();
            int tunnelServicePort = getTunPort.Invoke();
            LogLevel logLevel = getLogLevel.Invoke();

            // Tor sits in front of xray-core as a local SOCKS daemon. Start it (if enabled)
            // and rewrite the runtime config so xray's egress (or chain entry) goes through Tor.
            TorSettings torSettings = getTorSettings?.Invoke();
            bool torEnabled = torSettings != null && torSettings.GetEnabled();
            if (torEnabled)
            {
                DiagnosticLog.Write("Run", $"Tor enabled (mode={torSettings.GetMode()}). Starting tor daemon...");
                Status torStatus = torManager.Start(torSettings);
                if (torStatus.Code != Code.SUCCESS)
                {
                    DiagnosticLog.Write("Run", $"Tor failed to start: {torStatus.Content}");
                    throw new InvalidOperationException(torStatus.Content?.ToString() ?? "Tor failed to start.");
                }

                config = BuildTorRuntimeConfig(torSettings);
            }

            currentRuntimeConfig = config;

            string configName = getConfig.Invoke()?.Name ?? (torEnabled ? "Tor" : "config");
            string logPath = System.IO.Path.GetFullPath($"{getLogPath.Invoke()}/{configName}");
            bool isSocks = getProtocol.Invoke() == Protocol.SOCKS || mode == Mode.TUN;
            bool isUdpEnabled = getUdpEnabled.Invoke();
            bool systemProxy = getSystemProxyUsed.Invoke();
            LocalProxyCredentials localProxyCredentials = CreateLocalProxyCredentials(mode, isSocks);
            ActiveTunnelSession.Set(mode, localProxyCredentials);

            // Xray always listens on the local proxy port; the TUN port is reserved for the control service.
            DiagnosticLog.Write("Run", $"mode={mode}, proxyPort={proxyPort}, tunnelServicePort={tunnelServicePort}, logLevel={logLevel}, isSocks={isSocks}, isUdpEnabled={isUdpEnabled}, systemProxy={systemProxy}");
            DiagnosticLog.Write("Run", $"logPath={logPath}");
            DiagnosticLog.Write("Run", $"localSocksAuth={(localProxyCredentials.HasValue ? "enabled" : "disabled")}");
            DiagnosticLog.Write("Run", $"config length={config?.Length ?? 0}, first 200 chars: {(config?.Length > 200 ? config.Substring(0, 200) : config)}");

            SendServerStartEvent();

            bool serverStarted = false;
            Exception serverException = null;

            Thread serverThread = new Thread(() =>
            {
                try
                {
                    DiagnosticLog.Write("ServerThread", "Calling XRayCoreWrapper.StartServer...");
                    XRayCoreWrapper.StartServer(
                        config,
                        proxyPort,
                        logLevel,
                        logPath,
                        isSocks,
                        isUdpEnabled,
                        localProxyCredentials);
                    DiagnosticLog.Write("ServerThread", "StartServer returned (server stopped)");
                }
                catch (Exception ex)
                {
                    serverException = ex;
                    DiagnosticLog.WriteException("ServerThread", ex);
                }
            });
            serverThread.IsBackground = true;
            serverThread.Start();

            DiagnosticLog.Write("Run", $"Server thread started (ID={serverThread.ManagedThreadId}), waiting for port {proxyPort}...");

            bool portActive = WaitForPortActive(proxyPort, maxWaitMs: 5000);

            DiagnosticLog.Write("Run", $"WaitForPortActive result: portActive={portActive}");

            if (serverException != null)
            {
                DiagnosticLog.Write("Run", $"Server thread threw exception, aborting: {serverException.Message}");
                if (torEnabled) torManager.Stop();
                return;
            }

            if (!serverThread.IsAlive)
            {
                DiagnosticLog.Write("Run", "WARNING: Server thread is no longer alive! xray-core likely failed to start.");
                DiagnosticLog.Write("Run", "Skipping proxy enable since server is not running.");
                if (torEnabled) torManager.Stop();
                return;
            }

            if (!portActive)
            {
                DiagnosticLog.Write("Run", "Local proxy listener did not become active in time, stopping server.");
                XRayCoreWrapper.StopServer();
                serverThread.Join(2000);
                if (torEnabled) torManager.Stop();
                throw new InvalidOperationException(mode == Mode.TUN
                    ? LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM)
                    : LocalizationService.GetTerm(Localization.CANT_PROXY_SYSTEM));
            }

            if (mode == Mode.PROXY)
            {
                DiagnosticLog.Write("Run", "Enabling proxy...");
                Status proxyStatus = EnableProxy();
                DiagnosticLog.Write("Run", $"EnableProxy result: code={proxyStatus.Code}, subCode={proxyStatus.SubCode}");
            }
            else
            {
                DiagnosticLog.Write("Run", "Enabling tunnel...");
                Status tunnelStatus = EnableTunnel(localProxyCredentials);
                DiagnosticLog.Write("Run", $"EnableTunnel result: code={tunnelStatus.Code}, subCode={tunnelStatus.SubCode}");

                if (tunnelStatus.Code == Code.ERROR)
                {
                    XRayCoreWrapper.StopServer();
                    serverThread.Join(2000);
                    if (torEnabled) torManager.Stop();
                    throw new InvalidOperationException(
                        tunnelStatus.Content?.ToString()
                        ?? LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM));
                }
            }

            DiagnosticLog.Write("Run", "Waiting for server thread to complete (Join)...");
            serverThread.Join();
            DiagnosticLog.Write("Run", "Server thread completed.");

            if (mode == Mode.PROXY)
            {
                DiagnosticLog.Write("Run", "Disabling proxy...");
                DisableProxy();
                DiagnosticLog.Write("Run", "Proxy disabled.");
            }
            else
            {
                DiagnosticLog.Write("Run", "Disabling tunnel...");
                DisableTunnel();
                DiagnosticLog.Write("Run", "Tunnel disabled.");
            }

            if (torEnabled)
            {
                DiagnosticLog.Write("Run", "Stopping tor daemon...");
                torManager.Stop();
            }
            ActiveTunnelSession.Clear();
            currentRuntimeConfig = null;

            void SendServerStartEvent()
            {
                if (mode == Mode.PROXY)
                    AnalyticsService.SendEvent(new ProxyStartedEvent());
                else
                    AnalyticsService.SendEvent(new TunStartedEvent());
            }
        }

        /// <summary>
        /// Builds the runtime xray config for a Tor session as user-facing Xray JSON, then runs
        /// it through the native loader so StartServer receives the same marshalled core.Config
        /// form it expects (the JSON the wrapper produces from core.LoadConfig).
        /// </summary>
        private string BuildTorRuntimeConfig(TorSettings torSettings)
        {
            int torSocksPort = torSettings.GetSocksPort();
            string userJson;

            if (torSettings.GetMode() == TorMode.ONLY_TOR)
            {
                userJson = TorConfigBuilder.BuildTorOnlyConfig(torSocksPort);
            }
            else
            {
                string original = null;
                Config active = getConfig.Invoke();
                if (active != null && System.IO.File.Exists(active.Path))
                {
                    try { original = System.IO.File.ReadAllText(active.Path); } catch { }
                }

                userJson = string.IsNullOrWhiteSpace(original)
                    ? TorConfigBuilder.BuildTorOnlyConfig(torSocksPort)
                    : TorConfigBuilder.WrapConfigOverTor(original, torSocksPort);
            }

            System.IO.Directory.CreateDirectory(Values.Directory.TOR_DATA);
            string tempPath = System.IO.Path.Combine(Values.Directory.TOR_DATA, "runtime.json");
            System.IO.File.WriteAllText(tempPath, userJson);

            string format = XRayCoreWrapper.GetConfigFormat(tempPath);
            string loaded = XRayCoreWrapper.LoadConfig(format, tempPath);
            if (!JsonUtility.IsJsonValid(loaded))
                throw new InvalidOperationException("Failed to build the Tor runtime configuration.");

            return loaded;
        }

        private static bool WaitForPortActive(int port, int maxWaitMs)
        {
            int elapsed = 0;
            const int interval = 100;

            while (elapsed < maxWaitMs)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect("127.0.0.1", port);
                        DiagnosticLog.Write("WaitForPort", $"Port {port} is active after {elapsed}ms");
                        return true;
                    }
                }
                catch
                {
                    Thread.Sleep(interval);
                    elapsed += interval;
                }
            }

            DiagnosticLog.Write("WaitForPort", $"TIMEOUT: Port {port} not active after {maxWaitMs}ms");
            return false;
        }

        public void Stop()
        {
            DiagnosticLog.Write("Stop", "Stop requested: calling XRayCoreWrapper.StopServer()...");
            XRayCoreWrapper.StopServer();
            DiagnosticLog.Write("Stop", "StopServer returned, stopping Tor (if any)...");
            torManager.Stop();
            ActiveTunnelSession.Clear();
            DiagnosticLog.Write("Stop", "Stop sequence completed.");
            AnalyticsService.SendEvent(new StoppedEvent());
        }

        public void Cancel()
        {
            CancelProxy();
            CancelTunnel();
            torManager.Stop();
        }

        public int Test(string config)
        {
            return XRayCoreWrapper.TestConnection(config, getTestPort.Invoke());
        }

        public string GetVersion()
        {
            return XRayCoreWrapper.GetVersion();
        }

        private Status EnableProxy()
        {
            if (!ShouldChangeSystemProxy())
                return new Status(
                    code: Code.SUCCESS,
                    subCode: SubCode.SUCCESS,
                    content: null
                );

            IProxy proxy = getProxy.Invoke();

            return proxy.Enable(
                address: GetProxyAddress(),
                port: GetProxyPort()
            );

            int GetProxyPort() => getProxyPort.Invoke();

            string GetProxyAddress() => IsSocksProtocol() ? $"socks={Global.LOCAL_HOST}" : Global.LOCAL_HOST;

            bool IsSocksProtocol() => getProtocol.Invoke() == Protocol.SOCKS;
        }

        private void DisableProxy()
        {
            if (!ShouldChangeSystemProxy())
                return;
            
            IProxy proxy = getProxy.Invoke();
            proxy.Disable();
        }

        private Status EnableTunnel(LocalProxyCredentials localProxyCredentials)
        {
            Status configStatus = LoadConfigFile();
            if (configStatus.Code == Code.ERROR)
                return configStatus;

            string server = ResolveTunnelServerAddress(configStatus.Content?.ToString());
            DiagnosticLog.Write("EnableTunnel", $"Resolved bypass server address='{server}'");
            if (string.IsNullOrWhiteSpace(server))
            {
                DiagnosticLog.Write("EnableTunnel", "Failed to resolve outbound server address for TUN bypass route.");
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.CANT_TUNNEL,
                    content: LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM)
                );
            }

            int proxyPort = getProxyPort.Invoke();
            string address = getTunIp.Invoke();
            string dns = getDns.Invoke();
            
            ITunnel tunnel = getTunnel.Invoke();

            return tunnel.Enable(
                ip: Global.LOCAL_HOST,
                port: proxyPort,
                address: address,
                server: server,
                dns: dns,
                localProxyCredentials: localProxyCredentials
            );

            Status LoadConfigFile()
            {
                // When Tor rewrote the runtime config (Tor-only has no file on disk), use it
                // directly so server-address extraction / bypass routing still works.
                if (!string.IsNullOrEmpty(currentRuntimeConfig))
                    return new Status(Code.SUCCESS, SubCode.SUCCESS, currentRuntimeConfig.ToLower());

                Config config = getConfig.Invoke();

                if (config == null)
                    return new Status(Code.ERROR, SubCode.NO_CONFIG, LocalizationService.GetTerm(Localization.NO_CONFIGS_FOUND));
                
                return new Status(Code.SUCCESS, SubCode.SUCCESS, System.IO.File.ReadAllText(config.Path).ToLower());
            }
        }

        private string ResolveTunnelServerAddress(string runtimeConfig)
        {
            string server = JsonUtility.Find(
                key: "address",
                parent: "outbounds",
                jsonString: runtimeConfig
            );
            if (!string.IsNullOrWhiteSpace(server))
                return server;

            Config config = getConfig.Invoke();
            if (config == null || !System.IO.File.Exists(config.Path))
                return server;

            try
            {
                string userConfig = System.IO.File.ReadAllText(config.Path);
                server = JsonUtility.Find(
                    key: "address",
                    parent: "outbounds",
                    jsonString: userConfig
                );
                if (!string.IsNullOrWhiteSpace(server))
                    DiagnosticLog.Write("EnableTunnel", "Resolved bypass server from original config file fallback.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("EnableTunnel.ResolveTunnelServerAddress", ex);
            }

            return server;
        }

        private void DisableTunnel()
        {
            ITunnel tunnel = getTunnel.Invoke();
            tunnel.Disable();
        }

        private void CancelProxy()
        {
            IProxy proxy = getProxy.Invoke();
            proxy.Cancel();
        }

        private void CancelTunnel()
        {
            ITunnel tunnel = getTunnel.Invoke();
            tunnel.Cancel();
        }

        private static LocalProxyCredentials CreateLocalProxyCredentials(Mode mode, bool isSocks)
        {
            if (mode != Mode.TUN || !isSocks)
                return LocalProxyCredentials.None;

            return LocalProxyCredentials.CreateSessionScoped();
        }

        private bool ShouldChangeSystemProxy() => getSystemProxyUsed.Invoke();
    }
}