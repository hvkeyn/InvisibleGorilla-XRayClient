using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace InvisibleGorillaXRay.Android.Views
{
    using InvisibleGorillaXRay.Android.Handlers.DeepLinks;
    using InvisibleGorillaXRay.Android.Managers;
    using InvisibleGorillaXRay.Core;
    using InvisibleGorillaXRay.Handlers;
    using InvisibleGorillaXRay.Models;
    using InvisibleGorillaXRay.Utilities;

    public partial class MainView : UserControl
    {
        private enum NavigationSection { Home, Servers, Settings }
        private enum ServerTab { Configurations, Subscriptions }
        private enum ConnectionState { Stopped, Starting, Running }

        private static readonly IBrush StoppedBrush = new SolidColorBrush(Color.Parse("#D66A75"));
        private static readonly IBrush StartingBrush = new SolidColorBrush(Color.Parse("#C9A227"));
        private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#56B870"));

        private InvisibleGorillaXRay.Core.InvisibleGorillaXRayCore core = null!;
        private SettingsHandler settingsHandler = null!;
        private ConfigHandler configHandler = null!;
        private TemplateHandler templateHandler = null!;
        private UpdateHandler updateHandler = null!;
        private BroadcastHandler broadcastHandler = null!;
        private Android.Handlers.AndroidLocalizationHandler localizationHandler = null!;

        private List<ConfigListItem> configItems = new();
        private List<Subscription> subscriptionGroups = new();
        private Subscription? selectedSubscription;
        private bool isCheckWorkerBusy;
        private bool isRunWorkerBusy;
        private bool isInitialized;
        private bool isShowingAdvancedImport;
        private bool isServersSectionInitialized;
        private bool isSettingsSectionInitialized;
        private bool suppressConfigSelectionChanged;
        private bool suppressSubscriptionSelectionChanged;
        private bool updateAvailable;
        private string? broadcastMessage;

        public MainView()
        {
            DiagnosticLog.Write("MainView", "Constructor start");
            InitializeComponent();
            DiagnosticLog.Write("MainView", "XAML loaded");
        }

        public MainView(AndroidAppManager appManager) : this()
        {
            Setup(appManager);
        }

        public void Setup(AndroidAppManager appManager)
        {
            if (isInitialized)
            {
                DiagnosticLog.Write("MainView", "Setup skipped because view is already initialized");
                return;
            }

            DiagnosticLog.Write("MainView", "Setup start");

            core = appManager.Core;
            settingsHandler = appManager.HandlersManager.GetHandler<SettingsHandler>();
            configHandler = appManager.HandlersManager.GetHandler<ConfigHandler>();
            templateHandler = appManager.HandlersManager.GetHandler<TemplateHandler>();
            updateHandler = appManager.HandlersManager.GetHandler<UpdateHandler>();
            broadcastHandler = appManager.HandlersManager.GetHandler<BroadcastHandler>();
            localizationHandler = appManager.HandlersManager.GetHandler<Android.Handlers.AndroidLocalizationHandler>();
            DiagnosticLog.Write("MainView", "Handlers resolved");

            InitializeControls();
            ApplyLocalizedText();
            AndroidDeepLinkDispatcher.Register(HandlePendingImport);
            DiagnosticLog.Write("MainView", "Controls initialized");
            UpdateCurrentConfigSummary();
            DiagnosticLog.Write("MainView", "Current config summary updated");
            SetConnectionState(ConnectionState.Stopped);
            SetStatus(string.Empty);
            isInitialized = true;
            DiagnosticLog.Write("MainView", "Setup completed");
            _ = LoadRemoteInfoAsync();
            DiagnosticLog.Write("MainView", "Remote info background load started");
        }

        private StackPanel HomeSectionScroll => GetRequiredControl<StackPanel>("HomeSectionPanel");
        private StackPanel ServersSectionScroll => GetRequiredControl<StackPanel>("ServersSectionPanel");
        private StackPanel SettingsSectionScroll => GetRequiredControl<StackPanel>("SettingsSectionPanel");
        private Button HomeNavButton => GetRequiredControl<Button>("HomeSectionButton");
        private Button ServersNavButton => GetRequiredControl<Button>("ServersSectionButton");
        private Button SettingsNavButton => GetRequiredControl<Button>("SettingsSectionButton");
        private Button ConfigurationsTabNavButton => GetRequiredControl<Button>("ConfigurationsTabButton");
        private Button SubscriptionsTabNavButton => GetRequiredControl<Button>("SubscriptionsTabButton");
        private StackPanel ConfigurationsContentPanel => GetRequiredControl<StackPanel>("ConfigurationsPanel");
        private StackPanel SubscriptionsContentPanel => GetRequiredControl<StackPanel>("SubscriptionsPanel");
        private Border ConnectionHeroGlowBorder => GetRequiredControl<Border>("ConnectionHeroGlow");
        private Grid StoppedHeroIconShape => GetRequiredControl<Grid>("StoppedHeroIcon");
        private Grid RunningHeroIconShape => GetRequiredControl<Grid>("RunningHeroIcon");
        private Border ConnectionStateIndicatorDot => GetRequiredControl<Border>("ConnectionStateIndicator");
        private TextBlock ConnectionStateTitleText => GetRequiredControl<TextBlock>("ConnectionStateTitleTextBlock");
        private TextBlock ConnectionStateSubtitleText => GetRequiredControl<TextBlock>("ConnectionStateSubtitleTextBlock");
        private TextBlock CurrentConfigNameText => GetRequiredControl<TextBlock>("CurrentConfigNameTextBlock");
        private TextBlock ManageServerConfigurationText => GetRequiredControl<TextBlock>("ManageServerConfigurationTextBlock");
        private Border HomeStatusPanelContainer => GetRequiredControl<Border>("HomeStatusPanel");
        private TextBlock HomeStatusText => GetRequiredControl<TextBlock>("HomeStatusTextBlock");
        private Border HomeInfoPanelContainer => GetRequiredControl<Border>("HomeInfoPanel");
        private TextBlock HomeInfoText => GetRequiredControl<TextBlock>("HomeInfoTextBlock");
        private Border ServersStatusPanelContainer => GetRequiredControl<Border>("ServersStatusPanel");
        private TextBlock ServersStatusText => GetRequiredControl<TextBlock>("ServersStatusTextBlock");
        private TextBlock ServersTitleText => GetRequiredControl<TextBlock>("ServersTitleTextBlock");
        private TextBlock ServersDescriptionText => GetRequiredControl<TextBlock>("ServersDescriptionTextBlock");
        private TextBlock AvailableConfigurationsText => GetRequiredControl<TextBlock>("AvailableConfigurationsTextBlock");
        private TextBlock ImportConfigTitleText => GetRequiredControl<TextBlock>("ImportConfigTitleTextBlock");
        private Button ImportConfigActionButton => GetRequiredControl<Button>("ImportConfigButton");
        private Button ImportConfigFileActionButton => GetRequiredControl<Button>("ImportConfigFileButton");
        private Button CheckSelectedConfigActionButton => GetRequiredControl<Button>("CheckSelectedConfigButton");
        private Button ShareSelectedConfigActionButton => GetRequiredControl<Button>("ShareSelectedConfigButton");
        private Button DeleteSelectedConfigActionButton => GetRequiredControl<Button>("DeleteSelectedConfigButton");
        private TextBlock RuntimeText => GetRequiredControl<TextBlock>("RuntimeTextBlock");
        private Button RunActionButton => GetRequiredControl<Button>("RunButton");
        private Button StopActionButton => GetRequiredControl<Button>("StopButton");
        private Button AdvancedImportToggleActionButton => GetRequiredControl<Button>("AdvancedImportToggleButton");
        private Border AdvancedImportContainer => GetRequiredControl<Border>("AdvancedImportPanel");
        private TextBlock AdvancedImportTitleText => GetRequiredControl<TextBlock>("AdvancedImportTitleTextBlock");
        private TextBlock AdvancedImportDescriptionText => GetRequiredControl<TextBlock>("AdvancedImportDescriptionTextBlock");
        private Button SaveRawConfigActionButton => GetRequiredControl<Button>("SaveRawConfigButton");
        private TextBlock SubscriptionGroupsTitleText => GetRequiredControl<TextBlock>("SubscriptionGroupsTitleTextBlock");
        private Button RefreshSubscriptionActionButton => GetRequiredControl<Button>("RefreshSubscriptionButton");
        private Button ShareSubscriptionActionButton => GetRequiredControl<Button>("ShareSubscriptionButton");
        private Button DeleteSubscriptionActionButton => GetRequiredControl<Button>("DeleteSubscriptionButton");
        private TextBlock AddSubscriptionTitleText => GetRequiredControl<TextBlock>("AddSubscriptionTitleTextBlock");
        private TextBlock AddSubscriptionDescriptionText => GetRequiredControl<TextBlock>("AddSubscriptionDescriptionTextBlock");
        private Button SaveSubscriptionActionButton => GetRequiredControl<Button>("SaveSubscriptionButton");
        private Border SettingsStatusPanelContainer => GetRequiredControl<Border>("SettingsStatusPanel");
        private TextBlock SettingsStatusText => GetRequiredControl<TextBlock>("SettingsStatusTextBlock");
        private TextBlock SettingsTitleText => GetRequiredControl<TextBlock>("SettingsTitleTextBlock");
        private TextBlock SettingsDescriptionText => GetRequiredControl<TextBlock>("SettingsDescriptionTextBlock");
        private TextBlock BasicSettingsTitleText => GetRequiredControl<TextBlock>("BasicSettingsTitleTextBlock");
        private TextBlock ConnectionModeTitleText => GetRequiredControl<TextBlock>("ConnectionModeTitleTextBlock");
        private TextBlock ProxyModeBadgeText => GetRequiredControl<TextBlock>("ProxyModeBadgeTextBlock");
        private TextBlock VpnComingSoonBadgeText => GetRequiredControl<TextBlock>("VpnComingSoonBadgeTextBlock");
        private TextBlock ProtocolTitleText => GetRequiredControl<TextBlock>("ProtocolTitleTextBlock");
        private TextBlock NetworkTitleText => GetRequiredControl<TextBlock>("NetworkTitleTextBlock");
        private TextBlock ProxyPortTitleText => GetRequiredControl<TextBlock>("ProxyPortTitleTextBlock");
        private TextBlock DnsTitleText => GetRequiredControl<TextBlock>("DnsTitleTextBlock");
        private TextBlock TunTitleText => GetRequiredControl<TextBlock>("TunTitleTextBlock");
        private TextBlock TunDescriptionText => GetRequiredControl<TextBlock>("TunDescriptionTextBlock");
        private TextBlock LogsAndDiagnosticsTitleText => GetRequiredControl<TextBlock>("LogsAndDiagnosticsTitleTextBlock");
        private ListBox ConfigList => GetRequiredControl<ListBox>("ConfigsListBox");
        private ListBox SubscriptionGroupList => GetRequiredControl<ListBox>("SubscriptionsListBox");
        private ComboBox ProtocolSelector => GetRequiredControl<ComboBox>("ProtocolComboBox");
        private TextBox ProxyPortInput => GetRequiredControl<TextBox>("ProxyPortTextBox");
        private TextBox DnsInput => GetRequiredControl<TextBox>("DnsTextBox");
        private CheckBox UdpEnabledToggle => GetRequiredControl<CheckBox>("UdpEnabledCheckBox");
        private CheckBox AnalyticsToggle => GetRequiredControl<CheckBox>("AnalyticsCheckBox");
        private TextBox ConfigLinkInput => GetRequiredControl<TextBox>("ConfigLinkTextBox");
        private TextBox SubscriptionRemarkInput => GetRequiredControl<TextBox>("SubscriptionRemarkTextBox");
        private TextBox SubscriptionLinkInput => GetRequiredControl<TextBox>("SubscriptionLinkTextBox");
        private TextBox ConfigRemarkInput => GetRequiredControl<TextBox>("ConfigRemarkTextBox");
        private TextBox RawConfigInput => GetRequiredControl<TextBox>("RawConfigTextBox");
        private Button SaveSettingsActionButton => GetRequiredControl<Button>("SaveSettingsButton");

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
            ShowSection(NavigationSection.Home);
            SetRunningState(false);
        }

        private void ApplyLocalizedText()
        {
            ManageServerConfigurationText.Text = Localize("Lang.Window.Main.ManageServerConfiguration");

            RunActionButton.Content = Localize("Lang.Run");
            StopActionButton.Content = Localize("Lang.Stop");

            ServersTitleText.Text = Localize("Lang.Window.Server.Title");
            ServersDescriptionText.Text = Localize("Lang.Android.Server.Description");
            ConfigurationsTabNavButton.Content = Localize("Lang.Window.Server.Configs");
            SubscriptionsTabNavButton.Content = Localize("Lang.Window.Server.Subscriptions");
            AvailableConfigurationsText.Text = Localize("Lang.Android.Server.AvailableConfigurations");
            ImportConfigTitleText.Text = Localize("Lang.Window.Server.AddConfig.FromLink");
            ConfigLinkInput.Watermark = Localize("Lang.Window.Server.AddConfig.Placeholder");
            ImportConfigFileActionButton.Content = Localize("Lang.Window.Server.AddConfig.FromFile");
            ImportConfigActionButton.Content = Localize("Lang.Android.Server.ImportConfigButton");
            CheckSelectedConfigActionButton.Content = Localize("Lang.Config.Check");
            ShareSelectedConfigActionButton.Content = Localize("Lang.Config.Share");
            DeleteSelectedConfigActionButton.Content = Localize("Lang.Config.Delete");
            AdvancedImportTitleText.Text = Localize("Lang.Android.Server.AdvancedImport");
            AdvancedImportDescriptionText.Text = Localize("Lang.Android.Server.AdvancedImportDescription");
            ConfigRemarkInput.Watermark = Localize("Lang.Android.Server.ConfigName");
            SaveRawConfigActionButton.Content = Localize("Lang.Android.Server.SaveRawConfig");
            SubscriptionGroupsTitleText.Text = Localize("Lang.Android.Server.SubscriptionGroups");
            RefreshSubscriptionActionButton.Content = Localize("Lang.Android.Server.RefreshSelected");
            ShareSubscriptionActionButton.Content = Localize("Lang.Config.Share");
            DeleteSubscriptionActionButton.Content = Localize("Lang.Android.Server.DeleteSelected");
            AddSubscriptionTitleText.Text = Localize("Lang.Android.Server.AddOrReplaceSubscription");
            AddSubscriptionDescriptionText.Text = Localize("Lang.Android.Server.AddOrReplaceSubscriptionDescription");
            SubscriptionRemarkInput.Watermark = Localize("Lang.Android.Server.SubscriptionName");
            SubscriptionLinkInput.Watermark = Localize("Lang.Window.Server.AddSub.Placeholder");
            SaveSubscriptionActionButton.Content = Localize("Lang.Android.Server.SaveSubscription");

            SettingsTitleText.Text = Localize("Lang.Window.Settings.Title");
            SettingsDescriptionText.Text = Localize("Lang.Android.Settings.Description");
            BasicSettingsTitleText.Text = Localize("Lang.Window.Settings.Basic");
            ConnectionModeTitleText.Text = Localize("Lang.Window.Settings.Mode");
            ProxyModeBadgeText.Text = Localize("Lang.Notify.Mode.Proxy");
            VpnComingSoonBadgeText.Text = Localize("Lang.Android.Settings.VpnComingSoon");
            ProtocolTitleText.Text = Localize("Lang.Window.Settings.Protocol");
            UdpEnabledToggle.Content = Localize("Lang.Window.Settings.UDP");
            AnalyticsToggle.Content = Localize("Lang.Window.Settings.SendAnalytics");
            NetworkTitleText.Text = Localize("Lang.Android.Settings.Network");
            ProxyPortTitleText.Text = Localize("Lang.Window.Settings.ProxyPort");
            DnsTitleText.Text = Localize("Lang.Window.Settings.Dns");
            TunTitleText.Text = Localize("Lang.Window.Settings.TUN");
            TunDescriptionText.Text = Localize("Lang.Android.Settings.TunDescription");
            LogsAndDiagnosticsTitleText.Text = Localize("Lang.Android.Settings.LogsAndDiagnostics");
            SaveSettingsActionButton.Content = Localize("Lang.Window.Settings.Confirm");

            SetAdvancedImportVisible(isShowingAdvancedImport);
        }

        private string Localize(string key)
        {
            return localizationHandler.GetTerm(key);
        }

        private string LocalizeFormat(string key, params object?[] args)
        {
            return string.Format(Localize(key), args);
        }

        private void EnsureServersSectionInitialized()
        {
            if (!isServersSectionInitialized)
            {
                ShowServerTab(ServerTab.Configurations);
                SetAdvancedImportVisible(false);
                isServersSectionInitialized = true;
            }

            RefreshConfigs();
            RefreshSubscriptions();
        }

        private void EnsureSettingsSectionInitialized()
        {
            if (!isSettingsSectionInitialized)
            {
                ProtocolSelector.ItemsSource = Enum.GetNames(typeof(Protocol));
                isSettingsSectionInitialized = true;
            }

            LoadSettingsIntoControls();
            UpdateRuntimeSummary();
        }

        private void LoadSettingsIntoControls()
        {
            UserSettings settings = settingsHandler.UserSettings;

            ProtocolSelector.SelectedItem = settings.GetProtocol().ToString();
            ProxyPortInput.Text = settings.GetProxyPort().ToString();
            DnsInput.Text = settings.GetDns();
            UdpEnabledToggle.IsChecked = settings.GetUdpEnabled();
            AnalyticsToggle.IsChecked = settings.GetSendingAnalyticsEnabled();
        }

        private void RefreshConfigs()
        {
            string currentConfigPath = settingsHandler.UserSettings.GetCurrentConfigPath();
            configItems = BuildConfigList();

            suppressConfigSelectionChanged = true;
            ConfigList.ItemsSource = configItems.Select(item => item.Label).ToList();
            ConfigList.SelectedIndex = configItems.FindIndex(item =>
                string.Equals(item.Config.Path, currentConfigPath, StringComparison.OrdinalIgnoreCase));
            suppressConfigSelectionChanged = false;
            UpdateConfigActionAvailability();
        }

        private void RefreshSubscriptions()
        {
            string? previousSelection = selectedSubscription?.Directory.FullName;
            subscriptionGroups = configHandler.GetAllGroups();

            suppressSubscriptionSelectionChanged = true;
            SubscriptionGroupList.ItemsSource = subscriptionGroups.Select(group => group.Directory.Name).ToList();

            int selectedIndex = subscriptionGroups.FindIndex(group =>
                string.Equals(group.Directory.FullName, previousSelection, StringComparison.OrdinalIgnoreCase));

            if (selectedIndex < 0)
                selectedIndex = FindSubscriptionGroupIndexForCurrentConfig();

            SubscriptionGroupList.SelectedIndex = selectedIndex;
            suppressSubscriptionSelectionChanged = false;

            if (selectedIndex >= 0 && selectedIndex < subscriptionGroups.Count)
            {
                selectedSubscription = subscriptionGroups[selectedIndex];
                PopulateSubscriptionEditor(selectedSubscription);
            }
            else
            {
                selectedSubscription = null;
                ClearSubscriptionEditor();
            }

            UpdateSubscriptionActionAvailability();

        }

        private List<ConfigListItem> BuildConfigList()
        {
            string configPrefix = Localize("Lang.Android.Server.ConfigItemPrefix");
            string subscriptionPrefix = Localize("Lang.Android.Server.SubscriptionItemPrefix");

            List<ConfigListItem> items = configHandler
                .GetAllGeneralConfigs()
                .Select(config => new ConfigListItem($"[{configPrefix}] {config.Name}", config))
                .ToList();

            foreach (Subscription group in configHandler.GetAllGroups())
            {
                foreach (Config config in configHandler.GetAllSubscriptionConfigs(group.Directory.FullName))
                {
                    items.Add(new ConfigListItem(
                        label: $"[{subscriptionPrefix}] {group.Directory.Name} / {config.Name}",
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

                    updateAvailable = updateStatus.SubCode == SubCode.UPDATE_AVAILABLE;

                    if (broadcastStatus.Code == Code.SUCCESS && broadcastStatus.Content is Broadcast broadcast)
                        broadcastMessage = broadcast.Text;

                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateRuntimeSummary();
                        string statusMessage = updateAvailable || !string.IsNullOrWhiteSpace(broadcastMessage)
                            ? BuildAvailabilityMessage()
                            : string.Empty;
                        SetAvailabilityInfo(statusMessage);
                    });
                }
                catch
                {
                }
            });
        }

        private bool TrySaveSettings(bool showSuccessMessage)
        {
            EnsureSettingsSectionInitialized();

            if (!TryParseProxyPort(ProxyPortInput.Text, out int proxyPort))
                return false;

            UserSettings current = settingsHandler.UserSettings;
            settingsHandler.UpdateUserSettings(new UserSettings
            {
                Language = current.GetLanguage(),
                Mode = Mode.PROXY,
                Protocol = ParseProtocol(ProtocolSelector.SelectedItem?.ToString(), current.GetProtocol()),
                LogLevel = current.GetLogLevel(),
                IsSystemProxyUse = false,
                IsUdpEnable = UdpEnabledToggle.IsChecked ?? current.GetUdpEnabled(),
                IsRunningAtStartup = false,
                IsStartHidden = false,
                IsAutoConnect = false,
                IsSendingAnalytics = AnalyticsToggle.IsChecked ?? current.GetSendingAnalyticsEnabled(),
                ProxyPort = proxyPort,
                TunPort = current.GetTunPort(),
                TestPort = current.GetTestPort(),
                TunIp = current.GetTunIp(),
                Dns = string.IsNullOrWhiteSpace(DnsInput.Text) ? current.GetDns() : DnsInput.Text.Trim(),
                LogPath = current.GetLogPath()
            });

            UpdateCurrentConfigSummary();
            UpdateRuntimeSummary();

            if (showSuccessMessage)
                SetStatus(Localize("Lang.Android.Status.SettingsSaved"));

            return true;
        }

        private bool TryParseProxyPort(string? text, out int value)
        {
            if (int.TryParse(text, out value) && value > 0)
                return true;

            SetStatus(Localize("Lang.Android.Status.InvalidProxyPort"));
            return false;
        }

        private void UpdateCurrentConfigSummary()
        {
            Config? currentConfig = configHandler.GetCurrentConfig();
            CurrentConfigNameText.Text = currentConfig == null
                ? Localize("Lang.Message.NoServerConfiguration")
                : currentConfig.Group == GroupType.SUBSCRIPTION
                    ? $"{Localize("Lang.Window.Server.Subscriptions")} / {currentConfig.Name}"
                    : currentConfig.Name;
        }

        private void UpdateRuntimeSummary()
        {
            if (!isSettingsSectionInitialized)
                return;

            UserSettings settings = settingsHandler.UserSettings;
            string currentConfigPath = settings.GetCurrentConfigPath();

            if (string.IsNullOrWhiteSpace(currentConfigPath) || !System.IO.File.Exists(currentConfigPath))
                currentConfigPath = Localize("Lang.Android.Runtime.NoneSelected");

            StringBuilder builder = new();
            builder.AppendLine($"{Localize("Lang.Android.Runtime.AppRoot")}: {InvisibleGorillaXRay.Values.Directory.ROOT}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.CurrentConfigPath")}: {currentConfigPath}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.ProxyListener")}: 127.0.0.1:{settings.GetProxyPort()}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.Protocol")}: {settings.GetProtocol()}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.Dns")}: {settings.GetDns()}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.Udp")}: {(settings.GetUdpEnabled() ? Localize("Lang.Android.Runtime.Enabled") : Localize("Lang.Android.Runtime.Disabled"))}");
            builder.Append(Localize("Lang.Android.Runtime.SystemProxyNotice"));

            if (!string.IsNullOrWhiteSpace(broadcastMessage))
            {
                builder.Append(Environment.NewLine)
                       .Append(Environment.NewLine)
                       .Append($"{Localize("Lang.Android.Runtime.Broadcast")}: {broadcastMessage}");
            }

            RuntimeText.Text = builder.ToString();
        }

        private string BuildAvailabilityMessage()
        {
            string message = Localize("Lang.Android.Home.ProxyAvailability");

            if (updateAvailable)
                return $"{Localize("Lang.Android.Home.UpdateAvailable")} {message}";

            return message;
        }

        private void SetAvailabilityInfo(string message)
        {
            string normalizedMessage = NormalizeStatusMessage(message);
            HomeInfoText.Text = normalizedMessage;
            HomeInfoPanelContainer.IsVisible = !string.IsNullOrWhiteSpace(normalizedMessage);
        }

        private void SetStatus(string message)
        {
            string normalizedMessage = NormalizeStatusMessage(message);
            SetStatusPanel(HomeStatusPanelContainer, HomeStatusText, normalizedMessage);
            SetStatusPanel(ServersStatusPanelContainer, ServersStatusText, normalizedMessage);
            SetStatusPanel(SettingsStatusPanelContainer, SettingsStatusText, normalizedMessage);
        }

        private static void SetStatusPanel(Border container, TextBlock textBlock, string message)
        {
            textBlock.Text = message;
            container.IsVisible = !string.IsNullOrWhiteSpace(message);
        }

        private void SetRunningState(bool isRunning)
        {
            RunActionButton.IsVisible = !isRunning;
            StopActionButton.IsVisible = isRunning;
        }

        private void SetConnectionState(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Starting:
                    ConnectionHeroGlowBorder.IsVisible = false;
                    StoppedHeroIconShape.IsVisible = true;
                    RunningHeroIconShape.IsVisible = false;
                    ConnectionStateIndicatorDot.Background = StartingBrush;
                    ConnectionStateTitleText.Text = Localize("Lang.Status.WaitForRun");
                    ConnectionStateSubtitleText.Text = Localize("Lang.Android.Home.Subtitle.Starting");
                    break;

                case ConnectionState.Running:
                    ConnectionHeroGlowBorder.IsVisible = true;
                    StoppedHeroIconShape.IsVisible = false;
                    RunningHeroIconShape.IsVisible = true;
                    ConnectionStateIndicatorDot.Background = RunningBrush;
                    ConnectionStateTitleText.Text = Localize("Lang.Status.Running");
                    ConnectionStateSubtitleText.Text = Localize("Lang.Android.Home.Subtitle.Running");
                    break;

                default:
                    ConnectionHeroGlowBorder.IsVisible = false;
                    StoppedHeroIconShape.IsVisible = true;
                    RunningHeroIconShape.IsVisible = false;
                    ConnectionStateIndicatorDot.Background = StoppedBrush;
                    ConnectionStateTitleText.Text = Localize("Lang.Status.Stopped");
                    ConnectionStateSubtitleText.Text = Localize("Lang.Android.Home.Subtitle.Stopped");
                    break;
            }
        }

        private void ShowSection(NavigationSection section)
        {
            HomeSectionScroll.IsVisible = section == NavigationSection.Home;
            ServersSectionScroll.IsVisible = section == NavigationSection.Servers;
            SettingsSectionScroll.IsVisible = section == NavigationSection.Settings;

            HomeNavButton.IsEnabled = section != NavigationSection.Home;
            ServersNavButton.IsEnabled = section != NavigationSection.Servers;
            SettingsNavButton.IsEnabled = section != NavigationSection.Settings;
        }

        private void ShowServerTab(ServerTab tab)
        {
            ConfigurationsContentPanel.IsVisible = tab == ServerTab.Configurations;
            SubscriptionsContentPanel.IsVisible = tab == ServerTab.Subscriptions;

            ConfigurationsTabNavButton.IsEnabled = tab != ServerTab.Configurations;
            SubscriptionsTabNavButton.IsEnabled = tab != ServerTab.Subscriptions;
        }

        private void SetAdvancedImportVisible(bool isVisible)
        {
            isShowingAdvancedImport = isVisible;
            AdvancedImportContainer.IsVisible = isVisible;
            AdvancedImportToggleActionButton.Content = isVisible
                ? Localize("Lang.Android.Server.HideAdvancedTools")
                : Localize("Lang.Android.Server.ShowAdvancedTools");
        }

        private void PopulateSubscriptionEditor(Subscription subscription)
        {
            SubscriptionRemarkInput.Text = subscription.Directory.Name;
            SubscriptionLinkInput.Text = subscription.Url;
        }

        private void ClearSubscriptionEditor()
        {
            SubscriptionRemarkInput.Text = string.Empty;
            SubscriptionLinkInput.Text = string.Empty;
        }

        private int FindSubscriptionGroupIndexForCurrentConfig()
        {
            string? currentDirectory = System.IO.Path.GetDirectoryName(settingsHandler.UserSettings.GetCurrentConfigPath());
            if (string.IsNullOrWhiteSpace(currentDirectory))
                return -1;

            return subscriptionGroups.FindIndex(group =>
                string.Equals(group.Directory.FullName, currentDirectory, StringComparison.OrdinalIgnoreCase));
        }

        private ConfigListItem? GetSelectedConfigItem()
        {
            int selectedIndex = ConfigList.SelectedIndex;
            return selectedIndex >= 0 && selectedIndex < configItems.Count
                ? configItems[selectedIndex]
                : null;
        }

        private void UpdateConfigActionAvailability()
        {
            bool hasSelectedConfig = GetSelectedConfigItem() != null;
            CheckSelectedConfigActionButton.IsEnabled = hasSelectedConfig && !isCheckWorkerBusy;
            ShareSelectedConfigActionButton.IsEnabled = hasSelectedConfig;
            DeleteSelectedConfigActionButton.IsEnabled = hasSelectedConfig;
        }

        private void UpdateSubscriptionActionAvailability()
        {
            bool hasSelectedSubscription = selectedSubscription != null;
            RefreshSubscriptionActionButton.IsEnabled = hasSelectedSubscription;
            ShareSubscriptionActionButton.IsEnabled = hasSelectedSubscription;
            DeleteSubscriptionActionButton.IsEnabled = hasSelectedSubscription;
        }

        private bool TrySelectConfigByPath(string path)
        {
            int index = configItems.FindIndex(item =>
                string.Equals(item.Config.Path, path, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                return false;

            suppressConfigSelectionChanged = true;
            ConfigList.SelectedIndex = index;
            suppressConfigSelectionChanged = false;

            settingsHandler.UpdateCurrentConfigPath(path);
            UpdateCurrentConfigSummary();
            UpdateRuntimeSummary();
            UpdateConfigActionAvailability();
            return true;
        }

        private bool TrySelectConfigByName(string name)
        {
            string normalizedName = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedName))
                return false;

            ConfigListItem? selectedItem = configItems.LastOrDefault(item =>
                string.Equals(item.Config.Name, normalizedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    System.IO.Path.GetFileNameWithoutExtension(item.Config.Name),
                    normalizedName,
                    StringComparison.OrdinalIgnoreCase));

            return selectedItem != null && TrySelectConfigByPath(selectedItem.Config.Path);
        }

        private void EnsureCurrentConfigPathIsValid()
        {
            if (configHandler.GetCurrentConfig() != null)
                return;

            settingsHandler.UpdateCurrentConfigPath(string.Empty);
            UpdateCurrentConfigSummary();
            UpdateRuntimeSummary();
        }

        private async void OnRunClick(object? sender, RoutedEventArgs e)
        {
            if (isRunWorkerBusy)
                return;

            if (!TrySaveSettings(showSuccessMessage: false))
                return;

            ShowSection(NavigationSection.Home);
            isRunWorkerBusy = true;
            SetRunningState(true);
            SetConnectionState(ConnectionState.Starting);
            SetStatus("Lang.Android.Status.LoadingConfig");

            await Task.Run(() =>
            {
                bool started = false;
                string? failureMessage = null;

                try
                {
                    Status configStatus = core.LoadConfig();
                    if (configStatus.Code == Code.ERROR)
                    {
                        failureMessage = configStatus.Content?.ToString() ?? "Lang.Message.NoConfig";
                        return;
                    }

                    Status modeStatus = core.EnableMode();
                    if (modeStatus.Code == Code.ERROR)
                    {
                        failureMessage = modeStatus.Content?.ToString() ?? "Lang.Message.CantProxy";
                        return;
                    }

                    if (modeStatus.Code == Code.INFO && modeStatus.SubCode == SubCode.CANCELED)
                    {
                        failureMessage = "Lang.Android.Status.StartCanceled";
                        return;
                    }

                    started = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        SetConnectionState(ConnectionState.Running);
                        SetStatus("Lang.Android.Status.RunningProxy");
                    });

                    core.Run(configStatus.Content?.ToString() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    failureMessage = MapExceptionToStatus(ex);
                }
                finally
                {
                    isRunWorkerBusy = false;

                    Dispatcher.UIThread.Post(() =>
                    {
                        SetRunningState(false);
                        SetConnectionState(ConnectionState.Stopped);
                        UpdateRuntimeSummary();

                        if (!string.IsNullOrWhiteSpace(failureMessage))
                            SetStatus(failureMessage);
                        else if (started)
                            SetStatus("Lang.Status.Stopped");
                    });
                }
            });
        }

        private void OnStopClick(object? sender, RoutedEventArgs e)
        {
            core.Stop();
            _ = Task.Run(() => core.DisableMode());
            SetStatus("Lang.Android.Status.StopRequested");
        }

        private void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
        {
            TrySaveSettings(showSuccessMessage: true);
        }

        private void OnHomeSectionClick(object? sender, RoutedEventArgs e)
        {
            ShowSection(NavigationSection.Home);
        }

        private void OnServersSectionClick(object? sender, RoutedEventArgs e)
        {
            ShowSection(NavigationSection.Servers);
            EnsureServersSectionInitialized();
        }

        private void OnSettingsSectionClick(object? sender, RoutedEventArgs e)
        {
            ShowSection(NavigationSection.Settings);
            EnsureSettingsSectionInitialized();
        }

        private void OnOpenReleasesClick(object? sender, RoutedEventArgs e)
        {
            OpenExternalUrl(InvisibleGorillaXRay.Values.Route.LATEST_RELEASE);
        }

        private void OnOpenGitHubClick(object? sender, RoutedEventArgs e)
        {
            OpenExternalUrl(InvisibleGorillaXRay.Values.Route.REPOSITORY);
        }

        private void OnConfigurationsTabClick(object? sender, RoutedEventArgs e)
        {
            ShowServerTab(ServerTab.Configurations);
        }

        private void OnSubscriptionsTabClick(object? sender, RoutedEventArgs e)
        {
            ShowServerTab(ServerTab.Subscriptions);
        }

        private void OnToggleAdvancedImportClick(object? sender, RoutedEventArgs e)
        {
            SetAdvancedImportVisible(!isShowingAdvancedImport);
        }

        private void OnConfigSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (suppressConfigSelectionChanged)
                return;

            ConfigListItem? selectedItem = GetSelectedConfigItem();
            UpdateConfigActionAvailability();
            if (selectedItem == null)
                return;

            Config selectedConfig = selectedItem.Config;
            settingsHandler.UpdateCurrentConfigPath(selectedConfig.Path);
            UpdateCurrentConfigSummary();
            UpdateRuntimeSummary();
            SetStatus(LocalizeFormat("Lang.Android.Status.SelectedConfig", selectedConfig.Name));
        }

        private void OnSubscriptionSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (suppressSubscriptionSelectionChanged)
                return;

            int selectedIndex = SubscriptionGroupList.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= subscriptionGroups.Count)
            {
                selectedSubscription = null;
                UpdateSubscriptionActionAvailability();
                return;
            }

            selectedSubscription = subscriptionGroups[selectedIndex];
            PopulateSubscriptionEditor(selectedSubscription);
            UpdateSubscriptionActionAvailability();
            SetStatus(LocalizeFormat("Lang.Android.Status.SelectedSubscription", selectedSubscription.Directory.Name));
        }

        private void OnImportConfigLinkClick(object? sender, RoutedEventArgs e)
        {
            TryImportConfigLink(ConfigLinkInput.Text, clearInputOnSuccess: true);
        }

        private bool TryImportConfigLink(string? link, bool clearInputOnSuccess)
        {
            string normalizedLink = link?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedLink))
            {
                SetStatus("Lang.Message.NoConfigLinkEntered");
                return false;
            }

            Status status = templateHandler.ConverLinkToConfig(normalizedLink);
            if (status.Code == Code.ERROR)
            {
                SetStatus(status.Content?.ToString() ?? "Lang.Message.UnsopportedConfigLink");
                return false;
            }

            string[] payload = status.Content as string[] ?? Array.Empty<string>();
            if (payload.Length < 2)
            {
                SetStatus("Lang.Android.Status.ConvertConfigFailed");
                return false;
            }

            configHandler.CreateConfig(payload[0], payload[1]);
            if (clearInputOnSuccess)
                ConfigLinkInput.Text = string.Empty;

            RefreshConfigs();
            TrySelectConfigByName(payload[0]);
            ShowSection(NavigationSection.Servers);
            ShowServerTab(ServerTab.Configurations);
            SetStatus(LocalizeFormat("Lang.Android.Status.ImportedConfig", payload[0]));
            return true;
        }

        private async void OnImportConfigFileClick(object? sender, RoutedEventArgs e)
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null || !topLevel.StorageProvider.CanOpen)
            {
                SetStatus("Lang.Android.Status.FilePickerUnavailable");
                return;
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localize("Lang.Window.Server.AddConfig.FromFile"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(Localize("Lang.Window.Server.AddConfig.FromFile"))
                    {
                        Patterns = ["*.json", "*.toml", "*.yaml", "*.yml"]
                    },
                    new FilePickerFileType("All files")
                    {
                        Patterns = ["*.*"]
                    }
                ]
            });

            if (files.Count == 0)
                return;

            try
            {
                await using var stream = await files[0].OpenReadAsync();
                using StreamReader reader = new(stream);
                string content = await reader.ReadToEndAsync();
                TryImportConfigFileContent(files[0].Name, content);
            }
            catch (Exception ex)
            {
                SetStatus(MapExceptionToStatus(ex));
            }
        }

        private bool TryImportConfigFileContent(string fileName, string? content)
        {
            string normalizedContent = content?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedContent))
            {
                SetStatus("Lang.Message.InvalidConfig");
                return false;
            }

            string extension = System.IO.Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".json";

            string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");

            try
            {
                string normalizedConfig = normalizedContent;
                if (!JsonUtility.IsJsonValid(normalizedContent))
                {
                    System.IO.File.WriteAllText(tempPath, normalizedContent);
                    Status loadStatus = core.LoadConfig(tempPath);
                    if (loadStatus.Code == Code.ERROR)
                    {
                        SetStatus(loadStatus.Content?.ToString() ?? "Lang.Message.InvalidConfig");
                        return false;
                    }

                    normalizedConfig = loadStatus.Content?.ToString() ?? normalizedContent;
                }

                string remark = FileUtility.GetValidFileName(System.IO.Path.GetFileNameWithoutExtension(fileName));
                if (string.IsNullOrWhiteSpace(remark))
                    remark = "imported-config";

                configHandler.CreateConfig(remark, normalizedConfig);
                RefreshConfigs();
                TrySelectConfigByName(remark);
                ShowSection(NavigationSection.Servers);
                ShowServerTab(ServerTab.Configurations);
                SetStatus(LocalizeFormat("Lang.Android.Status.ImportedConfigFile", remark));
                return true;
            }
            catch (Exception ex)
            {
                SetStatus(MapExceptionToStatus(ex));
                return false;
            }
            finally
            {
                try
                {
                    if (System.IO.File.Exists(tempPath))
                        System.IO.File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        private void OnImportSubscriptionClick(object? sender, RoutedEventArgs e)
        {
            TryImportSubscription(SubscriptionRemarkInput.Text, SubscriptionLinkInput.Text);
        }

        private bool TryImportSubscription(string? remark, string? link)
        {
            string normalizedRemark = remark?.Trim() ?? string.Empty;
            string normalizedLink = link?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(normalizedRemark))
            {
                SetStatus("Lang.Message.NoSubscriptionRemarksEntered");
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalizedLink))
            {
                SetStatus("Lang.Message.NoSubscriptionLinkEntered");
                return false;
            }

            Status status = templateHandler.ConvertLinkToSubscription(normalizedRemark, normalizedLink);
            if (status.Code == Code.ERROR)
            {
                SetStatus(status.Content?.ToString() ?? "Lang.Message.UnsupportedSubscriptionLink");
                return false;
            }

            string[] payload = status.Content as string[] ?? Array.Empty<string>();
            if (payload.Length < 2)
            {
                SetStatus("Lang.Android.Status.ImportSubscriptionFailed");
                return false;
            }

            configHandler.CreateSubscription(payload[0], normalizedLink, payload[1]);
            RefreshSubscriptions();
            RefreshConfigs();

            string groupPath = System.IO.Path.Combine(InvisibleGorillaXRay.Values.Directory.CONFIGS, payload[0]);
            Config? firstConfig = configHandler.GetAllSubscriptionConfigs(groupPath).FirstOrDefault();
            if (firstConfig != null)
            {
                RefreshConfigs();
                TrySelectConfigByPath(firstConfig.Path);
            }

            ShowServerTab(ServerTab.Subscriptions);
            SetStatus(LocalizeFormat("Lang.Android.Status.SavedSubscription", payload[0]));
            return true;
        }

        private async void OnCheckSelectedConfigClick(object? sender, RoutedEventArgs e)
        {
            if (isCheckWorkerBusy)
                return;

            ConfigListItem? selectedItem = GetSelectedConfigItem();
            if (selectedItem == null)
            {
                SetStatus("Lang.Android.Status.SelectConfigFirst");
                return;
            }

            isCheckWorkerBusy = true;
            UpdateConfigActionAvailability();
            SetStatus(LocalizeFormat("Lang.Android.Status.CheckingConfig", selectedItem.Config.Name));

            try
            {
                await Task.Run(async () =>
                {
                    string configContent = System.IO.File.ReadAllText(selectedItem.Config.Path);
                    if (!TryExtractOutboundEndpoint(configContent, out string host, out int port))
                    {
                        Dispatcher.UIThread.Post(() => SetStatus("Lang.Android.Status.CheckConfigUnsupported"));
                        return;
                    }

                    using TcpClient tcpClient = new();
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    Task connectTask = tcpClient.ConnectAsync(host, port);
                    Task completedTask = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(5)));

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (completedTask == connectTask && tcpClient.Connected)
                        {
                            stopwatch.Stop();
                            SetStatus(LocalizeFormat(
                                "Lang.Android.Status.CheckedConfigSuccess",
                                selectedItem.Config.Name,
                                stopwatch.ElapsedMilliseconds));
                        }
                        else
                        {
                            SetStatus(LocalizeFormat("Lang.Android.Status.CheckedConfigTimeout", selectedItem.Config.Name));
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                SetStatus(MapExceptionToStatus(ex));
            }
            finally
            {
                isCheckWorkerBusy = false;
                UpdateConfigActionAvailability();
            }
        }

        private void OnShareSelectedConfigClick(object? sender, RoutedEventArgs e)
        {
            ConfigListItem? selectedItem = GetSelectedConfigItem();
            if (selectedItem == null)
            {
                SetStatus("Lang.Android.Status.SelectConfigFirst");
                return;
            }

            if (!System.IO.File.Exists(selectedItem.Config.Path))
            {
                SetStatus("Lang.Message.FileDoesntExists");
                return;
            }

            string configContent = System.IO.File.ReadAllText(selectedItem.Config.Path).Trim();
            if (string.IsNullOrWhiteSpace(configContent))
            {
                SetStatus("Lang.Message.InvalidConfig");
                return;
            }

            ShareText(configContent, selectedItem.Config.Name);
            SetStatus(LocalizeFormat("Lang.Android.Status.SharedConfig", selectedItem.Config.Name));
        }

        private void OnDeleteSelectedConfigClick(object? sender, RoutedEventArgs e)
        {
            ConfigListItem? selectedItem = GetSelectedConfigItem();
            if (selectedItem == null)
            {
                SetStatus("Lang.Android.Status.SelectConfigFirst");
                return;
            }

            DeleteSelectedConfig(selectedItem.Config);
        }

        private void DeleteSelectedConfig(Config config)
        {
            bool deletedCurrentConfig = string.Equals(
                settingsHandler.UserSettings.GetCurrentConfigPath(),
                config.Path,
                StringComparison.OrdinalIgnoreCase);

            try
            {
                System.IO.File.Delete(config.Path);
                CleanupEmptySubscriptionDirectory(config);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
                return;
            }

            RefreshConfigs();
            RefreshSubscriptions();

            if (deletedCurrentConfig)
            {
                if (configItems.Count > 0)
                    TrySelectConfigByPath(configItems.Last().Config.Path);
                else
                    settingsHandler.UpdateCurrentConfigPath(string.Empty);
            }

            EnsureCurrentConfigPathIsValid();
            UpdateCurrentConfigSummary();
            UpdateRuntimeSummary();
            SetStatus(LocalizeFormat("Lang.Android.Status.DeletedConfig", config.Name));
        }

        private static void CleanupEmptySubscriptionDirectory(Config config)
        {
            if (config.Group != GroupType.SUBSCRIPTION)
                return;

            string? directoryPath = System.IO.Path.GetDirectoryName(config.Path);
            if (string.IsNullOrWhiteSpace(directoryPath) || !System.IO.Directory.Exists(directoryPath))
                return;

            bool hasRemainingConfigs = System.IO.Directory
                .EnumerateFiles(directoryPath)
                .Any(filePath => !filePath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));

            if (!hasRemainingConfigs)
                System.IO.Directory.Delete(directoryPath, true);
        }

        private void OnShareSubscriptionClick(object? sender, RoutedEventArgs e)
        {
            if (selectedSubscription == null)
            {
                SetStatus("Lang.Android.Status.SelectSubscriptionFirst");
                return;
            }

            ShareText(selectedSubscription.Url, selectedSubscription.Directory.Name);
            SetStatus(LocalizeFormat("Lang.Android.Status.SharedSubscription", selectedSubscription.Directory.Name));
        }

        private void OnRefreshSubscriptionClick(object? sender, RoutedEventArgs e)
        {
            if (selectedSubscription == null)
            {
                SetStatus("Lang.Android.Status.SelectSubscriptionFirst");
                return;
            }

            Status status = templateHandler.ConvertLinkToSubscription(
                selectedSubscription.Directory.Name,
                selectedSubscription.Url);

            if (status.Code == Code.ERROR)
            {
                SetStatus(status.Content?.ToString() ?? "Lang.Android.Status.RefreshSubscriptionFailed");
                return;
            }

            string[] payload = status.Content as string[] ?? Array.Empty<string>();
            if (payload.Length < 2)
            {
                SetStatus("Lang.Android.Status.RefreshSubscriptionFailed");
                return;
            }

            configHandler.CreateSubscription(payload[0], selectedSubscription.Url, payload[1]);
            RefreshSubscriptions();
            RefreshConfigs();
            SetStatus(LocalizeFormat("Lang.Android.Status.RefreshedSubscription", payload[0]));
        }

        private void OnDeleteSubscriptionClick(object? sender, RoutedEventArgs e)
        {
            if (selectedSubscription == null)
            {
                SetStatus("Lang.Android.Status.SelectSubscriptionFirst");
                return;
            }

            bool deletedCurrentConfig = settingsHandler.UserSettings
                .GetCurrentConfigPath()
                .StartsWith(selectedSubscription.Directory.FullName, StringComparison.OrdinalIgnoreCase);

            string deletedName = selectedSubscription.Directory.Name;
            configHandler.DeleteSubscription(selectedSubscription);
            selectedSubscription = null;

            if (deletedCurrentConfig)
                settingsHandler.UpdateCurrentConfigPath(string.Empty);

            RefreshSubscriptions();
            RefreshConfigs();
            EnsureCurrentConfigPathIsValid();
            SetStatus(LocalizeFormat("Lang.Android.Status.DeletedSubscription", deletedName));
        }

        private void OnSaveRawConfigClick(object? sender, RoutedEventArgs e)
        {
            string remark = ConfigRemarkInput.Text?.Trim() ?? string.Empty;
            string rawConfig = RawConfigInput.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(remark))
            {
                SetStatus("Lang.Android.Status.EnterConfigName");
                return;
            }

            if (!JsonUtility.IsJsonValid(rawConfig))
            {
                SetStatus("Lang.Android.Status.RawConfigInvalidJson");
                return;
            }

            configHandler.CreateConfig(remark, rawConfig);
            ConfigRemarkInput.Text = string.Empty;
            RawConfigInput.Text = string.Empty;
            RefreshConfigs();
            TrySelectConfigByName(remark);
            SetStatus(LocalizeFormat("Lang.Android.Status.SavedRawConfig", remark));
        }

        private void HandlePendingImport(AndroidPendingImport pendingImport)
        {
            ShowSection(NavigationSection.Servers);
            EnsureServersSectionInitialized();

            switch (pendingImport.Kind)
            {
                case AndroidImportKind.ConfigLink:
                    ShowServerTab(ServerTab.Configurations);
                    TryImportConfigLink(pendingImport.Value, clearInputOnSuccess: false);
                    break;

                case AndroidImportKind.SubscriptionLink:
                    ShowServerTab(ServerTab.Subscriptions);
                    TryImportSubscription(BuildSubscriptionRemarkFromLink(pendingImport.Value), pendingImport.Value);
                    break;

                case AndroidImportKind.ConfigFile:
                    ShowServerTab(ServerTab.Configurations);
                    TryImportConfigFileContent(
                        fileName: pendingImport.DisplayName ?? "imported-config.json",
                        content: pendingImport.Value);
                    break;
            }
        }

        private static string BuildSubscriptionRemarkFromLink(string link)
        {
            if (Uri.TryCreate(link, UriKind.Absolute, out Uri? uri))
            {
                string hostRemark = FileUtility.GetValidFileName(uri.Host);
                if (!string.IsNullOrWhiteSpace(hostRemark))
                    return hostRemark;
            }

            return "imported-subscription";
        }

        private string MapExceptionToStatus(Exception exception)
        {
            return exception switch
            {
                DllNotFoundException => Localize("Lang.Android.Status.NativeRuntimeUnavailable"),
                EntryPointNotFoundException => Localize("Lang.Android.Status.NativeRuntimeUnavailable"),
                _ => exception.Message
            };
        }

        private static bool TryExtractOutboundEndpoint(string configContent, out string host, out int port)
        {
            host = string.Empty;
            port = 0;

            if (string.IsNullOrWhiteSpace(configContent))
                return false;

            using JsonDocument document = JsonDocument.Parse(configContent);
            if (!document.RootElement.TryGetProperty("outbounds", out JsonElement outbounds)
                || outbounds.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement outbound in outbounds.EnumerateArray())
            {
                if (TryExtractFromVnext(outbound, out host, out port))
                    return true;

                if (TryExtractFromServers(outbound, out host, out port))
                    return true;
            }

            return false;

            static bool TryExtractFromVnext(JsonElement outbound, out string extractedHost, out int extractedPort)
            {
                extractedHost = string.Empty;
                extractedPort = 0;

                if (!outbound.TryGetProperty("settings", out JsonElement settings)
                    || !settings.TryGetProperty("vnext", out JsonElement vnext)
                    || vnext.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                foreach (JsonElement endpoint in vnext.EnumerateArray())
                {
                    if (TryReadEndpoint(endpoint, out extractedHost, out extractedPort))
                        return true;
                }

                return false;
            }

            static bool TryExtractFromServers(JsonElement outbound, out string extractedHost, out int extractedPort)
            {
                extractedHost = string.Empty;
                extractedPort = 0;

                if (!outbound.TryGetProperty("settings", out JsonElement settings)
                    || !settings.TryGetProperty("servers", out JsonElement servers)
                    || servers.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                foreach (JsonElement endpoint in servers.EnumerateArray())
                {
                    if (TryReadEndpoint(endpoint, out extractedHost, out extractedPort))
                        return true;
                }

                return false;
            }

            static bool TryReadEndpoint(JsonElement endpoint, out string extractedHost, out int extractedPort)
            {
                extractedHost = string.Empty;
                extractedPort = 0;

                if (!endpoint.TryGetProperty("address", out JsonElement addressElement))
                    return false;

                string? address = addressElement.GetString();
                if (string.IsNullOrWhiteSpace(address))
                    return false;

                if (!endpoint.TryGetProperty("port", out JsonElement portElement))
                    return false;

                int parsedPort = portElement.ValueKind switch
                {
                    JsonValueKind.Number when portElement.TryGetInt32(out int numericPort) => numericPort,
                    JsonValueKind.String when int.TryParse(portElement.GetString(), out int stringPort) => stringPort,
                    _ => 0
                };

                if (parsedPort <= 0)
                    return false;

                extractedHost = address;
                extractedPort = parsedPort;
                return true;
            }
        }

        private static Protocol ParseProtocol(string? selectedValue, Protocol fallback)
        {
            return Enum.TryParse(selectedValue, ignoreCase: true, out Protocol parsed)
                ? parsed
                : fallback;
        }

        private string NormalizeStatusMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            return message.StartsWith("Lang.", StringComparison.Ordinal)
                ? Localize(message)
                : message;
        }

        private static void ShareText(string text, string title)
        {
            try
            {
                if (Application.Context?.GetSystemService(Context.ClipboardService) is ClipboardManager clipboardManager)
                    clipboardManager.PrimaryClip = ClipData.NewPlainText(title, text);

                Intent shareIntent = new Intent(Intent.ActionSend);
                shareIntent.SetType("text/plain");
                shareIntent.PutExtra(Intent.ExtraText, text);
                shareIntent.PutExtra(Intent.ExtraTitle, title);

                Intent? chooserIntent = Intent.CreateChooser(shareIntent, title);
                if (chooserIntent == null)
                    return;

                chooserIntent.AddFlags(ActivityFlags.NewTask);
                Application.Context?.StartActivity(chooserIntent);
            }
            catch
            {
            }
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Intent intent = new Intent(Intent.ActionView);
                intent.SetData(global::Android.Net.Uri.Parse(url));
                intent.AddFlags(ActivityFlags.NewTask);
                Application.Context?.StartActivity(intent);
            }
            catch
            {
            }
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
