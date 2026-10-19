using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InvisibleGorillaXRay
{
    using Models;
    using Services;
    using Services.Analytics.SettingsWindow;
    using Handlers.Tor;

    public partial class SettingsWindow : Window
    {
        private static readonly Dictionary<string, string> Languages = new Dictionary<string, string>() {
            { "en-US", "English" },
            { "ru-RU", "Русский" },
            { "fa-IR", "فارسی" }
        };

        private static readonly Dictionary<Mode, string> Modes = new Dictionary<Mode, string>() {
            { Mode.PROXY, "Proxy" },
            { Mode.TUN, "TUN" }
        };

        private static readonly Dictionary<Protocol, string> Protocols = new Dictionary<Protocol, string>() {
            { Protocol.HTTP, "http" },
            { Protocol.SOCKS, "socks" }
        };

        private static readonly Dictionary<LogLevel, string> logLevels = new Dictionary<LogLevel, string>() {
            { LogLevel.NONE, "None" },
            { LogLevel.DEBUG, "Debug" },
            { LogLevel.INFO, "Info"},
            { LogLevel.WARNING, "Warning" },
            { LogLevel.ERROR, "Error" }
        };

        private static readonly Dictionary<TorMode, string> TorModes = new Dictionary<TorMode, string>() {
            { TorMode.ONLY_TOR, "Tor only" },
            { TorMode.XRAY_OVER_TOR, "Xray over Tor" }
        };

        private static readonly Dictionary<BridgeType, string> BridgeTypes = new Dictionary<BridgeType, string>() {
            { BridgeType.NONE, "None (direct)" },
            { BridgeType.OBFS4, "obfs4" },
            { BridgeType.SNOWFLAKE, "Snowflake" },
            { BridgeType.MEEK_AZURE, "meek-azure" },
            { BridgeType.WEBTUNNEL, "WebTunnel" }
        };

        private static readonly Dictionary<string, string> ExitCountries = new Dictionary<string, string>() {
            { "", "Any exit" },
            { "de", "Germany (DE)" },
            { "nl", "Netherlands (NL)" },
            { "fr", "France (FR)" },
            { "us", "United States (US)" },
            { "gb", "United Kingdom (GB)" },
            { "se", "Sweden (SE)" },
            { "ch", "Switzerland (CH)" },
            { "fi", "Finland (FI)" },
            { "ro", "Romania (RO)" },
            { "pl", "Poland (PL)" },
            { "jp", "Japan (JP)" },
            { "ca", "Canada (CA)" },
            { "kr", "Korea (KR)" }
        };

        private readonly TorManager torManager = new TorManager();
        private MoatChallenge activeMoatChallenge;
        private bool suppressBridgeTypeLoad;
        private bool bridgeTypeHooked;
        private bool suppressSetLoad;
        private string loadedSetId = "";
        private TorSettings bridgeSets;

        private Func<string> getLanguage;
        private Func<Mode> getMode;
        private Func<Protocol> getProtocol;
        private Func<bool> getSystemProxyUsed;
        private Func<bool> getUdpEnabled;
        private Func<bool> getRunningAtStartupEnabled;
        private Func<bool> getStartHiddenEnabled;
        private Func<bool> getAutoConnectEnabled;
        private Func<bool> getSendingAnalyticsEnabled;
        private Func<int> getProxyPort;
        private Func<int> getTunPort;
        private Func<int> getTestPort;
        private Func<string> getDeviceIp;
        private Func<String> getDns;
        private Func<LogLevel> getLogLevel;
        private Func<string> getLogPath;
        private Func<UserSettings> getUserSettings;
        private Func<AppRulesMode> getAppRulesMode;
        private Func<List<AppRule>> getAppRules;
        private Func<AppRulesWindow> openAppRulesWindow;
        private Func<PolicyWindow> openPolicyWindow;

        private Action<UserSettings> onUpdateUserSettings;
        private readonly List<WindowsInstalledAppInfo> discoveredWindowsApps = new();
        private readonly Dictionary<string, CheckBox> appRuleToggles = new(StringComparer.OrdinalIgnoreCase);

        private AnalyticsService AnalyticsService => ServiceLocator.Get<AnalyticsService>();

        public SettingsWindow()
        {
            InitializeComponent();
            DesktopWindowFit.ClampToWorkArea(this);
            TextSelectionAssist.Attach(this);
            InitializeItems();

            void InitializeItems()
            {
                InitializeLanguageItems();
                InitializeModeItems();
                InitializeProtocolItems();
                InitializeLogLevelItems();
                InitializeTorItems();

                void InitializeLanguageItems() => comboBoxLanguage.ItemsSource = Languages;

                void InitializeModeItems() => comboBoxMode.ItemsSource = Modes;

                void InitializeProtocolItems() => comboBoxProtocol.ItemsSource = Protocols;

                void InitializeLogLevelItems() => comboBoxLogLevel.ItemsSource = logLevels;

                void InitializeTorItems()
                {
                    comboBoxTorMode.ItemsSource = TorModes;
                    comboBoxBridgeType.ItemsSource = BridgeTypes;
                    comboBoxExitCountry.ItemsSource = ExitCountries;
                }
            }
        }

        public void Setup(
            Func<string> getLanguage,
            Func<Mode> getMode,
            Func<Protocol> getProtocol,
            Func<bool> getSystemProxyUsed,
            Func<bool> getUdpEnabled,
            Func<bool> getRunningAtStartupEnabled,
            Func<bool> getStartHiddenEnabled,
            Func<bool> getAutoConnectEnabled,
            Func<bool> getSendingAnalyticsEnabled,
            Func<int> getProxyPort,
            Func<int> getTunPort,
            Func<int> getTestPort,
            Func<string> getDeviceIp,
            Func<string> getDns,
            Func<LogLevel> getLogLevel,
            Func<string> getLogPath,
            Func<UserSettings> getUserSettings,
            Func<AppRulesMode> getAppRulesMode,
            Func<List<AppRule>> getAppRules,
            Func<AppRulesWindow> openAppRulesWindow,
            Func<PolicyWindow> openPolicyWindow,
            Action<UserSettings> onUpdateUserSettings
        )
        {
            this.getLanguage = getLanguage;
            this.getMode = getMode;
            this.getProtocol = getProtocol;
            this.getSystemProxyUsed = getSystemProxyUsed;
            this.getUdpEnabled = getUdpEnabled;
            this.getRunningAtStartupEnabled = getRunningAtStartupEnabled;
            this.getStartHiddenEnabled = getStartHiddenEnabled;
            this.getAutoConnectEnabled = getAutoConnectEnabled;
            this.getSendingAnalyticsEnabled = getSendingAnalyticsEnabled;
            this.getProxyPort = getProxyPort;
            this.getTunPort = getTunPort;
            this.getTestPort = getTestPort;
            this.getDeviceIp = getDeviceIp;
            this.getDns = getDns;
            this.getLogLevel = getLogLevel;
            this.getLogPath = getLogPath;
            this.getUserSettings = getUserSettings;
            this.getAppRulesMode = getAppRulesMode;
            this.getAppRules = getAppRules;
            this.openAppRulesWindow = openAppRulesWindow;
            this.openPolicyWindow = openPolicyWindow;
            this.onUpdateUserSettings = onUpdateUserSettings;

            UpdateUI();
        }

        private void UpdateUI()
        {
            UpdateBasicPanelUI();
            UpdatePortPanelUI();
            UpdateTunPanelUI();
            UpdateLogPanelUI();
            LoadTorSettings();

            void UpdateBasicPanelUI()
            {
                comboBoxLanguage.SelectedValue = getLanguage.Invoke();
                comboBoxMode.SelectedValue = getMode.Invoke();
                comboBoxProtocol.SelectedValue = getProtocol.Invoke();
                checkBoxUseSystemProxy.IsChecked = getSystemProxyUsed.Invoke();
                checkBoxEnableUdp.IsChecked = getUdpEnabled.Invoke();
                checkBoxRunAtStartup.IsChecked = getRunningAtStartupEnabled.Invoke();
                checkBoxStartHidden.IsChecked = getStartHiddenEnabled.Invoke();
                checkBoxAutoConnect.IsChecked = getAutoConnectEnabled.Invoke();
                checkBoxSendAnalytics.IsChecked = getSendingAnalyticsEnabled.Invoke();
                RefreshAppRulesSummary();
            }

            void UpdatePortPanelUI()
            {
                textBoxProxyPort.Text = getProxyPort.Invoke().ToString();
                textBoxTunPort.Text = getTunPort.Invoke().ToString();
                textBoxTestPort.Text = getTestPort.Invoke().ToString();
            }

            void UpdateTunPanelUI()
            {
                textBoxTunDeviceIp.Text = getDeviceIp.Invoke();
                textBoxTunDns.Text = getDns.Invoke();
            }

            void UpdateLogPanelUI()
            {
                comboBoxLogLevel.SelectedValue = getLogLevel.Invoke();
                textBoxLogPath.Text = Path.GetFullPath(getLogPath.Invoke());
            }
        }

        private void LoadTorSettings()
        {
            TorSettings tor = getUserSettings.Invoke().GetTorSettings();
            bridgeSets = tor.Clone();
            bridgeSets.EnsureSets();
            suppressBridgeTypeLoad = true;
            checkBoxTorEnabled.IsChecked = tor.GetEnabled();
            comboBoxTorMode.SelectedValue = tor.GetMode();
            comboBoxExitCountry.SelectedValue = tor.GetExitCountry() ?? "";
            textBoxTorSocksPort.Text = tor.GetSocksPort().ToString();
            BindActiveSetToEditor();
            textBlockTorStatus.Text = torManager.IsAvailable
                ? Localize("Lang.Tor.Status.Ready")
                : Localize("Lang.Tor.Status.Unavailable");
            if (!bridgeTypeHooked)
            {
                comboBoxBridgeType.SelectionChanged += OnBridgeTypeChanged;
                bridgeTypeHooked = true;
            }
        }

        private TorSettings BuildTorSettingsFromUi()
        {
            TorSettings existing = getUserSettings.Invoke().GetTorSettings();
            EnsureBridgeSets(existing);
            FlushEditorInto(string.IsNullOrWhiteSpace(loadedSetId) ? bridgeSets.ActiveSetId : loadedSetId);
            return new TorSettings
            {
                Enabled = checkBoxTorEnabled.IsChecked == true,
                Mode = GetSelectedValue(comboBoxTorMode, existing.GetMode()),
                BridgeType = bridgeSets.GetBridgeType(),
                ExitCountry = GetSelectedValue(comboBoxExitCountry, existing.GetExitCountry() ?? ""),
                SocksPort = int.TryParse(textBoxTorSocksPort.Text, out int sp) && sp > 0 ? sp : existing.GetSocksPort(),
                ControlPort = existing.GetControlPort(),
                BridgeLines = bridgeSets.GetBridgeLines(),
                LastLatencyMs = existing.LastLatencyMs,
                ActiveSetId = bridgeSets.ActiveSetId,
                Sets = bridgeSets.GetSets().Select(set => set.Clone()).ToList()
            };
        }

        private void EnsureBridgeSets(TorSettings existing = null)
        {
            if (bridgeSets != null)
                return;

            bridgeSets = (existing ?? getUserSettings.Invoke().GetTorSettings()).Clone();
            bridgeSets.EnsureSets();
            loadedSetId = bridgeSets.ActiveSetId;
        }

        private void FlushEditorInto(string setId)
        {
            if (bridgeSets == null || string.IsNullOrWhiteSpace(setId))
                return;

            BridgeType type = comboBoxBridgeType.SelectedValue is BridgeType selected
                ? selected
                : bridgeSets.GetBridgeType();
            bridgeSets.ApplyToSet(setId, type, SplitBridgeLines(textBoxBridges.Text));
        }

        private void BindActiveSetToEditor()
        {
            suppressSetLoad = true;
            suppressBridgeTypeLoad = true;
            bridgeSets.RepairSetTypes();
            TorBridgeSet active = bridgeSets.GetActiveSet();
            textBoxBridges.Text = string.Join(Environment.NewLine, active.GetBridgeLines());
            comboBoxBridgeType.SelectedValue = active.BridgeType;
            comboBoxBridgeSets.ItemsSource = bridgeSets.GetSets()
                .Select(set => new KeyValuePair<string, string>(set.Id, DescribeSet(set)))
                .ToList();
            comboBoxBridgeSets.SelectedValue = bridgeSets.ActiveSetId;
            loadedSetId = bridgeSets.ActiveSetId;
            suppressBridgeTypeLoad = false;
            suppressSetLoad = false;
        }

        private string DescribeSet(TorBridgeSet set)
        {
            string typeName = BridgeTypes.TryGetValue(set.BridgeType, out string name)
                ? name
                : set.BridgeType.ToString();
            int count = set.GetBridgeLines().Count;
            int ordinal = bridgeSets.SetOrdinal(set);
            if (ordinal > 0)
                return string.Format(Localize("Lang.Tor.Set.Item.Numbered"), typeName, count, ordinal);
            return string.Format(Localize("Lang.Tor.Set.Item"), typeName, count);
        }

        private void OnBridgeSetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressSetLoad || bridgeSets == null)
                return;
            if (!(comboBoxBridgeSets.SelectedValue is string id) || string.IsNullOrWhiteSpace(id))
                return;
            if (string.Equals(id, loadedSetId, StringComparison.Ordinal))
                return;

            FlushEditorInto(loadedSetId);
            bridgeSets.SelectSet(id);
            BindActiveSetToEditor();
            PersistTorFromUi();
        }

        private void OnNewBridgeSetClick(object sender, RoutedEventArgs e)
        {
            EnsureBridgeSets();
            FlushEditorInto(loadedSetId);
            bridgeSets.AddSet(SelectedBridgeType(), new List<string>());
            BindActiveSetToEditor();
            textBlockTorStatus.Text = Localize("Lang.Tor.Status.SetAdded");
            PersistTorFromUi();
        }

        private void OnDeleteBridgeSetClick(object sender, RoutedEventArgs e)
        {
            EnsureBridgeSets();
            bridgeSets.RemoveActiveSet();
            BindActiveSetToEditor();
            PersistTorFromUi();
        }

        private static T GetSelectedValue<T>(ComboBox comboBox, T fallback)
        {
            if (comboBox.SelectedValue is T value)
                return value;

            if (comboBox.SelectedItem is KeyValuePair<T, string> pair)
                return pair.Key;

            return fallback;
        }

        private static List<string> SplitBridgeLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            return text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        public void ShowTorEditorOnly()
        {
            gridSettingsTabs.Visibility = Visibility.Collapsed;
            labelSettingsTitle.SetResourceReference(ContentProperty, "Lang.Tor.ServerListName");
            labelSettingsSubtitle.SetResourceReference(ContentProperty, "Lang.Tor.EditorSubtitle");
            Title = "Invisible Gorilla XRay - Tor";
            OnTorTabClick(null, null);
        }

        private void OnTorTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();

            SetEnableTorTabButton(false);
            SetActiveTorPanel(true);
        }

        private async void OnBridgeTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressBridgeTypeLoad)
                return;
            if (!(comboBoxBridgeType.SelectedValue is BridgeType type))
                return;

            if (type == BridgeType.NONE)
            {
                textBoxBridges.Text = string.Empty;
                textBlockTorStatus.Text = string.Format(Localize("Lang.Tor.Status.MethodSelected"), type);
                return;
            }

            List<string> existing = SplitBridgeLines(textBoxBridges.Text);
            if (existing.Count > 0 && TorrcBuilder.InferBridgeType(existing) == type)
            {
                textBlockTorStatus.Text = string.Format(Localize("Lang.Tor.Status.MethodSelected"), type);
                PersistTorFromUi();
                return;
            }

            List<string> defaults = DefaultBridges.ForType(type);
            if (defaults.Count > 0)
            {
                ApplyBridgeLines(type, defaults, string.Format(Localize("Lang.Tor.Status.LoadedDefaults"), defaults.Count));
                return;
            }

            if (type == BridgeType.WEBTUNNEL)
            {
                textBoxBridges.Text = string.Empty;
                textBlockTorStatus.Text = Localize("Lang.Tor.Status.AskingTor");
                await LoadRemoteBridgesAsync(type);
            }
        }

        private void OnUseDefaultBridgesClick(object sender, RoutedEventArgs e)
        {
            BridgeType type = comboBoxBridgeType.SelectedValue is BridgeType selected
                ? selected
                : BridgeType.OBFS4;
            if (type == BridgeType.NONE)
                type = BridgeType.OBFS4;

            List<string> defaults = DefaultBridges.ForType(type);
            if (defaults.Count == 0)
            {
                textBlockTorStatus.Text = type == BridgeType.WEBTUNNEL
                    ? Localize("Lang.Tor.Status.WebTunnelEmpty")
                    : string.Format(Localize("Lang.Tor.Status.LoadedDefaults"), 0);
                return;
            }

            ApplyBridgeLines(type, defaults, string.Format(Localize("Lang.Tor.Status.LoadedDefaults"), defaults.Count));
        }

        private async void OnCheckBridgeClick(object sender, RoutedEventArgs e)
        {
            if (!torManager.IsAvailable)
            {
                textBlockTorStatus.Text = Localize("Lang.Tor.Status.Unavailable");
                return;
            }

            BridgeType type = (BridgeType)comboBoxBridgeType.SelectedValue;
            List<string> lines = SplitBridgeLines(textBoxBridges.Text);

            buttonCheckBridge.IsEnabled = false;
            textBlockTorStatus.Text = Localize("Lang.Tor.Status.Checking");

            // CheckBridge creates/deletes temp directories and starts a tor process: any I/O
            // failure used to escape an async void handler (unhandled exception = crash) and
            // left the button disabled forever. Both are handled here.
            try
            {
                BridgeCheckResult result = await Task.Run(() => torManager.CheckBridge(type, lines, 150000));

                if (result.Success)
                {
                    string status = string.IsNullOrWhiteSpace(result.LinkLabel)
                        ? string.Format(Localize("Lang.Tor.Status.CheckOk"), result.LatencyMs)
                        : string.Format(Localize("Lang.Tor.Link.Badge"), result.LinkLabel);
                    textBlockTorStatus.Text = status;
                    PersistTorFromUi();
                }
                else
                {
                    textBlockTorStatus.Text = string.Format(Localize("Lang.Tor.Status.CheckFail"), result.Message);
                }
            }
            catch (Exception ex)
            {
                Core.DiagnosticLog.WriteException("SettingsWindow.CheckBridge", ex);
                textBlockTorStatus.Text = string.Format(
                    Localize("Lang.Tor.Status.CheckFail"),
                    ex.GetBaseException().Message);
            }
            finally
            {
                if (IsLoaded)
                    buttonCheckBridge.IsEnabled = true;
            }
        }

        private async void OnAskTorClick(object sender, RoutedEventArgs e)
        {
            buttonAskTor.IsEnabled = false;
            try
            {
                textBlockTorStatus.Text = Localize("Lang.Tor.Status.AskingTor");
                BridgeType type = SelectedBridgeType();
                await LoadRemoteBridgesAsync(type);
            }
            finally
            {
                if (IsLoaded)
                    buttonAskTor.IsEnabled = true;
            }
        }

        private void OnSnowflakeClick(object sender, RoutedEventArgs e)
            => ApplyBuiltinMethod(BridgeType.SNOWFLAKE, DefaultBridges.Snowflake);

        private void OnSnowflakeAmpClick(object sender, RoutedEventArgs e)
            => ApplyBuiltinMethod(BridgeType.SNOWFLAKE, DefaultBridges.SnowflakeAmp);

        private void OnMeekAzureClick(object sender, RoutedEventArgs e)
            => ApplyBuiltinMethod(BridgeType.MEEK_AZURE, DefaultBridges.MeekAzure);

        private void ApplyBuiltinMethod(BridgeType type, string bridgeLine)
        {
            EnsureBridgeSets();
            FlushEditorInto(loadedSetId);
            string origin = bridgeLine.IndexOf("ampcache=", StringComparison.OrdinalIgnoreCase) >= 0
                ? "snowflake-amp"
                : type == BridgeType.MEEK_AZURE ? "meek" : "snowflake";
            bridgeSets.OpenOrCreateOrigin(origin, type, new[] { bridgeLine });
            BindActiveSetToEditor();
            checkBoxTorEnabled.IsChecked = true;
            textBlockTorStatus.Text = string.Format(Localize("Lang.Tor.Status.MethodSelected"), type);
            PersistTorFromUi();
        }

        private BridgeType SelectedBridgeType()
        {
            return comboBoxBridgeType.SelectedValue is BridgeType type
                ? type
                : BridgeType.OBFS4;
        }

        private void ApplyBridgeLines(BridgeType type, IEnumerable<string> lines, string status)
        {
            suppressBridgeTypeLoad = true;
            comboBoxBridgeType.SelectedValue = type;
            suppressBridgeTypeLoad = false;
            textBoxBridges.Text = string.Join(Environment.NewLine, lines ?? Array.Empty<string>());
            checkBoxTorEnabled.IsChecked = true;
            textBlockTorStatus.Text = status;
            PersistTorFromUi();
        }

        private void PersistTorFromUi()
        {
            try
            {
                if (getUserSettings == null || onUpdateUserSettings == null)
                    return;
                UserSettings current = getUserSettings.Invoke();
                if (current == null)
                    return;
                current.Tor = BuildTorSettingsFromUi();
                onUpdateUserSettings.Invoke(current);
            }
            catch
            {
            }
        }

        private bool BridgeSelectionMatches(BridgeType type)
        {
            if (type == BridgeType.NONE)
                return true;
            return SelectedBridgeType() == type;
        }

        private async Task LoadRemoteBridgesAsync(BridgeType type)
        {
            string transport = type == BridgeType.NONE ? null : BridgeTypeToTransport(type);
            MoatResult result = await new MoatClient().GetCircumventionSettingsAsync(null, transport);
            if (!IsLoaded || !BridgeSelectionMatches(type))
                return;

            if (result.Success && result.Bridges.Count > 0)
            {
                BridgeType loaded = string.IsNullOrWhiteSpace(result.Transport)
                    ? (type == BridgeType.NONE ? BridgeType.OBFS4 : type)
                    : MapTransportToBridgeType(result.Transport);
                ApplyBridgeLines(
                    loaded,
                    result.Bridges,
                    string.Format(Localize("Lang.Tor.Status.AskTorOk"), result.Bridges.Count, loaded));
                return;
            }

            ApplyLocalBridgeFallback(type);
        }

        private void ApplyLocalBridgeFallback(BridgeType type)
        {
            BridgeType fallbackType = type == BridgeType.NONE ? BridgeType.OBFS4 : type;
            List<string> defaults = DefaultBridges.ForType(fallbackType);
            if (defaults.Count == 0)
            {
                textBlockTorStatus.Text = fallbackType == BridgeType.WEBTUNNEL
                    ? Localize("Lang.Tor.Status.WebTunnelEmpty")
                    : string.Format(Localize("Lang.Tor.Status.MoatFail"), "timed out");
                return;
            }

            ApplyBridgeLines(
                fallbackType,
                defaults,
                string.Format(Localize("Lang.Tor.Status.MoatFallback"), defaults.Count));
        }

        private static string BridgeTypeToTransport(BridgeType type)
        {
            switch (type)
            {
                case BridgeType.SNOWFLAKE: return "snowflake";
                case BridgeType.MEEK_AZURE: return "meek";
                case BridgeType.WEBTUNNEL: return "webtunnel";
                case BridgeType.OBFS4: return "obfs4";
                default: return "obfs4";
            }
        }

        private void OnBridgesEmailClick(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl(BridgeRequestLinks.BuildEmailUrl("obfs4"));
            textBlockTorStatus.Text = Localize("Lang.Tor.Status.RequestSent");
        }

        private void OnBridgesTelegramClick(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl(BridgeRequestLinks.TelegramBot);
            textBlockTorStatus.Text = Localize("Lang.Tor.Status.RequestSent");
        }

        private static BridgeType MapTransportToBridgeType(string transport)
        {
            switch ((transport ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "snowflake": return BridgeType.SNOWFLAKE;
                case "meek": case "meek-azure": case "meek_lite": return BridgeType.MEEK_AZURE;
                case "webtunnel": return BridgeType.WEBTUNNEL;
                default: return BridgeType.OBFS4;
            }
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        private async void OnFetchMoatClick(object sender, RoutedEventArgs e)
        {
            buttonFetchMoat.IsEnabled = false;
            try
            {
                BridgeType type = SelectedBridgeType();
                textBlockTorStatus.Text = Localize("Lang.Tor.Status.AskingTor");

                string transport = type == BridgeType.NONE ? "obfs4" : BridgeTypeToTransport(type);
                MoatResult result = await new MoatClient().GetCircumventionSettingsAsync(null, transport);
                if (!IsLoaded || !BridgeSelectionMatches(type))
                    return;

                if (result.Success && result.Bridges.Count > 0)
                {
                    BridgeType loaded = string.IsNullOrWhiteSpace(result.Transport)
                        ? (type == BridgeType.NONE ? BridgeType.OBFS4 : type)
                        : MapTransportToBridgeType(result.Transport);
                    ApplyBridgeLines(
                        loaded,
                        result.Bridges,
                        string.Format(Localize("Lang.Tor.Status.MoatBridges"), result.Bridges.Count));
                    return;
                }

                if (type == BridgeType.OBFS4 || type == BridgeType.NONE)
                {
                    textBlockTorStatus.Text = Localize("Lang.Tor.Status.FetchingCaptcha");
                    MoatResult challenge = await new MoatClient().RequestChallengeAsync("obfs4");
                    if (!IsLoaded || !BridgeSelectionMatches(type))
                        return;

                    if (challenge.Success && challenge.Challenge != null)
                    {
                        activeMoatChallenge = challenge.Challenge;
                        try
                        {
                            BitmapImage bitmap = new BitmapImage();
                            using (MemoryStream stream = new MemoryStream(challenge.Challenge.ImagePng))
                            {
                                bitmap.BeginInit();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.StreamSource = stream;
                                bitmap.EndInit();
                            }
                            imageCaptcha.Source = bitmap;
                        }
                        catch { }
                        panelCaptcha.Visibility = Visibility.Visible;
                        textBoxCaptcha.Text = string.Empty;
                        textBlockTorStatus.Text = Localize("Lang.Tor.Status.CaptchaReady");
                        return;
                    }
                }

                ApplyLocalBridgeFallback(type == BridgeType.NONE ? BridgeType.OBFS4 : type);
            }
            finally
            {
                if (IsLoaded)
                    buttonFetchMoat.IsEnabled = true;
            }
        }

        private async void OnSubmitCaptchaClick(object sender, RoutedEventArgs e)
        {
            if (activeMoatChallenge == null)
                return;

            buttonSubmitCaptcha.IsEnabled = false;
            textBlockTorStatus.Text = Localize("Lang.Tor.Status.SubmittingCaptcha");

            MoatClient client = new MoatClient();
            MoatResult result = await client.SubmitSolutionAsync(activeMoatChallenge, textBoxCaptcha.Text ?? string.Empty);

            if (result.Success && result.Bridges.Count > 0)
            {
                ApplyBridgeLines(BridgeType.OBFS4, result.Bridges, string.Format(Localize("Lang.Tor.Status.MoatBridges"), result.Bridges.Count));
                panelCaptcha.Visibility = Visibility.Collapsed;
                activeMoatChallenge = null;
                textBlockTorStatus.Text = string.Format(Localize("Lang.Tor.Status.MoatBridges"), result.Bridges.Count);
            }
            else
            {
                textBlockTorStatus.Text = string.Format(Localize("Lang.Tor.Status.MoatFail"), result.Error);
            }

            buttonSubmitCaptcha.IsEnabled = true;
        }

        private void ReloadDiscoveredApps()
        {
            HashSet<string> selectedPaths = GetCurrentSelectedAppPathSet();
            discoveredWindowsApps.Clear();
            discoveredWindowsApps.AddRange(WindowsInstalledAppDiscovery.GetApps());
            RenderWindowsAppRules(selectedPaths);
        }

        private void RenderWindowsAppRules(ISet<string> selectedPaths)
        {
            panelAppRulesItems.Children.Clear();
            appRuleToggles.Clear();

            foreach (WindowsInstalledAppInfo app in discoveredWindowsApps)
            {
                CheckBox toggle = new()
                {
                    IsChecked = selectedPaths.Contains(app.ExecutablePath),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = Brushes.White
                };

                appRuleToggles[app.ExecutablePath] = toggle;

                TextBlock title = new()
                {
                    Text = app.DisplayName,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };

                TextBlock meta = new()
                {
                    Text = app.ExecutablePath,
                    Foreground = new SolidColorBrush(Color.FromRgb(201, 201, 201)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };

                StackPanel textPanel = new();
                textPanel.Children.Add(title);
                textPanel.Children.Add(meta);

                Grid contentGrid = new();
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(toggle, 0);
                Grid.SetColumn(textPanel, 1);
                contentGrid.Children.Add(toggle);
                contentGrid.Children.Add(textPanel);

                Border card = new()
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
                    BorderThickness = new Thickness(1),
                    Child = contentGrid
                };

                card.MouseLeftButtonUp += (_, eventArgs) =>
                {
                    if (eventArgs.OriginalSource is CheckBox)
                        return;

                    toggle.IsChecked = !(toggle.IsChecked ?? false);
                };
                panelAppRulesItems.Children.Add(card);
            }

            textBlockNoDiscoveredApps.Visibility = discoveredWindowsApps.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private HashSet<string> GetSavedSelectedAppPathSet()
        {
            return getAppRules.Invoke()
                .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.AppId))
                .Select(rule => rule.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private HashSet<string> GetCurrentSelectedAppPathSet()
        {
            if (appRuleToggles.Count == 0)
                return GetSavedSelectedAppPathSet();

            return appRuleToggles
                .Where(pair => pair.Value.IsChecked == true)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private List<AppRule> BuildSelectedDesktopAppRules()
        {
            if (appRuleToggles.Count == 0)
                return getAppRules.Invoke();

            HashSet<string> selectedIds = GetCurrentSelectedAppPathSet();
            List<AppRule> rules = discoveredWindowsApps
                .Where(app => selectedIds.Contains(app.ExecutablePath))
                .Select(app => new AppRule(
                    appId: app.ExecutablePath,
                    displayName: app.DisplayName,
                    iconRef: app.IconRef,
                    enabled: true))
                .ToList();

            foreach (AppRule existingRule in getAppRules.Invoke())
            {
                if (!existingRule.Enabled || string.IsNullOrWhiteSpace(existingRule.AppId))
                    continue;

                if (!selectedIds.Contains(existingRule.AppId))
                    continue;

                if (rules.Any(rule => string.Equals(rule.AppId, existingRule.AppId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                rules.Add(existingRule.Clone());
            }

            return rules;
        }

        private void OnBasicTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();

            SetEnableBasicTabButton(false);
            SetActiveBasicPanel(true);
        }

        private void OnPortTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();

            SetEnablePortTabButton(false);
            SetActivePortPanel(true);
        }

        private void OnTunTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();

            SetEnableTunTabButton(false);
            SetActiveTunPanel(true);
        }

        private void OnLogTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();

            SetEnableLogTabButton(false);
            SetActiveLogPanel(true);
        }

        private void OnModeComboBoxSelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateUIBasedOnMode();

            void UpdateUIBasedOnMode()
            {
                Mode mode = (Mode)comboBoxMode.SelectedValue;
                
                comboBoxProtocol.IsEnabled = mode != Mode.TUN;
                checkBoxUseSystemProxy.IsEnabled = mode != Mode.TUN;
                checkBoxEnableUdp.IsEnabled = mode == Mode.TUN;
            }
        }

        private void OnAnalyticsClick(object sender, RoutedEventArgs e)
        {
            PolicyWindow policyWindow = openPolicyWindow.Invoke();
            policyWindow.Owner = this;
            policyWindow.ShowDialog();
        }

        private void OnConfirmButtonClick(object sender, RoutedEventArgs e)
        {
            // Ports used to go straight into int.Parse: an empty field threw inside the
            // dispatcher handler (silently swallowed, CONFIRM looked dead) and a value like
            // 70000 wrapped to 4464 in the native core.
            if (!TryReadPort(textBoxProxyPort))
                return;
            if (!TryReadPort(textBoxTunPort))
                return;
            if (!TryReadPort(textBoxTestPort))
                return;

            UserSettings currentSettings = getUserSettings.Invoke();
            UserSettings userSettings = new UserSettings(
                language: comboBoxLanguage.SelectedValue.ToString(),
                mode: (Mode)comboBoxMode.SelectedValue,
                protocol: (Protocol)comboBoxProtocol.SelectedValue,
                logLevel: (LogLevel)comboBoxLogLevel.SelectedValue,
                isSystemProxyUse: checkBoxUseSystemProxy.IsChecked.Value,
                isUdpEnable: checkBoxEnableUdp.IsChecked.Value,
                isRunningAtStartup: checkBoxRunAtStartup.IsChecked.Value,
                isStartHidden: checkBoxStartHidden.IsChecked.Value,
                isAutoConnect: checkBoxAutoConnect.IsChecked.Value,
                isSendingAnalytics: checkBoxSendAnalytics.IsChecked.Value,
                proxyPort: ReadPort(textBoxProxyPort),
                tunPort: ReadPort(textBoxTunPort),
                testPort: ReadPort(textBoxTestPort),
                tunIp: textBoxTunDeviceIp.Text,
                dns: textBoxTunDns.Text,
                logPath: textBoxLogPath.Text,
                appRulesMode: currentSettings.GetAppRulesMode(),
                appRules: currentSettings.GetAppRules(),
                appRuleTemplates: currentSettings.GetAppRuleTemplates(),
                appRuleTemplateBindings: currentSettings.GetAppRuleTemplateBindings()
            );

            userSettings.Tor = BuildTorSettingsFromUi();
            userSettings.Goida = currentSettings.GetGoidaSettings().Clone();
            userSettings.OpenFlux = currentSettings.GetOpenFluxProfile().Clone();
            userSettings.OpenFluxProfiles = currentSettings.GetOpenFluxProfiles();

            SendRunAtStartupActivationEvent();
            ForceSendAnalyticsActivationEvent();
            onUpdateUserSettings.Invoke(userSettings);

            Close();

            void SendRunAtStartupActivationEvent()
            {
                if (!IsUserChangeRunningAtStartupSetting())
                    return;

                if (userSettings.GetRunningAtStartupEnabled())
                    AnalyticsService.SendEvent(new RunAtStartupActivatedEvent());
                else
                    AnalyticsService.SendEvent(new RunAtStartupDeactivatedEvent());

                bool IsUserChangeRunningAtStartupSetting()
                {
                    return getRunningAtStartupEnabled.Invoke() != checkBoxRunAtStartup.IsChecked.Value;
                }
            }

            void ForceSendAnalyticsActivationEvent()
            {
                if (!IsUserChangeSendingAnalyticsEnabledSetting())
                    return;

                if (userSettings.GetSendingAnalyticsEnabled())
                    AnalyticsService.SendEvent(new AnalyticsActivatedEvent(), true);
                else
                    AnalyticsService.SendEvent(new AnalyticsDeactivatedEvent(), true);

                bool IsUserChangeSendingAnalyticsEnabledSetting()
                {
                    return getSendingAnalyticsEnabled.Invoke() != checkBoxSendAnalytics.IsChecked.Value;
                }
            }
        }

        private bool TryReadPort(TextBox box)
        {
            if (TryParsePort(box?.Text, out _))
                return true;

            MessageBox.Show(
                this,
                string.Format("{0}: 1-65535", Localize("Lang.Window.Settings.Tab.Port")),
                Localize("Lang.Window.Settings.WindowTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            box?.Focus();
            box?.SelectAll();
            return false;
        }

        private static int ReadPort(TextBox box) => TryParsePort(box?.Text, out int port) ? port : 0;

        private static bool TryParsePort(string text, out int port)
        {
            return int.TryParse((text ?? string.Empty).Trim(), out port) && port >= 1 && port <= 65535;
        }

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnRefreshAppRulesClick(object sender, RoutedEventArgs e)
        {
            RefreshAppRulesSummary();
        }

        private void OnManageAppRulesClick(object sender, RoutedEventArgs e)
        {
            AppRulesWindow appRulesWindow = openAppRulesWindow.Invoke();
            appRulesWindow.Owner = this;
            appRulesWindow.ShowDialog();
            RefreshAppRulesSummary();
        }

        private void RefreshAppRulesSummary()
        {
            UserSettings settings = getUserSettings.Invoke();
            AppRulesMode mode = settings.GetEffectiveAppRulesMode();
            AppRuleTemplate template = settings.GetEffectiveAppRuleTemplate();
            int selectedCount = settings.GetEffectiveEnabledAppRules().Count;

            textBlockAppRulesSummary.Text = string.Format(
                Localize("Lang.AppRules.Summary"),
                GetTemplateDisplayName(settings.GetBoundAppRuleTemplateId(), template),
                LocalizeMode(mode),
                selectedCount);

            textBlockAppRulesConfigHint.Text = string.IsNullOrWhiteSpace(settings.GetCurrentConfigPath())
                ? Localize("Lang.AppRules.NoConfigSelected")
                : string.Format(
                    Localize("Lang.AppRules.BoundConfig"),
                    settings.GetCurrentConfigPath());
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

        private string Localize(string key)
        {
            return TryFindResource(key)?.ToString() ?? key;
        }

        private void SetActiveBasicPanel(bool isActive) => SetActivePanel(panelBasic, isActive);

        private void SetActivePortPanel(bool isActive) => SetActivePanel(panelPort, isActive);

        private void SetActiveTunPanel(bool isActive) => SetActivePanel(panelTun, isActive);

        private void SetActiveLogPanel(bool isActive) => SetActivePanel(panelLog, isActive);

        private void SetActiveTorPanel(bool isActive) => SetActivePanel(panelTor, isActive);

        private void SetActivePanel(UIElement panel, bool isActive)
        {
            panel.Visibility = isActive ? Visibility.Visible : Visibility.Hidden;
        }
        
        private void HideAllPanels()
        {
            SetActiveBasicPanel(false);
            SetActivePortPanel(false);
            SetActiveTunPanel(false);
            SetActiveLogPanel(false);
            SetActiveTorPanel(false);
        }

        private void SetEnableBasicTabButton(bool isEnabled) => SetEnableButton(buttonBasicTab, isEnabled);

        private void SetEnablePortTabButton(bool isEnabled) => SetEnableButton(buttonPortTab, isEnabled);

        private void SetEnableTunTabButton(bool isEnabled) => SetEnableButton(buttonTunTab, isEnabled);

        private void SetEnableLogTabButton(bool isEnabled) => SetEnableButton(buttonLogTab, isEnabled);

        private void SetEnableTorTabButton(bool isEnabled) => SetEnableButton(buttonTorTab, isEnabled);

        private void SetEnableButton(Button button, bool isEnabled)
        {
            button.IsEnabled = isEnabled;
        }

        private void EnableAllTabs()
        {
            SetEnableBasicTabButton(true);
            SetEnablePortTabButton(true);
            SetEnableTunTabButton(true);
            SetEnableLogTabButton(true);
            SetEnableTorTabButton(true);
        }
    }
}