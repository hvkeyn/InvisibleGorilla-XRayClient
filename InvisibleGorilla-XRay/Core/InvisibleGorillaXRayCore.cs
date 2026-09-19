using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace InvisibleGorillaXRay.Core
{
    using Models;
    using Handlers.Proxies;
    using Handlers.Tunnels;
    using Handlers.Tor;
    using Handlers.OpenFlux;
    using Values;
    using Utilities;
    using Services;
    using Services.OpenFlux;
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
        private Func<OpenFluxProfile> getOpenFluxProfile;

        private readonly TorManager torManager = new TorManager();
        private readonly OpenFluxManager openFluxManager = new OpenFluxManager();
        private readonly OpenFluxHttpBridge openFluxHttpBridge = new OpenFluxHttpBridge();
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

        public void SetupOpenFlux(Func<OpenFluxProfile> getOpenFluxProfile)
        {
            this.getOpenFluxProfile = getOpenFluxProfile;
        }

        public TorManager GetTorManager() => torManager;

        public OpenFluxManager GetOpenFluxManager() => openFluxManager;

        public Status LoadConfig()
        {
            Config config = getConfig.Invoke();

            if (config == null)
                return new Status(Code.ERROR, SubCode.NO_CONFIG, LocalizationService.GetTerm(Localization.NO_CONFIGS_FOUND));

            return LoadConfig(config.Path);
        }

        public Status LoadConfig(string path)
        {
            if (OpenFluxProfilePaths.IsMarker(path))
                return new Status(Code.SUCCESS, SubCode.SUCCESS, OpenFluxManager.DummyConfig);

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

        public void Run(string config, Action? onReady = null)
        {
            DiagnosticLog.Clear();

            if (IsOpenFluxProfile())
            {
                RunOpenFlux(onReady);
                return;
            }

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

            // 5s was too tight on a fresh install: the first launch of the freshly extracted
            // self-contained build is often slowed by on-access AV scanning of the large exe and
            // XRayCore.dll, so the local SOCKS listener can need >5s to come up. A premature
            // timeout surfaced as "The application can't tunnel the system". 15s is forgiving
            // without making a genuine startup failure feel hung.
            bool portActive = WaitForPortActive(proxyPort, maxWaitMs: 15000);

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

            try
            {
                onReady?.Invoke();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Run.OnReady", ex);
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

        private bool openFluxProxyActive;

        private bool IsOpenFluxProfile()
        {
            Config config = getConfig?.Invoke();
            return config != null && OpenFluxProfilePaths.IsMarker(config.Path);
        }

        private void RunOpenFlux(Action? onReady)
        {
            Mode mode = getMode.Invoke();
            bool useTun = mode == Mode.TUN;

            DiagnosticLog.Write("Run", useTun
                ? "OpenFlux profile selected; using TUN over local SOCKS."
                : "OpenFlux profile selected; using system Proxy.");

            try { getTunnel.Invoke()?.Disable(); } catch (Exception ex) { DiagnosticLog.WriteException("Run.OpenFlux.DisableTunnel", ex); }
            WindowsStaleTunCleanup.TryDisableStaleTunnel(getTunPort.Invoke());

            OpenFluxProfile profile = getOpenFluxProfile?.Invoke()?.Clone() ?? new OpenFluxProfile();
            openFluxManager.BeginSession();
            openFluxProxyActive = !useTun;
            WindowsProxy.ExtraBypass = useTun ? "" : OpenFluxYandexBypass;
            bool proxyEnabled = false;
            bool tunnelEnabled = false;
            int lastTunSocksPort = 0;

            try
            {
                bool first = true;
                while (true)
                {
                    profile = getOpenFluxProfile?.Invoke()?.Clone() ?? profile;
                    profile.DocUrl = OpenFluxUrl.Trim(profile.DocUrl);
                    profile.EncryptionKey = OpenFluxUrl.DeriveKey(profile.DocUrl);
                    Status start = openFluxManager.Start(profile, getLogPath.Invoke());
                    if (start.Code != Code.SUCCESS)
                    {
                        throw new InvalidOperationException(
                            LocalizationService.GetTerm(MapOpenFluxError(start.Content?.ToString())));
                    }

                    OpenFluxExitRegistry.RegisterInBackground(
                        profile.DocUrl,
                        profile.EncryptionKey);

                    int socksPort = openFluxManager.BoundSocksPort;
                    ActiveTunnelSession.SetOpenFlux(socksPort);

                    if (useTun)
                    {
                        if (!tunnelEnabled || lastTunSocksPort != socksPort)
                        {
                            if (tunnelEnabled)
                            {
                                try { DisableTunnel(); } catch (Exception ex) { DiagnosticLog.WriteException("Run.OpenFlux.Retune", ex); }
                                tunnelEnabled = false;
                            }

                            Status tunnelStatus = EnableOpenFluxTunnel(socksPort);
                            DiagnosticLog.Write("Run", $"EnableOpenFluxTunnel result: code={tunnelStatus.Code}, subCode={tunnelStatus.SubCode}");
                            if (tunnelStatus.Code == Code.ERROR)
                            {
                                openFluxManager.Stop();
                                throw new InvalidOperationException(
                                    tunnelStatus.Content?.ToString()
                                    ?? LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM));
                            }

                            tunnelEnabled = true;
                            lastTunSocksPort = socksPort;
                        }
                    }
                    else
                    {
                        openFluxHttpBridge.Start(socksPort);
                        Status proxyStatus = EnableProxy();
                        if (proxyStatus.Code == Code.ERROR)
                        {
                            openFluxManager.Stop();
                            throw new InvalidOperationException(
                                proxyStatus.Content?.ToString()
                                ?? LocalizationService.GetTerm(Localization.CANT_PROXY_SYSTEM));
                        }
                        proxyEnabled = true;
                    }

                    if (first)
                    {
                        first = false;
                        try { onReady?.Invoke(); } catch (Exception ex) { DiagnosticLog.WriteException("Run.OpenFlux.OnReady", ex); }
                    }

                    openFluxManager.WaitSession();
                    if (!openFluxManager.ConsumeRestartRequest())
                        break;
                }
            }
            finally
            {
                openFluxManager.Stop();
                openFluxHttpBridge.Stop();
                if (proxyEnabled)
                    DisableProxy();
                if (tunnelEnabled)
                    DisableTunnel();
                openFluxProxyActive = false;
                WindowsProxy.ExtraBypass = "";
                WindowsTunnel.ExtraBypassAppPath = "";
                WindowsTunnel.ExtraBypassExclusive = false;
                ActiveTunnelSession.Clear();
            }
        }

        public Status ApplyOpenFluxDocUrl(string url)
        {
            string trimmed = OpenFluxUrl.Trim(url);
            if (!OpenFluxUrl.TryValidate(trimmed, out string error))
                return new Status(Code.ERROR, SubCode.INVALID_CONFIG, error);

            string key = OpenFluxUrl.DeriveKey(trimmed);
            if (string.IsNullOrWhiteSpace(key) || key.Length < 16)
                return new Status(Code.ERROR, SubCode.INVALID_CONFIG, "key-short");

            OpenFluxProfile profile = getOpenFluxProfile?.Invoke();
            if (profile != null)
                profile.EncryptionKey = key;

            bool registered = OpenFluxExitRegistry.Register(trimmed, key, null, CancellationToken.None);
            string tag = registered ? "registered" : "register-fail";

            if (openFluxManager.SameLiveUrl(trimmed) && openFluxManager.IsRunning)
                return new Status(Code.SUCCESS, SubCode.SUCCESS, tag);

            if (openFluxManager.IsRunning)
                openFluxManager.RequestRestart();

            return new Status(Code.SUCCESS, SubCode.SUCCESS, tag);
        }

        private static string MapOpenFluxError(string detail)
        {
            return detail switch
            {
                "empty" or "scheme" or "host" => "Lang.OpenFlux.Error.BadUrl",
                "key-short" => "Lang.OpenFlux.Error.KeyShort",
                "document" => "Lang.OpenFlux.Error.Document",
                "transport" => "Lang.OpenFlux.Error.Transport",
                "listen" => "Lang.OpenFlux.Error.Listen",
                _ => "Lang.OpenFlux.Error.Generic"
            };
        }

        private const string OpenFluxYandexBypass =
            "disk.yandex.ru;volga.yandex.ru;push.yandex.ru;office-online.disk.yandex.net;passport.yandex.ru;yandex.ru;yandex.net";

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
            DiagnosticLog.Write("Stop", "StopServer returned, stopping Tor/OpenFlux (if any)...");
            torManager.Stop();
            openFluxManager.Stop();
            ActiveTunnelSession.Clear();
            WindowsProxy.ExtraBypass = "";
            DiagnosticLog.Write("Stop", "Stop sequence completed.");
            AnalyticsService.SendEvent(new StoppedEvent());
        }

        public void Cancel()
        {
            CancelProxy();
            CancelTunnel();
            torManager.Stop();
            openFluxManager.Stop();
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

            int GetProxyPort()
            {
                if (openFluxProxyActive && openFluxHttpBridge.ListenPort > 0)
                    return openFluxHttpBridge.ListenPort;
                if (openFluxProxyActive && openFluxManager.BoundSocksPort > 0)
                    return openFluxManager.BoundSocksPort;
                return getProxyPort.Invoke();
            }

            string GetProxyAddress()
            {
                if (openFluxProxyActive && openFluxHttpBridge.ListenPort > 0)
                    return Global.LOCAL_HOST;
                return (openFluxProxyActive || IsSocksProtocol())
                    ? $"socks={Global.LOCAL_HOST}"
                    : Global.LOCAL_HOST;
            }

            bool IsSocksProtocol() => getProtocol.Invoke() == Protocol.SOCKS;
        }

        private void DisableProxy()
        {
            if (!ShouldChangeSystemProxy())
                return;
            
            IProxy proxy = getProxy.Invoke();
            proxy.Disable();
        }

        private Status EnableOpenFluxTunnel(int socksPort)
        {
            string server = ResolveOpenFluxTunnelBypassHost();
            DiagnosticLog.Write("EnableTunnel", $"OpenFlux TUN bypass host='{server}', socks=127.0.0.1:{socksPort}");
            if (string.IsNullOrWhiteSpace(server))
            {
                DiagnosticLog.Write("EnableTunnel", "Failed to resolve a Yandex host for OpenFlux TUN bypass.");
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.CANT_TUNNEL,
                    content: LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM)
                );
            }

            WindowsTunnel.ExtraBypassAppPath = System.IO.Path.GetFullPath(Values.Path.OPENFLUX_EXE);
            WindowsTunnel.ExtraBypassExclusive = false;
            ITunnel tunnel = getTunnel.Invoke();
            return tunnel.Enable(
                ip: Global.LOCAL_HOST,
                port: socksPort,
                address: getTunIp.Invoke(),
                server: server,
                dns: getDns.Invoke(),
                localProxyCredentials: LocalProxyCredentials.None
            );
        }

        private static string ResolveOpenFluxTunnelBypassHost()
        {
            foreach (string host in OpenFluxYandexBypass.Split(';'))
            {
                string trimmed = host.Trim();
                if (trimmed.Length == 0)
                    continue;
                try
                {
                    IPAddress[] addresses = Dns.GetHostAddresses(trimmed);
                    IPAddress ipv4 = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
                    if (ipv4 != null)
                        return trimmed;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("EnableTunnel", $"DNS {trimmed}: {ex.Message}");
                }
            }

            return "";
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

            // Windows TUN captures all local traffic and stale WinHTTP/WinINet proxy
            // state can still hit 127.0.0.1:10801 directly. Requiring random SOCKS
            // credentials turns that into a flood of auth failures inside native Xray.
            // The listener is localhost-only, so keep Windows TUN unauthenticated.
            return LocalProxyCredentials.None;
        }

        private bool ShouldChangeSystemProxy() => getSystemProxyUsed.Invoke();
    }
}