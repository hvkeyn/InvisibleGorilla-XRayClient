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

        public bool IsServerRunning => isRunWorkerBusy;
        private bool isDialogOpen;
        private readonly ConnectionInfoService connectionInfoService = new();
        private DispatcherTimer connectionInfoTimer;
        private CancellationTokenSource connectionInfoCancellation;
        private CancellationTokenSource scheduledRefreshCts;
        private readonly SemaphoreSlim connectionInfoRefreshGate = new(1, 1);
        private int connectionInfoFailureRetries;
        private int connectionInfoProxyWaitRetries;
        private const int ConnectionInfoMaxFailureRetries = 4;
        private const int ConnectionInfoMaxProxyWaitRetries = 8;
        private DateTime connectionCheckGraceUntil = DateTime.MinValue;
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
        private Func<string> getServerDisplayText;
        private Action<string> onRunServer;
        private Action onCancelServer;
        private Action onStopServer;
        private Action onDisableMode;
        private Action onGenerateClientId;
        private Action onGitHubClick;
        private Action onBugReportingClick;
        private Action<string> onCustomLinkClick;
        private Func<InvisibleGorillaXRay.Services.Goida.GoidaMainPresentation> getGoidaPresentation;
        private Func<IWebProxy> createActiveProbeProxy;

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
            Func<string> getServerDisplayText,
            Action<string> onRunServer,
            Action onStopServer,
            Action onCancelServer,
            Action onDisableMode,
            Action onGenerateClientId,
            Action onGitHubClick,
            Action onBugReportingClick,
            Action<string> onCustomLinkClick,
            Func<InvisibleGorillaXRay.Services.Goida.GoidaMainPresentation> getGoidaPresentation = null,
            Func<IWebProxy> createActiveProbeProxy = null)
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
            this.getServerDisplayText = getServerDisplayText;
            this.onRunServer = onRunServer;
            this.onCancelServer = onCancelServer;
            this.onStopServer = onStopServer;
            this.enableMode = enableMode;
            this.onDisableMode = onDisableMode;
            this.onGenerateClientId = onGenerateClientId;
            this.onGitHubClick = onGitHubClick;
            this.onBugReportingClick = onBugReportingClick;
            this.onCustomLinkClick = onCustomLinkClick;
            this.getGoidaPresentation = getGoidaPresentation;
            this.createActiveProbeProxy = createActiveProbeProxy;

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
            if (getServerDisplayText != null)
                textServerConfig.Text = getServerDisplayText.Invoke();
            else
            {
                Config config = getConfig?.Invoke();
                textServerConfig.Text = config == null
                    ? LocalizationService.GetTerm(Localization.NO_SERVER_CONFIGURATION)
                    : config.Name;
            }

            ApplyGoidaSummary();
        }

        private static readonly IBrush WifiInactiveBrush = new SolidColorBrush(Color.Parse("#4A4A4A"));

        private void ApplyGoidaSummary()
        {
            if (panelGoidaDetails == null)
                return;

            InvisibleGorillaXRay.Services.Goida.GoidaMainPresentation presentation =
                getGoidaPresentation?.Invoke() ?? new InvisibleGorillaXRay.Services.Goida.GoidaMainPresentation();

            if (string.IsNullOrWhiteSpace(presentation.Summary))
            {
                panelGoidaDetails.IsVisible = false;
                return;
            }

            IBrush statusBrush = new SolidColorBrush(Color.Parse(presentation.ColorHex));
            int level = presentation.SignalLevel;

            wifiDot.Fill = statusBrush;
            wifiArcInner.Stroke = level >= 2 ? statusBrush : WifiInactiveBrush;
            wifiArcMiddle.Stroke = level >= 3 ? statusBrush : WifiInactiveBrush;
            wifiArcOuter.Stroke = level >= 4 ? statusBrush : WifiInactiveBrush;

            textGoidaSignalLabel.Text = LocalizeOrKey(presentation.QualityLabel);
            textGoidaSignalLabel.Foreground = statusBrush;
            textGoidaLatency.Text = presentation.LatencyText;
            textGoidaSummary.Text = presentation.Summary;
            panelGoidaDetails.IsVisible = true;
        }

        private string LocalizeOrKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            object value = null;
            Avalonia.Application.Current?.TryFindResource(key, out value);
            return value as string ?? key;
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
            connectionInfoFailureRetries = 0;
            connectionInfoProxyWaitRetries = 0;
            connectionCheckGraceUntil = DateTime.UtcNow.AddSeconds(20);
            statusRun.IsVisible = true;
            statusStop.IsVisible = false;
            statusWaitForRun.IsVisible = false;

            buttonStop.IsVisible = true;
            buttonCancel.IsVisible = false;
            buttonRun.IsVisible = false;
            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(12));
        }

        private void ShowStopStatus()
        {
            isConnected = false;
            connectionInfoFailureRetries = 0;
            connectionInfoProxyWaitRetries = 0;
            connectionCheckGraceUntil = DateTime.MinValue;
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
                Interval = TimeSpan.FromSeconds(60)
            };
            connectionInfoTimer.Tick += (_, _) => _ = RefreshConnectionInfoAsync();
            connectionInfoTimer.Start();
            _ = RefreshConnectionInfoAsync();
        }

        private void ScheduleConnectionInfoRefresh(TimeSpan delay)
        {
            scheduledRefreshCts?.Cancel();
            scheduledRefreshCts = new CancellationTokenSource();
            CancellationToken token = scheduledRefreshCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    await RefreshConnectionInfoAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch
                {
                }
            });
        }

        private async Task RefreshConnectionInfoAsync()
        {
            if (!await connectionInfoRefreshGate.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                connectionInfoCancellation?.Cancel();
                connectionInfoCancellation = new CancellationTokenSource();
                CancellationToken token = connectionInfoCancellation.Token;

                bool connected = isConnected;

                if (connected && DateTime.UtcNow < connectionCheckGraceUntil)
                {
                    TimeSpan wait = connectionCheckGraceUntil - DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    ScheduleConnectionInfoRefresh(wait);
                    return;
                }

                IWebProxy probeProxy = null;
                string modeText = string.Empty;
                UserSettings settings = getUserSettings?.Invoke();
                if (settings != null)
                {
                    if (connected)
                        probeProxy = createActiveProbeProxy?.Invoke();

                    if (probeProxy == null)
                    {
                        probeProxy = ConnectionProbe.BuildExitProxy(
                            connected,
                            settings.GetMode(),
                            settings.GetProtocol(),
                            settings.GetProxyPort());
                    }

                    string outbound = ConnectionProbe.DetectOutboundProtocol(getConfig?.Invoke()?.Path);
                    modeText = ConnectionProbe.DescribeMode(
                        settings.GetMode(),
                        settings.GetProtocol(),
                        settings.GetTorSettings(),
                        outbound);
                }

                if (connected && probeProxy == null)
                {
                    if (connectionInfoProxyWaitRetries < ConnectionInfoMaxProxyWaitRetries)
                    {
                        connectionInfoProxyWaitRetries++;
                        ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(3));
                        return;
                    }

                    connectionInfoProxyWaitRetries = 0;
                }
                else
                {
                    connectionInfoProxyWaitRetries = 0;
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
            finally
            {
                connectionInfoRefreshGate.Release();
            }
        }

        private void ApplyConnectionInfo(ConnectionInfo info)
        {
            if (!info.Ok)
            {
                if (isConnected
                    && !string.Equals(info.Error, "Canceled", StringComparison.OrdinalIgnoreCase)
                    && connectionInfoFailureRetries < ConnectionInfoMaxFailureRetries)
                {
                    connectionInfoFailureRetries++;
                    infoStatusDot.Fill = Brushes.Gray;
                    textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Checking");
                    ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(5));
                    return;
                }

                infoStatusDot.Fill = Brushes.IndianRed;
                textInfoIp.Text = Loc("Lang.ConnectionInfo.Unknown");
                textInfoFlag.Text = "🌐";
                textInfoLocation.Text = "—";
                textInfoCountry.Text = string.Empty;
                textInfoOrg.Text = "—";
                textInfoVerdict.Text = string.Format(Loc("Lang.ConnectionInfo.Error"), info.Error);
                return;
            }

            connectionInfoFailureRetries = 0;

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
