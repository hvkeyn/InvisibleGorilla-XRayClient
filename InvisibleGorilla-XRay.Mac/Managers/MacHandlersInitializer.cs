using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Handlers;
using InvisibleGorillaXRay.Mac.Factories;
using InvisibleGorillaXRay.Mac.Handlers;
using InvisibleGorillaXRay.Mac.Handlers.DeepLinks;
using InvisibleGorillaXRay.Mac.Handlers.Proxies;
using InvisibleGorillaXRay.Mac.Handlers.Settings;
using InvisibleGorillaXRay.Mac.Handlers.Tunnels;
using InvisibleGorillaXRay.Managers;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Services;
using InvisibleGorillaXRay.Services.Goida;

namespace InvisibleGorillaXRay.Mac.Managers
{
    public class MacHandlersInitializer
    {
        public HandlersManager HandlersManager { get; private set; }

        public void Register()
        {
            HandlersManager = new HandlersManager();

            HandlersManager.AddHandler(new SettingsHandler(() => new MacStartup()));
            HandlersManager.AddHandler(new TemplateHandler());
            HandlersManager.AddHandler(new ProcessHandler());
            HandlersManager.AddHandler(new ConfigHandler());
            HandlersManager.AddHandler(new ProxyHandler(() => new MacProxy()));
            HandlersManager.AddHandler(new TunnelHandler(() => new MacTunnel()));
            HandlersManager.AddHandler(new MacNotifyHandler());
            HandlersManager.AddHandler(new VersionHandler());
            HandlersManager.AddHandler(new UpdateHandler());
            HandlersManager.AddHandler(new BroadcastHandler());
            HandlersManager.AddHandler(new DeepLinkHandler(() => new MacDeepLink()));
            HandlersManager.AddHandler(new LinkHandler());
            HandlersManager.AddHandler(new MacLocalizationHandler());
            HandlersManager.AddHandler(new GoidaProfileHandler());
        }

        public void Setup(
            InvisibleGorillaXRayCore core,
            HandlersManager handlersManager,
            MacWindowFactory windowFactory)
        {
            SetupProcessHandler();
            SetupTunnelHandler();
            SetupConfigHandler();
            SetupNotifyHandler();
            SetupDeepLinkHandler();
            SetupLocalizationHandler();
            SetupGoidaProfileHandler();

            handlersManager.GetHandler<GoidaProfileHandler>().StartBackground();

            void SetupProcessHandler()
            {
                var settingsHandler = handlersManager.GetHandler<SettingsHandler>();
                var processHandler = handlersManager.GetHandler<ProcessHandler>();
                processHandler.Setup(getTunnelPort: settingsHandler.UserSettings.GetTunPort);
            }

            void SetupTunnelHandler()
            {
                var processHandler = handlersManager.GetHandler<ProcessHandler>();
                var tunnelHandler = handlersManager.GetHandler<TunnelHandler>();
                tunnelHandler.Setup(
                    onStartTunnelingService: () => processHandler.TunnelProcess.Start(),
                    isServiceRunning: () => processHandler.TunnelProcess.IsProcessRunning(),
                    isServicePortActive: () => processHandler.TunnelProcess.IsProcessPortActive(),
                    connectTunnelingService: () => processHandler.TunnelProcess.Connect(),
                    executeCommand: command => processHandler.TunnelProcess.Execute(command)
                );
            }

            void SetupConfigHandler()
            {
                var settingsHandler = handlersManager.GetHandler<SettingsHandler>();
                var goidaHandler = handlersManager.GetHandler<GoidaProfileHandler>();

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

                    config.SetAvailability(MapNodeLatency(activeNode.LatencyMs, activeNode.Status));
                }

                static int MapNodeLatency(int latencyMs, GoidaNodeStatus status)
                {
                    if (latencyMs >= 0)
                        return latencyMs;

                    return status switch
                    {
                        GoidaNodeStatus.Timeout => Values.Availability.TIMEOUT,
                        GoidaNodeStatus.Error => Values.Availability.ERROR,
                        _ => Values.Availability.NOT_CHECKED
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

            void SetupNotifyHandler()
            {
                var settingsHandler = handlersManager.GetHandler<SettingsHandler>();
                var notifyHandler = handlersManager.GetHandler<MacNotifyHandler>();
                notifyHandler.Setup(
                    getMode: settingsHandler.UserSettings.GetMode,
                    onOpenClick: () => OpenApplication(),
                    onUpdateClick: () => { },
                    onAboutClick: () => { },
                    onCloseClick: () => CloseApplication(),
                    onProxyModeClick: () =>
                    {
                        settingsHandler.UpdateMode(Mode.PROXY);
                        windowFactory.GetMainWindow()?.TryDisableModeAndRerun();
                    },
                    onTunnelModeClick: () =>
                    {
                        settingsHandler.UpdateMode(Mode.TUN);
                        windowFactory.GetMainWindow()?.TryDisableModeAndRerun();
                    }
                );
            }

            void SetupDeepLinkHandler()
            {
                var deepLinkHandler = handlersManager.GetHandler<DeepLinkHandler>();
                deepLinkHandler.Setup(
                    onReceiveArg: ref MacPipeManager.OnReceiveArg,
                    onConfigLinkFetched: link =>
                    {
                        windowFactory.GetMainWindow()?.UpdateUI();
                    },
                    onSubscriptionLinkFetched: link =>
                    {
                        windowFactory.GetMainWindow()?.UpdateUI();
                    }
                );
            }

            void SetupLocalizationHandler()
            {
                var settingsHandler = handlersManager.GetHandler<SettingsHandler>();
                var locHandler = handlersManager.GetHandler<MacLocalizationHandler>();
                locHandler.Setup(getCurrentLanguage: settingsHandler.UserSettings.GetLanguage);
            }

            void SetupGoidaProfileHandler()
            {
                var settingsHandler = handlersManager.GetHandler<SettingsHandler>();
                var templateHandler = handlersManager.GetHandler<TemplateHandler>();
                var goidaHandler = handlersManager.GetHandler<GoidaProfileHandler>();

                goidaHandler.Setup(
                    convertConfigLinkToV2Ray: templateHandler.ConverLinkToConfig,
                    testConnection: GoidaConnectionTest.CreateFromConfigPath(core.LoadConfig, core.Test),
                    getUserSettings: () => settingsHandler.UserSettings,
                    updateUserSettings: settingsHandler.UpdateUserSettings,
                    onActiveNodeChanged: node =>
                    {
                        if (node == null || string.IsNullOrWhiteSpace(node.ConfigPath))
                            return;

                        if (!settingsHandler.UserSettings.GetGoidaSettings().Enabled)
                            return;

                        settingsHandler.UpdateCurrentConfigPath(GoidaProfilePaths.MarkerPath);

                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            windowFactory.GetMainWindow()?.UpdateUI();
                            windowFactory.GetMainWindow()?.TryRerun();
                        });
                    });
            }
        }

        private void OpenApplication()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is Mac.Views.MainWindow mainWindow)
            {
                mainWindow.ShowAndActivate();
            }
        }

        private void CloseApplication()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
    }
}
