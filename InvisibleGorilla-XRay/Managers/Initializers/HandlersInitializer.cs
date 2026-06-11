using System;
using System.Windows;

namespace InvisibleGorillaXRay.Managers.Initializers
{
    using Models;
    using Core;
    using Managers;
    using Handlers;
    using Factories;
    using Services;
    using Services.Goida;
    using Values;

    public class HandlersInitializer
    {
        public HandlersManager HandlersManager { get; private set; }

        public void Register()
        {
            HandlersManager = new HandlersManager();

            HandlersManager.AddHandler(new SettingsHandler());
            HandlersManager.AddHandler(new TemplateHandler());
            HandlersManager.AddHandler(new ProcessHandler());
            HandlersManager.AddHandler(new ConfigHandler());
            HandlersManager.AddHandler(new ProxyHandler());
            HandlersManager.AddHandler(new TunnelHandler());
            HandlersManager.AddHandler(new NotifyHandler());
            HandlersManager.AddHandler(new VersionHandler());
            HandlersManager.AddHandler(new UpdateHandler());
            HandlersManager.AddHandler(new BroadcastHandler());
            HandlersManager.AddHandler(new DeepLinkHandler());
            HandlersManager.AddHandler(new LinkHandler());
            HandlersManager.AddHandler(new LocalizationHandler());
            HandlersManager.AddHandler(new GoidaProfileHandler());
        }

        public void Setup(
            InvisibleGorillaXRayCore core, 
            HandlersManager handlersManager, 
            WindowFactory windowFactory
        )
        {
            SetupProcessHandler();
            SetupTunnelHandler();
            SetupConfigHandler();
            SetupUpdateHandler();
            SetupNotifyHandler();
            SetupDeepLinkHandler();
            SetupLocalizationHandler();
            SetupGoidaProfileHandler();

            handlersManager.GetHandler<GoidaProfileHandler>().StartBackground();

            void SetupProcessHandler()
            {
                SettingsHandler settingsHandler = handlersManager.GetHandler<SettingsHandler>();
                handlersManager.GetHandler<ProcessHandler>().Setup(
                    getTunnelPort: settingsHandler.UserSettings.GetTunPort
                );
            }

            void SetupTunnelHandler()
            {
                ProcessHandler processHandler = handlersManager.GetHandler<ProcessHandler>();

                handlersManager.GetHandler<TunnelHandler>().Setup(
                    onStartTunnelingService: processHandler.TunnelProcess.Start,
                    isServiceRunning: processHandler.TunnelProcess.IsProcessRunning,
                    isServicePortActive: processHandler.TunnelProcess.IsProcessPortActive,
                    connectTunnelingService: processHandler.TunnelProcess.Connect,
                    executeCommand: processHandler.TunnelProcess.Execute
                );
            }

            void SetupConfigHandler()
            {
                SettingsHandler settingsHandler = handlersManager.GetHandler<SettingsHandler>();
                GoidaProfileHandler goidaHandler = handlersManager.GetHandler<GoidaProfileHandler>();

                handlersManager.GetHandler<ConfigHandler>().Setup(
                    getCurrentConfigPath: settingsHandler.UserSettings.GetCurrentConfigPath,
                    getGoidaListConfig: BuildGoidaListConfig,
                    getGoidaRuntimeConfig: BuildGoidaRuntimeConfig);

                Config? BuildGoidaListConfig()
                {
                    GoidaProfileSettings settings = settingsHandler.UserSettings.GetGoidaSettings();
                    if (!settings.ShouldShowInServerList())
                        return null;

                    GoidaNode? activeNode = goidaHandler.Manager.GetActiveNode();
                    string updateTime = settings.LastRefreshUtc == default
                        ? "-"
                        : settings.LastRefreshUtc.ToLocalTime().ToString("dd.MM.yyyy");

                    string name = activeNode == null
                        ? LocalizeGoida("Lang.Goida.ServerListName", "Goida profile")
                        : string.Format(
                            LocalizeGoida("Lang.Goida.ServerListNameWithNode", "Goida · {0}"),
                            activeNode.DisplayName);

                    Config config = new Config(
                        path: GoidaProfilePaths.MarkerPath,
                        name: name,
                        type: ConfigType.FILE,
                        group: GroupType.GENERAL,
                        updateTime: updateTime);
                    ApplyGoidaAvailability(config, activeNode);
                    return config;
                }

                static void ApplyGoidaAvailability(Config config, GoidaNode? activeNode)
                {
                    if (activeNode == null)
                        return;

                    if (!activeNode.VlessVerified || activeNode.Status != GoidaNodeStatus.Ok)
                    {
                        config.SetAvailability(Values.Availability.NOT_CHECKED);
                        return;
                    }

                    config.SetAvailability(MapNodeLatency(activeNode.LatencyMs, activeNode.Status));
                }

                static int MapNodeLatency(int latencyMs, GoidaNodeStatus status)
                {
                    if (latencyMs >= 0)
                        return latencyMs;

                    return status switch
                    {
                        GoidaNodeStatus.Timeout => Availability.TIMEOUT,
                        GoidaNodeStatus.Error => Availability.ERROR,
                        _ => Availability.NOT_CHECKED
                    };
                }

                Config? BuildGoidaRuntimeConfig()
                {
                    if (!goidaHandler.Manager.TryEnsureActiveNode())
                        return BuildGoidaListConfig();

                    GoidaNode? activeNode = goidaHandler.Manager.GetActiveNode();
                    if (activeNode == null || !System.IO.File.Exists(activeNode.ConfigPath))
                        return BuildGoidaListConfig();

                    return new Config(
                        path: activeNode.ConfigPath,
                        name: string.Format(
                            LocalizeGoida("Lang.Goida.ServerListNameWithNode", "Goida · {0}"),
                            activeNode.DisplayName),
                        type: ConfigType.FILE,
                        group: GroupType.GENERAL,
                        updateTime: activeNode.LastCheckedUtc == default
                            ? "-"
                            : activeNode.LastCheckedUtc.ToLocalTime().ToString("dd.MM.yyyy"));
                }

                static string LocalizeGoida(string key, string fallback)
                {
                    try
                    {
                        string? term = ServiceLocator.Get<LocalizationService>().GetTerm(key);
                        return string.IsNullOrWhiteSpace(term) ? fallback : term;
                    }
                    catch
                    {
                        return fallback;
                    }
                }
            }

            void SetupUpdateHandler()
            {
                VersionHandler versionHandler = handlersManager.GetHandler<VersionHandler>();

                handlersManager.GetHandler<UpdateHandler>().Setup(
                    getApplicationVersion: versionHandler.GetApplicationVersion,
                    convertToAppVersion: versionHandler.ConvertToAppVersion
                );
            }

            void SetupNotifyHandler()
            {
                SettingsHandler settingsHandler = handlersManager.GetHandler<SettingsHandler>();

                handlersManager.GetHandler<NotifyHandler>().Setup(
                    getMode: settingsHandler.UserSettings.GetMode,
                    onOpenClick: OpenApplication,
                    onUpdateClick: OpenUpdateWindow,
                    onAboutClick: OpenAboutWindow,
                    onCloseClick: CloseApplication,
                    onProxyModeClick: () => { OnModeClick(Mode.PROXY); },
                    onTunnelModeClick: () => { OnModeClick(Mode.TUN); }
                );

                void CloseApplication()
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            handlersManager.GetHandler<GoidaProfileHandler>().StopBackground();
                        }
                        catch
                        {
                        }

                        MainWindow? mainWindow = windowFactory.GetMainWindow();
                        if (mainWindow != null)
                            mainWindow.RequestGracefulShutdown();
                        else
                            ShutdownCoreAndExit();
                    }));
                }

                void ShutdownCoreAndExit()
                {
                    try
                    {
                        core.Stop();
                        core.DisableMode();
                        handlersManager.GetHandler<GoidaProfileHandler>().StopBackground();
                    }
                    catch
                    {
                    }

                    Application.Current?.Shutdown();
                }
                
                void OpenUpdateWindow() 
                {
                    OpenApplication();
                    if(IsAnotherWindowOpened())
                        CloseOtherWindows();

                    UpdateWindow updateWindow = windowFactory.CreateUpdateWindow();
                    updateWindow.Owner = Application.Current.MainWindow;
                    updateWindow.ShowDialog();
                }

                void OpenAboutWindow()
                {
                    OpenApplication();
                    if(IsAnotherWindowOpened())
                        CloseOtherWindows();

                    AboutWindow aboutWindow = windowFactory.CreateAboutWindow();
                    aboutWindow.Owner = Application.Current.MainWindow;
                    aboutWindow.ShowDialog();
                }

                void OnModeClick(Mode mode) 
                {
                    if (mode == settingsHandler.UserSettings.GetMode())
                        return;

                    MainWindow mainWindow = windowFactory.GetMainWindow();
                    settingsHandler.UpdateMode(mode);
                    mainWindow.TryDisableModeAndRerun();
                }
            }

            void SetupDeepLinkHandler()
            {
                HandlersManager.GetHandler<DeepLinkHandler>().Setup(
                    onReceiveArg: ref PipeManager.OnReceiveArg,
                    onConfigLinkFetched: PrepareToImportConfigLink,
                    onSubscriptionLinkFetched: PrepareToImportSubscriptionLink
                );

                void PrepareToImportConfigLink(string link)
                {
                    ServerWindow serverWindow = GetServerWindow();
                    serverWindow.OpenImportConfigWithLinkSection(link);
                    serverWindow.ShowDialog();
                }

                void PrepareToImportSubscriptionLink(string link)
                {
                    ServerWindow serverWindow = GetServerWindow();
                    serverWindow.OpenImportSubscriptionWithLinkSection(link);
                    serverWindow.ShowDialog();
                }

                ServerWindow GetServerWindow()
                {
                    OpenApplication();
                    if (IsAnotherWindowOpened())
                        CloseOtherWindows();

                    ServerWindow serverWindow = windowFactory.CreateServerWindow();
                    serverWindow.Owner = Application.Current.MainWindow;

                    return serverWindow;
                }
            }

            void SetupLocalizationHandler()
            {
                SettingsHandler settingsHandler = handlersManager.GetHandler<SettingsHandler>();

                HandlersManager.GetHandler<LocalizationHandler>().Setup(
                    getCurrentLanguage: settingsHandler.UserSettings.GetLanguage
                );
            }

            void SetupGoidaProfileHandler()
            {
                SettingsHandler settingsHandler = handlersManager.GetHandler<SettingsHandler>();
                TemplateHandler templateHandler = handlersManager.GetHandler<TemplateHandler>();
                GoidaProfileHandler goidaHandler = handlersManager.GetHandler<GoidaProfileHandler>();

                goidaHandler.Setup(
                    convertConfigLinkToV2Ray: templateHandler.ConverLinkToConfig,
                    testConnection: GoidaConnectionTest.CreateFromConfigPath(core.LoadConfig, core.Test),
                    getUserSettings: () => settingsHandler.UserSettings,
                    updateUserSettings: settingsHandler.UpdateUserSettings,
                    onActiveNodeChanged: OnGoidaActiveNodeChanged,
                    pauseNativeForTest: PauseNativeForGoidaProbe,
                    resumeNativeAfterTest: ResumeNativeAfterGoidaProbe,
                    isVpnSessionActive: IsVpnSessionActive);

                bool IsVpnSessionActive() => InvokeOnUi(
                    () => windowFactory.GetMainWindow()?.IsServerRunning ?? false);

                bool PauseNativeForGoidaProbe() => InvokeOnUi(() =>
                {
                    MainWindow? mainWindow = windowFactory.GetMainWindow();
                    return mainWindow?.TryPauseForNativeTest() ?? false;
                });

                void ResumeNativeAfterGoidaProbe() => InvokeOnUi(() =>
                {
                    windowFactory.GetMainWindow()?.TryResumeAfterNativeTest();
                    return true;
                });

                static T InvokeOnUi<T>(Func<T> action)
                {
                    if (Application.Current?.Dispatcher == null)
                        return action();

                    if (Application.Current.Dispatcher.CheckAccess())
                        return action();

                    return Application.Current.Dispatcher.Invoke(action);
                }

                void OnGoidaActiveNodeChanged(GoidaNode node)
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.ConfigPath))
                        return;

                    if (!GoidaProfilePaths.IsMarker(settingsHandler.UserSettings.GetCurrentConfigPath()))
                        return;

                    settingsHandler.UpdateCurrentConfigPath(GoidaProfilePaths.MarkerPath);

                    Application.Current?.Dispatcher?.BeginInvoke(new System.Action(() =>
                    {
                        MainWindow? mainWindow = windowFactory.GetMainWindow();
                        mainWindow?.UpdateUI();
                        if (mainWindow?.IsServerRunning == true)
                            mainWindow.TryRerun();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }

                DateTime lastGoidaMainUiUpdateUtc = DateTime.MinValue;
                goidaHandler.Manager.NodesUpdated += () =>
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new System.Action(() =>
                    {
                        if ((DateTime.UtcNow - lastGoidaMainUiUpdateUtc).TotalMilliseconds < 400)
                            return;

                        lastGoidaMainUiUpdateUtc = DateTime.UtcNow;
                        windowFactory.GetMainWindow()?.UpdateUI();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                };
            }
        }
        
        private void OpenApplication()
        {
            var mainWindow = Application.Current.MainWindow;
            mainWindow.Show();
            ForceShowWindowOnTop();
            mainWindow.WindowState = WindowState.Normal;

            void ForceShowWindowOnTop()
            {
                mainWindow.Topmost = true;
                mainWindow.Topmost = false;
                mainWindow.Activate();
            }
        }

        private void CloseOtherWindows()
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (!IsMainWindow(window))
                    window.Close();
            }
        }

        private bool IsAnotherWindowOpened() => Application.Current.Windows.Count > 1;

        private bool IsMainWindow(Window window) => window == Application.Current.MainWindow;
    }
}