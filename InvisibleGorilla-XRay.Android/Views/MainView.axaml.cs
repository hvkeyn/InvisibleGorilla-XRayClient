using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace InvisibleGorillaXRay.Android.Views
{
    using InvisibleGorillaXRay.Android.Managers;
    using InvisibleGorillaXRay.Handlers;
    using InvisibleGorillaXRay.Models;
    using InvisibleGorillaXRay.Utilities;

    public partial class MainView : UserControl
    {
        private readonly AndroidAppManager appManager;
        private readonly InvisibleGorillaXRay.Core.InvisibleGorillaXRayCore core;
        private readonly SettingsHandler settingsHandler;
        private readonly ConfigHandler configHandler;
        private readonly TemplateHandler templateHandler;
        private readonly UpdateHandler updateHandler;
        private readonly BroadcastHandler broadcastHandler;

        private List<ConfigListItem> configItems = new();
        private bool isRunWorkerBusy;

        public MainView(AndroidAppManager appManager)
        {
            this.appManager = appManager;
            core = appManager.Core;
            settingsHandler = appManager.HandlersManager.GetHandler<SettingsHandler>();
            configHandler = appManager.HandlersManager.GetHandler<ConfigHandler>();
            templateHandler = appManager.HandlersManager.GetHandler<TemplateHandler>();
            updateHandler = appManager.HandlersManager.GetHandler<UpdateHandler>();
            broadcastHandler = appManager.HandlersManager.GetHandler<BroadcastHandler>();

            InitializeComponent();
            InitializeControls();
            LoadSettingsIntoControls();
            RefreshConfigs();
            UpdateRuntimeSummary();
            SetStatus(BuildModeMessage());
            _ = LoadRemoteInfoAsync();
        }

        private ComboBox ModeSelector => GetRequiredControl<ComboBox>("ModeComboBox");
        private ComboBox ProtocolSelector => GetRequiredControl<ComboBox>("ProtocolComboBox");
        private TextBox ProxyPortInput => GetRequiredControl<TextBox>("ProxyPortTextBox");
        private TextBox TunPortInput => GetRequiredControl<TextBox>("TunPortTextBox");
        private TextBox TunIpInput => GetRequiredControl<TextBox>("TunIpTextBox");
        private TextBox DnsInput => GetRequiredControl<TextBox>("DnsTextBox");
        private TextBox ConfigLinkInput => GetRequiredControl<TextBox>("ConfigLinkTextBox");
        private TextBox SubscriptionRemarkInput => GetRequiredControl<TextBox>("SubscriptionRemarkTextBox");
        private TextBox SubscriptionLinkInput => GetRequiredControl<TextBox>("SubscriptionLinkTextBox");
        private TextBox ConfigRemarkInput => GetRequiredControl<TextBox>("ConfigRemarkTextBox");
        private TextBox RawConfigInput => GetRequiredControl<TextBox>("RawConfigTextBox");
        private ListBox ConfigList => GetRequiredControl<ListBox>("ConfigsListBox");
        private TextBlock StatusText => GetRequiredControl<TextBlock>("StatusTextBlock");
        private TextBlock RuntimeText => GetRequiredControl<TextBlock>("RuntimeTextBlock");
        private Button RunActionButton => GetRequiredControl<Button>("RunButton");
        private Button StopActionButton => GetRequiredControl<Button>("StopButton");

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private T GetRequiredControl<T>(string name) where T : Control
        {
            return this.FindControl<T>(name)
                ?? throw new InvalidOperationException($"Required control '{name}' was not found.");
        }

        private void InitializeControls()
        {
            ModeSelector.ItemsSource = Enum.GetNames(typeof(Mode));
            ProtocolSelector.ItemsSource = Enum.GetNames(typeof(Protocol));
            StopActionButton.IsEnabled = false;
        }

        private void LoadSettingsIntoControls()
        {
            UserSettings settings = settingsHandler.UserSettings;

            ModeSelector.SelectedItem = settings.GetMode().ToString();
            ProtocolSelector.SelectedItem = settings.GetProtocol().ToString();
            ProxyPortInput.Text = settings.GetProxyPort().ToString();
            TunPortInput.Text = settings.GetTunPort().ToString();
            TunIpInput.Text = settings.GetTunIp();
            DnsInput.Text = settings.GetDns();
        }

        private void RefreshConfigs()
        {
            configItems = BuildConfigList();
            ConfigList.ItemsSource = configItems.Select(item => item.Label).ToList();

            string currentConfigPath = settingsHandler.UserSettings.GetCurrentConfigPath();
            int selectedIndex = configItems.FindIndex(item =>
                string.Equals(item.Config.Path, currentConfigPath, StringComparison.OrdinalIgnoreCase));

            if (selectedIndex >= 0)
                ConfigList.SelectedIndex = selectedIndex;
        }

        private List<ConfigListItem> BuildConfigList()
        {
            List<ConfigListItem> items = configHandler
                .GetAllGeneralConfigs()
                .Select(config => new ConfigListItem($"[General] {config.Name}", config))
                .ToList();

            foreach (Subscription group in configHandler.GetAllGroups())
            {
                foreach (Config config in configHandler.GetAllSubscriptionConfigs(group.Directory.FullName))
                {
                    items.Add(new ConfigListItem(
                        label: $"[Subscription] {group.Directory.Name} / {config.Name}",
                        config: config));
                }
            }

            return items;
        }

        private async Task LoadRemoteInfoAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    Status updateStatus = updateHandler.CheckForUpdate();
                    Status broadcastStatus = broadcastHandler.CheckForBroadcast();

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (updateStatus.SubCode == SubCode.UPDATE_AVAILABLE)
                            SetStatus("Update available on GitHub Releases. " + BuildModeMessage());

                        if (broadcastStatus.Code == Code.SUCCESS && broadcastStatus.Content is Broadcast broadcast)
                        {
                            RuntimeText.Text = RuntimeText.Text +
                                Environment.NewLine + Environment.NewLine +
                                "Broadcast: " + broadcast.Text;
                        }
                    });
                }
                catch
                {
                }
            });
        }

        private bool TrySaveSettings(bool showSuccessMessage)
        {
            if (!TryParsePort(ProxyPortInput.Text, out int proxyPort, "proxy"))
                return false;

            if (!TryParsePort(TunPortInput.Text, out int tunPort, "TUN"))
                return false;

            UserSettings current = settingsHandler.UserSettings;
            settingsHandler.UpdateUserSettings(new UserSettings
            {
                Language = current.GetLanguage(),
                Mode = ParseMode(ModeSelector.SelectedItem?.ToString(), current.GetMode()),
                Protocol = ParseProtocol(ProtocolSelector.SelectedItem?.ToString(), current.GetProtocol()),
                LogLevel = current.GetLogLevel(),
                IsSystemProxyUse = false,
                IsUdpEnable = current.GetUdpEnabled(),
                IsRunningAtStartup = false,
                IsStartHidden = false,
                IsAutoConnect = false,
                IsSendingAnalytics = current.GetSendingAnalyticsEnabled(),
                ProxyPort = proxyPort,
                TunPort = tunPort,
                TestPort = current.GetTestPort(),
                TunIp = TunIpInput.Text?.Trim() ?? current.GetTunIp(),
                Dns = DnsInput.Text?.Trim() ?? current.GetDns(),
                LogPath = current.GetLogPath()
            });

            UpdateRuntimeSummary();

            if (showSuccessMessage)
                SetStatus("Settings saved. " + BuildModeMessage());

            return true;
        }

        private bool TryParsePort(string? text, out int value, string name)
        {
            if (int.TryParse(text, out value) && value > 0)
                return true;

            SetStatus($"Invalid {name} port.");
            return false;
        }

        private void UpdateRuntimeSummary()
        {
            UserSettings settings = settingsHandler.UserSettings;
            string currentConfigPath = settings.GetCurrentConfigPath();

            RuntimeText.Text =
                $"App root: {InvisibleGorillaXRay.Values.Directory.ROOT}{Environment.NewLine}" +
                $"Current config: {currentConfigPath}{Environment.NewLine}" +
                $"Proxy listener: 127.0.0.1:{settings.GetProxyPort()}{Environment.NewLine}" +
                $"TUN service port: {settings.GetTunPort()}{Environment.NewLine}" +
                $"DNS: {settings.GetDns()}{Environment.NewLine}" +
                $"Android system proxy switching is disabled; proxy mode runs a local Xray listener.";
        }

        private string BuildModeMessage()
        {
            string mode = ModeSelector.SelectedItem?.ToString() ?? settingsHandler.UserSettings.GetMode().ToString();

            if (string.Equals(mode, Mode.TUN.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return "TUN/VPN mode is staged for Android, but the mobile tunnel bridge is not bundled yet in this repository.";
            }

            return "Proxy mode starts a local Xray listener on Android. Configure apps to use 127.0.0.1 and the selected proxy port.";
        }

        private void SetStatus(string message)
        {
            StatusText.Text = message;
        }

        private void SetRunningState(bool isRunning)
        {
            RunActionButton.IsEnabled = !isRunning;
            StopActionButton.IsEnabled = isRunning;
        }

        private async void OnRunClick(object? sender, RoutedEventArgs e)
        {
            if (isRunWorkerBusy)
                return;

            if (!TrySaveSettings(showSuccessMessage: false))
                return;

            isRunWorkerBusy = true;
            SetRunningState(true);
            SetStatus("Loading config...");

            await Task.Run(() =>
            {
                bool started = false;
                string? failureMessage = null;

                try
                {
                    Status configStatus = core.LoadConfig();
                    if (configStatus.Code == Code.ERROR)
                    {
                        failureMessage = configStatus.Content?.ToString() ?? "No config selected.";
                        return;
                    }

                    Status modeStatus = core.EnableMode();
                    if (modeStatus.Code == Code.ERROR)
                    {
                        failureMessage = modeStatus.Content?.ToString() ?? "Failed to enable mode.";
                        return;
                    }

                    if (modeStatus.Code == Code.INFO && modeStatus.SubCode == SubCode.CANCELED)
                    {
                        failureMessage = "Start canceled.";
                        return;
                    }

                    started = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        SetStatus($"Running in {settingsHandler.UserSettings.GetMode()} mode...");
                    });

                    core.Run(configStatus.Content?.ToString() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    failureMessage = ex.Message;
                }
                finally
                {
                    isRunWorkerBusy = false;

                    Dispatcher.UIThread.Post(() =>
                    {
                        SetRunningState(false);
                        UpdateRuntimeSummary();

                        if (!string.IsNullOrWhiteSpace(failureMessage))
                            SetStatus(failureMessage);
                        else if (started)
                            SetStatus("Stopped.");
                    });
                }
            });
        }

        private void OnStopClick(object? sender, RoutedEventArgs e)
        {
            core.Stop();
            _ = Task.Run(() => core.DisableMode());
            SetStatus("Stop requested...");
            SetRunningState(false);
        }

        private void OnRefreshConfigsClick(object? sender, RoutedEventArgs e)
        {
            RefreshConfigs();
            UpdateRuntimeSummary();
            SetStatus("Config list refreshed.");
        }

        private void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
        {
            TrySaveSettings(showSuccessMessage: true);
        }

        private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
        {
            SetStatus(BuildModeMessage());
        }

        private void OnConfigSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            int selectedIndex = ConfigList.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= configItems.Count)
                return;

            Config selectedConfig = configItems[selectedIndex].Config;
            settingsHandler.UpdateCurrentConfigPath(selectedConfig.Path);
            UpdateRuntimeSummary();
            SetStatus($"Selected config: {selectedConfig.Name}");
        }

        private void OnImportConfigLinkClick(object? sender, RoutedEventArgs e)
        {
            string link = ConfigLinkInput.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(link))
            {
                SetStatus("Paste a config link first.");
                return;
            }

            Status status = templateHandler.ConverLinkToConfig(link);
            if (status.Code == Code.ERROR)
            {
                SetStatus(status.Content?.ToString() ?? "Unsupported config link.");
                return;
            }

            string[] payload = status.Content as string[] ?? Array.Empty<string>();
            if (payload.Length < 2)
            {
                SetStatus("Failed to convert config link.");
                return;
            }

            configHandler.CreateConfig(payload[0], payload[1]);
            ConfigLinkInput.Text = string.Empty;
            RefreshConfigs();
            SetStatus($"Imported config: {payload[0]}");
        }

        private void OnImportSubscriptionClick(object? sender, RoutedEventArgs e)
        {
            string remark = SubscriptionRemarkInput.Text?.Trim() ?? string.Empty;
            string link = SubscriptionLinkInput.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(remark) || string.IsNullOrWhiteSpace(link))
            {
                SetStatus("Enter both a subscription name and URL.");
                return;
            }

            Status status = templateHandler.ConvertLinkToSubscription(remark, link);
            if (status.Code == Code.ERROR)
            {
                SetStatus(status.Content?.ToString() ?? "Unsupported subscription link.");
                return;
            }

            string[] payload = status.Content as string[] ?? Array.Empty<string>();
            if (payload.Length < 2)
            {
                SetStatus("Failed to import subscription.");
                return;
            }

            configHandler.CreateSubscription(payload[0], link, payload[1]);
            SubscriptionRemarkInput.Text = string.Empty;
            SubscriptionLinkInput.Text = string.Empty;
            RefreshConfigs();
            SetStatus($"Imported subscription: {payload[0]}");
        }

        private void OnSaveRawConfigClick(object? sender, RoutedEventArgs e)
        {
            string remark = ConfigRemarkInput.Text?.Trim() ?? string.Empty;
            string rawConfig = RawConfigInput.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(remark))
            {
                SetStatus("Enter a config name.");
                return;
            }

            if (!JsonUtility.IsJsonValid(rawConfig))
            {
                SetStatus("Raw config is not valid JSON.");
                return;
            }

            configHandler.CreateConfig(remark, rawConfig);
            ConfigRemarkInput.Text = string.Empty;
            RawConfigInput.Text = string.Empty;
            RefreshConfigs();
            SetStatus($"Saved raw config: {remark}");
        }

        private static Mode ParseMode(string? selectedValue, Mode fallback)
        {
            return Enum.TryParse(selectedValue, ignoreCase: true, out Mode parsed)
                ? parsed
                : fallback;
        }

        private static Protocol ParseProtocol(string? selectedValue, Protocol fallback)
        {
            return Enum.TryParse(selectedValue, ignoreCase: true, out Protocol parsed)
                ? parsed
                : fallback;
        }

        private sealed class ConfigListItem
        {
            public ConfigListItem(string label, Config config)
            {
                Label = label;
                Config = config;
            }

            public string Label { get; }
            public Config Config { get; }
        }
    }
}
