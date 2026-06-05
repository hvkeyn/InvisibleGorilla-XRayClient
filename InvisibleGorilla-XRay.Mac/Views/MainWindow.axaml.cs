using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace InvisibleGorillaXRay.Mac.Views
{
    using Core;
    using Models;
    using Values;
    using InvisibleGorillaXRay.Services;
    using InvisibleGorillaXRay.Services.Analytics.General;
    using InvisibleGorillaXRay.Services.Analytics.MainWindow;

    public partial class MainWindow : Window
    {
        private bool isRerunRequest;
        private bool isRunWorkerBusy;
        private bool isDialogOpen;
        private readonly ConnectionInfoService connectionInfoService = new();
        private DispatcherTimer connectionInfoTimer;
        private CancellationTokenSource connectionInfoCancellation;
        private string baselineIp = string.Empty;
        private bool isConnected;

        private Func<bool> isNeedToShowPolicyWindow;
        private Func<bool> shouldStartHidden;
        private Func<bool> isNeedToAutoConnect;
        private Func<Config> getConfig;
        private Func<UserSettings> getUserSettings;
        private Func<Status> loadConfig;
        private Func<Status> enableMode;
        private Func<Status> checkForUpdate;
        private Func<ServerWindow> openServerWindow;
        private Func<SettingsWindow> openSettingsWindow;
        private Func<UpdateWindow> openUpdateWindow;
        private Func<AboutWindow> openAboutWindow;
        private Func<PolicyWindow> openPolicyWindow;
        private Action<string> onRunServer;
        private Action onCancelServer;
        private Action onStopServer;
        private Action onDisableMode;
        private Action onGenerateClientId;
        private Action onGitHubClick;
        private Action onBugReportingClick;
        private Action<string> onCustomLinkClick;

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();
        private AnalyticsService AnalyticsService => ServiceLocator.Get<AnalyticsService>();

        public MainWindow()
        {
            InitializeComponent();

            Opened += OnWindowOpened;
        }

        public void Setup(
            Func<bool> isNeedToShowPolicyWindow,
            Func<bool> shouldStartHidden,
            Func<bool> isNeedToAutoConnect,
            Func<Config> getConfig,
            Func<UserSettings> getUserSettings,
            Func<Status> loadConfig,
            Func<Status> enableMode,
            Func<Status> checkForUpdate,
            Func<ServerWindow> openServerWindow,
            Func<SettingsWindow> openSettingsWindow,
            Func<UpdateWindow> openUpdateWindow,
            Func<AboutWindow> openAboutWindow,
            Func<PolicyWindow> openPolicyWindow,
            Action<string> onRunServer,
            Action onStopServer,
            Action onCancelServer,
            Action onDisableMode,
            Action onGenerateClientId,
            Action onGitHubClick,
            Action onBugReportingClick,
            Action<string> onCustomLinkClick)
        {
            this.isNeedToShowPolicyWindow = isNeedToShowPolicyWindow;
            this.shouldStartHidden = shouldStartHidden;
            this.isNeedToAutoConnect = isNeedToAutoConnect;
            this.getConfig = getConfig;
            this.getUserSettings = getUserSettings;
            this.loadConfig = loadConfig;
            this.checkForUpdate = checkForUpdate;
            this.openServerWindow = openServerWindow;
            this.openSettingsWindow = openSettingsWindow;
            this.openUpdateWindow = openUpdateWindow;
            this.openAboutWindow = openAboutWindow;
            this.openPolicyWindow = openPolicyWindow;
            this.onRunServer = onRunServer;
            this.onCancelServer = onCancelServer;
            this.onStopServer = onStopServer;
            this.enableMode = enableMode;
            this.onDisableMode = onDisableMode;
            this.onGenerateClientId = onGenerateClientId;
            this.onGitHubClick = onGitHubClick;
            this.onBugReportingClick = onBugReportingClick;
            this.onCustomLinkClick = onCustomLinkClick;

            UpdateUI();
        }

        private void OnWindowOpened(object sender, EventArgs e)
        {
            try
            {
                TryOpenPolicyWindow();
                InitializeConnectionInfoTimer();
                TryStartHidden();
                TryAutoConnect();
                RunUpdateCheck();

                AnalyticsService.SendEvent(new AppOpenedEvent());
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MacMainWindow.OnWindowOpened", ex);
            }
        }

        public void UpdateUI()
        {
            Config config = getConfig?.Invoke();

            if (config == null)
            {
                textServerConfig.Text = LocalizationService.GetTerm(Localization.NO_SERVER_CONFIGURATION);
                return;
            }

            textServerConfig.Text = config.Name;
        }

        public void TryRerun()
        {
            if (!isRunWorkerBusy)
                return;

            onStopServer.Invoke();
            isRerunRequest = true;
        }

        public void TryDisableModeAndRerun()
        {
            if (!isRunWorkerBusy)
                return;

            Task.Run(() => onDisableMode.Invoke());
            onStopServer.Invoke();
            isRerunRequest = true;
        }

        private void RunWorkerAsync()
        {
            if (isRunWorkerBusy)
                return;

            isRunWorkerBusy = true;

            Task.Run(() =>
            {
                try
                {
                    Dispatcher.UIThread.InvokeAsync(ShowWaitForRunStatus);

                    Status configStatus = loadConfig.Invoke();

                    if (configStatus.Code == Code.ERROR)
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            HandleConfigError(configStatus);
                            ShowStopStatus();
                        });
                        return;
                    }

                    Status modeStatus = enableMode.Invoke();

                    if (modeStatus.Code == Code.ERROR)
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"Mode error: {modeStatus.Content}");
                            ShowStopStatus();
                        });
                        return;
                    }
                    else if (modeStatus.Code == Code.INFO && modeStatus.SubCode == SubCode.CANCELED)
                    {
                        Dispatcher.UIThread.InvokeAsync(ShowStopStatus);
                        return;
                    }

                    Dispatcher.UIThread.InvokeAsync(ShowRunStatus);

                    onRunServer.Invoke(configStatus.Content.ToString());

                    Dispatcher.UIThread.InvokeAsync(ShowStopStatus);
                }
                finally
                {
                    isRunWorkerBusy = false;

                    if (isRerunRequest)
                    {
                        isRerunRequest = false;
                        RunWorkerAsync();
                    }
                }
            });
        }

        private void HandleConfigError(Status configStatus)
        {
            switch (configStatus.SubCode)
            {
                case SubCode.NO_CONFIG:
                    OpenServerWindow();
                    break;
                case SubCode.INVALID_CONFIG:
                    System.Diagnostics.Debug.WriteLine(
                        $"Invalid config: {configStatus.Content}");
                    break;
            }
        }

        private void RunUpdateCheck()
        {
            Task.Run(() =>
            {
                try
                {
                    Status updateStatus = checkForUpdate.Invoke();
                    if (updateStatus.SubCode == SubCode.UPDATE_AVAILABLE)
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            notificationUpdate.IsVisible = true;
                        });
                    }
                }
                catch { }
            });
        }

        private void OnManageServersClick(object sender, TappedEventArgs e)
        {
            e.Handled = true;
            OpenServerWindow();
            AnalyticsService.SendEvent(new ManageServersButtonClickedEvent());
        }

        private void OnRunButtonClick(object sender, RoutedEventArgs e)
        {
            RunWorkerAsync();
            AnalyticsService.SendEvent(new RunButtonClickedEvent());
        }

        private void OnStopButtonClick(object sender, RoutedEventArgs e)
        {
            onStopServer.Invoke();
            Task.Run(() => onDisableMode.Invoke());
            isRerunRequest = false;
            AnalyticsService.SendEvent(new StopButtonClickedEvent());
        }

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            if (!isRunWorkerBusy)
                return;

            onCancelServer.Invoke();
        }

        private void OnGitHubButtonClick(object sender, RoutedEventArgs e)
        {
            onGitHubClick.Invoke();
            AnalyticsService.SendEvent(new GitHubButtonClickedEvent());
        }

        private void OnBugReportingButtonClick(object sender, RoutedEventArgs e)
        {
            onBugReportingClick.Invoke();
            AnalyticsService.SendEvent(new BugReportingButtonClickedEvent());
        }

        private void OnSettingsButtonClick(object sender, RoutedEventArgs e)
        {
            OpenSettingsWindow();
            AnalyticsService.SendEvent(new SettingsButtonClickedEvent());
        }

        private void OnUpdateButtonClick(object sender, RoutedEventArgs e)
        {
            OpenUpdateWindow();
            AnalyticsService.SendEvent(new UpdateButtonClickedEvent());
        }

        private void OnRefreshInfoButtonClick(object sender, RoutedEventArgs e)
        {
            _ = RefreshConnectionInfoAsync();
        }

        private void OnAboutButtonClick(object sender, RoutedEventArgs e)
        {
            OpenAboutWindow();
            AnalyticsService.SendEvent(new AboutButtonClickedEvent());
        }

        private void TryStartHidden()
        {
            if (!shouldStartHidden.Invoke())
                return;

            Hide();
        }

        private void TryAutoConnect()
        {
            if (!isNeedToAutoConnect.Invoke())
                return;

            OnRunButtonClick(null, null);
        }

        private void TryOpenPolicyWindow()
        {
            if (!isNeedToShowPolicyWindow.Invoke())
                return;

            onGenerateClientId.Invoke();
            AnalyticsService.SendEvent(new NewUserEvent());

            PolicyWindow policyWindow = openPolicyWindow.Invoke();
            policyWindow.ShowDialog(this);
        }

        private async void OpenServerWindow()
        {
            if (isDialogOpen) return;
            isDialogOpen = true;
            try
            {
                ServerWindow serverWindow = openServerWindow.Invoke();
                await serverWindow.ShowDialog(this);
                UpdateUI();
            }
            finally { isDialogOpen = false; }
        }

        private async void OpenSettingsWindow()
        {
            if (isDialogOpen) return;
            isDialogOpen = true;
            try
            {
                SettingsWindow settingsWindow = openSettingsWindow.Invoke();
                await settingsWindow.ShowDialog(this);
                UpdateUI();
            }
            finally { isDialogOpen = false; }
        }

        private void OpenUpdateWindow()
        {
            UpdateWindow updateWindow = openUpdateWindow.Invoke();
            updateWindow.ShowDialog(this);
        }

        private void OpenAboutWindow()
        {
            AboutWindow aboutWindow = openAboutWindow.Invoke();
            aboutWindow.ShowDialog(this);
        }

        private void ShowRunStatus()
        {
            isConnected = true;
            statusRun.IsVisible = true;
            statusStop.IsVisible = false;
            statusWaitForRun.IsVisible = false;

            buttonStop.IsVisible = true;
            buttonCancel.IsVisible = false;
            buttonRun.IsVisible = false;
            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(3));
        }

        private void ShowStopStatus()
        {
            isConnected = false;
            statusStop.IsVisible = true;
            statusRun.IsVisible = false;
            statusWaitForRun.IsVisible = false;

            buttonRun.IsVisible = true;
            buttonCancel.IsVisible = false;
            buttonStop.IsVisible = false;
            ScheduleConnectionInfoRefresh(TimeSpan.FromMilliseconds(200));
        }

        private void ShowWaitForRunStatus()
        {
            statusWaitForRun.IsVisible = true;
            statusStop.IsVisible = false;
            statusRun.IsVisible = false;

            buttonCancel.IsVisible = true;
            buttonRun.IsVisible = false;
            buttonStop.IsVisible = false;
        }

        private void InitializeConnectionInfoTimer()
        {
            if (connectionInfoTimer != null)
                return;

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
            connectionInfoCancellation?.Cancel();
            connectionInfoCancellation = new CancellationTokenSource();
            CancellationToken token = connectionInfoCancellation.Token;

            bool connected = isConnected;

            // Mirror real traffic: probe through the local xray listener in proxy mode, or
            // directly in TUN/disconnected. A direct request would ignore a SOCKS proxy and
            // always report the real ISP IP even while the tunnel works.
            IWebProxy probeProxy = null;
            string modeText = string.Empty;
            UserSettings settings = getUserSettings?.Invoke();
            if (settings != null)
            {
                probeProxy = ConnectionProbe.BuildExitProxy(connected, settings.GetMode(), settings.GetProtocol(), settings.GetProxyPort());
                string outbound = ConnectionProbe.DetectOutboundProtocol(getConfig?.Invoke()?.Path);
                modeText = ConnectionProbe.DescribeMode(settings.GetMode(), settings.GetProtocol(), settings.GetTorSettings(), outbound);
            }

            Dispatcher.UIThread.Post(() =>
            {
                infoStatusDot.Fill = Brushes.Gray;
                textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Checking");
                SetModeBadge(connected, modeText);
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
                infoStatusDot.Fill = Brushes.IndianRed;
                textInfoIp.Text = Loc("Lang.ConnectionInfo.Unknown");
                textInfoFlag.Text = "🌐";
                textInfoLocation.Text = "—";
                textInfoCountry.Text = string.Empty;
                textInfoOrg.Text = "—";
                textInfoVerdict.Text = string.Format(Loc("Lang.ConnectionInfo.Error"), info.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(baselineIp) && !isConnected)
                baselineIp = info.Ip;

            bool changedFromBaseline = !string.IsNullOrWhiteSpace(baselineIp)
                && !string.Equals(baselineIp, info.Ip, StringComparison.OrdinalIgnoreCase);

            infoStatusDot.Fill = isConnected && changedFromBaseline
                ? Brushes.LightGreen
                : isConnected
                    ? Brushes.IndianRed
                    : Brushes.Gray;

            textInfoIp.Text = info.Ip;
            textInfoFlag.Text = !string.IsNullOrWhiteSpace(info.FlagEmoji) ? info.FlagEmoji : "🌐";
            textInfoLocation.Text = !string.IsNullOrWhiteSpace(info.PlaceLine)
                ? info.PlaceLine
                : !string.IsNullOrWhiteSpace(info.CountryName) ? info.CountryName : "—";
            textInfoCountry.Text = !string.IsNullOrWhiteSpace(info.PlaceLine) ? info.CountryName : string.Empty;
            textInfoOrg.Text = string.IsNullOrWhiteSpace(info.Org) ? "—" : info.Org;

            if (!isConnected)
            {
                baselineIp = info.Ip;
                textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Idle");
            }
            else if (changedFromBaseline)
            {
                bool torActive = getUserSettings?.Invoke()?.GetTorSettings()?.GetEnabled() == true;
                textInfoVerdict.Text = Loc(torActive ? "Lang.ConnectionInfo.ProtectedTor" : "Lang.ConnectionInfo.Protected");
            }
            else
            {
                textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Exposed");
            }
        }

        private void SetModeBadge(bool connected, string modeText)
        {
            if (connected && !string.IsNullOrEmpty(modeText))
            {
                textInfoMode.Text = $"{Loc("Lang.ConnectionInfo.Mode")} {modeText}";
                borderInfoMode.IsVisible = true;
                return;
            }

            textInfoMode.Text = string.Empty;
            borderInfoMode.IsVisible = false;
        }

        private string Loc(string key)
        {
            string term = LocalizationService.GetTerm(key);
            return string.IsNullOrWhiteSpace(term) ? key : term;
        }

        public void ShowAndActivate()
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
