using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InvisibleGorillaXRay.Mac.Views
{
    using Models;
    using Services;
    using Services.Analytics.SettingsWindow;

    public partial class SettingsWindow : Window
    {
        private static readonly Dictionary<string, string> Languages = new()
        {
            { "en-US", "English" },
            { "ru-RU", "Русский" }
        };

        private static readonly Dictionary<Mode, string> Modes = new()
        {
            { Mode.PROXY, "Proxy" },
            { Mode.TUN, "TUN" }
        };

        private static readonly Dictionary<Protocol, string> Protocols = new()
        {
            { Protocol.HTTP, "http" },
            { Protocol.SOCKS, "socks" }
        };

        private static readonly Dictionary<LogLevel, string> LogLevels = new()
        {
            { LogLevel.NONE, "None" },
            { LogLevel.DEBUG, "Debug" },
            { LogLevel.INFO, "Info" },
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
        private Func<string> getDns;
        private Func<LogLevel> getLogLevel;
        private Func<string> getLogPath;
        private Func<PolicyWindow> openPolicyWindow;

        private Action<UserSettings> onUpdateUserSettings;

        private AnalyticsService AnalyticsService => ServiceLocator.Get<AnalyticsService>();

        public SettingsWindow()
        {
            InitializeComponent();
            InitializeItems();
        }

        private void InitializeItems()
        {
            comboBoxLanguage.ItemsSource = Languages.ToList();
            comboBoxLanguage.DisplayMemberBinding = new Avalonia.Data.Binding("Value");
            comboBoxLanguage.SelectedValueBinding = new Avalonia.Data.Binding("Key");

            comboBoxMode.ItemsSource = Modes.ToList();
            comboBoxMode.DisplayMemberBinding = new Avalonia.Data.Binding("Value");
            comboBoxMode.SelectedValueBinding = new Avalonia.Data.Binding("Key");

            comboBoxProtocol.ItemsSource = Protocols.ToList();
            comboBoxProtocol.DisplayMemberBinding = new Avalonia.Data.Binding("Value");
            comboBoxProtocol.SelectedValueBinding = new Avalonia.Data.Binding("Key");

            comboBoxLogLevel.ItemsSource = LogLevels.ToList();
            comboBoxLogLevel.DisplayMemberBinding = new Avalonia.Data.Binding("Value");
            comboBoxLogLevel.SelectedValueBinding = new Avalonia.Data.Binding("Key");
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
            Func<PolicyWindow> openPolicyWindow,
            Action<UserSettings> onUpdateUserSettings)
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
            this.openPolicyWindow = openPolicyWindow;
            this.onUpdateUserSettings = onUpdateUserSettings;

            UpdateUI();
        }

        private void UpdateUI()
        {
            SelectComboBoxItem(comboBoxLanguage, getLanguage.Invoke());
            SelectComboBoxItem(comboBoxMode, getMode.Invoke());
            SelectComboBoxItem(comboBoxProtocol, getProtocol.Invoke());

            checkBoxUseSystemProxy.IsChecked = getSystemProxyUsed.Invoke();
            checkBoxEnableUdp.IsChecked = getUdpEnabled.Invoke();
            checkBoxRunAtStartup.IsChecked = getRunningAtStartupEnabled.Invoke();
            checkBoxStartHidden.IsChecked = getStartHiddenEnabled.Invoke();
            checkBoxAutoConnect.IsChecked = getAutoConnectEnabled.Invoke();
            checkBoxSendAnalytics.IsChecked = getSendingAnalyticsEnabled.Invoke();

            textBoxProxyPort.Text = getProxyPort.Invoke().ToString();
            textBoxTunPort.Text = getTunPort.Invoke().ToString();
            textBoxTestPort.Text = getTestPort.Invoke().ToString();

            textBoxTunDeviceIp.Text = getDeviceIp.Invoke();
            textBoxTunDns.Text = getDns.Invoke();

            SelectComboBoxItem(comboBoxLogLevel, getLogLevel.Invoke());
            textBoxLogPath.Text = Path.GetFullPath(getLogPath.Invoke());
        }

        private void SelectComboBoxItem<T>(ComboBox comboBox, T key)
        {
            var items = comboBox.ItemsSource as System.Collections.IEnumerable;
            if (items == null) return;

            int index = 0;
            foreach (var item in items)
            {
                if (item is KeyValuePair<T, string> kvp && EqualityComparer<T>.Default.Equals(kvp.Key, key))
                {
                    comboBox.SelectedIndex = index;
                    return;
                }
                index++;
            }
        }

        private T GetComboBoxSelectedKey<T>(ComboBox comboBox)
        {
            if (comboBox.SelectedItem is KeyValuePair<T, string> kvp)
                return kvp.Key;
            return default;
        }

        private void OnBasicTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();
            buttonBasicTab.IsEnabled = false;
            panelBasic.IsVisible = true;
        }

        private void OnPortTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();
            buttonPortTab.IsEnabled = false;
            panelPort.IsVisible = true;
        }

        private void OnTunTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();
            buttonTunTab.IsEnabled = false;
            panelTun.IsVisible = true;
        }

        private void OnLogTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();
            buttonLogTab.IsEnabled = false;
            panelLog.IsVisible = true;
        }

        private void OnModeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Mode mode = GetComboBoxSelectedKey<Mode>(comboBoxMode);
            comboBoxProtocol.IsEnabled = mode != Mode.TUN;
            checkBoxUseSystemProxy.IsEnabled = mode != Mode.TUN;
            checkBoxEnableUdp.IsEnabled = mode == Mode.TUN;
        }

        private void OnConfirmButtonClick(object sender, RoutedEventArgs e)
        {
            UserSettings userSettings = new UserSettings(
                language: GetComboBoxSelectedKey<string>(comboBoxLanguage) ?? "en-US",
                mode: GetComboBoxSelectedKey<Mode>(comboBoxMode),
                protocol: GetComboBoxSelectedKey<Protocol>(comboBoxProtocol),
                logLevel: GetComboBoxSelectedKey<LogLevel>(comboBoxLogLevel),
                isSystemProxyUse: checkBoxUseSystemProxy.IsChecked == true,
                isUdpEnable: checkBoxEnableUdp.IsChecked == true,
                isRunningAtStartup: checkBoxRunAtStartup.IsChecked == true,
                isStartHidden: checkBoxStartHidden.IsChecked == true,
                isAutoConnect: checkBoxAutoConnect.IsChecked == true,
                isSendingAnalytics: checkBoxSendAnalytics.IsChecked == true,
                proxyPort: int.TryParse(textBoxProxyPort.Text, out int pp) ? pp : 10801,
                tunPort: int.TryParse(textBoxTunPort.Text, out int tp) ? tp : 10802,
                testPort: int.TryParse(textBoxTestPort.Text, out int tep) ? tep : 10803,
                tunIp: textBoxTunDeviceIp.Text ?? "10.0.236.10",
                dns: textBoxTunDns.Text ?? "8.8.8.8",
                logPath: textBoxLogPath.Text ?? "./Logs"
            );

            SendRunAtStartupActivationEvent(userSettings);
            ForceSendAnalyticsActivationEvent(userSettings);
            onUpdateUserSettings.Invoke(userSettings);

            Close();
        }

        private void SendRunAtStartupActivationEvent(UserSettings userSettings)
        {
            if (getRunningAtStartupEnabled.Invoke() == (checkBoxRunAtStartup.IsChecked == true))
                return;

            if (userSettings.GetRunningAtStartupEnabled())
                AnalyticsService.SendEvent(new RunAtStartupActivatedEvent());
            else
                AnalyticsService.SendEvent(new RunAtStartupDeactivatedEvent());
        }

        private void ForceSendAnalyticsActivationEvent(UserSettings userSettings)
        {
            if (getSendingAnalyticsEnabled.Invoke() == (checkBoxSendAnalytics.IsChecked == true))
                return;

            if (userSettings.GetSendingAnalyticsEnabled())
                AnalyticsService.SendEvent(new AnalyticsActivatedEvent(), true);
            else
                AnalyticsService.SendEvent(new AnalyticsDeactivatedEvent(), true);
        }

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void HideAllPanels()
        {
            panelBasic.IsVisible = false;
            panelPort.IsVisible = false;
            panelTun.IsVisible = false;
            panelLog.IsVisible = false;
        }

        private void EnableAllTabs()
        {
            buttonBasicTab.IsEnabled = true;
            buttonPortTab.IsEnabled = true;
            buttonTunTab.IsEnabled = true;
            buttonLogTab.IsEnabled = true;
        }
    }
}
