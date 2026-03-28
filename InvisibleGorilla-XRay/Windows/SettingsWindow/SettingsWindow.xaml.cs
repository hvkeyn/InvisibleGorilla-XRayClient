using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InvisibleGorillaXRay
{
    using Models;
    using Services;
    using Services.Analytics.SettingsWindow;

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
        private Func<AppRulesMode> getAppRulesMode;
        private Func<List<AppRule>> getAppRules;
        private Func<PolicyWindow> openPolicyWindow;

        private Action<UserSettings> onUpdateUserSettings;
        private readonly List<WindowsInstalledAppInfo> discoveredWindowsApps = new();
        private readonly Dictionary<string, CheckBox> appRuleToggles = new(StringComparer.OrdinalIgnoreCase);

        private AnalyticsService AnalyticsService => ServiceLocator.Get<AnalyticsService>();

        public SettingsWindow()
        {
            InitializeComponent();
            InitializeItems();

            void InitializeItems()
            {
                InitializeLanguageItems();
                InitializeModeItems();
                InitializeProtocolItems();
                InitializeLogLevelItems();

                void InitializeLanguageItems() => comboBoxLanguage.ItemsSource = Languages;

                void InitializeModeItems() => comboBoxMode.ItemsSource = Modes;

                void InitializeProtocolItems() => comboBoxProtocol.ItemsSource = Protocols;

                void InitializeLogLevelItems() => comboBoxLogLevel.ItemsSource = logLevels;
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
            Func<AppRulesMode> getAppRulesMode,
            Func<List<AppRule>> getAppRules,
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
            this.getAppRulesMode = getAppRulesMode;
            this.getAppRules = getAppRules;
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
                checkBoxEnableAppRules.IsChecked = getAppRulesMode.Invoke() == AppRulesMode.BYPASS_SELECTED_APPS;
                ReloadDiscoveredApps();
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
                proxyPort: int.Parse(textBoxProxyPort.Text),
                tunPort: int.Parse(textBoxTunPort.Text),
                testPort: int.Parse(textBoxTestPort.Text),
                tunIp: textBoxTunDeviceIp.Text,
                dns: textBoxTunDns.Text,
                logPath: textBoxLogPath.Text,
                appRulesMode: checkBoxEnableAppRules.IsChecked == true
                    ? AppRulesMode.BYPASS_SELECTED_APPS
                    : AppRulesMode.DISABLED,
                appRules: BuildSelectedDesktopAppRules()
            );
            
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

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnRefreshAppRulesClick(object sender, RoutedEventArgs e)
        {
            ReloadDiscoveredApps();
        }

        private void SetActiveBasicPanel(bool isActive) => SetActivePanel(panelBasic, isActive);

        private void SetActivePortPanel(bool isActive) => SetActivePanel(panelPort, isActive);

        private void SetActiveTunPanel(bool isActive) => SetActivePanel(panelTun, isActive);

        private void SetActiveLogPanel(bool isActive) => SetActivePanel(panelLog, isActive);

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
        }

        private void SetEnableBasicTabButton(bool isEnabled) => SetEnableButton(buttonBasicTab, isEnabled);

        private void SetEnablePortTabButton(bool isEnabled) => SetEnableButton(buttonPortTab, isEnabled);

        private void SetEnableTunTabButton(bool isEnabled) => SetEnableButton(buttonTunTab, isEnabled);

        private void SetEnableLogTabButton(bool isEnabled) => SetEnableButton(buttonLogTab, isEnabled);

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
        }
    }
}