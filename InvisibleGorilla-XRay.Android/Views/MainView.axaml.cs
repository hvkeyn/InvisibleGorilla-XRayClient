using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace InvisibleGorillaXRay.Android.Views
{
    using InvisibleGorillaXRay.Android.Handlers.DeepLinks;
    using InvisibleGorillaXRay.Android.Managers;
    using InvisibleGorillaXRay.Android.Services;
    using InvisibleGorillaXRay.Core;
    using InvisibleGorillaXRay.Handlers;
    using InvisibleGorillaXRay.Handlers.SmartInput;
    using InvisibleGorillaXRay.Handlers.Tor;
    using InvisibleGorillaXRay.Models;
    using InvisibleGorillaXRay.Services;
    using InvisibleGorillaXRay.Services.Goida;
    using InvisibleGorillaXRay.Utilities;
    using InvisibleGorillaXRay.Values;

    public partial class MainView : UserControl
    {
        private enum NavigationSection { Home, Servers, Settings }
        private enum ServerTab { Configurations, Subscriptions }
        private enum ConnectionState { Stopped, Starting, Running }
        private enum ServersViewMode { Browse, AddConfig, AddSubscription }
        private enum ConfigImportMode { File, Link }

        private sealed class TemplateComboItem
        {
            public string Id { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;

            public override string ToString() => Name;
        }

        private sealed class GoidaNodeRow
        {
            public int ListId { get; init; }
            public string Id { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string LatencyText { get; init; } = string.Empty;
            public string ActiveMark { get; init; } = string.Empty;
        }

        private static readonly IBrush StoppedBrush = new SolidColorBrush(Color.Parse("#D66A75"));
        private static readonly IBrush StartingBrush = new SolidColorBrush(Color.Parse("#C9A227"));
        private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#56B870"));
        private static readonly IBrush SelectedConfigBrush = new SolidColorBrush(Color.Parse("#343434"));
        private static readonly IBrush IdleConfigBrush = new SolidColorBrush(Color.Parse("#252526"));
        private static readonly IBrush SelectedMarkerBrush = new SolidColorBrush(Color.Parse("#6DCC8E"));
        private static readonly IBrush IdleMarkerBrush = new SolidColorBrush(Color.Parse("#5A5A5A"));
        private static readonly IBrush AvailabilityPendingBrush = new SolidColorBrush(Color.Parse("#8C8C8C"));
        private static readonly IBrush AvailabilityErrorBrush = new SolidColorBrush(Color.Parse("#D95F5F"));
        private static readonly IBrush AvailabilitySuccessBrush = new SolidColorBrush(Color.Parse("#6DCC8E"));

        private readonly ConnectionInfoService connectionInfoService = new();
        private InvisibleGorillaXRay.Core.InvisibleGorillaXRayCore core = null!;
        private SettingsHandler settingsHandler = null!;
        private ConfigHandler configHandler = null!;
        private TemplateHandler templateHandler = null!;
        private UpdateHandler updateHandler = null!;
        private BroadcastHandler broadcastHandler = null!;
        private Android.Handlers.AndroidLocalizationHandler localizationHandler = null!;
        private GoidaProfileHandler goidaHandler = null!;
        private bool isApplyingGoidaSettings;

        private List<Config> generalConfigs = new();
        private List<Config> subscriptionConfigs = new();
        private List<Subscription> subscriptionGroups = new();
        private List<AndroidInstalledAppInfo> discoveredAndroidApps = new();
        private Subscription? selectedSubscription;
        private readonly Dictionary<string, int> configAvailability = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CheckBox> appRuleToggles = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<AppRuleTemplate> workingAppRuleTemplates = new();
        private readonly List<AppRuleTemplateBinding> workingAppRuleBindings = new();
        private AppRuleTemplate workingDefaultAppRuleTemplate = new();
        private string activeAppRulesTemplateId = AppRuleTemplate.DefaultTemplateId;
        private bool isApplyingAppRulesEditor;
        private bool isCheckWorkerBusy;
        private bool isRunWorkerBusy;
        private bool isStopWorkerBusy;
        private bool isInitialized;
        private bool isShowingAdvancedImport;
        private bool isServersSectionInitialized;
        private bool isSettingsSectionInitialized;
        private bool isSettingsLoadedIntoControls;
        private bool suppressSubscriptionSelectionChanged;
        private bool updateAvailable;
        private string latestRemoteVersion = string.Empty;
        private string? broadcastMessage;
        private Config? pendingConfigShare;
        private ServersViewMode currentServersViewMode;
        private ConfigImportMode currentConfigImportMode;
        private IStorageFile? pendingConfigImportFile;
        private bool isCheckingForUpdate;
        private bool isDownloadingUpdate;
        private UpdateInfo? pendingUpdateInfo;
        private ReleaseAsset? pendingUpdateAsset;
        private string? pendingUpdateLocalApkPath;
        private DispatcherTimer? connectionInfoTimer;
        private CancellationTokenSource? connectionInfoLookupCancellation;
        private string baselineIp = string.Empty;
        private bool isConnectionInfoConnected;
        private bool isConnectionInfoTor;

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
            goidaHandler = appManager.HandlersManager.GetHandler<GoidaProfileHandler>();
            DiagnosticLog.Write("MainView", "Handlers resolved");

            GoidaActiveNodeBridge.OnActiveNodeChanged = HandleGoidaActiveNodeChanged;
            goidaHandler.Manager.NodesUpdated += OnGoidaNodesUpdated;
            InitializeGoidaControls();

            InitializeControls();
            ApplyLocalizedText();
            AndroidDeepLinkDispatcher.Register(HandlePendingImport);
            DiagnosticLog.Write("MainView", "Controls initialized");
            UpdateCurrentConfigSummary();
            DiagnosticLog.Write("MainView", "Current config summary updated");
            SetConnectionState(ConnectionState.Stopped);
            InitializeConnectionInfo();
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
        private Button SettingsNavButton => GetRequiredControl<Button>("SettingsSectionButton");
        private Ellipse UpdateNotificationIndicator => GetRequiredControl<Ellipse>("UpdateNotificationDot");
        private Button ConfigurationsTabNavButton => GetRequiredControl<Button>("ConfigurationsTabButton");
        private Button SubscriptionsTabNavButton => GetRequiredControl<Button>("SubscriptionsTabButton");
        private StackPanel ServersBrowseContainer => GetRequiredControl<StackPanel>("ServersBrowsePanel");
        private StackPanel ConfigurationsContentPanel => GetRequiredControl<StackPanel>("ConfigurationsPanel");
        private StackPanel SubscriptionsContentPanel => GetRequiredControl<StackPanel>("SubscriptionsPanel");
        private StackPanel AddConfigContainer => GetRequiredControl<StackPanel>("AddConfigPanel");
        private StackPanel AddSubscriptionContainer => GetRequiredControl<StackPanel>("AddSubscriptionPanel");
        private Border ConnectionHeroGlowBorder => GetRequiredControl<Border>("ConnectionHeroGlow");
        private Grid StoppedHeroIconShape => GetRequiredControl<Grid>("StoppedHeroIcon");
        private Grid RunningHeroIconShape => GetRequiredControl<Grid>("RunningHeroIcon");
        private Border ConnectionStateIndicatorDot => GetRequiredControl<Border>("ConnectionStateIndicator");
        private TextBlock ConnectionStateTitleText => GetRequiredControl<TextBlock>("ConnectionStateTitleTextBlock");
        private TextBlock ConnectionStateSubtitleText => GetRequiredControl<TextBlock>("ConnectionStateSubtitleTextBlock");
        private TextBlock CurrentConfigNameText => GetRequiredControl<TextBlock>("CurrentConfigNameTextBlock");
        private TextBlock CurrentConfigAppRulesSummaryText => GetRequiredControl<TextBlock>("CurrentConfigAppRulesSummaryTextBlock");
        private TextBlock CurrentConfigAppRulesButtonText => GetRequiredControl<TextBlock>("CurrentConfigAppRulesButtonTextBlock");
        private TextBlock ManageServerConfigurationText => GetRequiredControl<TextBlock>("ManageServerConfigurationTextBlock");
        private Border HomeStatusPanelContainer => GetRequiredControl<Border>("HomeStatusPanel");
        private TextBlock HomeStatusText => GetRequiredControl<TextBlock>("HomeStatusTextBlock");
        private Border HomeInfoPanelContainer => GetRequiredControl<Border>("HomeInfoPanel");
        private TextBlock HomeInfoText => GetRequiredControl<TextBlock>("HomeInfoTextBlock");
        private Border ConnectionInfoDotControl => GetRequiredControl<Border>("ConnectionInfoDot");
        private TextBlock ConnectionInfoTitleText => GetRequiredControl<TextBlock>("ConnectionInfoTitleTextBlock");
        private TextBlock ConnectionInfoIpText => GetRequiredControl<TextBlock>("ConnectionInfoIpTextBlock");
        private TextBlock ConnectionInfoLocationText => GetRequiredControl<TextBlock>("ConnectionInfoLocationTextBlock");
        private TextBlock ConnectionInfoOrgText => GetRequiredControl<TextBlock>("ConnectionInfoOrgTextBlock");
        private TextBlock ConnectionInfoModeText => GetRequiredControl<TextBlock>("ConnectionInfoModeTextBlock");
        private TextBlock ConnectionInfoVerdictText => GetRequiredControl<TextBlock>("ConnectionInfoVerdictTextBlock");
        private Border ServersStatusPanelContainer => GetRequiredControl<Border>("ServersStatusPanel");
        private TextBlock ServersStatusText => GetRequiredControl<TextBlock>("ServersStatusTextBlock");
        private TextBlock ServersTitleText => GetRequiredControl<TextBlock>("ServersTitleTextBlock");
        private TextBlock ServersDescriptionText => GetRequiredControl<TextBlock>("ServersDescriptionTextBlock");
        private Border ConfigShareActionSheet => GetRequiredControl<Border>("ConfigShareActionSheetBorder");
        private TextBlock ConfigShareTitleText => GetRequiredControl<TextBlock>("ConfigShareTitleLabel");
        private Button CopyConfigLinkActionButton => GetRequiredControl<Button>("CopyConfigLinkSheetButton");
        private Button ExportConfigActionButton => GetRequiredControl<Button>("ExportConfigSheetButton");
        private Button CancelConfigShareActionButton => GetRequiredControl<Button>("CancelConfigShareSheetButton");
        private TextBlock AvailableConfigurationsText => GetRequiredControl<TextBlock>("AvailableConfigurationsTextBlock");
        private StackPanel GeneralConfigsItemsHost => GetRequiredControl<StackPanel>("GeneralConfigsListPanel");
        private TextBlock NoGeneralConfigsText => GetRequiredControl<TextBlock>("NoGeneralConfigsTextBlock");
        private TextBlock SubscriptionGroupsTitleText => GetRequiredControl<TextBlock>("SubscriptionGroupsTitleTextBlock");
        private ComboBox SubscriptionGroupSelector => GetRequiredControl<ComboBox>("SubscriptionGroupComboBox");
        private StackPanel SubscriptionConfigsItemsHost => GetRequiredControl<StackPanel>("SubscriptionConfigsListPanel");
        private TextBlock NoSubscriptionConfigsText => GetRequiredControl<TextBlock>("NoSubscriptionConfigsTextBlock");
        private TextBlock AddConfigTitleText => GetRequiredControl<TextBlock>("AddConfigTitleTextBlock");
        private TextBlock AddConfigDescriptionText => GetRequiredControl<TextBlock>("AddConfigDescriptionTextBlock");
        private Button ConfigImportFileModeActionButton => GetRequiredControl<Button>("ConfigImportFileModeButton");
        private Button ConfigImportLinkModeActionButton => GetRequiredControl<Button>("ConfigImportLinkModeButton");
        private Border ConfigFileImportContainer => GetRequiredControl<Border>("ConfigFileImportPanel");
        private Border ConfigLinkImportContainer => GetRequiredControl<Border>("ConfigLinkImportPanel");
        private TextBlock ConfigFileImportTitleText => GetRequiredControl<TextBlock>("ConfigFileImportTitleTextBlock");
        private Button ChooseConfigFileActionButton => GetRequiredControl<Button>("ChooseConfigFileButton");
        private TextBlock SelectedConfigFileText => GetRequiredControl<TextBlock>("SelectedConfigFileTextBlock");
        private TextBlock ImportConfigTitleText => GetRequiredControl<TextBlock>("ImportConfigTitleTextBlock");
        private Button PasteConfigLinkActionButton => GetRequiredControl<Button>("PasteConfigLinkButton");
        private Button ConfirmConfigImportActionButton => GetRequiredControl<Button>("ConfirmConfigImportButton");
        private Button CancelConfigImportActionButton => GetRequiredControl<Button>("CancelConfigImportButton");
        private TextBlock RuntimeText => GetRequiredControl<TextBlock>("RuntimeTextBlock");
        private Button RunActionButton => GetRequiredControl<Button>("RunButton");
        private Button StopActionButton => GetRequiredControl<Button>("StopButton");
        private Button AdvancedImportToggleActionButton => GetRequiredControl<Button>("AdvancedImportToggleButton");
        private Border AdvancedImportContainer => GetRequiredControl<Border>("AdvancedImportPanel");
        private TextBlock AdvancedImportTitleText => GetRequiredControl<TextBlock>("AdvancedImportTitleTextBlock");
        private TextBlock AdvancedImportDescriptionText => GetRequiredControl<TextBlock>("AdvancedImportDescriptionTextBlock");
        private Button SaveRawConfigActionButton => GetRequiredControl<Button>("SaveRawConfigButton");
        private TextBlock AddSubscriptionTitleText => GetRequiredControl<TextBlock>("AddSubscriptionTitleTextBlock");
        private TextBlock AddSubscriptionDescriptionText => GetRequiredControl<TextBlock>("AddSubscriptionDescriptionTextBlock");
        private Button RefreshSubscriptionActionButton => GetRequiredControl<Button>("RefreshSubscriptionButton");
        private Button ShareSubscriptionActionButton => GetRequiredControl<Button>("ShareSubscriptionButton");
        private Button DeleteSubscriptionActionButton => GetRequiredControl<Button>("DeleteSubscriptionButton");
        private Button CancelSubscriptionActionButton => GetRequiredControl<Button>("CancelSubscriptionButton");
        private Button SaveSubscriptionActionButton => GetRequiredControl<Button>("SaveSubscriptionButton");
        private Button PasteSubscriptionLinkActionButton => GetRequiredControl<Button>("PasteSubscriptionLinkButton");
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
        private TextBlock AppRulesTitleText => GetRequiredControl<TextBlock>("AppRulesTitleTextBlock");
        private TextBlock AppRulesDescriptionText => GetRequiredControl<TextBlock>("AppRulesDescriptionTextBlock");
        private TextBlock AppRulesSummaryText => GetRequiredControl<TextBlock>("AppRulesSummaryTextBlock");
        private TextBlock AppRulesConfigHintText => GetRequiredControl<TextBlock>("AppRulesConfigHintTextBlock");
        private TextBlock OpenAppRulesEditorButtonText => GetRequiredControl<TextBlock>("OpenAppRulesEditorButtonTextBlock");
        private TextBlock NoDiscoveredAppsText => GetRequiredControl<TextBlock>("NoDiscoveredAppsTextBlock");
        private TextBlock TunTitleText => GetRequiredControl<TextBlock>("TunTitleTextBlock");
        private TextBlock TunDescriptionText => GetRequiredControl<TextBlock>("TunDescriptionTextBlock");
        private TextBlock LogsAndDiagnosticsTitleText => GetRequiredControl<TextBlock>("LogsAndDiagnosticsTitleTextBlock");
        private TextBlock LogsDescriptionText => GetRequiredControl<TextBlock>("LogsDescriptionTextBlock");
        private TextBlock LogsStatusText => GetRequiredControl<TextBlock>("LogsStatusTextBlock");
        private Button ShareLogActionButton => GetRequiredControl<Button>("ShareLogButton");
        private TextBlock ShareLogButtonText => GetRequiredControl<TextBlock>("ShareLogButtonTextBlock");
        private Button SaveLogActionButton => GetRequiredControl<Button>("SaveLogButton");
        private TextBlock SaveLogButtonText => GetRequiredControl<TextBlock>("SaveLogButtonTextBlock");
        private Button ClearLogActionButton => GetRequiredControl<Button>("ClearLogButton");
        private TextBlock ClearLogButtonText => GetRequiredControl<TextBlock>("ClearLogButtonTextBlock");
        private TextBlock UpdatesSectionTitleText => GetRequiredControl<TextBlock>("UpdatesSectionTitleTextBlock");
        private TextBlock UpdatesCurrentVersionText => GetRequiredControl<TextBlock>("UpdatesCurrentVersionTextBlock");
        private TextBlock UpdatesStatusText => GetRequiredControl<TextBlock>("UpdatesStatusTextBlock");
        private global::Avalonia.Controls.ProgressBar UpdatesProgressIndicator => GetRequiredControl<global::Avalonia.Controls.ProgressBar>("UpdatesProgressBar");
        private Button CheckForUpdateActionButton => GetRequiredControl<Button>("CheckForUpdateButton");
        private TextBlock CheckForUpdateActionButtonText => GetRequiredControl<TextBlock>("CheckForUpdateButtonTextBlock");
        private Button InstallUpdateActionButton => GetRequiredControl<Button>("InstallUpdateButton");
        private TextBlock InstallUpdateActionButtonText => GetRequiredControl<TextBlock>("InstallUpdateButtonTextBlock");
        private Button OpenReleasePageActionButton => GetRequiredControl<Button>("OpenReleasePageButton");
        private TextBlock OpenReleasePageActionButtonText => GetRequiredControl<TextBlock>("OpenReleasePageButtonTextBlock");
        private ComboBox ProtocolSelector => GetRequiredControl<ComboBox>("ProtocolComboBox");
        private TextBox ProxyPortInput => GetRequiredControl<TextBox>("ProxyPortTextBox");
        private TextBox DnsInput => GetRequiredControl<TextBox>("DnsTextBox");
        private CheckBox UdpEnabledToggle => GetRequiredControl<CheckBox>("UdpEnabledCheckBox");
        private CheckBox AnalyticsToggle => GetRequiredControl<CheckBox>("AnalyticsCheckBox");
        private CheckBox AppRulesEnabledToggle => GetRequiredControl<CheckBox>("AppRulesEnabledCheckBox");
        private StackPanel AppRulesItemsHost => GetRequiredControl<StackPanel>("AppRulesItemsPanel");
        private Button RefreshInstalledAppsActionButton => GetRequiredControl<Button>("RefreshInstalledAppsButton");
        private Border AppRulesEditorOverlay => GetRequiredControl<Border>("AppRulesEditorOverlayBorder");
        private TextBlock AppRulesEditorTitleText => GetRequiredControl<TextBlock>("AppRulesEditorTitleTextBlock");
        private TextBlock AppRulesEditorDescriptionText => GetRequiredControl<TextBlock>("AppRulesEditorDescriptionTextBlock");
        private TextBlock AppRulesEditorCurrentConfigLabelText => GetRequiredControl<TextBlock>("AppRulesEditorCurrentConfigLabelTextBlock");
        private TextBlock AppRulesEditorCurrentConfigText => GetRequiredControl<TextBlock>("AppRulesEditorCurrentConfigTextBlock");
        private TextBlock AppRulesEditorTemplateTitleText => GetRequiredControl<TextBlock>("AppRulesEditorTemplateTitleTextBlock");
        private TextBlock AppRulesEditorTemplateNameLabelText => GetRequiredControl<TextBlock>("AppRulesEditorTemplateNameLabelTextBlock");
        private TextBlock AppRulesEditorModeTitleText => GetRequiredControl<TextBlock>("AppRulesEditorModeTitleTextBlock");
        private TextBlock AppRulesEditorModeDescriptionText => GetRequiredControl<TextBlock>("AppRulesEditorModeDescriptionTextBlock");
        private TextBlock AppRulesEditorSelectedCountText => GetRequiredControl<TextBlock>("AppRulesEditorSelectedCountTextBlock");
        private Button OpenAppPickerActionButton => GetRequiredControl<Button>("OpenAppPickerButton");
        private TextBlock OpenAppPickerButtonText => GetRequiredControl<TextBlock>("OpenAppPickerButtonTextBlock");
        private ComboBox AppRulesTemplateSelector => GetRequiredControl<ComboBox>("AppRulesTemplateComboBox");
        private Button NewAppRulesTemplateActionButton => GetRequiredControl<Button>("NewAppRulesTemplateButton");
        private Button DeleteAppRulesTemplateActionButton => GetRequiredControl<Button>("DeleteAppRulesTemplateButton");
        private TextBox AppRulesTemplateNameInput => GetRequiredControl<TextBox>("AppRulesTemplateNameTextBox");
        private RadioButton AppRulesModeAllAppsOption => GetRequiredControl<RadioButton>("AppRulesModeAllAppsRadioButton");
        private RadioButton AppRulesModeBypassOption => GetRequiredControl<RadioButton>("AppRulesModeBypassRadioButton");
        private RadioButton AppRulesModeOnlySelectedOption => GetRequiredControl<RadioButton>("AppRulesModeOnlySelectedRadioButton");
        private Button CancelAppRulesEditorActionButton => GetRequiredControl<Button>("CancelAppRulesEditorButton");
        private Button SaveAppRulesEditorActionButton => GetRequiredControl<Button>("SaveAppRulesEditorButton");
        private Border AppPickerOverlay => GetRequiredControl<Border>("AppPickerOverlayBorder");
        private TextBlock AppPickerTitleText => GetRequiredControl<TextBlock>("AppPickerTitleTextBlock");
        private TextBox AppPickerSearchInput => GetRequiredControl<TextBox>("AppPickerSearchTextBox");
        private StackPanel AppPickerItemsHost => GetRequiredControl<StackPanel>("AppPickerItemsPanel");
        private TextBlock AppPickerLoadingText => GetRequiredControl<TextBlock>("AppPickerLoadingTextBlock");
        private TextBlock AppPickerNoAppsText => GetRequiredControl<TextBlock>("AppPickerNoAppsTextBlock");
        private TextBlock AppPickerDoneButtonText => GetRequiredControl<TextBlock>("AppPickerDoneButtonTextBlock");
        private TextBox ConfigLinkInput => GetRequiredControl<TextBox>("ConfigLinkTextBox");
        private TextBox SubscriptionRemarkInput => GetRequiredControl<TextBox>("SubscriptionRemarkTextBox");
        private TextBox SubscriptionLinkInput => GetRequiredControl<TextBox>("SubscriptionLinkTextBox");
        private TextBox ConfigRemarkInput => GetRequiredControl<TextBox>("ConfigRemarkTextBox");
        private TextBox RawConfigInput => GetRequiredControl<TextBox>("RawConfigTextBox");
        private Button SaveSettingsActionButton => GetRequiredControl<Button>("SaveSettingsButton");

        private CheckBox TorEnabledToggle => GetRequiredControl<CheckBox>("TorEnabledCheckBox");
        private ComboBox TorModeSelector => GetRequiredControl<ComboBox>("TorModeComboBox");
        private ComboBox BridgeTypeSelector => GetRequiredControl<ComboBox>("BridgeTypeComboBox");
        private TextBox TorSocksPortInput => GetRequiredControl<TextBox>("TorSocksPortTextBox");
        private TextBox BridgesInput => GetRequiredControl<TextBox>("BridgesTextBox");
        private Button UseDefaultBridgesActionButton => GetRequiredControl<Button>("UseDefaultBridgesButton");
        private Button FetchMoatActionButton => GetRequiredControl<Button>("FetchMoatButton");
        private Button CheckBridgeActionButton => GetRequiredControl<Button>("CheckBridgeButton");
        private StackPanel CaptchaHost => GetRequiredControl<StackPanel>("CaptchaPanel");
        private global::Avalonia.Controls.Image CaptchaImageControl => GetRequiredControl<global::Avalonia.Controls.Image>("CaptchaImage");
        private TextBox CaptchaInput => GetRequiredControl<TextBox>("CaptchaTextBox");
        private Button SubmitCaptchaActionButton => GetRequiredControl<Button>("SubmitCaptchaButton");
        private TextBlock TorStatusText => GetRequiredControl<TextBlock>("TorStatusTextBlock");

        private static readonly Dictionary<TorMode, string> TorModeOptions = new()
        {
            { TorMode.ONLY_TOR, "Tor only" },
            { TorMode.XRAY_OVER_TOR, "Xray over Tor" }
        };

        private static readonly Dictionary<BridgeType, string> BridgeTypeOptions = new()
        {
            { BridgeType.NONE, "None (direct)" },
            { BridgeType.OBFS4, "obfs4" },
            { BridgeType.SNOWFLAKE, "Snowflake" },
            { BridgeType.MEEK_AZURE, "meek-azure" },
            { BridgeType.WEBTUNNEL, "WebTunnel" }
        };

        private readonly TorManager torManager = new TorManager();
        private MoatChallenge? activeMoatChallenge;

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private T GetRequiredControl<T>(string name) where T : Control
        {
            return this.FindControl<T>(name)
                ?? throw new InvalidOperationException($"Required control '{name}' was not found.");
        }

        private object? GetViewResource(string key)
        {
            if (Resources.TryGetValue(key, out object? resource))
                return resource;

            if (Avalonia.Application.Current?.TryFindResource(key, out object? appResource) == true)
                return appResource;

            return null;
        }

        private IBrush GetBrushResource(string key, IBrush fallback)
        {
            return GetViewResource(key) as IBrush ?? fallback;
        }

        private ControlTheme? GetControlThemeResource(string key)
        {
            return GetViewResource(key) as ControlTheme;
        }

        private void InitializeControls()
        {
            ShowSection(NavigationSection.Home);
            SetRunningState(false);
            SetServersViewMode(ServersViewMode.Browse);
            SetConfigImportMode(ConfigImportMode.Link);
            SetConfigShareVisible(false);
            UpdateNotificationIndicator.IsVisible = false;
        }

        private void ApplyLocalizedText()
        {
            ManageServerConfigurationText.Text = Localize("Lang.Window.Main.ManageServerConfiguration");
            CurrentConfigAppRulesButtonText.Text = Localize("Lang.AppRules.Manage");

            RunActionButton.Content = Localize("Lang.Run");
            StopActionButton.Content = Localize("Lang.Stop");

            ServersTitleText.Text = Localize("Lang.Android.Server.HeaderTitle");
            ServersDescriptionText.Text = Localize("Lang.Window.Main.ManageServerConfiguration");
            ConfigurationsTabNavButton.Content = Localize("Lang.Window.Server.Tab.Configuration");
            SubscriptionsTabNavButton.Content = Localize("Lang.Window.Server.Tab.Subscription");
            AvailableConfigurationsText.Text = Localize("Lang.Android.Server.AvailableConfigurations");
            NoGeneralConfigsText.Text = Localize("Lang.Message.NoServerConfiguration");
            SubscriptionGroupsTitleText.Text = Localize("Lang.Android.Server.SubscriptionGroups");
            NoSubscriptionConfigsText.Text = Localize("Lang.Android.Server.NoSubscriptions");
            GoidaTitleText.Text = Localize("Lang.Goida.Title");
            GoidaDescriptionText.Text = Localize("Lang.Goida.Description");
            GoidaEnabledToggle.Content = Localize("Lang.Goida.Enabled");
            GoidaAutoSwitchToggle.Content = Localize("Lang.Goida.AutoSwitch");
            GoidaFilterListInput.Watermark = Localize("Lang.Goida.FilterListHint");
            GoidaRefreshActionButton.Content = Localize("Lang.Goida.Refresh");
            GoidaProbeActionButton.Content = Localize("Lang.Goida.ProbeAll");
            GoidaSetActiveActionButton.Content = Localize("Lang.Goida.SetActive");
            GoidaAddToPoolActionButton.Content = Localize("Lang.Goida.AddToPool");
            AddConfigTitleText.Text = Localize("Lang.Android.Server.AddConfigTitle");
            AddConfigDescriptionText.Text = Localize("Lang.Android.Server.AddConfigDescription");
            ConfigImportFileModeActionButton.Content = Localize("Lang.Window.Server.Import.File");
            ConfigImportLinkModeActionButton.Content = Localize("Lang.Window.Server.Import.Link");
            ConfigFileImportTitleText.Text = Localize("Lang.Window.Server.Import.File");
            ChooseConfigFileActionButton.Content = Localize("Lang.Button.ChooseFile");
            SelectedConfigFileText.Text = Localize("Lang.Message.NoFileChoosen");
            ImportConfigTitleText.Text = Localize("Lang.Window.Server.Import.Link");
            PasteConfigLinkActionButton.Content = Localize("Lang.Button.Paste");
            ConfigLinkInput.Watermark = Localize("Lang.Window.Server.AddConfig.Placeholder");
            ConfirmConfigImportActionButton.Content = Localize("Lang.Button.Import");
            CancelConfigImportActionButton.Content = Localize("Lang.Button.Cancel");
            AdvancedImportTitleText.Text = Localize("Lang.Android.Server.AdvancedImport");
            AdvancedImportDescriptionText.Text = Localize("Lang.Android.Server.AdvancedImportDescription");
            ConfigRemarkInput.Watermark = Localize("Lang.Android.Server.ConfigName");
            SaveRawConfigActionButton.Content = Localize("Lang.Android.Server.SaveRawConfig");
            AddSubscriptionTitleText.Text = Localize("Lang.Android.Server.AddOrReplaceSubscription");
            AddSubscriptionDescriptionText.Text = Localize("Lang.Android.Server.AddOrReplaceSubscriptionDescription");
            SubscriptionRemarkInput.Watermark = Localize("Lang.Window.Server.Import.Remarks");
            SubscriptionLinkInput.Watermark = Localize("Lang.Window.Server.AddSub.Placeholder");
            PasteSubscriptionLinkActionButton.Content = Localize("Lang.Button.Paste");
            CancelSubscriptionActionButton.Content = Localize("Lang.Button.Cancel");
            SaveSubscriptionActionButton.Content = Localize("Lang.Button.Import");
            ConfigShareTitleText.Text = Localize("Lang.Android.Share.Title");
            CopyConfigLinkActionButton.Content = Localize("Lang.Android.Share.Option.CopyLink");
            ExportConfigActionButton.Content = Localize("Lang.Android.Share.Option.ExportConfig");
            CancelConfigShareActionButton.Content = Localize("Lang.Button.Cancel");
            ConnectionInfoTitleText.Text = Localize("Lang.ConnectionInfo.Title");
            ConnectionInfoVerdictText.Text = Localize("Lang.ConnectionInfo.Idle");

            SettingsTitleText.Text = Localize("Lang.Window.Settings.Title");
            SettingsDescriptionText.Text = Localize("Lang.Android.Settings.Description");
            BasicSettingsTitleText.Text = Localize("Lang.Window.Settings.Basic");
            ConnectionModeTitleText.Text = Localize("Lang.Window.Settings.Mode");
            ProxyModeBadgeText.Text = Localize("Lang.Notify.Mode.TUN");
            VpnComingSoonBadgeText.Text = Localize("Lang.Android.Settings.VpnReady");
            ProtocolTitleText.Text = Localize("Lang.Window.Settings.Protocol");
            UdpEnabledToggle.Content = Localize("Lang.Window.Settings.UDP");
            AnalyticsToggle.Content = Localize("Lang.Window.Settings.SendAnalytics");
            NetworkTitleText.Text = Localize("Lang.Android.Settings.Network");
            ProxyPortTitleText.Text = Localize("Lang.Window.Settings.ProxyPort");
            DnsTitleText.Text = Localize("Lang.Window.Settings.Dns");
            AppRulesTitleText.Text = Localize("Lang.AppRules.Title");
            AppRulesDescriptionText.Text = Localize("Lang.AppRules.Description.Android");
            OpenAppRulesEditorButtonText.Text = Localize("Lang.AppRules.Manage");
            AppRulesConfigHintText.Text = Localize("Lang.AppRules.NoConfigSelected");
            NoDiscoveredAppsText.Text = Localize("Lang.AppRules.NoAppsFound");
            AppRulesEditorTitleText.Text = Localize("Lang.AppRules.Title");
            AppRulesEditorDescriptionText.Text = Localize("Lang.AppRules.ManagerDescription");
            AppRulesEditorCurrentConfigLabelText.Text = Localize("Lang.AppRules.CurrentConfig");
            AppRulesEditorTemplateTitleText.Text = Localize("Lang.AppRules.Template.Title");
            NewAppRulesTemplateActionButton.Content = Localize("Lang.AppRules.Template.New");
            DeleteAppRulesTemplateActionButton.Content = Localize("Lang.AppRules.Template.Delete");
            AppRulesEditorTemplateNameLabelText.Text = Localize("Lang.AppRules.Template.Name");
            AppRulesEditorModeTitleText.Text = Localize("Lang.AppRules.Mode.Title");
            AppRulesEditorModeDescriptionText.Text = Localize("Lang.AppRules.Mode.Description");
            AppRulesModeAllAppsOption.Content = Localize("Lang.AppRules.Mode.AllApps");
            AppRulesModeBypassOption.Content = Localize("Lang.AppRules.Mode.Bypass");
            AppRulesModeOnlySelectedOption.Content = Localize("Lang.AppRules.Mode.OnlySelected");
            OpenAppPickerButtonText.Text = Localize("Lang.AppRules.SelectApps");
            CancelAppRulesEditorActionButton.Content = Localize("Lang.Button.Cancel");
            SaveAppRulesEditorActionButton.Content = Localize("Lang.AppRules.Save");
            AppPickerTitleText.Text = Localize("Lang.AppRules.SelectApps");
            AppPickerSearchInput.Watermark = Localize("Lang.AppRules.Search");
            AppPickerLoadingText.Text = Localize("Lang.AppRules.Loading");
            AppPickerNoAppsText.Text = Localize("Lang.AppRules.NoAppsFound");
            AppPickerDoneButtonText.Text = Localize("Lang.AppRules.Done");
            TunTitleText.Text = Localize("Lang.Window.Settings.TUN");
            TunDescriptionText.Text = Localize("Lang.Android.Settings.TunDescription");
            GetRequiredControl<TextBlock>("TorTitleTextBlock").Text = Localize("Lang.Window.Settings.Tor");
            TorEnabledToggle.Content = Localize("Lang.Tor.Enable");
            GetRequiredControl<TextBlock>("TorHowToTextBlock").Text = Localize("Lang.Tor.HowTo");
            GetRequiredControl<TextBlock>("TorModeTitleTextBlock").Text = Localize("Lang.Tor.Mode");
            GetRequiredControl<TextBlock>("BridgeTypeTitleTextBlock").Text = Localize("Lang.Tor.BridgeType");
            GetRequiredControl<TextBlock>("TorSocksPortTitleTextBlock").Text = Localize("Lang.Tor.SocksPort");
            GetRequiredControl<TextBlock>("BridgesTitleTextBlock").Text = Localize("Lang.Tor.Bridges");
            GetRequiredControl<TextBlock>("UseDefaultBridgesButtonTextBlock").Text = Localize("Lang.Tor.UseDefault");
            GetRequiredControl<TextBlock>("FetchMoatButtonTextBlock").Text = Localize("Lang.Tor.FetchMoat");
            GetRequiredControl<TextBlock>("CheckBridgeButtonTextBlock").Text = Localize("Lang.Tor.Check");
            GetRequiredControl<TextBlock>("QuickConnectTitleTextBlock").Text = Localize("Lang.Tor.QuickConnect");
            GetRequiredControl<TextBlock>("AskTorButtonTextBlock").Text = Localize("Lang.Tor.AskTor");
            GetRequiredControl<TextBlock>("SnowflakeButtonTextBlock").Text = Localize("Lang.Tor.Method.Snowflake");
            GetRequiredControl<TextBlock>("SnowflakeAmpButtonTextBlock").Text = Localize("Lang.Tor.Method.SnowflakeAmp");
            GetRequiredControl<TextBlock>("MeekAzureButtonTextBlock").Text = Localize("Lang.Tor.Method.Meek");
            GetRequiredControl<TextBlock>("GetBridgesTitleTextBlock").Text = Localize("Lang.Tor.GetBridges");
            GetRequiredControl<TextBlock>("BridgesEmailButtonTextBlock").Text = Localize("Lang.Tor.GetBridges.Email");
            GetRequiredControl<TextBlock>("BridgesTelegramButtonTextBlock").Text = Localize("Lang.Tor.GetBridges.Telegram");
            GetRequiredControl<TextBlock>("CaptchaHintTextBlock").Text = Localize("Lang.Tor.Captcha.Hint");
            GetRequiredControl<TextBlock>("SubmitCaptchaButtonTextBlock").Text = Localize("Lang.Tor.Captcha.Submit");
            LogsAndDiagnosticsTitleText.Text = Localize("Lang.Android.Settings.LogsAndDiagnostics");
            LogsDescriptionText.Text = Localize("Lang.Android.Logs.Description");
            ShareLogButtonText.Text = Localize("Lang.Android.Logs.Share");
            SaveLogButtonText.Text = Localize("Lang.Android.Logs.Save");
            ClearLogButtonText.Text = Localize("Lang.Android.Logs.Clear");
            RefreshLogsStatus();
            UpdatesSectionTitleText.Text = Localize("Lang.Android.Updates.Title");
            CheckForUpdateActionButtonText.Text = Localize("Lang.Android.Updates.Check");
            InstallUpdateActionButtonText.Text = Localize("Lang.Android.Updates.Install");
            OpenReleasePageActionButtonText.Text = Localize("Lang.Android.Updates.OpenRelease");
            RefreshUpdatesStatus();
            SaveSettingsActionButton.Content = Localize("Lang.Window.Settings.Confirm");

            SetAdvancedImportVisible(isShowingAdvancedImport);
            SetConfigImportMode(currentConfigImportMode);
            UpdateSubscriptionEmptyState();
            RefreshAppRulesSummary();
        }

        private string Localize(string key)
        {
            return localizationHandler.GetTerm(key);
        }

        private string LocalizeFormat(string key, params object?[] args)
        {
            return string.Format(Localize(key), args);
        }

        private AndroidConnectionNotificationText CreateConnectionNotificationText()
        {
            return new AndroidConnectionNotificationText
            {
                AppName = "Invisible Gorilla XRay",
                ChannelName = Localize("Lang.Android.Notification.ChannelName"),
                ChannelDescription = Localize("Lang.Android.Notification.ChannelDescription"),
                StateStarting = Localize("Lang.Status.WaitForRun"),
                StateRunning = Localize("Lang.Status.Running"),
                StateStopping = Localize("Lang.Android.Notification.State.Stopping"),
                StateStopped = Localize("Lang.Status.Stopped"),
                ConfigLabel = Localize("Lang.Android.Notification.Config"),
                EndpointLabel = Localize("Lang.Android.Notification.Endpoint"),
                ListenerLabel = Localize("Lang.Android.Runtime.ProxyListener"),
                ProtocolLabel = Localize("Lang.Window.Settings.Protocol"),
                TrafficLabel = Localize("Lang.Android.Notification.Traffic"),
                SpeedLabel = Localize("Lang.Android.Notification.Speed"),
                UptimeLabel = Localize("Lang.Android.Notification.Uptime"),
                UnknownEndpoint = Localize("Lang.Android.Notification.UnknownEndpoint")
            };
        }

        private AndroidConnectionNotificationSession BuildConnectionNotificationSession(
            string configContent,
            AndroidConnectionNotificationText notificationText)
        {
            Config? currentConfig = configHandler.GetCurrentConfig();

            string endpoint = notificationText.UnknownEndpoint;
            if (TryExtractOutboundEndpoint(configContent, out string host, out int port))
            {
                endpoint = port > 0 ? $"{host}:{port}" : host;
            }
            else
            {
                string? fallbackHost = JsonUtility.Find("address", "outbounds", configContent)
                    ?? JsonUtility.Find("server", "outbounds", configContent)
                    ?? JsonUtility.Find("host", "outbounds", configContent);
                string? fallbackPort = JsonUtility.Find("port", "outbounds", configContent)
                    ?? JsonUtility.Find("server_port", "outbounds", configContent);

                if (!string.IsNullOrWhiteSpace(fallbackHost))
                {
                    endpoint = int.TryParse(fallbackPort, out int parsedFallbackPort) && parsedFallbackPort > 0
                        ? $"{fallbackHost}:{parsedFallbackPort}"
                        : fallbackHost;
                }
            }

            return new AndroidConnectionNotificationSession
            {
                ConfigName = currentConfig?.Name ?? Localize("Lang.Message.NoServerConfiguration"),
                Endpoint = endpoint,
                Listener = $"127.0.0.1:{settingsHandler.UserSettings.GetProxyPort()}",
                Protocol = settingsHandler.UserSettings.GetProtocol().ToString(),
                Text = notificationText
            };
        }

        private void EnsureServersSectionInitialized()
        {
            if (!isServersSectionInitialized)
            {
                Config? currentConfig = configHandler.GetCurrentConfig();
                ShowServerTab(currentConfig?.Group == GroupType.SUBSCRIPTION
                    ? ServerTab.Subscriptions
                    : ServerTab.Configurations);
                SetServersViewMode(ServersViewMode.Browse);
                SetAdvancedImportVisible(false);
                SetConfigImportMode(ConfigImportMode.Link);
                isServersSectionInitialized = true;
            }

            RefreshConfigs();
            RefreshSubscriptions();
        }

        private void EnsureSettingsSectionInitialized()
        {
            EnsureSettingsControlsCreated();
            LoadSettingsIntoControls();
            UpdateRuntimeSummary();
        }

        // One-time control wiring (ItemsSource) without touching the user-entered values,
        // so it is safe to call right before reading the UI on save.
        private void EnsureSettingsControlsCreated()
        {
            if (isSettingsSectionInitialized)
                return;

            ProtocolSelector.ItemsSource = Enum.GetNames(typeof(Protocol));
            TorModeSelector.ItemsSource = TorModeOptions.Values.ToList();
            BridgeTypeSelector.ItemsSource = BridgeTypeOptions.Values.ToList();
            isSettingsSectionInitialized = true;
        }

        private void LoadSettingsIntoControls()
        {
            UserSettings settings = settingsHandler.UserSettings;

            ProtocolSelector.SelectedItem = Protocol.SOCKS.ToString();
            ProtocolSelector.IsEnabled = false;
            ProxyPortInput.Text = settings.GetProxyPort().ToString();
            DnsInput.Text = settings.GetDns();
            UdpEnabledToggle.IsChecked = settings.GetUdpEnabled();
            AnalyticsToggle.IsChecked = settings.GetSendingAnalyticsEnabled();
            LoadTorSettingsIntoControls(settings);
            RefreshAppRulesSummary();
            isSettingsLoadedIntoControls = true;
        }

        private void LoadTorSettingsIntoControls(UserSettings settings)
        {
            TorSettings tor = settings.GetTorSettings();
            TorEnabledToggle.IsChecked = tor.GetEnabled();
            TorModeSelector.SelectedIndex = TorModeOptions.Keys.ToList().IndexOf(tor.GetMode());
            BridgeTypeSelector.SelectedIndex = BridgeTypeOptions.Keys.ToList().IndexOf(tor.GetBridgeType());
            TorSocksPortInput.Text = tor.GetSocksPort().ToString();
            BridgesInput.Text = string.Join(Environment.NewLine, tor.GetBridgeLines());
            TorStatusText.Text = torManager.IsAvailable
                ? Localize("Lang.Tor.Status.Ready")
                : Localize("Lang.Tor.Status.Unavailable");
        }

        private TorSettings BuildTorSettingsFromUi(UserSettings current)
        {
            TorSettings existing = current.GetTorSettings();
            return new TorSettings
            {
                Enabled = TorEnabledToggle.IsChecked == true,
                Mode = GetSelectedKey(TorModeOptions, TorModeSelector.SelectedIndex, existing.GetMode()),
                BridgeType = GetSelectedKey(BridgeTypeOptions, BridgeTypeSelector.SelectedIndex, existing.GetBridgeType()),
                SocksPort = int.TryParse(TorSocksPortInput.Text, out int sp) && sp > 0 ? sp : existing.GetSocksPort(),
                ControlPort = existing.GetControlPort(),
                BridgeLines = SplitBridgeLines(BridgesInput.Text)
            };
        }

        private static TKey GetSelectedKey<TKey>(Dictionary<TKey, string> options, int index, TKey fallback) where TKey : notnull
        {
            if (index < 0 || index >= options.Count)
                return fallback;
            return options.Keys.ToList()[index];
        }

        private static List<string> SplitBridgeLines(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            return text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        private void OnUseDefaultBridgesClick(object? sender, RoutedEventArgs e)
        {
            BridgeType type = GetSelectedKey(BridgeTypeOptions, BridgeTypeSelector.SelectedIndex, BridgeType.OBFS4);
            if (type == BridgeType.NONE)
            {
                type = BridgeType.OBFS4;
                BridgeTypeSelector.SelectedIndex = BridgeTypeOptions.Keys.ToList().IndexOf(BridgeType.OBFS4);
            }

            List<string> defaults = DefaultBridges.ForType(type);
            BridgesInput.Text = string.Join(Environment.NewLine, defaults);
            TorStatusText.Text = LocalizeFormat("Lang.Tor.Status.LoadedDefaults", defaults.Count);
        }

        private async void OnAskTorClick(object? sender, RoutedEventArgs e)
        {
            Button? button = sender as Button;
            if (button != null) button.IsEnabled = false;
            TorStatusText.Text = Localize("Lang.Tor.Status.AskingTor");

            try
            {
                MoatResult result = await new MoatClient().GetCircumventionSettingsAsync();
                if (result.Success && result.Bridges.Count > 0)
                {
                    BridgeType type = MapTransportToBridgeType(result.Transport);
                    BridgeTypeSelector.SelectedIndex = BridgeTypeOptions.Keys.ToList().IndexOf(type);
                    BridgesInput.Text = string.Join(Environment.NewLine, result.Bridges);
                    TorEnabledToggle.IsChecked = true;
                    TorStatusText.Text = LocalizeFormat("Lang.Tor.Status.AskTorOk", result.Bridges.Count, type);
                }
                else
                {
                    ApplyBuiltinFallback();
                }
            }
            catch (Exception)
            {
                ApplyBuiltinFallback();
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private void OnSnowflakeClick(object? sender, RoutedEventArgs e)
            => ApplyBuiltinMethod(BridgeType.SNOWFLAKE, DefaultBridges.Snowflake);

        private void OnSnowflakeAmpClick(object? sender, RoutedEventArgs e)
            => ApplyBuiltinMethod(BridgeType.SNOWFLAKE, DefaultBridges.SnowflakeAmp);

        private void OnMeekAzureClick(object? sender, RoutedEventArgs e)
            => ApplyBuiltinMethod(BridgeType.MEEK_AZURE, DefaultBridges.MeekAzure);

        private void ApplyBuiltinMethod(BridgeType type, string bridgeLine)
        {
            BridgeTypeSelector.SelectedIndex = BridgeTypeOptions.Keys.ToList().IndexOf(type);
            BridgesInput.Text = bridgeLine;
            TorEnabledToggle.IsChecked = true;
            TorStatusText.Text = LocalizeFormat("Lang.Tor.Status.MethodSelected", type);
        }

        /// <summary>
        /// When the moat / circumvention service is unreachable (e.g. censored), load the bundled
        /// built-in obfs4 bridges so the user still gets a working set instead of an empty box.
        /// </summary>
        private void ApplyBuiltinFallback()
        {
            List<string> defaults = DefaultBridges.ForType(BridgeType.OBFS4);
            BridgeTypeSelector.SelectedIndex = BridgeTypeOptions.Keys.ToList().IndexOf(BridgeType.OBFS4);
            BridgesInput.Text = string.Join(Environment.NewLine, defaults);
            TorEnabledToggle.IsChecked = true;
            TorStatusText.Text = LocalizeFormat("Lang.Tor.Status.MoatFallback", defaults.Count);
        }

        private void OnBridgesEmailClick(object? sender, RoutedEventArgs e)
        {
            OpenExternalUrl(BridgeRequestLinks.BuildEmailUrl("obfs4"));
            TorStatusText.Text = Localize("Lang.Tor.Status.RequestSent");
        }

        private void OnBridgesTelegramClick(object? sender, RoutedEventArgs e)
        {
            OpenExternalUrl(BridgeRequestLinks.TelegramBot);
            TorStatusText.Text = Localize("Lang.Tor.Status.RequestSent");
        }

        private static BridgeType MapTransportToBridgeType(string? transport)
        {
            switch ((transport ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "snowflake": return BridgeType.SNOWFLAKE;
                case "meek": case "meek-azure": case "meek_lite": return BridgeType.MEEK_AZURE;
                case "webtunnel": return BridgeType.WEBTUNNEL;
                default: return BridgeType.OBFS4;
            }
        }

        private async void OnCheckBridgeClick(object? sender, RoutedEventArgs e)
        {
            if (!torManager.IsAvailable)
            {
                TorStatusText.Text = Localize("Lang.Tor.Status.Unavailable");
                return;
            }

            BridgeType type = GetSelectedKey(BridgeTypeOptions, BridgeTypeSelector.SelectedIndex, BridgeType.OBFS4);
            string firstLine = SplitBridgeLines(BridgesInput.Text).FirstOrDefault() ?? string.Empty;

            CheckBridgeActionButton.IsEnabled = false;
            TorStatusText.Text = Localize("Lang.Tor.Status.Checking");

            BridgeCheckResult result = await Task.Run(() => torManager.CheckBridge(type, firstLine));

            TorStatusText.Text = result.Success
                ? LocalizeFormat("Lang.Tor.Status.CheckOk", result.LatencyMs)
                : LocalizeFormat("Lang.Tor.Status.CheckFail", result.Message);
            CheckBridgeActionButton.IsEnabled = true;
        }

        private async void OnFetchMoatClick(object? sender, RoutedEventArgs e)
        {
            FetchMoatActionButton.IsEnabled = false;
            TorStatusText.Text = Localize("Lang.Tor.Status.FetchingCaptcha");

            try
            {
                MoatClient client = new MoatClient();
                MoatResult result = await client.RequestChallengeAsync("obfs4");

                if (result.Success && result.Challenge != null)
                {
                    activeMoatChallenge = result.Challenge;
                    try
                    {
                        using MemoryStream stream = new MemoryStream(result.Challenge.ImagePng);
                        CaptchaImageControl.Source = new Bitmap(stream);
                    }
                    catch { }
                    CaptchaHost.IsVisible = true;
                    CaptchaInput.Text = string.Empty;
                    TorStatusText.Text = Localize("Lang.Tor.Status.CaptchaReady");
                }
                else
                {
                    ApplyBuiltinFallback();
                }
            }
            catch (Exception)
            {
                ApplyBuiltinFallback();
            }
            finally
            {
                FetchMoatActionButton.IsEnabled = true;
            }
        }

        private async void OnSubmitCaptchaClick(object? sender, RoutedEventArgs e)
        {
            if (activeMoatChallenge == null)
                return;

            SubmitCaptchaActionButton.IsEnabled = false;
            TorStatusText.Text = Localize("Lang.Tor.Status.SubmittingCaptcha");

            try
            {
                MoatClient client = new MoatClient();
                MoatResult result = await client.SubmitSolutionAsync(activeMoatChallenge, CaptchaInput.Text ?? string.Empty);

                if (result.Success && result.Bridges.Count > 0)
                {
                    BridgeTypeSelector.SelectedIndex = BridgeTypeOptions.Keys.ToList().IndexOf(BridgeType.OBFS4);
                    BridgesInput.Text = string.Join(Environment.NewLine, result.Bridges);
                    CaptchaHost.IsVisible = false;
                    activeMoatChallenge = null;
                    TorStatusText.Text = LocalizeFormat("Lang.Tor.Status.MoatBridges", result.Bridges.Count);
                }
                else
                {
                    TorStatusText.Text = LocalizeFormat("Lang.Tor.Status.MoatFail", result.Error);
                }
            }
            catch (Exception ex)
            {
                TorStatusText.Text = LocalizeFormat("Lang.Tor.Status.MoatFail", ex.Message);
            }
            finally
            {
                SubmitCaptchaActionButton.IsEnabled = true;
            }
        }

        private void ReloadDiscoveredAndroidApps()
        {
            HashSet<string> selectedPackages = GetCurrentlySelectedBypassPackageSet();
            discoveredAndroidApps = AndroidInstalledAppDiscovery.GetLaunchableApps().ToList();
            RenderAndroidAppRuleCards(selectedPackages);
        }

        private void RenderAndroidAppRuleCards(ISet<string> selectedPackages)
        {
            AppRulesItemsHost.Children.Clear();
            appRuleToggles.Clear();

            foreach (AndroidInstalledAppInfo app in discoveredAndroidApps)
            {
                CheckBox toggle = new()
                {
                    IsChecked = selectedPackages.Contains(app.PackageName),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0)
                };

                appRuleToggles[app.PackageName] = toggle;

                StackPanel textPanel = new()
                {
                    Spacing = 2
                };
                textPanel.Children.Add(new TextBlock
                {
                    Text = app.DisplayName,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                StringBuilder metaBuilder = new(app.PackageName);
                if (app.IsSystemApp)
                {
                    metaBuilder.Append(" • ")
                        .Append(Localize("Lang.AppRules.SystemBadge"));
                }

                textPanel.Children.Add(new TextBlock
                {
                    Text = metaBuilder.ToString(),
                    Foreground = GetBrushResource("TextMuted", Brushes.Gray),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });

                Grid root = new();
                root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                Grid.SetColumn(toggle, 0);
                root.Children.Add(toggle);

                Grid.SetColumn(textPanel, 1);
                root.Children.Add(textPanel);

                Border card = new()
                {
                    Padding = new Thickness(12, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    Background = GetBrushResource("SurfaceDark", Brushes.Transparent),
                    BorderBrush = GetBrushResource("SurfaceBright", IdleMarkerBrush),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Child = root
                };

                card.PointerPressed += (_, e) =>
                {
                    if (e.Source is Button || e.Source is CheckBox)
                        return;

                    toggle.IsChecked = !(toggle.IsChecked ?? false);
                };

                AppRulesItemsHost.Children.Add(card);
            }

            NoDiscoveredAppsText.IsVisible = discoveredAndroidApps.Count == 0;
        }

        private static HashSet<string> GetSelectedBypassPackageSet(UserSettings settings)
        {
            return settings.GetEnabledAppRules()
                .Select(rule => rule.AppId)
                .Where(appId => !string.IsNullOrWhiteSpace(appId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private HashSet<string> GetCurrentlySelectedBypassPackageSet()
        {
            if (appRuleToggles.Count == 0)
                return GetSelectedBypassPackageSet(settingsHandler.UserSettings);

            return appRuleToggles
                .Where(pair => pair.Value.IsChecked == true)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private void RefreshAppRulesSummary()
        {
            UserSettings settings = settingsHandler.UserSettings;
            string summary = BuildAppRulesSummary(settings);
            string boundConfigText = string.IsNullOrWhiteSpace(settings.GetCurrentConfigPath())
                ? Localize("Lang.AppRules.NoConfigSelected")
                : LocalizeFormat("Lang.AppRules.BoundConfig", settings.GetCurrentConfigPath());

            AppRulesSummaryText.Text = summary;
            AppRulesConfigHintText.Text = boundConfigText;
            CurrentConfigAppRulesSummaryText.Text = summary;
        }

        private string BuildAppRulesSummary(UserSettings settings)
        {
            AppRulesMode mode = settings.GetEffectiveAppRulesMode();
            AppRuleTemplate template = settings.GetEffectiveAppRuleTemplate();
            int selectedCount = settings.GetEffectiveEnabledAppRules().Count;
            return LocalizeFormat(
                "Lang.AppRules.Summary",
                GetTemplateDisplayName(settings.GetBoundAppRuleTemplateId(), template),
                LocalizeMode(mode),
                selectedCount);
        }

        private string GetTemplateDisplayName(string templateId, AppRuleTemplate template)
        {
            if (string.IsNullOrWhiteSpace(templateId)
                || string.Equals(templateId, AppRuleTemplate.DefaultTemplateId, StringComparison.OrdinalIgnoreCase))
            {
                return Localize("Lang.AppRules.Template.Default");
            }

            return string.IsNullOrWhiteSpace(template.Name)
                ? Localize("Lang.AppRules.Template.Unnamed")
                : template.Name;
        }

        private string LocalizeMode(AppRulesMode mode)
        {
            return mode switch
            {
                AppRulesMode.BYPASS_SELECTED_APPS => Localize("Lang.AppRules.Mode.Bypass"),
                AppRulesMode.ONLY_SELECTED_APPS => Localize("Lang.AppRules.Mode.OnlySelected"),
                _ => Localize("Lang.AppRules.Mode.AllApps")
            };
        }

        private void OnOpenAppRulesEditorClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                OpenAppRulesEditor();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.OpenAppRulesEditor", ex);
                SetStatus("Lang.AppRules.LoadFailed");
            }
        }

        private void OpenAppRulesEditor()
        {
            CaptureActiveAppRulesEditorTemplateState();

            UserSettings settings = settingsHandler.UserSettings;
            workingDefaultAppRuleTemplate = settings.GetAppRuleTemplateById(AppRuleTemplate.DefaultTemplateId);

            workingAppRuleTemplates.Clear();
            workingAppRuleTemplates.AddRange(settings.GetAppRuleTemplates());

            workingAppRuleBindings.Clear();
            workingAppRuleBindings.AddRange(settings.GetAppRuleTemplateBindings());

            AppRulesEditorCurrentConfigText.Text = CurrentConfigNameText.Text;

            PopulateAppRulesTemplateSelector(settings.GetBoundAppRuleTemplateId());
            AppRulesEditorOverlay.IsVisible = true;
        }

        private void CloseAppRulesEditor()
        {
            CloseAppPicker();
            AppRulesEditorOverlay.IsVisible = false;
            isApplyingAppRulesEditor = false;
        }

        private async void OnOpenAppPickerClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                CaptureActiveAppRulesEditorTemplateState();
                AppPickerSearchInput.Text = string.Empty;
                AppPickerOverlay.IsVisible = true;

                if (discoveredAndroidApps == null || discoveredAndroidApps.Count == 0)
                {
                    AppPickerLoadingText.IsVisible = true;
                    AppPickerItemsHost.Children.Clear();
                    AppPickerNoAppsText.IsVisible = false;

                    List<AndroidInstalledAppInfo> apps = await Task.Run(() =>
                        AndroidInstalledAppDiscovery.GetLaunchableApps().ToList());

                    discoveredAndroidApps = apps;
                    AppPickerLoadingText.IsVisible = false;
                }

                RenderAppPickerCards(GetActiveAppRulesTemplate());
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.OpenAppPicker", ex);
                SetStatus(Localize("Lang.AppRules.LoadFailed"));
                AppPickerLoadingText.IsVisible = false;
            }
        }

        private void CloseAppPicker()
        {
            AppPickerOverlay.IsVisible = false;
        }

        private void OnAppPickerBackClick(object? sender, RoutedEventArgs e)
        {
            CaptureActiveAppRulesEditorTemplateState();
            UpdateEditorSelectedCount();
            CloseAppPicker();
        }

        private void OnAppPickerDoneClick(object? sender, RoutedEventArgs e)
        {
            CaptureActiveAppRulesEditorTemplateState();
            UpdateEditorSelectedCount();
            CloseAppPicker();
        }

        private void OnAppPickerSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (isApplyingAppRulesEditor)
                return;

            CaptureActiveAppRulesEditorTemplateState();
            RenderAppPickerCards(GetActiveAppRulesTemplate());
        }

        private async void OnAppPickerRefreshClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                CaptureActiveAppRulesEditorTemplateState();
                AppPickerLoadingText.IsVisible = true;
                AppPickerItemsHost.Children.Clear();
                appRuleToggles.Clear();

                List<AndroidInstalledAppInfo> apps = await Task.Run(() =>
                    AndroidInstalledAppDiscovery.GetLaunchableApps().ToList());

                discoveredAndroidApps = apps;
                AppPickerLoadingText.IsVisible = false;
                RenderAppPickerCards(GetActiveAppRulesTemplate());
                SetStatus(Localize("Lang.AppRules.AppsRefreshed"));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.AppPickerRefresh", ex);
                AppPickerLoadingText.IsVisible = false;
                SetStatus(Localize("Lang.AppRules.LoadFailed"));
            }
        }

        private void UpdateEditorSelectedCount()
        {
            AppRuleTemplate template = GetActiveAppRulesTemplate();
            int count = template.AppRules.Count(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.AppId));
            AppRulesEditorSelectedCountText.Text = LocalizeFormat("Lang.AppRules.SelectedCount", count);
        }

        private void PopulateAppRulesTemplateSelector(string preferredTemplateId)
        {
            List<TemplateComboItem> items = new();
            items.Add(new TemplateComboItem
            {
                Id = AppRuleTemplate.DefaultTemplateId,
                Name = Localize("Lang.AppRules.Template.Default")
            });

            items.AddRange(
                workingAppRuleTemplates.Select(template => new TemplateComboItem
                {
                    Id = template.Id,
                    Name = GetTemplateDisplayName(template.Id, template)
                }));

            AppRulesTemplateSelector.ItemsSource = items;

            TemplateComboItem selectedItem = items.FirstOrDefault(item =>
                    string.Equals(item.Id, preferredTemplateId, StringComparison.OrdinalIgnoreCase))
                ?? items[0];

            isApplyingAppRulesEditor = true;
            AppRulesTemplateSelector.SelectedItem = selectedItem;
            activeAppRulesTemplateId = selectedItem.Id;
            ApplyTemplateToAppRulesEditor(GetActiveAppRulesTemplate());
            isApplyingAppRulesEditor = false;
        }

        private void ApplyTemplateToAppRulesEditor(AppRuleTemplate template)
        {
            isApplyingAppRulesEditor = true;
            try
            {
                AppRulesTemplateNameInput.Text = template.Name ?? string.Empty;
                AppRulesTemplateNameInput.IsEnabled = !IsDefaultAppRulesTemplate(template.Id);
                DeleteAppRulesTemplateActionButton.IsEnabled = !IsDefaultAppRulesTemplate(template.Id);

                switch (template.Mode)
                {
                    case AppRulesMode.BYPASS_SELECTED_APPS:
                        AppRulesModeBypassOption.IsChecked = true;
                        break;
                    case AppRulesMode.ONLY_SELECTED_APPS:
                        AppRulesModeOnlySelectedOption.IsChecked = true;
                        break;
                    default:
                        AppRulesModeAllAppsOption.IsChecked = true;
                        break;
                }

                appRuleToggles.Clear();
                UpdateEditorSelectedCount();
            }
            finally
            {
                isApplyingAppRulesEditor = false;
            }
        }

        private void RenderAppPickerCards(AppRuleTemplate template)
        {
            AppPickerItemsHost.Children.Clear();
            appRuleToggles.Clear();

            if (discoveredAndroidApps == null || discoveredAndroidApps.Count == 0)
            {
                AppPickerNoAppsText.IsVisible = true;
                return;
            }

            string filter = AppPickerSearchInput.Text?.Trim() ?? string.Empty;
            IEnumerable<AndroidInstalledAppInfo> filteredApps = discoveredAndroidApps;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                filteredApps = filteredApps.Where(app =>
                    app.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || app.PackageName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            HashSet<string> selectedPackages = template.AppRules
                .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.AppId))
                .Select(rule => rule.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (AndroidInstalledAppInfo app in filteredApps)
            {
                const double tapMoveThreshold = 10;

                CheckBox toggle = new()
                {
                    IsChecked = selectedPackages.Contains(app.PackageName),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };

                appRuleToggles[app.PackageName] = toggle;

                Avalonia.Controls.Image iconImage = new()
                {
                    Width = 36,
                    Height = 36,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                TryLoadAppIcon(app.PackageName, iconImage);

                StackPanel textPanel = new() { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                textPanel.Children.Add(new TextBlock
                {
                    Text = app.DisplayName,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                StringBuilder metaBuilder = new(app.PackageName);
                if (app.IsSystemApp)
                    metaBuilder.Append(" \u2022 ").Append(Localize("Lang.AppRules.SystemBadge"));

                textPanel.Children.Add(new TextBlock
                {
                    Text = metaBuilder.ToString(),
                    Foreground = GetBrushResource("TextMuted", Brushes.Gray),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });

                Grid root = new();
                root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

                Grid.SetColumn(toggle, 0);
                root.Children.Add(toggle);
                Grid.SetColumn(iconImage, 1);
                root.Children.Add(iconImage);
                Grid.SetColumn(textPanel, 2);
                root.Children.Add(textPanel);

                Border card = new()
                {
                    Padding = new Thickness(12, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    Background = GetBrushResource("SurfaceDark", Brushes.Transparent),
                    BorderBrush = GetBrushResource("SurfaceBright", IdleMarkerBrush),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Child = root
                };

                Point pointerPressedPosition = default;
                bool canToggleOnRelease = false;

                card.PointerPressed += (_, args) =>
                {
                    Control? sourceControl = args.Source as Control;
                    if (!IsAppPickerTapTarget(sourceControl))
                    {
                        canToggleOnRelease = false;
                        return;
                    }

                    pointerPressedPosition = args.GetCurrentPoint(card).Position;
                    canToggleOnRelease = true;
                };

                card.PointerMoved += (_, args) =>
                {
                    if (!canToggleOnRelease)
                        return;

                    Point currentPosition = args.GetCurrentPoint(card).Position;
                    if (!IsWithinTapThreshold(pointerPressedPosition, currentPosition, tapMoveThreshold))
                        canToggleOnRelease = false;
                };

                card.PointerReleased += (_, args) =>
                {
                    if (!canToggleOnRelease)
                        return;

                    canToggleOnRelease = false;

                    Control? sourceControl = args.Source as Control;
                    if (!IsAppPickerTapTarget(sourceControl))
                        return;

                    Point currentPosition = args.GetCurrentPoint(card).Position;
                    if (!IsWithinTapThreshold(pointerPressedPosition, currentPosition, tapMoveThreshold))
                        return;

                    toggle.IsChecked = !(toggle.IsChecked ?? false);
                };

                AppPickerItemsHost.Children.Add(card);
            }

            AppPickerNoAppsText.IsVisible = AppPickerItemsHost.Children.Count == 0;
        }

        private static void TryLoadAppIcon(string packageName, Avalonia.Controls.Image target)
        {
            try
            {
                Context? context = global::Android.App.Application.Context;
                if (context?.PackageManager == null)
                    return;

                global::Android.Graphics.Drawables.Drawable? drawable =
                    context.PackageManager.GetApplicationIcon(packageName);
                if (drawable == null)
                    return;

                int size = 72;
                global::Android.Graphics.Bitmap bmp =
                    global::Android.Graphics.Bitmap.CreateBitmap(size, size,
                        global::Android.Graphics.Bitmap.Config.Argb8888!)!;
                global::Android.Graphics.Canvas canvas = new(bmp);
                drawable.SetBounds(0, 0, size, size);
                drawable.Draw(canvas);

                using MemoryStream ms = new();
                bmp.Compress(global::Android.Graphics.Bitmap.CompressFormat.Png!, 80, ms);
                bmp.Recycle();
                ms.Position = 0;

                target.Source = new Bitmap(ms);
            }
            catch
            {
                // icon load is best-effort
            }
        }

        private static bool IsWithinTapThreshold(Point origin, Point current, double threshold)
        {
            return Math.Abs(current.X - origin.X) <= threshold
                   && Math.Abs(current.Y - origin.Y) <= threshold;
        }

        private static bool IsAppPickerTapTarget(Control? sourceControl)
        {
            return sourceControl is not CheckBox
                   && sourceControl?.FindAncestorOfType<CheckBox>() == null
                   && sourceControl is not Button
                   && sourceControl?.FindAncestorOfType<Button>() == null;
        }

        private void CaptureActiveAppRulesEditorTemplateState()
        {
            if (isApplyingAppRulesEditor)
                return;

            AppRuleTemplate template = GetActiveAppRulesTemplate();
            template.Mode = GetSelectedAppRulesEditorMode();
            template.AppRules = BuildSelectedAppRulesForEditor(template);

            if (!IsDefaultAppRulesTemplate(template.Id))
                template.Name = AppRulesTemplateNameInput.Text?.Trim() ?? string.Empty;
        }

        private List<AppRule> BuildSelectedAppRulesForEditor(AppRuleTemplate template)
        {
            HashSet<string> selectedPackages = template.AppRules
                .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.AppId))
                .Select(rule => rule.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (appRuleToggles.Count > 0)
            {
                foreach (string visiblePackageId in appRuleToggles.Keys)
                    selectedPackages.Remove(visiblePackageId);

                foreach (string checkedPackageId in appRuleToggles
                             .Where(pair => pair.Value.IsChecked == true)
                             .Select(pair => pair.Key))
                {
                    selectedPackages.Add(checkedPackageId);
                }
            }

            List<AppRule> rules = discoveredAndroidApps
                .Where(app => selectedPackages.Contains(app.PackageName))
                .Select(app => new AppRule(
                    appId: app.PackageName,
                    displayName: app.DisplayName,
                    iconRef: app.IconRef,
                    enabled: true))
                .ToList();

            foreach (AppRule existingRule in template.AppRules)
            {
                if (!existingRule.Enabled || string.IsNullOrWhiteSpace(existingRule.AppId))
                    continue;

                if (!selectedPackages.Contains(existingRule.AppId))
                    continue;

                if (rules.Any(rule => string.Equals(rule.AppId, existingRule.AppId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                rules.Add(existingRule.Clone());
            }

            return rules;
        }

        private AppRuleTemplate GetActiveAppRulesTemplate()
        {
            if (IsDefaultAppRulesTemplate(activeAppRulesTemplateId))
                return workingDefaultAppRuleTemplate;

            return workingAppRuleTemplates.FirstOrDefault(template =>
                       string.Equals(template.Id, activeAppRulesTemplateId, StringComparison.OrdinalIgnoreCase))
                ?? workingDefaultAppRuleTemplate;
        }

        private AppRulesMode GetSelectedAppRulesEditorMode()
        {
            if (AppRulesModeOnlySelectedOption.IsChecked == true)
                return AppRulesMode.ONLY_SELECTED_APPS;

            if (AppRulesModeBypassOption.IsChecked == true)
                return AppRulesMode.BYPASS_SELECTED_APPS;

            return AppRulesMode.ALL_APPS;
        }

        private bool IsDefaultAppRulesTemplate(string? templateId)
        {
            return string.IsNullOrWhiteSpace(templateId)
                || string.Equals(templateId, AppRuleTemplate.DefaultTemplateId, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeConfigPathKey(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return System.IO.Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Trim();
            }
        }

        private void OnAppRulesTemplateSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (isApplyingAppRulesEditor)
                return;

            CaptureActiveAppRulesEditorTemplateState();
            activeAppRulesTemplateId = (AppRulesTemplateSelector.SelectedItem as TemplateComboItem)?.Id
                ?? AppRuleTemplate.DefaultTemplateId;
            ApplyTemplateToAppRulesEditor(GetActiveAppRulesTemplate());
        }

        private void OnAppRulesTemplateNameChanged(object? sender, TextChangedEventArgs e)
        {
            if (isApplyingAppRulesEditor || IsDefaultAppRulesTemplate(activeAppRulesTemplateId))
                return;

            GetActiveAppRulesTemplate().Name = AppRulesTemplateNameInput.Text?.Trim() ?? string.Empty;
        }

        private void OnAppRulesModeChanged(object? sender, RoutedEventArgs e)
        {
            if (isApplyingAppRulesEditor)
                return;

            CaptureActiveAppRulesEditorTemplateState();
        }

        

        private void OnNewAppRulesTemplateClick(object? sender, RoutedEventArgs e)
        {
            CaptureActiveAppRulesEditorTemplateState();

            AppRuleTemplate newTemplate = GetActiveAppRulesTemplate().Clone();
            newTemplate.Id = Guid.NewGuid().ToString("N");
            newTemplate.Name = LocalizeFormat("Lang.AppRules.Template.NewName", workingAppRuleTemplates.Count + 1);
            workingAppRuleTemplates.Add(newTemplate);

            PopulateAppRulesTemplateSelector(newTemplate.Id);
        }

        private void OnDeleteAppRulesTemplateClick(object? sender, RoutedEventArgs e)
        {
            if (IsDefaultAppRulesTemplate(activeAppRulesTemplateId))
                return;

            workingAppRuleTemplates.RemoveAll(template =>
                string.Equals(template.Id, activeAppRulesTemplateId, StringComparison.OrdinalIgnoreCase));
            activeAppRulesTemplateId = AppRuleTemplate.DefaultTemplateId;
            PopulateAppRulesTemplateSelector(activeAppRulesTemplateId);
        }

        private void OnCancelAppRulesEditorClick(object? sender, RoutedEventArgs e)
        {
            CloseAppRulesEditor();
        }

        private void OnSaveAppRulesEditorClick(object? sender, RoutedEventArgs e)
        {
            CaptureActiveAppRulesEditorTemplateState();

            UserSettings current = settingsHandler.UserSettings;
            List<AppRuleTemplate> templates = workingAppRuleTemplates
                .Select(template => template.Clone())
                .Where(template => !string.IsNullOrWhiteSpace(template.Id))
                .ToList();

            HashSet<string> validTemplateIds = templates
                .Select(template => template.Id)
                .Append(AppRuleTemplate.DefaultTemplateId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string normalizedCurrentConfigPath = NormalizeConfigPathKey(current.GetCurrentConfigPath());
            List<AppRuleTemplateBinding> bindings = workingAppRuleBindings
                .Select(binding => binding.Clone())
                .Where(binding => !string.Equals(
                    NormalizeConfigPathKey(binding.ConfigPath),
                    normalizedCurrentConfigPath,
                    StringComparison.OrdinalIgnoreCase))
                .Where(binding => validTemplateIds.Contains(binding.TemplateId))
                .ToList();

            if (!string.IsNullOrWhiteSpace(normalizedCurrentConfigPath))
            {
                bindings.Add(new AppRuleTemplateBinding(
                    configPath: normalizedCurrentConfigPath,
                    templateId: activeAppRulesTemplateId));
            }

            AppRulesMode previousMode = current.GetEffectiveAppRulesMode();
            HashSet<string> previousIds = current.GetEffectiveEnabledAppRules()
                .Select(rule => rule.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            AppRuleTemplate activeTemplate = GetActiveAppRulesTemplate();
            HashSet<string> nextIds = activeTemplate.AppRules
                .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.AppId))
                .Select(rule => rule.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool isActive = IsConnectionActive();
            bool modeChanged = previousMode != activeTemplate.Mode;
            bool appsChanged = !previousIds.SetEquals(nextIds);
            bool shouldRestartVpn = isActive && (modeChanged || appsChanged);

            DiagnosticLog.Write($"[AppRules] Save: prevMode={previousMode}, newMode={activeTemplate.Mode}, prevApps={previousIds.Count}, newApps={nextIds.Count}, vpnActive={isActive}, modeChanged={modeChanged}, appsChanged={appsChanged}, shouldRestart={shouldRestartVpn}");
            DiagnosticLog.Write($"[AppRules] Save: templateId={activeAppRulesTemplateId}, configPath={normalizedCurrentConfigPath}, bindings={bindings.Count}");

            settingsHandler.UpdateUserSettings(new UserSettings
            {
                Language = current.GetLanguage(),
                Mode = current.GetMode(),
                Protocol = current.GetProtocol(),
                LogLevel = current.GetLogLevel(),
                IsSystemProxyUse = false,
                IsUdpEnable = current.GetUdpEnabled(),
                IsRunningAtStartup = current.GetRunningAtStartupEnabled(),
                IsStartHidden = current.GetStartHiddenEnabled(),
                IsAutoConnect = current.GetAutoConnectEnabled(),
                IsSendingAnalytics = current.GetSendingAnalyticsEnabled(),
                ProxyPort = current.GetProxyPort(),
                TunPort = current.GetTunPort(),
                TestPort = current.GetTestPort(),
                TunIp = current.GetTunIp(),
                Dns = current.GetDns(),
                LogPath = current.GetLogPath(),
                AppRulesMode = workingDefaultAppRuleTemplate.Mode,
                AppRules = workingDefaultAppRuleTemplate.AppRules.Select(rule => rule.Clone()).ToList(),
                AppRuleTemplates = templates,
                AppRuleTemplateBindings = bindings,
                Tor = current.GetTorSettings()
            });

            RefreshAppRulesSummary();
            UpdateRuntimeSummary();
            CloseAppRulesEditor();

            SetStatus(shouldRestartVpn
                ? Localize("Lang.AppRules.RestartingVpn")
                : Localize("Lang.Android.Status.SettingsSaved"));

            if (shouldRestartVpn)
                _ = RestartConnectionAfterSettingsChangeAsync();
        }

        private void RefreshConfigs()
        {
            generalConfigs = configHandler.GetAllGeneralConfigs();

            foreach (Config config in generalConfigs)
                ApplyCachedAvailability(config);

            RenderConfigCards(GeneralConfigsItemsHost, generalConfigs);
            NoGeneralConfigsText.IsVisible = generalConfigs.Count == 0;
        }

        private void RefreshSubscriptions()
        {
            string? previousSelection = selectedSubscription?.Directory.FullName;
            subscriptionGroups = configHandler.GetAllGroups();

            suppressSubscriptionSelectionChanged = true;
            SubscriptionGroupSelector.ItemsSource = subscriptionGroups.Select(group => group.Directory.Name).ToList();

            int selectedIndex = FindSubscriptionGroupIndexForCurrentConfig();
            if (selectedIndex < 0)
            {
                selectedIndex = subscriptionGroups.FindIndex(group =>
                    string.Equals(group.Directory.FullName, previousSelection, StringComparison.OrdinalIgnoreCase));
            }

            SubscriptionGroupSelector.SelectedIndex = selectedIndex;
            suppressSubscriptionSelectionChanged = false;

            if (selectedIndex >= 0 && selectedIndex < subscriptionGroups.Count)
            {
                selectedSubscription = subscriptionGroups[selectedIndex];
            }
            else
            {
                selectedSubscription = null;
            }

            UpdateSubscriptionActionAvailability();
            RefreshSubscriptionConfigs();
        }

        private void RefreshSubscriptionConfigs()
        {
            subscriptionConfigs = selectedSubscription == null
                ? new List<Config>()
                : configHandler.GetAllSubscriptionConfigs(selectedSubscription.Directory.FullName);

            foreach (Config config in subscriptionConfigs)
                ApplyCachedAvailability(config);

            RenderConfigCards(SubscriptionConfigsItemsHost, subscriptionConfigs);
            UpdateSubscriptionEmptyState();
        }

        private void UpdateSubscriptionEmptyState()
        {
            if (subscriptionGroups.Count == 0)
            {
                NoSubscriptionConfigsText.Text = Localize("Lang.Android.Server.NoSubscriptions");
                NoSubscriptionConfigsText.IsVisible = true;
                return;
            }

            NoSubscriptionConfigsText.Text = Localize("Lang.Android.Server.NoSubscriptionConfigs");
            NoSubscriptionConfigsText.IsVisible = subscriptionConfigs.Count == 0;
        }

        private void RenderConfigCards(StackPanel host, IReadOnlyList<Config> configs)
        {
            host.Children.Clear();

            foreach (Config config in configs)
                host.Children.Add(CreateConfigCard(config));
        }

        private Border CreateConfigCard(Config config)
        {
            bool isSelected = string.Equals(
                settingsHandler.UserSettings.GetCurrentConfigPath(),
                config.Path,
                StringComparison.OrdinalIgnoreCase);

            TorProfile? torProfile = settingsHandler.UserSettings.FindTorProfileByPath(config.Path);

            Border border = new()
            {
                Background = isSelected ? SelectedConfigBrush : IdleConfigBrush,
                BorderBrush = GetBrushResource("SurfaceBright", IdleMarkerBrush),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10)
            };

            Grid root = new();
            root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5, GridUnitType.Pixel)));
            root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(124, GridUnitType.Pixel)));

            root.Children.Add(new Border
            {
                Background = isSelected ? SelectedMarkerBrush : IdleMarkerBrush
            });

            StackPanel textColumn = new()
            {
                Margin = new Thickness(12, 8),
                Spacing = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            textColumn.Children.Add(new TextBlock
            {
                Text = config.Name,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textColumn.Children.Add(new TextBlock
            {
                Text = torProfile != null ? BuildTorProfileSubtitle(torProfile) : config.UpdateTime,
                Foreground = torProfile != null
                    ? (IBrush)SelectedMarkerBrush
                    : GetBrushResource("TextMuted", Brushes.Gray),
                FontSize = 12
            });
            Grid.SetColumn(textColumn, 1);
            root.Children.Add(textColumn);

            Grid rightColumn = new()
            {
                Margin = new Thickness(8, 8, 8, 8)
            };
            rightColumn.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            rightColumn.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            (string statusText, IBrush statusBrush) = GetAvailabilityPresentation(config.Availability);
            StackPanel statusRow = new()
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Spacing = 4
            };
            statusRow.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = statusBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            statusRow.Children.Add(new TextBlock
            {
                Text = statusText,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeight.Light,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetRow(statusRow, 0);
            rightColumn.Children.Add(statusRow);

            StackPanel actionRow = new()
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
                Spacing = 4
            };
            if (torProfile == null)
                actionRow.Children.Add(CreateIconActionButton("Icon.Share", 11, 13, () => _ = ShareConfigAsync(config)));
            actionRow.Children.Add(CreateIconActionButton("Icon.Delete", 12, 12, () => DeleteSelectedConfig(config)));
            actionRow.Children.Add(CreateIconActionButton("Icon.Connection", 15, 11, () =>
            {
                if (torProfile != null)
                    _ = CheckTorProfileAsync(torProfile);
                else
                    _ = CheckConfigAsync(config);
            }));
            Grid.SetRow(actionRow, 1);
            rightColumn.Children.Add(actionRow);

            Grid.SetColumn(rightColumn, 2);
            root.Children.Add(rightColumn);
            border.Child = root;

            border.PointerPressed += (_, e) =>
            {
                Control? sourceControl = e.Source as Control;
                if (sourceControl is Button || sourceControl?.FindAncestorOfType<Button>() != null)
                    return;

                TrySelectConfigByPath(config.Path, showStatus: true);
            };

            return border;
        }

        private Button CreateIconActionButton(string iconKey, double iconWidth, double iconHeight, Action onClick)
        {
            Button button = new()
            {
                Width = 36,
                Height = 32,
                Theme = GetControlThemeResource("IconActionButton")
            };
            button.Click += (_, _) => onClick();
            button.Content = new Rectangle
            {
                Fill = GetBrushResource(iconKey, Brushes.White),
                Width = iconWidth,
                Height = iconHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return button;
        }

        private (string Text, IBrush Brush) GetAvailabilityPresentation(int availability)
        {
            if (availability == InvisibleGorillaXRay.Values.Availability.NOT_CHECKED)
                return (Localize("Lang.Config.Status.NotChecked"), AvailabilityPendingBrush);

            if (availability == InvisibleGorillaXRay.Values.Availability.TIMEOUT)
                return (Localize("Lang.Config.Status.Timeout"), AvailabilityErrorBrush);

            if (availability == InvisibleGorillaXRay.Values.Availability.ERROR)
                return (Localize("Lang.Config.Status.Error"), AvailabilityErrorBrush);

            return ($"{availability} ms", AvailabilitySuccessBrush);
        }

        private void ApplyCachedAvailability(Config config)
        {
            if (configAvailability.TryGetValue(config.Path, out int availability))
                config.SetAvailability(availability);
        }

        private void SetConfigAvailability(string path, int availability)
        {
            configAvailability[path] = availability;
        }

        private void SetServersViewMode(ServersViewMode mode)
        {
            currentServersViewMode = mode;
            if (mode != ServersViewMode.Browse)
                SetConfigShareVisible(false);
            ServersBrowseContainer.IsVisible = mode == ServersViewMode.Browse;
            AddConfigContainer.IsVisible = mode == ServersViewMode.AddConfig;
            AddSubscriptionContainer.IsVisible = mode == ServersViewMode.AddSubscription;

            if (mode == ServersViewMode.AddConfig && currentConfigImportMode == ConfigImportMode.Link)
                RequestConfigLinkFocus();
            else if (mode == ServersViewMode.AddSubscription)
                RequestSubscriptionLinkFocus();
        }

        private void SetConfigImportMode(ConfigImportMode mode)
        {
            currentConfigImportMode = mode;
            ConfigFileImportContainer.IsVisible = mode == ConfigImportMode.File;
            ConfigLinkImportContainer.IsVisible = mode == ConfigImportMode.Link;

            ConfigImportFileModeActionButton.Background = mode == ConfigImportMode.File
                ? GetBrushResource("SurfaceBright", Brushes.Gray)
                : GetBrushResource("SurfaceLight", Brushes.Transparent);
            ConfigImportLinkModeActionButton.Background = mode == ConfigImportMode.Link
                ? GetBrushResource("SurfaceBright", Brushes.Gray)
                : GetBrushResource("SurfaceLight", Brushes.Transparent);

            ConfigImportFileModeActionButton.FontWeight = mode == ConfigImportMode.File
                ? FontWeight.SemiBold
                : FontWeight.Normal;
            ConfigImportLinkModeActionButton.FontWeight = mode == ConfigImportMode.Link
                ? FontWeight.SemiBold
                : FontWeight.Normal;

            if (mode == ConfigImportMode.Link && currentServersViewMode == ServersViewMode.AddConfig)
                RequestConfigLinkFocus();
        }

        private void ResetConfigImportState()
        {
            pendingConfigImportFile = null;
            ConfigLinkInput.Text = string.Empty;
            ConfigRemarkInput.Text = string.Empty;
            RawConfigInput.Text = string.Empty;
            SelectedConfigFileText.Text = Localize("Lang.Message.NoFileChoosen");
            SetAdvancedImportVisible(false);
            SetConfigImportMode(ConfigImportMode.Link);
        }

        private async Task LoadRemoteInfoAsync()
        {
            try
            {
                UpdateInfo? info = await AndroidUpdateService.CheckForUpdateAsync().ConfigureAwait(false);
                Status broadcastStatus = await Task.Run(() => broadcastHandler.CheckForBroadcast()).ConfigureAwait(false);

                updateAvailable = info != null && info.IsNewerThanCurrent;
                latestRemoteVersion = info?.Version ?? string.Empty;

                if (broadcastStatus.Code == Code.SUCCESS && broadcastStatus.Content is Broadcast broadcast)
                    broadcastMessage = broadcast.Text;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateRuntimeSummary();
                    UpdateNotificationIndicator.IsVisible = updateAvailable;
                    string statusMessage = updateAvailable || !string.IsNullOrWhiteSpace(broadcastMessage)
                        ? BuildAvailabilityMessage()
                        : string.Empty;
                    SetAvailabilityInfo(statusMessage);
                    SyncUpdatesPanelFromRemoteInfo(info);
                });
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.LoadRemoteInfoAsync", ex);
            }
        }

        private bool TrySaveSettings(bool showSuccessMessage)
        {
            EnsureSettingsControlsCreated();

            // If the Settings panel was never opened this session, the controls hold no
            // user input (they were never populated). Reading them here would wipe the
            // persisted Tor bridges / ports, so keep the saved settings untouched.
            if (!isSettingsLoadedIntoControls)
            {
                if (showSuccessMessage)
                    SetStatus(Localize("Lang.Android.Status.SettingsSaved"));
                return true;
            }

            if (!TryParseProxyPort(ProxyPortInput.Text, out int proxyPort))
                return false;

            UserSettings current = settingsHandler.UserSettings;

            settingsHandler.UpdateUserSettings(new UserSettings
            {
                Language = current.GetLanguage(),
                Mode = current.GetMode(),
                Protocol = current.GetProtocol(),
                LogLevel = current.GetLogLevel(),
                IsSystemProxyUse = false,
                IsUdpEnable = UdpEnabledToggle.IsChecked ?? current.GetUdpEnabled(),
                IsRunningAtStartup = current.GetRunningAtStartupEnabled(),
                IsStartHidden = current.GetStartHiddenEnabled(),
                IsAutoConnect = current.GetAutoConnectEnabled(),
                IsSendingAnalytics = AnalyticsToggle.IsChecked ?? current.GetSendingAnalyticsEnabled(),
                ProxyPort = proxyPort,
                TunPort = current.GetTunPort(),
                TestPort = current.GetTestPort(),
                TunIp = current.GetTunIp(),
                Dns = string.IsNullOrWhiteSpace(DnsInput.Text) ? current.GetDns() : DnsInput.Text.Trim(),
                LogPath = current.GetLogPath(),
                AppRulesMode = current.GetAppRulesMode(),
                AppRules = current.GetAppRules(),
                AppRuleTemplates = current.GetAppRuleTemplates(),
                AppRuleTemplateBindings = current.GetAppRuleTemplateBindings(),
                Tor = BuildTorSettingsFromUi(current)
            });

            UpdateCurrentConfigSummary();
            UpdateRuntimeSummary();
            RefreshAppRulesSummary();

            if (showSuccessMessage)
                SetStatus(Localize("Lang.Android.Status.SettingsSaved"));

            return true;
        }

        private List<AppRule> BuildSelectedAndroidAppRules()
        {
            if (appRuleToggles.Count == 0)
                return settingsHandler.UserSettings.GetAppRules();

            HashSet<string> selectedPackages = GetCurrentlySelectedBypassPackageSet();
            return discoveredAndroidApps
                .Where(app => selectedPackages.Contains(app.PackageName))
                .Select(app => new AppRule(
                    appId: app.PackageName,
                    displayName: app.DisplayName,
                    iconRef: app.IconRef,
                    enabled: true))
                .ToList();
        }

        private static bool AreAppRuleSetsEquivalent(IReadOnlyCollection<AppRule> currentRules, IReadOnlyCollection<AppRule> nextRules)
        {
            HashSet<string> currentIds = currentRules
                .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.AppId))
                .Select(rule => rule.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            HashSet<string> nextIds = nextRules
                .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.AppId))
                .Select(rule => rule.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return currentIds.SetEquals(nextIds);
        }

        private bool IsConnectionActive()
        {
            return StopActionButton.IsVisible
                || isRunWorkerBusy
                || AndroidVpnServiceController.IsRunning
                || AndroidVpnServiceController.IsStopping;
        }

        private async Task RestartConnectionAfterSettingsChangeAsync()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!isStopWorkerBusy && StopActionButton.IsVisible)
                    OnStopClick(null, new RoutedEventArgs());
            });

            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (!StopActionButton.IsVisible
                    && !isRunWorkerBusy
                    && !AndroidVpnServiceController.IsRunning
                    && !AndroidVpnServiceController.IsStopping)
                {
                    break;
                }

                await Task.Delay(250);
            }

            if (StopActionButton.IsVisible
                || isRunWorkerBusy
                || AndroidVpnServiceController.IsRunning
                || AndroidVpnServiceController.IsStopping)
            {
                Dispatcher.UIThread.Post(() => SetStatus(Localize("Lang.AppRules.RestartRequired")));
                return;
            }

            await Task.Delay(300);
            Dispatcher.UIThread.Post(() =>
            {
                if (!isRunWorkerBusy && !AndroidVpnServiceController.IsRunning)
                    OnRunClick(null, new RoutedEventArgs());
            });
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
            GoidaProfileSettings goidaSettings = settingsHandler.UserSettings.GetGoidaSettings();
            GoidaNode? activeGoidaNode = goidaSettings.Enabled
                ? goidaHandler.Manager.GetActiveNode()
                : null;

            string currentConfigText;
            if (activeGoidaNode != null)
            {
                string latency = activeGoidaNode.LatencyMs >= 0
                    ? $"{activeGoidaNode.LatencyMs} ms"
                    : "-";
                currentConfigText = string.Format(
                    Localize("Lang.Goida.ActiveSummary"),
                    activeGoidaNode.DisplayName,
                    latency);
            }
            else if (currentConfig == null)
            {
                currentConfigText = Localize("Lang.Message.NoServerConfiguration");
            }
            else
            {
                currentConfigText = currentConfig.Group == GroupType.SUBSCRIPTION
                    ? $"{Localize("Lang.Window.Server.Subscriptions")} / {currentConfig.Name}"
                    : currentConfig.Name;
            }

            CurrentConfigNameText.Text = currentConfigText;
            AppRulesEditorCurrentConfigText.Text = currentConfigText;
            RefreshGoidaSummary();
            RefreshAppRulesSummary();
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
            builder.AppendLine($"{Localize("Lang.Window.Settings.Mode")}: {Localize("Lang.Notify.Mode.TUN")}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.ProxyListener")}: 127.0.0.1:{settings.GetProxyPort()}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.Protocol")}: {settings.GetProtocol()}");
            builder.AppendLine($"{Localize("Lang.Window.Settings.TunIp")}: {settings.GetTunIp()}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.Dns")}: {settings.GetDns()}");
            builder.AppendLine($"{Localize("Lang.Android.Runtime.Udp")}: {(settings.GetUdpEnabled() ? Localize("Lang.Android.Runtime.Enabled") : Localize("Lang.Android.Runtime.Disabled"))}");
            builder.AppendLine(
                $"{Localize("Lang.AppRules.Title")}: " +
                $"{BuildAppRulesSummary(settings)}");
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
            {
                string template = Localize("Lang.Android.Home.UpdateAvailable");
                string updateLine = string.IsNullOrWhiteSpace(latestRemoteVersion)
                    ? template
                    : string.Format(template, latestRemoteVersion);
                return $"{updateLine} {message}";
            }

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
            isConnectionInfoConnected = state == ConnectionState.Running;

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

            TimeSpan delay = state == ConnectionState.Running
                ? TimeSpan.FromSeconds(3)
                : TimeSpan.FromMilliseconds(200);
            ScheduleConnectionInfoRefresh(delay);
        }

        private void InitializeConnectionInfo()
        {
            connectionInfoTimer?.Stop();
            connectionInfoTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(45)
            };
            connectionInfoTimer.Tick += (_, _) => _ = RefreshConnectionInfoAsync();
            connectionInfoTimer.Start();
            _ = RefreshConnectionInfoAsync();
        }

        private void ScheduleConnectionInfoRefresh(TimeSpan delay)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    await RefreshConnectionInfoAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        private async Task RefreshConnectionInfoAsync()
        {
            connectionInfoLookupCancellation?.Cancel();
            connectionInfoLookupCancellation = new CancellationTokenSource();
            CancellationToken token = connectionInfoLookupCancellation.Token;

            bool connected = isConnectionInfoConnected;

            // The Android app excludes itself from its own VpnService, so a direct request
            // always leaks the real ISP IP. When connected, probe through the running xray
            // local SOCKS listener so the reported IP matches the actual tunnel exit.
            System.Net.IWebProxy probeProxy = null;
            string modeText = string.Empty;
            try
            {
                UserSettings settings = settingsHandler.UserSettings;
                if (connected)
                    probeProxy = core.CreateActiveProbeProxy();
                string outbound = ConnectionProbe.DetectOutboundProtocol(settings.GetCurrentConfigPath());
                modeText = ConnectionProbe.DescribeMode(settings.GetMode(), settings.GetProtocol(), settings.GetTorSettings(), outbound);
                isConnectionInfoTor = settings.GetTorSettings().GetEnabled();
            }
            catch
            {
            }

            Dispatcher.UIThread.Post(() =>
            {
                ConnectionInfoDotControl.Background = AvailabilityPendingBrush;
                ConnectionInfoVerdictText.Text = Localize("Lang.ConnectionInfo.Checking");
                ConnectionInfoModeText.Text = connected && !string.IsNullOrEmpty(modeText)
                    ? $"{Localize("Lang.ConnectionInfo.Mode")} {modeText}"
                    : string.Empty;
            });

            ConnectionInfo info = await connectionInfoService.LookupAsync(probeProxy, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return;

            Dispatcher.UIThread.Post(() => ApplyConnectionInfo(info));
        }

        private void ApplyConnectionInfo(ConnectionInfo info)
        {
            if (!info.Ok)
            {
                ConnectionInfoDotControl.Background = AvailabilityErrorBrush;
                ConnectionInfoIpText.Text = Localize("Lang.ConnectionInfo.Unknown");
                ConnectionInfoLocationText.Text = string.Empty;
                ConnectionInfoOrgText.Text = string.Empty;
                ConnectionInfoVerdictText.Text = LocalizeFormat("Lang.ConnectionInfo.Error", info.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(baselineIp) && !isConnectionInfoConnected)
                baselineIp = info.Ip;

            bool changedFromBaseline = !string.IsNullOrWhiteSpace(baselineIp)
                && !string.Equals(baselineIp, info.Ip, StringComparison.OrdinalIgnoreCase);

            ConnectionInfoDotControl.Background = isConnectionInfoConnected && changedFromBaseline
                ? AvailabilitySuccessBrush
                : isConnectionInfoConnected
                    ? AvailabilityErrorBrush
                    : AvailabilityPendingBrush;

            ConnectionInfoIpText.Text = info.Ip;
            string flag = !string.IsNullOrWhiteSpace(info.FlagEmoji) ? $"{info.FlagEmoji} " : string.Empty;
            string place = !string.IsNullOrWhiteSpace(info.PlaceLine) ? info.PlaceLine : info.CountryName;
            string countrySuffix = !string.IsNullOrWhiteSpace(info.PlaceLine) && !string.IsNullOrWhiteSpace(info.CountryName)
                ? $"\n{info.CountryName}"
                : string.Empty;
            ConnectionInfoLocationText.Text = string.IsNullOrWhiteSpace(place)
                ? string.Empty
                : $"{flag}{place}{countrySuffix}";
            ConnectionInfoOrgText.Text = string.IsNullOrWhiteSpace(info.Org) ? "—" : info.Org;

            if (!isConnectionInfoConnected)
            {
                baselineIp = info.Ip;
                ConnectionInfoVerdictText.Text = Localize("Lang.ConnectionInfo.Idle");
            }
            else if (changedFromBaseline)
            {
                ConnectionInfoVerdictText.Text = isConnectionInfoTor
                    ? Localize("Lang.ConnectionInfo.ProtectedTor")
                    : Localize("Lang.ConnectionInfo.Protected");
            }
            else
            {
                ConnectionInfoVerdictText.Text = Localize("Lang.ConnectionInfo.Exposed");
            }
        }

        private void ShowSection(NavigationSection section)
        {
            HomeSectionScroll.IsVisible = section == NavigationSection.Home;
            ServersSectionScroll.IsVisible = section == NavigationSection.Servers;
            SettingsSectionScroll.IsVisible = section == NavigationSection.Settings;

            if (section != NavigationSection.Servers)
                SetConfigShareVisible(false);

            HomeNavButton.IsEnabled = section != NavigationSection.Home;
            SettingsNavButton.IsEnabled = section != NavigationSection.Settings;
        }

        private void ShowServerTab(ServerTab tab)
        {
            ConfigurationsContentPanel.IsVisible = tab == ServerTab.Configurations;
            SubscriptionsContentPanel.IsVisible = tab == ServerTab.Subscriptions;
            ConfigurationsTabNavButton.Background = tab == ServerTab.Configurations
                ? GetBrushResource("SurfaceBright", Brushes.Gray)
                : GetBrushResource("SurfaceLight", Brushes.Transparent);
            SubscriptionsTabNavButton.Background = tab == ServerTab.Subscriptions
                ? GetBrushResource("SurfaceBright", Brushes.Gray)
                : GetBrushResource("SurfaceLight", Brushes.Transparent);
            ConfigurationsTabNavButton.FontWeight = tab == ServerTab.Configurations
                ? FontWeight.SemiBold
                : FontWeight.Normal;
            SubscriptionsTabNavButton.FontWeight = tab == ServerTab.Subscriptions
                ? FontWeight.SemiBold
                : FontWeight.Normal;
        }

        private void SetAdvancedImportVisible(bool isVisible)
        {
            isShowingAdvancedImport = isVisible;
            AdvancedImportContainer.IsVisible = isVisible;
            AdvancedImportToggleActionButton.Content = isVisible
                ? Localize("Lang.Android.Server.HideAdvancedTools")
                : Localize("Lang.Android.Server.ShowAdvancedTools");
        }

        private void SetConfigShareVisible(bool isVisible)
        {
            ConfigShareActionSheet.IsVisible = isVisible;
            if (!isVisible)
                pendingConfigShare = null;
        }

        private void ClearSubscriptionEditor()
        {
            SubscriptionRemarkInput.Text = string.Empty;
            SubscriptionLinkInput.Text = string.Empty;
        }

        private void RequestConfigLinkFocus()
        {
            RequestControlFocus(ConfigLinkInput);
        }

        private void RequestSubscriptionLinkFocus()
        {
            RequestControlFocus(SubscriptionLinkInput);
        }

        private void RequestControlFocus(Control control)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!control.IsVisible)
                    return;

                control.Focus();
                if (control is TextBox textBox)
                    textBox.CaretIndex = textBox.Text?.Length ?? 0;
            }, DispatcherPriority.Background);
        }

        private int FindSubscriptionGroupIndexForCurrentConfig()
        {
            string? currentDirectory = System.IO.Path.GetDirectoryName(settingsHandler.UserSettings.GetCurrentConfigPath());
            if (string.IsNullOrWhiteSpace(currentDirectory))
                return -1;

            return subscriptionGroups.FindIndex(group =>
                string.Equals(group.Directory.FullName, currentDirectory, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateSubscriptionActionAvailability()
        {
            bool hasSelectedSubscription = selectedSubscription != null;
            SubscriptionGroupSelector.IsEnabled = subscriptionGroups.Count > 0;
            RefreshSubscriptionActionButton.IsEnabled = hasSelectedSubscription;
            ShareSubscriptionActionButton.IsEnabled = hasSelectedSubscription;
            DeleteSubscriptionActionButton.IsEnabled = hasSelectedSubscription;
        }

        private bool TrySelectConfigByPath(string path, bool showStatus = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            settingsHandler.UpdateCurrentConfigPath(path);
            Config? selectedConfig = configHandler.GetCurrentConfig();
            if (selectedConfig == null)
                return false;

            ApplyTorStateForSelectedConfig(path);

            UpdateCurrentConfigSummary();
            UpdateRuntimeSummary();
            RefreshConfigs();
            RefreshSubscriptions();
            ShowServerTab(selectedConfig.Group == GroupType.SUBSCRIPTION
                ? ServerTab.Subscriptions
                : ServerTab.Configurations);

            if (showStatus)
                SetStatus(LocalizeFormat("Lang.Android.Status.SelectedConfig", selectedConfig.Name));

            return true;
        }

        /// <summary>
        /// Selecting a Tor bridge profile activates Tor with that profile's bridges; selecting any
        /// normal server switches Tor back off. This makes a Tor profile behave like a VLESS key in
        /// the list: pick it to route through it, pick another server to leave it.
        /// </summary>
        private void ApplyTorStateForSelectedConfig(string path)
        {
            UserSettings current = settingsHandler.UserSettings;
            TorProfile? profile = current.FindTorProfileByPath(path);
            TorSettings tor = current.GetTorSettings();

            if (profile != null)
            {
                tor.Enabled = true;
                tor.Mode = profile.Mode;
                tor.BridgeType = profile.BridgeType;
                tor.BridgeLines = profile.GetBridgeLines();
                if (profile.GetSocksPort() > 0)
                    tor.SocksPort = profile.GetSocksPort();
            }
            else
            {
                if (!tor.Enabled)
                    return;
                tor.Enabled = false;
            }

            current.Tor = tor;
            settingsHandler.UpdateUserSettings(current);

            if (isSettingsLoadedIntoControls)
                LoadTorSettingsIntoControls(settingsHandler.UserSettings);
        }

        /// <summary>
        /// Creates a switchable Tor bridge profile (a minimal Tor-only Xray config file that shows up
        /// in the server list) from a set of pasted/fetched bridge lines, and returns its config path.
        /// </summary>
        private string CreateTorBridgeProfile(BridgeType bridgeType, List<string> bridgeLines)
        {
            List<string> cleaned = (bridgeLines ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            int socksPort = settingsHandler.UserSettings.GetTorSettings().GetSocksPort();
            string remark = MakeUniqueConfigRemark($"Tor bridge ({DescribeBridgeShort(bridgeType)})");

            configHandler.CreateConfig(remark, TorConfigBuilder.BuildTorOnlyConfig(socksPort));

            Config? created = configHandler.GetAllGeneralConfigs().LastOrDefault(config =>
                string.Equals(config.Name, remark, StringComparison.OrdinalIgnoreCase)
                || string.Equals(System.IO.Path.GetFileNameWithoutExtension(config.Name), remark, StringComparison.OrdinalIgnoreCase));
            string path = created?.Path ?? BuildGeneralConfigPath(remark);

            UserSettings current = settingsHandler.UserSettings;
            current.GetTorProfiles().Add(new TorProfile
            {
                Name = remark,
                ConfigPath = path,
                Mode = TorMode.ONLY_TOR,
                BridgeType = bridgeType,
                BridgeLines = cleaned,
                SocksPort = socksPort,
                LastLatencyMs = -1
            });
            settingsHandler.UpdateUserSettings(current);

            return path;
        }

        private string MakeUniqueConfigRemark(string baseRemark)
        {
            string safeBase = FileUtility.GetValidFileName(baseRemark);
            if (string.IsNullOrWhiteSpace(safeBase))
                safeBase = "Tor bridge";

            HashSet<string> existing = new(
                configHandler.GetAllGeneralConfigs()
                    .Select(config => System.IO.Path.GetFileNameWithoutExtension(config.Name)),
                StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(safeBase))
                return safeBase;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{safeBase} {i}";
                if (!existing.Contains(candidate))
                    return candidate;
            }

            return $"{safeBase} {Guid.NewGuid():N}".Substring(0, safeBase.Length + 6);
        }

        private static string DescribeBridgeShort(BridgeType bridgeType) => bridgeType switch
        {
            BridgeType.OBFS4 => "obfs4",
            BridgeType.SNOWFLAKE => "snowflake",
            BridgeType.MEEK_AZURE => "meek",
            BridgeType.WEBTUNNEL => "webtunnel",
            _ => "tor"
        };

        private string BuildTorProfileSubtitle(TorProfile profile)
        {
            string subtitle = LocalizeFormat("Lang.Tor.Profile.Subtitle", DescribeBridgeShort(profile.BridgeType));
            if (profile.LastLatencyMs >= 0)
                subtitle += $" · {profile.LastLatencyMs} ms";
            return subtitle;
        }

        private void RemoveTorProfileForPath(string path)
        {
            UserSettings current = settingsHandler.UserSettings;
            TorProfile? profile = current.FindTorProfileByPath(path);
            if (profile == null)
                return;

            current.GetTorProfiles().Remove(profile);
            settingsHandler.UpdateUserSettings(current);
        }

        private async Task CheckTorProfileAsync(TorProfile profile)
        {
            if (!torManager.IsAvailable)
            {
                SetStatus(Localize("Lang.Tor.Status.Unavailable"));
                return;
            }

            SetStatus(Localize("Lang.Tor.Status.Checking"));
            string firstLine = profile.GetBridgeLines().FirstOrDefault() ?? string.Empty;

            BridgeCheckResult result = await Task.Run(() => torManager.CheckBridge(profile.BridgeType, firstLine));

            if (result.Success)
            {
                UserSettings current = settingsHandler.UserSettings;
                TorProfile? stored = current.FindTorProfileByPath(profile.ConfigPath);
                if (stored != null)
                {
                    stored.LastLatencyMs = result.LatencyMs;
                    settingsHandler.UpdateUserSettings(current);
                }
                SetStatus(LocalizeFormat("Lang.Tor.Status.CheckOk", result.LatencyMs));
            }
            else
            {
                SetStatus(LocalizeFormat("Lang.Tor.Status.CheckFail", result.Message));
            }

            RefreshConfigs();
        }

        private bool TrySelectConfigByName(string name)
        {
            string normalizedName = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedName))
                return false;

            Config? generalConfig = generalConfigs.LastOrDefault(config =>
                string.Equals(config.Name, normalizedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    System.IO.Path.GetFileNameWithoutExtension(config.Name),
                    normalizedName,
                    StringComparison.OrdinalIgnoreCase));

            if (generalConfig != null)
                return TrySelectConfigByPath(generalConfig.Path);

            foreach (Subscription group in subscriptionGroups)
            {
                Config? subscriptionConfig = configHandler
                    .GetAllSubscriptionConfigs(group.Directory.FullName)
                    .LastOrDefault(config =>
                        string.Equals(config.Name, normalizedName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            System.IO.Path.GetFileNameWithoutExtension(config.Name),
                            normalizedName,
                            StringComparison.OrdinalIgnoreCase));

                if (subscriptionConfig != null)
                    return TrySelectConfigByPath(subscriptionConfig.Path);
            }

            return false;
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

            bool vpnPrepared = await global::InvisibleGorillaXRay.Android.MainActivity.EnsureVpnPreparedAsync();
            if (!vpnPrepared)
            {
                SetStatus("Lang.Android.Status.VpnPermissionDenied");
                return;
            }

            ShowSection(NavigationSection.Home);
            isRunWorkerBusy = true;
            isStopWorkerBusy = false;
            SetRunningState(true);
            StopActionButton.IsEnabled = true;
            SetConnectionState(ConnectionState.Starting);
            SetStatus("Lang.Android.Status.LoadingConfig");

            AndroidConnectionNotificationText notificationText = CreateConnectionNotificationText();

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

                    string activeConfig = configStatus.Content?.ToString() ?? string.Empty;
                    AndroidConnectionNotificationManager.ShowStarting(
                        BuildConnectionNotificationSession(activeConfig, notificationText));

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
                    AndroidConnectionNotificationManager.MarkRunning();
                    Dispatcher.UIThread.Post(() =>
                    {
                        SetConnectionState(ConnectionState.Running);
                        SetStatus("Lang.Android.Status.RunningTunnel");
                    });

                    core.Run(activeConfig);
                }
                catch (Exception ex)
                {
                    failureMessage = MapExceptionToStatus(ex);
                }
                finally
                {
                    isRunWorkerBusy = false;
                    isStopWorkerBusy = false;
                    if (AndroidVpnServiceController.IsStopping)
                    {
                        // Let the Android VPN service publish the final stop-state notification.
                    }
                    else if (AndroidVpnServiceController.IsRunning)
                    {
                        AndroidConnectionNotificationManager.MarkRunning();
                    }
                    else if (started)
                    {
                        AndroidConnectionNotificationManager.MarkStopped();
                    }
                    else
                    {
                        AndroidConnectionNotificationManager.Stop();
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        SetRunningState(false);
                        StopActionButton.IsEnabled = true;
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
            if (isStopWorkerBusy || !StopActionButton.IsVisible)
                return;

            isStopWorkerBusy = true;
            StopActionButton.IsEnabled = false;
            core.Stop();
            AndroidConnectionNotificationManager.MarkStopping();
            SetStatus("Lang.Android.Status.StopRequested");
        }

        private void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
        {
            TrySaveSettings(showSuccessMessage: true);
        }

        private void OnRefreshConnectionInfoClick(object? sender, RoutedEventArgs e)
        {
            _ = RefreshConnectionInfoAsync();
        }

        private void OnRefreshInstalledAppsClick(object? sender, RoutedEventArgs e)
        {
            ReloadDiscoveredAndroidApps();
            SetStatus(Localize("Lang.AppRules.AppsRefreshed"));
        }

        private async void OnShareLogClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                ShareLogActionButton.IsEnabled = false;
                SetStatus(Localize("Lang.Android.Logs.Preparing"));

                global::Android.App.Activity? activity = global::Android.App.Application.Context as global::Android.App.Activity
                    ?? global::InvisibleGorillaXRay.Android.MainActivity.CurrentActivity;
                if (activity == null)
                {
                    SetStatus(Localize("Lang.Android.Logs.ShareFailed"));
                    return;
                }

                bool ok = await AndroidLogShareService.ShareDiagnosticLogAsync(
                    activity,
                    Localize("Lang.Android.Logs.ShareChooserTitle"));

                SetStatus(Localize(ok ? "Lang.Android.Logs.ShareLaunched" : "Lang.Android.Logs.ShareFailed"));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.ShareLog", ex);
                SetStatus(Localize("Lang.Android.Logs.ShareFailed"));
            }
            finally
            {
                ShareLogActionButton.IsEnabled = true;
                RefreshLogsStatus();
            }
        }

        private async void OnSaveLogClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                SaveLogActionButton.IsEnabled = false;
                SetStatus(Localize("Lang.Android.Logs.Saving"));

                global::Android.Content.Context? context = global::Android.App.Application.Context;
                if (context == null)
                {
                    SetStatus(Localize("Lang.Android.Logs.SaveFailed"));
                    return;
                }

                AndroidLogShareService.SaveResult result = await AndroidLogShareService.SaveDiagnosticLogAsync(context);
                if (result.Succeeded)
                {
                    string template = Localize("Lang.Android.Logs.SavedTo");
                    SetStatus(string.Format(template, result.Path ?? string.Empty));
                }
                else
                {
                    string template = Localize("Lang.Android.Logs.SaveFailedWithReason");
                    SetStatus(string.Format(template, result.ErrorMessage ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.SaveLog", ex);
                SetStatus(Localize("Lang.Android.Logs.SaveFailed"));
            }
            finally
            {
                SaveLogActionButton.IsEnabled = true;
                RefreshLogsStatus();
            }
        }

        private void OnClearLogClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                bool cleared = AndroidLogShareService.ClearDiagnosticLog();
                SetStatus(Localize(cleared ? "Lang.Android.Logs.Cleared" : "Lang.Android.Logs.ClearFailed"));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.ClearLog", ex);
                SetStatus(Localize("Lang.Android.Logs.ClearFailed"));
            }
            finally
            {
                RefreshLogsStatus();
            }
        }

        private void RefreshLogsStatus()
        {
            try
            {
                long bytes = AndroidLogShareService.GetDiagnosticLogSizeBytes();
                string path = AndroidLogShareService.GetDiagnosticLogPath();
                string template = Localize("Lang.Android.Logs.Status");
                LogsStatusText.Text = string.Format(template, FormatLogSize(bytes), path);
            }
            catch
            {
                LogsStatusText.Text = string.Empty;
            }
        }

        private static string FormatLogSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        private void RefreshUpdatesStatus()
        {
            try
            {
                string installed = AndroidUpdateService.GetInstalledVersion();
                UpdatesCurrentVersionText.Text = string.Format(
                    Localize("Lang.Android.Updates.CurrentVersion"),
                    string.IsNullOrEmpty(installed) ? "?" : installed);
            }
            catch
            {
                UpdatesCurrentVersionText.Text = string.Empty;
            }
        }

        private void SyncUpdatesPanelFromRemoteInfo(UpdateInfo? info)
        {
            if (isCheckingForUpdate || isDownloadingUpdate)
                return;

            try
            {
                if (info == null)
                    return;

                if (info.IsNewerThanCurrent)
                {
                    ReleaseAsset? asset = AndroidUpdateService.PickApkAssetForCurrentDevice(info);
                    if (asset == null)
                    {
                        UpdatesStatusText.Text = Localize("Lang.Android.Updates.NoApkAsset");
                        InstallUpdateActionButton.IsVisible = false;
                        pendingUpdateInfo = null;
                        pendingUpdateAsset = null;
                        return;
                    }

                    pendingUpdateInfo = info;
                    pendingUpdateAsset = asset;
                    UpdatesStatusText.Text = string.Format(
                        Localize("Lang.Android.Updates.Available"),
                        info.Version,
                        asset.Name);
                    InstallUpdateActionButton.IsVisible = true;
                }
                else
                {
                    pendingUpdateInfo = null;
                    pendingUpdateAsset = null;
                    InstallUpdateActionButton.IsVisible = false;
                    UpdatesStatusText.Text = string.Format(
                        Localize("Lang.Android.Updates.UpToDate"),
                        info.Version);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.SyncUpdatesPanelFromRemoteInfo", ex);
            }
        }

        private async void OnCheckForUpdateClick(object? sender, RoutedEventArgs e)
        {
            if (isCheckingForUpdate || isDownloadingUpdate)
                return;

            try
            {
                isCheckingForUpdate = true;
                CheckForUpdateActionButton.IsEnabled = false;
                UpdatesStatusText.Text = Localize("Lang.Android.Updates.Checking");
                InstallUpdateActionButton.IsVisible = false;
                pendingUpdateInfo = null;
                pendingUpdateAsset = null;
                pendingUpdateLocalApkPath = null;

                UpdateInfo? info = await AndroidUpdateService.CheckForUpdateAsync().ConfigureAwait(true);

                if (info == null)
                {
                    UpdatesStatusText.Text = Localize("Lang.Android.Updates.CheckFailed");
                    return;
                }

                if (!info.IsNewerThanCurrent)
                {
                    UpdatesStatusText.Text = string.Format(
                        Localize("Lang.Android.Updates.UpToDate"),
                        info.Version);
                    return;
                }

                ReleaseAsset? asset = AndroidUpdateService.PickApkAssetForCurrentDevice(info);
                if (asset == null)
                {
                    UpdatesStatusText.Text = Localize("Lang.Android.Updates.NoApkAsset");
                    return;
                }

                pendingUpdateInfo = info;
                pendingUpdateAsset = asset;
                UpdatesStatusText.Text = string.Format(
                    Localize("Lang.Android.Updates.Available"),
                    info.Version,
                    asset.Name);
                InstallUpdateActionButton.IsVisible = true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.OnCheckForUpdateClick", ex);
                UpdatesStatusText.Text = Localize("Lang.Android.Updates.CheckFailed");
            }
            finally
            {
                isCheckingForUpdate = false;
                CheckForUpdateActionButton.IsEnabled = true;
            }
        }

        private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
        {
            if (isDownloadingUpdate || pendingUpdateAsset == null)
                return;

            global::Android.App.Activity? activity = global::InvisibleGorillaXRay.Android.MainActivity.CurrentActivity;
            if (activity == null)
            {
                UpdatesStatusText.Text = Localize("Lang.Android.Updates.InstallFailed");
                return;
            }

            try
            {
                isDownloadingUpdate = true;
                InstallUpdateActionButton.IsEnabled = false;
                CheckForUpdateActionButton.IsEnabled = false;
                UpdatesProgressIndicator.Value = 0;
                UpdatesProgressIndicator.IsVisible = true;
                UpdatesStatusText.Text = Localize("Lang.Android.Updates.Downloading");

                Progress<double> progress = new Progress<double>(ratio =>
                {
                    double percent = Math.Clamp(ratio * 100.0, 0.0, 100.0);
                    UpdatesProgressIndicator.Value = percent;
                    UpdatesStatusText.Text = string.Format(
                        Localize("Lang.Android.Updates.DownloadProgress"),
                        percent.ToString("F0"));
                });

                string? localPath = await AndroidUpdateService.DownloadApkAsync(
                    activity,
                    pendingUpdateAsset!,
                    progress,
                    CancellationToken.None).ConfigureAwait(true);

                if (string.IsNullOrEmpty(localPath))
                {
                    UpdatesStatusText.Text = Localize("Lang.Android.Updates.DownloadFailed");
                    return;
                }

                pendingUpdateLocalApkPath = localPath;
                UpdatesStatusText.Text = Localize("Lang.Android.Updates.LaunchingInstaller");

                bool launched = AndroidUpdateService.LaunchPackageInstaller(activity, localPath);
                UpdatesStatusText.Text = launched
                    ? Localize("Lang.Android.Updates.InstallerLaunched")
                    : Localize("Lang.Android.Updates.InstallFailed");
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.OnInstallUpdateClick", ex);
                UpdatesStatusText.Text = Localize("Lang.Android.Updates.InstallFailed");
            }
            finally
            {
                isDownloadingUpdate = false;
                InstallUpdateActionButton.IsEnabled = true;
                CheckForUpdateActionButton.IsEnabled = true;
                UpdatesProgressIndicator.IsVisible = false;
            }
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

        private void OnOpenBugReportingClick(object? sender, RoutedEventArgs e)
        {
            OpenExternalUrl(InvisibleGorillaXRay.Values.Route.ISSUES);
        }

        private void OnConfigurationsTabClick(object? sender, RoutedEventArgs e)
        {
            ShowServerTab(ServerTab.Configurations);
        }

        private void OnSubscriptionsTabClick(object? sender, RoutedEventArgs e)
        {
            ShowServerTab(ServerTab.Subscriptions);
        }

        private void OnAddConfigButtonClick(object? sender, RoutedEventArgs e)
        {
            ResetConfigImportState();
            SetServersViewMode(ServersViewMode.AddConfig);
        }

        private void OnAddSubscriptionButtonClick(object? sender, RoutedEventArgs e)
        {
            ClearSubscriptionEditor();
            SetServersViewMode(ServersViewMode.AddSubscription);
        }

        private void OnConfigFileModeClick(object? sender, RoutedEventArgs e)
        {
            SetConfigImportMode(ConfigImportMode.File);
        }

        private void OnConfigLinkModeClick(object? sender, RoutedEventArgs e)
        {
            SetConfigImportMode(ConfigImportMode.Link);
        }

        private void OnPasteConfigLinkClick(object? sender, RoutedEventArgs e)
        {
            PasteClipboardTextInto(ConfigLinkInput);
        }

        private async void OnChooseConfigFileClick(object? sender, RoutedEventArgs e)
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null || !topLevel.StorageProvider.CanOpen)
            {
                SetStatus("Lang.Android.Status.FilePickerUnavailable");
                return;
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localize("Lang.Window.Server.Import.File"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(Localize("Lang.Window.Server.Import.File"))
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

            pendingConfigImportFile = files[0];
            SelectedConfigFileText.Text = files[0].Name;
        }

        private void OnToggleAdvancedImportClick(object? sender, RoutedEventArgs e)
        {
            SetAdvancedImportVisible(!isShowingAdvancedImport);
        }

        private void OnCancelConfigImportClick(object? sender, RoutedEventArgs e)
        {
            ResetConfigImportState();
            SetServersViewMode(ServersViewMode.Browse);
        }

        private void OnSubscriptionSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (suppressSubscriptionSelectionChanged)
                return;

            int selectedIndex = SubscriptionGroupSelector.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= subscriptionGroups.Count)
            {
                selectedSubscription = null;
                UpdateSubscriptionActionAvailability();
                RefreshSubscriptionConfigs();
                return;
            }

            selectedSubscription = subscriptionGroups[selectedIndex];
            UpdateSubscriptionActionAvailability();
            RefreshSubscriptionConfigs();
            SetStatus(LocalizeFormat("Lang.Android.Status.SelectedSubscription", selectedSubscription.Directory.Name));
        }

        private async void OnImportConfigClick(object? sender, RoutedEventArgs e)
        {
            if (currentConfigImportMode == ConfigImportMode.Link)
            {
                if (TryImportConfigLink(ConfigLinkInput.Text, clearInputOnSuccess: true))
                {
                    ResetConfigImportState();
                    SetServersViewMode(ServersViewMode.Browse);
                }

                return;
            }

            if (pendingConfigImportFile == null)
            {
                SetStatus(Localize("Lang.Message.NoFileChoosen"));
                return;
            }

            try
            {
                await using var stream = await pendingConfigImportFile.OpenReadAsync();
                using StreamReader reader = new(stream);
                string content = await reader.ReadToEndAsync();
                if (TryImportConfigFileContent(pendingConfigImportFile.Name, content))
                {
                    ResetConfigImportState();
                    SetServersViewMode(ServersViewMode.Browse);
                }
            }
            catch (Exception ex)
            {
                SetStatus(MapExceptionToStatus(ex));
            }
        }

        private bool TryImportConfigLink(string? link, bool clearInputOnSuccess)
        {
            string text = link ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("Lang.Message.NoConfigLinkEntered");
                return false;
            }

            PastedInputResult classified = PastedInputClassifier.Classify(text);
            if (!classified.HasAny)
            {
                SetStatus("Lang.SmartImport.Nothing");
                return false;
            }

            string? firstServerRemark = null;
            SmartImportOutcome outcome = SmartImportService.Apply(
                classified,
                templateHandler.ConverLinkToConfig,
                (remark, data) =>
                {
                    configHandler.CreateConfig(remark, data);
                    firstServerRemark ??= remark;
                },
                templateHandler.ConvertLinkToSubscription,
                (remark, url, data) => configHandler.CreateSubscription(remark, url, data),
                ApplyBridgesFromSmartImport);

            if (classified.ServerLinks.Count == 1 && outcome.ServersAdded == 1 && firstServerRemark != null)
                AndroidConfigShareLinkStore.SaveSourceLink(BuildGeneralConfigPath(firstServerRemark), classified.ServerLinks[0].Trim());

            if (clearInputOnSuccess)
                ConfigLinkInput.Text = string.Empty;

            RefreshConfigs();
            RefreshSubscriptions();
            if (firstServerRemark != null)
                TrySelectConfigByName(firstServerRemark);
            ShowSection(NavigationSection.Servers);
            ShowServerTab(ServerTab.Configurations);
            SetServersViewMode(ServersViewMode.Browse);
            SetStatus(BuildSmartImportSummary(outcome));
            return outcome.AnyAdded;
        }

        private bool ApplyBridgesFromSmartImport(List<string> bridgeLines, BridgeType bridgeType)
        {
            // Create a switchable Tor bridge profile (a server-list entry) from the pasted bridges,
            // then select it so the app immediately routes through it — just like pasting a VLESS key.
            string profilePath = CreateTorBridgeProfile(bridgeType, bridgeLines);

            if (!string.IsNullOrWhiteSpace(profilePath))
                TrySelectConfigByPath(profilePath, showStatus: false);

            LoadTorSettingsIntoControls(settingsHandler.UserSettings);
            return true;
        }

        private string BuildSmartImportSummary(SmartImportOutcome outcome)
        {
            string summary = LocalizeFormat(
                "Lang.SmartImport.Summary",
                outcome.ServersAdded,
                outcome.SubscriptionsAdded,
                outcome.BridgesAdded);

            if (outcome.Failures > 0)
                summary += LocalizeFormat("Lang.SmartImport.Failures", outcome.Failures);

            if (outcome.BridgesUpdated)
                summary += " " + LocalizeFormat("Lang.SmartImport.BridgesEnabled", outcome.BridgeType);

            return summary;
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
                AndroidConfigShareLinkStore.DeleteSourceLink(BuildGeneralConfigPath(remark));
                RefreshConfigs();
                TrySelectConfigByName(remark);
                ShowSection(NavigationSection.Servers);
                ShowServerTab(ServerTab.Configurations);
                SetServersViewMode(ServersViewMode.Browse);
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
            if (TryImportSubscription(SubscriptionRemarkInput.Text, SubscriptionLinkInput.Text))
            {
                ClearSubscriptionEditor();
                SetServersViewMode(ServersViewMode.Browse);
            }
        }

        private void OnCancelSubscriptionClick(object? sender, RoutedEventArgs e)
        {
            ClearSubscriptionEditor();
            SetServersViewMode(ServersViewMode.Browse);
        }

        private void OnPasteSubscriptionLinkClick(object? sender, RoutedEventArgs e)
        {
            PasteClipboardTextInto(SubscriptionLinkInput);
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

            ShowSection(NavigationSection.Servers);
            ShowServerTab(ServerTab.Subscriptions);
            SetServersViewMode(ServersViewMode.Browse);
            SetStatus(LocalizeFormat("Lang.Android.Status.SavedSubscription", payload[0]));
            return true;
        }

        private async void OnCheckSelectedConfigClick(object? sender, RoutedEventArgs e)
        {
            Config? currentConfig = configHandler.GetCurrentConfig();
            if (currentConfig == null)
            {
                SetStatus("Lang.Android.Status.SelectConfigFirst");
                return;
            }

            await CheckConfigAsync(currentConfig);
        }

        private async Task CheckConfigAsync(Config config)
        {
            if (isCheckWorkerBusy)
                return;

            isCheckWorkerBusy = true;
            SetStatus(LocalizeFormat("Lang.Android.Status.CheckingConfig", config.Name));
            try
            {
                await Task.Run(() =>
                {
                    Status loadStatus = core.LoadConfig(config.Path);
                    if (loadStatus.Code == Code.ERROR)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            SetConfigAvailability(config.Path, InvisibleGorillaXRay.Values.Availability.ERROR);
                            RefreshConfigs();
                            RefreshSubscriptions();
                            SetStatus(loadStatus.Content?.ToString() ?? "Lang.Message.InvalidConfig");
                        });
                        return;
                    }

                    string configContent = loadStatus.Content?.ToString() ?? string.Empty;
                    int ping = core.Test(configContent);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ping >= 0)
                        {
                            SetConfigAvailability(config.Path, ping);
                            SetStatus(LocalizeFormat(
                                "Lang.Android.Status.CheckedConfigSuccess",
                                config.Name,
                                ping));
                        }
                        else if (ping == -1)
                        {
                            SetConfigAvailability(config.Path, InvisibleGorillaXRay.Values.Availability.TIMEOUT);
                            SetStatus(LocalizeFormat("Lang.Android.Status.CheckedConfigTimeout", config.Name));
                        }
                        else
                        {
                            SetConfigAvailability(config.Path, InvisibleGorillaXRay.Values.Availability.ERROR);
                            SetStatus(LocalizeFormat("Lang.Android.Status.CheckedConfigError", config.Name));
                        }

                        RefreshConfigs();
                        RefreshSubscriptions();
                    });
                });
            }
            catch (Exception ex)
            {
                SetConfigAvailability(config.Path, InvisibleGorillaXRay.Values.Availability.ERROR);
                SetStatus(MapExceptionToStatus(ex));
            }
            finally
            {
                isCheckWorkerBusy = false;
                RefreshConfigs();
                RefreshSubscriptions();
            }
        }

        private void OnShareSelectedConfigClick(object? sender, RoutedEventArgs e)
        {
            Config? currentConfig = configHandler.GetCurrentConfig();
            if (currentConfig == null)
            {
                SetStatus("Lang.Android.Status.SelectConfigFirst");
                return;
            }

            _ = ShareConfigAsync(currentConfig);
        }

        private Task ShareConfigAsync(Config config)
        {
            pendingConfigShare = config;
            ShowSection(NavigationSection.Servers);
            SetConfigShareVisible(true);
            return Task.CompletedTask;
        }

        private void OnCopyConfigLinkClick(object? sender, RoutedEventArgs e)
        {
            if (!TryGetPendingShareConfig(out Config? config))
                return;

            string? sourceLink = GetShareableLink(config);
            if (string.IsNullOrWhiteSpace(sourceLink))
            {
                SetStatus("Lang.Android.Status.ConfigLinkUnavailable");
                SetConfigShareVisible(false);
                return;
            }

            CopyTextToClipboard(sourceLink, config.Name);
            SetStatus(LocalizeFormat("Lang.Android.Status.CopiedConfigLink", config.Name));
            SetConfigShareVisible(false);
        }

        private void OnExportConfigClick(object? sender, RoutedEventArgs e)
        {
            if (!TryGetPendingShareConfig(out Config? config))
                return;

            if (!TryGetConfigContent(config, out string configContent))
                return;

            ShareText(configContent, config.Name);
            SetStatus(LocalizeFormat("Lang.Android.Status.SharedConfig", config.Name));
            SetConfigShareVisible(false);
        }

        private void OnCancelConfigShareClick(object? sender, RoutedEventArgs e)
        {
            SetConfigShareVisible(false);
        }

        private void OnDeleteSelectedConfigClick(object? sender, RoutedEventArgs e)
        {
            Config? currentConfig = configHandler.GetCurrentConfig();
            if (currentConfig == null)
            {
                SetStatus("Lang.Android.Status.SelectConfigFirst");
                return;
            }

            DeleteSelectedConfig(currentConfig);
        }

        private void DeleteSelectedConfig(Config config)
        {
            if (pendingConfigShare != null &&
                string.Equals(pendingConfigShare.Path, config.Path, StringComparison.OrdinalIgnoreCase))
            {
                SetConfigShareVisible(false);
            }

            bool deletedCurrentConfig = string.Equals(
                settingsHandler.UserSettings.GetCurrentConfigPath(),
                config.Path,
                StringComparison.OrdinalIgnoreCase);

            try
            {
                System.IO.File.Delete(config.Path);
                CleanupEmptySubscriptionDirectory(config);
                AndroidConfigShareLinkStore.DeleteSourceLink(config.Path);
                configAvailability.Remove(config.Path);
                RemoveTorProfileForPath(config.Path);
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
                string? fallbackPath = generalConfigs.LastOrDefault()?.Path;
                if (string.IsNullOrWhiteSpace(fallbackPath))
                {
                    Subscription? fallbackGroup = subscriptionGroups.LastOrDefault();
                    fallbackPath = fallbackGroup == null
                        ? null
                        : configHandler.GetAllSubscriptionConfigs(fallbackGroup.Directory.FullName).LastOrDefault()?.Path;
                }

                if (!string.IsNullOrWhiteSpace(fallbackPath))
                    TrySelectConfigByPath(fallbackPath);
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
            {
                string? fallbackPath = configHandler.GetAllGeneralConfigs().LastOrDefault()?.Path;
                if (string.IsNullOrWhiteSpace(fallbackPath))
                {
                    Subscription? fallbackGroup = configHandler.GetAllGroups().LastOrDefault();
                    fallbackPath = fallbackGroup == null
                        ? null
                        : configHandler.GetAllSubscriptionConfigs(fallbackGroup.Directory.FullName).LastOrDefault()?.Path;
                }
                settingsHandler.UpdateCurrentConfigPath(fallbackPath ?? string.Empty);
            }

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
            AndroidConfigShareLinkStore.DeleteSourceLink(BuildGeneralConfigPath(remark));
            ConfigRemarkInput.Text = string.Empty;
            RawConfigInput.Text = string.Empty;
            RefreshConfigs();
            TrySelectConfigByName(remark);
            ShowSection(NavigationSection.Servers);
            ShowServerTab(ServerTab.Configurations);
            SetServersViewMode(ServersViewMode.Browse);
            SetAdvancedImportVisible(false);
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

        private static string BuildGeneralConfigPath(string remark)
        {
            string safeRemark = FileUtility.GetValidFileName(remark);
            if (string.IsNullOrWhiteSpace(safeRemark))
                safeRemark = "imported-config";

            return System.IO.Path.Combine(InvisibleGorillaXRay.Values.Directory.CONFIGS, $"{safeRemark}.json");
        }

        private string? GetShareableLink(Config config)
        {
            if (AndroidConfigShareLinkStore.TryGetSourceLink(config.Path, out string? sourceLink))
                return sourceLink;

            if (config.Group != GroupType.SUBSCRIPTION)
                return null;

            string? configDirectory = System.IO.Path.GetDirectoryName(config.Path);
            if (string.IsNullOrWhiteSpace(configDirectory))
                return null;

            Subscription? matchingGroup = subscriptionGroups.FirstOrDefault(group =>
                string.Equals(group.Directory.FullName, configDirectory, StringComparison.OrdinalIgnoreCase));

            matchingGroup ??= configHandler.GetAllGroups().FirstOrDefault(group =>
                string.Equals(group.Directory.FullName, configDirectory, StringComparison.OrdinalIgnoreCase));

            return matchingGroup?.Url?.Trim();
        }

        private bool TryGetPendingShareConfig(out Config config)
        {
            if (pendingConfigShare != null)
            {
                config = pendingConfigShare;
                return true;
            }

            config = null!;
            SetStatus("Lang.Android.Status.SelectConfigFirst");
            SetConfigShareVisible(false);
            return false;
        }

        private bool TryGetConfigContent(Config config, out string configContent)
        {
            configContent = string.Empty;
            if (!System.IO.File.Exists(config.Path))
            {
                SetStatus("Lang.Message.FileDoesntExists");
                SetConfigShareVisible(false);
                return false;
            }

            configContent = System.IO.File.ReadAllText(config.Path).Trim();
            if (!string.IsNullOrWhiteSpace(configContent))
                return true;

            SetStatus("Lang.Message.InvalidConfig");
            SetConfigShareVisible(false);
            return false;
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

                if (TryExtractRecursively(outbound, out host, out port))
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

                string? address = TryGetString(endpoint, "address")
                    ?? TryGetString(endpoint, "server")
                    ?? TryGetString(endpoint, "host");

                if (string.IsNullOrWhiteSpace(address))
                    return false;

                int parsedPort = TryGetInt(endpoint, "port");
                if (parsedPort <= 0)
                    parsedPort = TryGetInt(endpoint, "server_port");

                if (parsedPort <= 0)
                    return false;

                extractedHost = address;
                extractedPort = parsedPort;
                return true;

                static string? TryGetString(JsonElement element, string propertyName)
                {
                    if (!element.TryGetProperty(propertyName, out JsonElement property))
                        return null;

                    return property.ValueKind == JsonValueKind.String
                        ? property.GetString()
                        : null;
                }

                static int TryGetInt(JsonElement element, string propertyName)
                {
                    if (!element.TryGetProperty(propertyName, out JsonElement property))
                        return 0;

                    return property.ValueKind switch
                    {
                        JsonValueKind.Number when property.TryGetInt32(out int numericValue) => numericValue,
                        JsonValueKind.String when int.TryParse(property.GetString(), out int stringValue) => stringValue,
                        _ => 0
                    };
                }
            }

            static bool TryExtractRecursively(JsonElement element, out string extractedHost, out int extractedPort)
            {
                extractedHost = string.Empty;
                extractedPort = 0;

                if (TryReadEndpoint(element, out extractedHost, out extractedPort))
                    return true;

                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (JsonProperty property in element.EnumerateObject())
                        {
                            if (TryExtractRecursively(property.Value, out extractedHost, out extractedPort))
                                return true;
                        }
                        break;

                    case JsonValueKind.Array:
                        foreach (JsonElement item in element.EnumerateArray())
                        {
                            if (TryExtractRecursively(item, out extractedHost, out extractedPort))
                                return true;
                        }
                        break;
                }

                return false;
            }
        }

        private static Protocol ParseProtocol(string? selectedValue, Protocol fallback)
        {
            return Enum.TryParse(selectedValue, ignoreCase: true, out Protocol parsed)
                ? parsed
                : fallback;
        }

        private void PasteClipboardTextInto(TextBox target)
        {
            try
            {
                if (global::Android.App.Application.Context?.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboardManager)
                {
                    SetStatus("Lang.Android.Status.ClipboardEmpty");
                    return;
                }

                ClipData? primaryClip = clipboardManager.PrimaryClip;
                if (primaryClip == null || primaryClip.ItemCount == 0)
                {
                    SetStatus("Lang.Android.Status.ClipboardEmpty");
                    return;
                }

                ClipData.Item? item = primaryClip.GetItemAt(0);
                string? text = item?.Text;

                if (string.IsNullOrWhiteSpace(text))
                    text = item?.CoerceToText(global::Android.App.Application.Context)?.ToString();

                if (string.IsNullOrWhiteSpace(text))
                {
                    SetStatus("Lang.Android.Status.ClipboardEmpty");
                    return;
                }

                target.Text = text.Trim();
                RequestControlFocus(target);
                SetStatus("Lang.Android.Status.PastedFromClipboard");
            }
            catch
            {
                SetStatus("Lang.Android.Status.ClipboardEmpty");
            }
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
                Intent shareIntent = new Intent(Intent.ActionSend);
                shareIntent.SetType("text/plain");
                shareIntent.PutExtra(Intent.ExtraText, text);
                shareIntent.PutExtra(Intent.ExtraTitle, title);

                Intent? chooserIntent = Intent.CreateChooser(shareIntent, title);
                if (chooserIntent == null)
                    return;

                chooserIntent.AddFlags(ActivityFlags.NewTask);
                global::Android.App.Application.Context?.StartActivity(chooserIntent);
            }
            catch
            {
            }
        }

        private void CopyTextToClipboard(string text, string title)
        {
            try
            {
                if (global::Android.App.Application.Context?.GetSystemService(Context.ClipboardService) is ClipboardManager clipboardManager)
                    clipboardManager.PrimaryClip = ClipData.NewPlainText(title, text);
            }
            catch
            {
                SetStatus("Lang.Android.Status.ClipboardEmpty");
            }
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Intent intent = new Intent(Intent.ActionView);
                intent.SetData(global::Android.Net.Uri.Parse(url));
                intent.AddFlags(ActivityFlags.NewTask);
                global::Android.App.Application.Context?.StartActivity(intent);
            }
            catch
            {
            }
        }

        private TextBlock GoidaTitleText => GetRequiredControl<TextBlock>("GoidaTitleTextBlock");
        private TextBlock GoidaDescriptionText => GetRequiredControl<TextBlock>("GoidaDescriptionTextBlock");
        private Button GoidaRefreshActionButton => GetRequiredControl<Button>("GoidaRefreshButton");
        private Button GoidaProbeActionButton => GetRequiredControl<Button>("GoidaProbeButton");
        private Button GoidaSetActiveActionButton => GetRequiredControl<Button>("GoidaSetActiveButton");
        private Button GoidaAddToPoolActionButton => GetRequiredControl<Button>("GoidaAddToPoolButton");
        private CheckBox GoidaEnabledToggle => GetRequiredControl<CheckBox>("GoidaEnabledCheckBox");
        private CheckBox GoidaAutoSwitchToggle => GetRequiredControl<CheckBox>("GoidaAutoSwitchCheckBox");
        private ComboBox GoidaSelectionModeSelector => GetRequiredControl<ComboBox>("GoidaSelectionModeComboBox");
        private TextBox GoidaFilterListInput => GetRequiredControl<TextBox>("GoidaFilterListTextBox");
        private ListBox GoidaNodesList => GetRequiredControl<ListBox>("GoidaNodesListBox");
        private TextBlock GoidaActiveSummaryText => GetRequiredControl<TextBlock>("GoidaActiveSummaryTextBlock");

        private void InitializeGoidaControls()
        {
            GoidaSelectionModeSelector.ItemsSource = new[]
            {
                Localize("Lang.Goida.Mode.AutoBest"),
                Localize("Lang.Goida.Mode.ManualFixed"),
                Localize("Lang.Goida.Mode.ManualPool")
            };
            GoidaSelectionModeSelector.SelectedIndex = 0;
            ApplyGoidaSettingsToControls();
            RefreshGoidaNodesList();
        }

        private void ApplyGoidaSettingsToControls()
        {
            isApplyingGoidaSettings = true;
            try
            {
                GoidaProfileSettings settings = settingsHandler.UserSettings.GetGoidaSettings();
                GoidaEnabledToggle.IsChecked = settings.Enabled;
                GoidaAutoSwitchToggle.IsChecked = settings.AutoSwitchOnFly;
                GoidaSelectionModeSelector.SelectedIndex = settings.SelectionMode switch
                {
                    GoidaSelectionMode.ManualFixed => 1,
                    GoidaSelectionMode.ManualPool => 2,
                    _ => 0
                };
            }
            finally
            {
                isApplyingGoidaSettings = false;
            }
        }

        private void CaptureGoidaSettingsFromControls()
        {
            if (isApplyingGoidaSettings)
                return;

            UserSettings current = settingsHandler.UserSettings;
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.Enabled = GoidaEnabledToggle.IsChecked == true;
            settings.AutoSwitchOnFly = GoidaAutoSwitchToggle.IsChecked == true;
            settings.SelectionMode = GoidaSelectionModeSelector.SelectedIndex switch
            {
                1 => GoidaSelectionMode.ManualFixed,
                2 => GoidaSelectionMode.ManualPool,
                _ => GoidaSelectionMode.AutoBest
            };

            current.Goida = settings;
            settingsHandler.UpdateUserSettings(current);
            goidaHandler.Manager.UpdateSettings(settings);
            RefreshGoidaSummary();
        }

        private void RefreshGoidaSummary()
        {
            GoidaNode? activeNode = goidaHandler.Manager.GetActiveNode();
            Border banner = GetRequiredControl<Border>("GoidaSignalBanner");

            if (activeNode == null)
            {
                GoidaActiveSummaryText.Text = string.Empty;
                banner.IsVisible = false;
                return;
            }

            string latency = activeNode.LatencyMs >= 0
                ? $"{activeNode.LatencyMs} ms"
                : "-";
            GoidaActiveSummaryText.Text = string.Format(
                Localize("Lang.Goida.ActiveSummary"),
                activeNode.DisplayName,
                latency);

            InvisibleGorillaXRay.Services.Goida.GoidaMainPresentation presentation =
                InvisibleGorillaXRay.Services.Goida.GoidaNodeDisplay.BuildMainPresentation(activeNode);

            var statusBrush = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(presentation.ColorHex));
            var inactiveBrush = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#4A4A4A"));
            int level = presentation.SignalLevel;

            GetRequiredControl<Avalonia.Controls.Shapes.Ellipse>("GoidaWifiDot").Fill = statusBrush;
            GetRequiredControl<Avalonia.Controls.Shapes.Path>("GoidaWifiArcInner").Stroke =
                level >= 2 ? statusBrush : inactiveBrush;
            GetRequiredControl<Avalonia.Controls.Shapes.Path>("GoidaWifiArcMiddle").Stroke =
                level >= 3 ? statusBrush : inactiveBrush;
            GetRequiredControl<Avalonia.Controls.Shapes.Path>("GoidaWifiArcOuter").Stroke =
                level >= 4 ? statusBrush : inactiveBrush;

            TextBlock signalLabel = GetRequiredControl<TextBlock>("GoidaSignalLabelTextBlock");
            signalLabel.Text = Localize(presentation.QualityLabel);
            signalLabel.Foreground = statusBrush;
            GetRequiredControl<TextBlock>("GoidaSignalLatencyTextBlock").Text = presentation.LatencyText;
            banner.IsVisible = true;
        }

        private void RefreshGoidaNodesList()
        {
            GoidaProfileSettings settings = settingsHandler.UserSettings.GetGoidaSettings();
            string filter = GoidaFilterListInput.Text?.Trim() ?? string.Empty;
            int? listFilter = int.TryParse(filter, out int listId) ? listId : null;

            List<GoidaNodeRow> rows = goidaHandler.Manager.GetNodesSorted()
                .Where(node => listFilter == null || node.ListId == listFilter)
                .Select(node => new GoidaNodeRow
                {
                    ListId = node.ListId,
                    Id = node.Id,
                    DisplayName = node.DisplayName,
                    LatencyText = FormatGoidaLatency(node.LatencyMs),
                    ActiveMark = string.Equals(node.Id, settings.ActiveNodeId, StringComparison.OrdinalIgnoreCase)
                        ? "*"
                        : string.Empty
                })
                .ToList();

            GoidaNodesList.ItemsSource = rows;
        }

        private static string FormatGoidaLatency(int latencyMs)
        {
            return latencyMs switch
            {
                Availability.NOT_CHECKED => "-",
                Availability.TIMEOUT => "timeout",
                Availability.ERROR => "error",
                _ when latencyMs >= 0 => $"{latencyMs} ms",
                _ => "-"
            };
        }

        private GoidaNodeRow? GetSelectedGoidaRow()
        {
            return GoidaNodesList.SelectedItem as GoidaNodeRow;
        }

        private void HandleGoidaActiveNodeChanged(GoidaNode node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.ConfigPath))
                return;

            if (!settingsHandler.UserSettings.GetGoidaSettings().Enabled)
                return;

            settingsHandler.UpdateCurrentConfigPath(node.ConfigPath);

            Dispatcher.UIThread.Post(() =>
            {
                UpdateCurrentConfigSummary();
                if (IsConnectionActive())
                    _ = RestartConnectionAfterSettingsChangeAsync();
            });
        }

        private void OnGoidaNodesUpdated()
        {
            Dispatcher.UIThread.Post(() =>
            {
                RefreshGoidaNodesList();
                RefreshGoidaSummary();
                UpdateCurrentConfigSummary();
            });
        }

        private void OnGoidaSettingsChanged(object? sender, RoutedEventArgs e)
        {
            CaptureGoidaSettingsFromControls();
        }

        private void OnGoidaSelectionModeChanged(object? sender, SelectionChangedEventArgs e)
        {
            CaptureGoidaSettingsFromControls();
        }

        private void OnGoidaFilterChanged(object? sender, TextChangedEventArgs e)
        {
            RefreshGoidaNodesList();
        }

        private async void OnGoidaRefreshClick(object? sender, RoutedEventArgs e)
        {
            await goidaHandler.Manager.RefreshListsAsync();
            RefreshGoidaNodesList();
        }

        private async void OnGoidaProbeClick(object? sender, RoutedEventArgs e)
        {
            await goidaHandler.Manager.ProbeAsync();
            RefreshGoidaNodesList();
        }

        private void OnGoidaNodeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
        }

        private void OnGoidaSetActiveClick(object? sender, RoutedEventArgs e)
        {
            GoidaNodeRow? row = GetSelectedGoidaRow();
            if (row == null)
                return;

            UserSettings current = settingsHandler.UserSettings;
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.Enabled = true;
            settings.ActiveNodeId = row.Id;
            settings.PinnedNodeId = row.Id;
            settings.SelectionMode = GoidaSelectionMode.ManualFixed;
            current.Goida = settings;
            settingsHandler.UpdateUserSettings(current);

            GoidaNode? node = goidaHandler.Manager.GetNodesSorted()
                .FirstOrDefault(candidate => string.Equals(candidate.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (node != null)
                HandleGoidaActiveNodeChanged(node);

            ApplyGoidaSettingsToControls();
            RefreshGoidaNodesList();
        }

        private void OnGoidaAddToPoolClick(object? sender, RoutedEventArgs e)
        {
            GoidaNodeRow? row = GetSelectedGoidaRow();
            if (row == null)
                return;

            UserSettings current = settingsHandler.UserSettings;
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.ManualPoolNodeIds ??= new List<string>();
            if (!settings.ManualPoolNodeIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase))
                settings.ManualPoolNodeIds.Add(row.Id);
            settings.SelectionMode = GoidaSelectionMode.ManualPool;
            current.Goida = settings;
            settingsHandler.UpdateUserSettings(current);
            goidaHandler.Manager.UpdateSettings(settings);
            ApplyGoidaSettingsToControls();
            RefreshGoidaNodesList();
        }

    }
}
