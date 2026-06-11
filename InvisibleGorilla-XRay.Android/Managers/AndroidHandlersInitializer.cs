using InvisibleGorillaXRay.Handlers;
using InvisibleGorillaXRay.Handlers.DeepLinks;
using InvisibleGorillaXRay.Handlers.Settings.Startup;
using InvisibleGorillaXRay.Managers;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Services;
using InvisibleGorillaXRay.Services.Goida;

namespace InvisibleGorillaXRay.Android.Managers
{
    using InvisibleGorillaXRay.Android.Handlers;
    using InvisibleGorillaXRay.Android.Handlers.DeepLinks;
    using InvisibleGorillaXRay.Android.Handlers.Proxies;
    using InvisibleGorillaXRay.Android.Handlers.Settings;
    using InvisibleGorillaXRay.Android.Handlers.Tunnels;
    using InvisibleGorillaXRay.Android.Services;
    using InvisibleGorillaXRay.Core;

    public sealed class AndroidHandlersInitializer
    {
        public HandlersManager HandlersManager { get; private set; } = null!;
        public AndroidLocalizationHandler LocalizationHandler { get; private set; } = null!;

        public void Register()
        {
            HandlersManager = new HandlersManager();
            LocalizationHandler = new AndroidLocalizationHandler();

            HandlersManager.AddHandler(new SettingsHandler(() => new AndroidStartup()));
            HandlersManager.AddHandler(new TemplateHandler());
            HandlersManager.AddHandler(new ConfigHandler());
            HandlersManager.AddHandler(new ProxyHandler(() => new AndroidProxy()));
            HandlersManager.AddHandler(new TunnelHandler(() => new AndroidTunnel()));
            HandlersManager.AddHandler(new VersionHandler());
            HandlersManager.AddHandler(new UpdateHandler());
            HandlersManager.AddHandler(new BroadcastHandler());
            HandlersManager.AddHandler(new DeepLinkHandler(() => new AndroidDeepLink()));
            HandlersManager.AddHandler(LocalizationHandler);
            HandlersManager.AddHandler(new GoidaProfileHandler());
        }

        public void Setup(InvisibleGorillaXRayCore core)
        {
            SetupTunnelHandler();
            SetupConfigHandler();
            SetupUpdateHandler();
            SetupDeepLinkHandler();
            SetupLocalizationHandler();
            SetupGoidaProfileHandler(core);

            HandlersManager.GetHandler<GoidaProfileHandler>().StartBackground();

            void SetupTunnelHandler()
            {
                HandlersManager.GetHandler<TunnelHandler>().Setup(
                    onStartTunnelingService: () => { },
                    isServiceRunning: () => false,
                    isServicePortActive: () => false,
                    connectTunnelingService: () => new Status(Code.ERROR, SubCode.CANT_TUNNEL, string.Empty),
                    executeCommand: command => new Status(Code.ERROR, SubCode.CANT_TUNNEL, command)
                );
            }

            void SetupConfigHandler()
            {
                SettingsHandler settingsHandler = HandlersManager.GetHandler<SettingsHandler>();
                GoidaProfileHandler goidaHandler = HandlersManager.GetHandler<GoidaProfileHandler>();

                HandlersManager.GetHandler<ConfigHandler>().Setup(
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

                string LocalizeGoida(string key, string fallback)
                {
                    try
                    {
                        string? term = LocalizationHandler.GetTerm(key);
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
                VersionHandler versionHandler = HandlersManager.GetHandler<VersionHandler>();
                HandlersManager.GetHandler<UpdateHandler>().Setup(
                    getApplicationVersion: () =>
                    {
                        // The assembly version on the Android head defaults to 1.0.0.0 because we
                        // only ship a display version through ApplicationDisplayVersion. Read the
                        // real installed PackageInfo.VersionName so the GitHub comparison reflects
                        // what the user actually has on the device.
                        string installed = global::InvisibleGorillaXRay.Android.Services.AndroidUpdateService.GetInstalledVersion();
                        return string.IsNullOrWhiteSpace(installed)
                            ? versionHandler.GetApplicationVersion()
                            : versionHandler.ConvertToAppVersion(installed);
                    },
                    convertToAppVersion: versionHandler.ConvertToAppVersion
                );
            }

            void SetupDeepLinkHandler()
            {
                HandlersManager.GetHandler<DeepLinkHandler>().Setup(
                    ref AndroidDeepLinkDispatcher.OnReceiveArg,
                    onConfigLinkFetched: _ => { },
                    onSubscriptionLinkFetched: _ => { }
                );
            }

            void SetupLocalizationHandler()
            {
                SettingsHandler settingsHandler = HandlersManager.GetHandler<SettingsHandler>();
                LocalizationHandler.Setup(settingsHandler.UserSettings.GetLanguage);
            }

            void SetupGoidaProfileHandler(InvisibleGorillaXRayCore coreInstance)
            {
                SettingsHandler settingsHandler = HandlersManager.GetHandler<SettingsHandler>();
                TemplateHandler templateHandler = HandlersManager.GetHandler<TemplateHandler>();
                GoidaProfileHandler goidaHandler = HandlersManager.GetHandler<GoidaProfileHandler>();

                goidaHandler.Setup(
                    convertConfigLinkToV2Ray: templateHandler.ConverLinkToConfig,
                    testConnection: GoidaConnectionTest.CreateFromConfigPath(coreInstance.LoadConfig, coreInstance.Test),
                    getUserSettings: () => settingsHandler.UserSettings,
                    updateUserSettings: settingsHandler.UpdateUserSettings,
                    onActiveNodeChanged: node => GoidaActiveNodeBridge.OnActiveNodeChanged?.Invoke(node));
            }
        }
    }
}
