using System;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.ComponentModel;

namespace InvisibleGorillaXRay
{
    using Core;
    using Models;
    using Values;
    using Services;
    using Services.Goida;
    using Services.Analytics.General;
    using Services.Analytics.MainWindow;

    public partial class MainWindow : Window
    {
        private bool isRerunRequest;
        private bool shutdownRequested;

        private readonly ConnectionInfoService connectionInfoService = new ConnectionInfoService();
        private DispatcherTimer connectionInfoTimer;
        private CancellationTokenSource connectionInfoCts;
        private bool isConnected;
        private string baselineIp;

        // null = not checked yet / VPN idle, true = tunnel passes traffic, false = confirmed IP leak.
        private bool? lastTunnelCheckOk;
        private int consecutiveTunnelFailures;
        private int connectionInfoFailureRetries;
        private const int ConnectionInfoMaxFailureRetries = 4;
        private DateTime goidaSwitchGraceUntil = DateTime.MinValue;
        private DateTime connectionCheckGraceUntil = DateTime.MinValue;

        private Func<bool> isNeedToShowPolicyWindow;
        private Func<bool> shouldStartHidden;
        private Func<bool> isNeedToAutoConnect;
        private Func<Config> getConfig;
        private Func<UserSettings> getUserSettings;
        private Func<Status> loadConfig;
        private Func<Status> enableMode;
        private Func<Status> checkForUpdate;
        private Func<Status> checkForBroadcast;
        private Func<ServerWindow> openServerWindow;
        private Func<SettingsWindow> openSettingsWindow;
        private Func<UpdateWindow> openUpdateWindow;
        private Func<AboutWindow> openAboutWindow;
        private Func<PolicyWindow> openPolicyWindow;
        private Func<string> getServerDisplayText;
        private Func<GoidaMainPresentation> getGoidaPresentation;
        private Action<string> onRunServer;
        private Action onCancelServer;
        private Action onStopServer;
        private Action onDisableMode;
        private Action onGenerateClientId;
        private Action onGitHubClick;
        private Action onBugReportingClick;
        private Action<string> onCustomLinkClick;
        private Func<bool> onTunnelBroken;

        private BackgroundWorker runWorker;
        private BackgroundWorker updateWorker;
        private BackgroundWorker broadcastWorker;

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();
        private AnalyticsService AnalyticsService => ServiceLocator.Get<AnalyticsService>();

        public MainWindow()
        {
            InitializeComponent();
            InitializeRunWorker();
            InitializeUpdateWorker();
            InitializeBroadcastWorker();

            updateWorker.RunWorkerAsync();
            broadcastWorker.RunWorkerAsync();

            InitializeConnectionInfoTimer();

            void InitializeConnectionInfoTimer()
            {
                connectionInfoTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(20)
                };
                connectionInfoTimer.Tick += (sender, e) => RefreshConnectionInfo();
            }

            void InitializeRunWorker()
            {
                runWorker = new BackgroundWorker();

                runWorker.RunWorkerCompleted += (sender, e) => {
                    if (isRerunRequest)
                    {
                        runWorker.RunWorkerAsync();
                        isRerunRequest = false;
                    }
                };

                runWorker.DoWork += (sender, e) => {
                    EnsureBaselineBeforeConnect();

                    Dispatcher.BeginInvoke(new Action(delegate {
                        ShowWaitForRunStatus();
                    }));

                    Status configStatus = loadConfig.Invoke();

                    if (configStatus.Code == Code.ERROR)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate {
                            HandleError();
                            ShowStopStatus();
                        }));

                        return;
                    }

                    Status modeStatus = enableMode.Invoke();

                    if (modeStatus.Code == Code.ERROR)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate {
                            MessageBox.Show(
                                this,
                                modeStatus.Content.ToString(), 
                                Caption.ERROR, 
                                MessageBoxButton.OK, 
                                MessageBoxImage.Error
                            );
                            ShowStopStatus();
                        }));

                        return;
                    }
                    else if (modeStatus.Code == Code.INFO)
                    {
                        if (modeStatus.SubCode == SubCode.CANCELED)
                        {
                            Dispatcher.BeginInvoke(new Action(delegate {
                                ShowStopStatus();
                            }));

                            return;
                        }
                    }

                    Dispatcher.BeginInvoke(new Action(delegate {
                        ShowRunStatus();
                    }));

                    try
                    {
                        onRunServer.Invoke(configStatus.Content.ToString());
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate {
                            MessageBox.Show(
                                this,
                                ex.Message,
                                Caption.ERROR,
                                MessageBoxButton.OK,
                                MessageBoxImage.Error
                            );
                            ShowStopStatus();
                        }));

                        return;
                    }

                    Dispatcher.BeginInvoke(new Action(delegate {
                        ShowStopStatus();
                    }));

                    void HandleError()
                    {
                        if (IsAnotherWindowOpened())
                            return;
                        
                        ForceShowWindowIfNeeded();

                        switch (configStatus.SubCode)
                        {
                            case SubCode.NO_CONFIG:
                                HandleNoConfigError();
                                break;
                            case SubCode.INVALID_CONFIG:
                                HandleInvalidConfigError();
                                break;
                            default:
                                return;
                        }

                        bool IsWindowHidden() => this.Visibility == Visibility.Hidden;

                        bool IsAnotherWindowOpened() => Application.Current.Windows.Count > 1;

                        void ForceShowWindowIfNeeded()
                        {
                            if (!IsWindowHidden())
                                return;
                            
                            this.Show();
                        }

                        void HandleNoConfigError()
                        {
                            MessageBoxResult result = MessageBox.Show(
                                this,
                                configStatus.Content.ToString(), 
                                Caption.WARNING, 
                                MessageBoxButton.OK, 
                                MessageBoxImage.Warning
                            );

                            if (result == MessageBoxResult.OK)
                                OpenServerWindow();
                        }

                        void HandleInvalidConfigError()
                        {
                            MessageBox.Show(
                                this,
                                configStatus.Content.ToString(), 
                                Caption.ERROR, 
                                MessageBoxButton.OK, 
                                MessageBoxImage.Error
                            );
                        }
                    }
                };
            }

            void InitializeUpdateWorker()
            {
                updateWorker = new BackgroundWorker();

                updateWorker.DoWork += (sender, e) => {
                    Status updateStatus = checkForUpdate.Invoke();
                    if (IsUpdateAvailable())
                        Dispatcher.BeginInvoke(new Action(delegate {
                            notificationUpdate.Visibility = Visibility.Visible;
                        }));

                    bool IsUpdateAvailable() => updateStatus.SubCode == SubCode.UPDATE_AVAILABLE;
                };
            }

            void InitializeBroadcastWorker()
            {
                broadcastWorker = new BackgroundWorker();

                broadcastWorker.DoWork += (sender, e) => {
                    Status broadcastStatus = checkForBroadcast.Invoke();
                    if (IsBroadcastAvailable())
                        Dispatcher.BeginInvoke(new Action(delegate {
                            barBroadcast.Setup(broadcastStatus.Content as Broadcast, onCustomLinkClick);
                            barBroadcast.Appear();
                        }));

                    bool IsBroadcastAvailable() => broadcastStatus.Code == Code.SUCCESS;
                };
            }
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
            Func<Status> checkForBroadcast,
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
            Func<GoidaMainPresentation> getGoidaPresentation = null,
            Func<bool> onTunnelBroken = null)
        {
            this.isNeedToShowPolicyWindow = isNeedToShowPolicyWindow;
            this.shouldStartHidden = shouldStartHidden;
            this.isNeedToAutoConnect = isNeedToAutoConnect;
            this.getConfig = getConfig;
            this.getUserSettings = getUserSettings;
            this.loadConfig = loadConfig;
            this.checkForUpdate = checkForUpdate;
            this.checkForBroadcast = checkForBroadcast;
            this.openServerWindow = openServerWindow;
            this.openSettingsWindow = openSettingsWindow;
            this.openUpdateWindow = openUpdateWindow;
            this.openAboutWindow = openAboutWindow;
            this.openPolicyWindow = openPolicyWindow;
            this.getServerDisplayText = getServerDisplayText;
            this.getGoidaPresentation = getGoidaPresentation;
            this.onRunServer = onRunServer;
            this.onCancelServer = onCancelServer;
            this.onStopServer = onStopServer;
            this.enableMode = enableMode;
            this.onDisableMode = onDisableMode;
            this.onGenerateClientId = onGenerateClientId;
            this.onGitHubClick = onGitHubClick;
            this.onBugReportingClick = onBugReportingClick;
            this.onCustomLinkClick = onCustomLinkClick;
            this.onTunnelBroken = onTunnelBroken;

            UpdateUI();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            TryOpenPolicyWindow();
            TryStartHidden();
            TryAutoConnect();

            connectionInfoTimer.Start();
            RefreshConnectionInfo();

            AnalyticsService.SendEvent(new AppOpenedEvent());
        }

        public void UpdateUI()
        {
            if (getServerDisplayText != null)
                textServerConfig.Text = getServerDisplayText.Invoke();
            else
            {
                Config config = getConfig.Invoke();
                textServerConfig.Text = config == null
                    ? LocalizationService.GetTerm(Localization.NO_SERVER_CONFIGURATION)
                    : config.Name;
            }

            ApplyGoidaSummary();
        }

        private void ApplyGoidaSummary()
        {
            if (textGoidaSummary == null || panelGoidaDetails == null)
                return;

            GoidaMainPresentation presentation = getGoidaPresentation?.Invoke() ?? new GoidaMainPresentation();
            if (string.IsNullOrWhiteSpace(presentation.Summary))
            {
                textGoidaSummary.Text = string.Empty;
                textGoidaSignalLabel.Text = string.Empty;
                textGoidaLatency.Text = string.Empty;
                panelGoidaDetails.Visibility = Visibility.Collapsed;
                return;
            }

            string qualityLabel = presentation.QualityLabel;
            string colorHex = presentation.ColorHex;
            int signalLevel = presentation.SignalLevel;
            string latencyText = presentation.LatencyText;

            // TCP probe latency ≠ working VLESS. While connected, only show "excellent"
            // after the live tunnel check confirms traffic actually flows.
            if (isConnected && lastTunnelCheckOk != true)
            {
                qualityLabel = "Lang.Goida.Signal.NoTunnel";
                colorHex = "#E85D5D";
                signalLevel = 0;
                latencyText = string.Empty;
            }

            Brush statusBrush = (Brush)new BrushConverter().ConvertFromString(colorHex)!;

            textGoidaSummary.Text = presentation.Summary;
            textGoidaSignalLabel.Text = Loc(qualityLabel);
            textGoidaSignalLabel.Foreground = statusBrush;
            textGoidaLatency.Text = latencyText;
            wifiGoidaSignal.SetSignal(signalLevel, statusBrush);
            panelGoidaDetails.Visibility = Visibility.Visible;
        }

        public void RequestGracefulShutdown()
        {
            if (shutdownRequested)
                return;

            shutdownRequested = true;
            isRerunRequest = false;
            connectionInfoCts?.Cancel();
            ShowStopStatus();

            // Hard watchdog: if anything below hangs (native StopServer, dispatcher),
            // kill the process so the app never appears frozen in the tray.
            // Process.Kill (unlike Environment.Exit) skips ProcessExit handlers
            // which could block on the same hung native call.
            Thread watchdog = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(15));
                DiagnosticLog.Write("MainWindow.RequestGracefulShutdown", "Watchdog fired: killing process");
                try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
                catch { Environment.Exit(0); }
            });
            watchdog.IsBackground = true;
            watchdog.Start();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    System.Threading.Tasks.Task stopTask = System.Threading.Tasks.Task.Run(() =>
                    {
                        onStopServer.Invoke();
                        onDisableMode.Invoke();
                    });

                    if (!stopTask.Wait(TimeSpan.FromSeconds(10)))
                        DiagnosticLog.Write("MainWindow.RequestGracefulShutdown", "Stop timed out after 10s");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("MainWindow.RequestGracefulShutdown", ex);
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
                }
            });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!shutdownRequested && runWorker.IsBusy)
            {
                e.Cancel = true;
                RequestGracefulShutdown();
                return;
            }

            if (!shutdownRequested)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnClosing(e);
        }

        public void TryRerun()
        {
            if (!runWorker.IsBusy)
                return;

            isRerunRequest = true;
            System.Threading.Tasks.Task.Run(() => onStopServer.Invoke());
        }

        public void TryDisableModeAndRerun()
        {
            if (!runWorker.IsBusy)
                return;

            isRerunRequest = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                onStopServer.Invoke();
                onDisableMode.Invoke();
            });
        }

        public bool IsServerRunning => runWorker.IsBusy;

        private bool resumeServerAfterNativeTest;

        public bool TryPauseForNativeTest()
        {
            if (!runWorker.IsBusy)
                return false;

            resumeServerAfterNativeTest = true;
            onStopServer.Invoke();

            for (int attempt = 0; attempt < 60 && runWorker.IsBusy; attempt++)
                Thread.Sleep(50);

            if (runWorker.IsBusy)
            {
                resumeServerAfterNativeTest = false;
                return false;
            }

            return true;
        }

        public void TryResumeAfterNativeTest()
        {
            if (!resumeServerAfterNativeTest || runWorker.IsBusy)
                return;

            resumeServerAfterNativeTest = false;
            runWorker.RunWorkerAsync();
        }

        private void OnManageServersClick(object sender, RoutedEventArgs e)
        {
            OpenServerWindow();
            AnalyticsService.SendEvent(new ManageServersButtonClickedEvent());
        }

        private void OnRunButtonClick(object sender, RoutedEventArgs e)
        {
            // After Stop the UI shows RUN immediately, but the worker stays busy until
            // the native server actually shuts down. A silent return here made the
            // button feel dead — queue a restart instead so the click always works.
            if (runWorker.IsBusy)
            {
                isRerunRequest = true;
                ShowWaitForRunStatus();
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { onStopServer.Invoke(); }
                    catch (Exception ex) { DiagnosticLog.WriteException("MainWindow.OnRunButtonClick", ex); }
                });
                return;
            }

            runWorker.RunWorkerAsync();
            AnalyticsService.SendEvent(new RunButtonClickedEvent());
        }

        private void OnStopButtonClick(object sender, RoutedEventArgs e)
        {
            isRerunRequest = false;
            connectionInfoCts?.Cancel();
            ShowStopStatus();
            AnalyticsService.SendEvent(new StopButtonClickedEvent());

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    onStopServer.Invoke();
                    onDisableMode.Invoke();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("MainWindow.OnStopButtonClick", ex);
                }
            });
        }

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            // Also drops a queued restart (RUN pressed while the previous session
            // was still stopping), otherwise CANCEL appears to do nothing.
            isRerunRequest = false;

            if (!runWorker.IsBusy)
            {
                ShowStopStatus();
                return;
            }

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

        private void OnAboutButtonClick(object sender, RoutedEventArgs e)
        {
            OpenAboutWindow();
            AnalyticsService.SendEvent(new AboutButtonClickedEvent());
        }

        private void TryStartHidden()
        {
            if (!shouldStartHidden.Invoke())
                return;

            if(ShouldAvoidStartHidden())
                return;
            
            OnClosing(new CancelEventArgs());

            bool ShouldAvoidStartHidden() => Application.Current.Windows.Count > 1;
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
            policyWindow.Owner = this;
            policyWindow.ShowDialog();
        }

        private void OpenServerWindow()
        {
            ServerWindow serverWindow = openServerWindow.Invoke();
            serverWindow.Owner = this;
            serverWindow.ShowDialog();
        }

        private void OpenSettingsWindow()
        {
            SettingsWindow settingsWindow = openSettingsWindow.Invoke();
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
        }

        private void OpenUpdateWindow()
        {
            UpdateWindow updateWindow = openUpdateWindow.Invoke();
            updateWindow.Owner = this;
            updateWindow.ShowDialog();
        }

        private void OpenAboutWindow()
        {
            AboutWindow aboutWindow = openAboutWindow.Invoke();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }

        private void ShowRunStatus()
        {
            statusRun.Visibility = Visibility.Visible;
            statusStop.Visibility = Visibility.Hidden;
            statusWaitForRun.Visibility = Visibility.Hidden;

            buttonStop.Visibility = Visibility.Visible;
            buttonCancel.Visibility = Visibility.Hidden;
            buttonRun.Visibility = Visibility.Hidden;

            isConnected = true;
            consecutiveTunnelFailures = 0;
            connectionInfoFailureRetries = 0;
            connectionCheckGraceUntil = DateTime.UtcNow.AddSeconds(15);
            if (IsGoidaProfileActive())
                goidaSwitchGraceUntil = DateTime.UtcNow.AddSeconds(20);
            connectionInfoTimer.Interval = IsGoidaProfileActive()
                ? TimeSpan.FromSeconds(8)
                : TimeSpan.FromSeconds(20);
            if (!connectionInfoTimer.IsEnabled)
                connectionInfoTimer.Start();
            // Give the tunnel/proxy a moment to take over routing before probing the exit IP.
            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(3));
        }

        private void ShowStopStatus()
        {
            statusStop.Visibility = Visibility.Visible;
            statusRun.Visibility = Visibility.Hidden;
            statusWaitForRun.Visibility = Visibility.Hidden;

            buttonRun.Visibility = Visibility.Visible;
            buttonCancel.Visibility = Visibility.Hidden;
            buttonStop.Visibility = Visibility.Hidden;

            isConnected = false;
            consecutiveTunnelFailures = 0;
            connectionInfoTimer.Interval = TimeSpan.FromSeconds(20);
            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(1));
        }

        private void ShowWaitForRunStatus()
        {
            statusWaitForRun.Visibility = Visibility.Visible;
            statusStop.Visibility = Visibility.Hidden;
            statusRun.Visibility = Visibility.Hidden;

            buttonCancel.Visibility = Visibility.Visible;
            buttonRun.Visibility = Visibility.Hidden;
            buttonStop.Visibility = Visibility.Hidden;
        }

        private void OnRefreshInfoButtonClick(object sender, RoutedEventArgs e)
        {
            RefreshConnectionInfo();
        }

        private void ScheduleConnectionInfoRefresh(TimeSpan delay)
        {
            DispatcherTimer once = new DispatcherTimer { Interval = delay };
            once.Tick += (s, e) =>
            {
                once.Stop();
                RefreshConnectionInfo();
            };
            once.Start();
        }

        private async void RefreshConnectionInfo()
        {
            connectionInfoCts?.Cancel();
            connectionInfoCts = new CancellationTokenSource();
            CancellationToken token = connectionInfoCts.Token;

            bool connected = isConnected;

            textInfoIp.Text = Loc("Lang.ConnectionInfo.Checking");
            ClearConnectionInfoDetails();
            infoStatusDot.Fill = Brushes.Gray;

            // Route the probe exactly like user traffic: through the local xray listener in
            // proxy mode, or directly in TUN/disconnected. A plain request ignores a SOCKS
            // system proxy and would always report the real ISP IP.
            IWebProxy probeProxy = null;
            string modeText = string.Empty;
            UserSettings settings = getUserSettings?.Invoke();
            if (settings != null)
            {
                probeProxy = ConnectionProbe.BuildExitProxy(connected, settings.GetMode(), settings.GetProtocol(), settings.GetProxyPort());
                string outbound = ConnectionProbe.DetectOutboundProtocol(getConfig?.Invoke()?.Path);
                modeText = ConnectionProbe.DescribeMode(settings.GetMode(), settings.GetProtocol(), settings.GetTorSettings(), outbound);
            }

            SetModeBadge(connected, modeText);

            ConnectionInfo info;
            try
            {
                info = await connectionInfoService.LookupAsync(probeProxy, token);
            }
            catch (Exception)
            {
                info = new ConnectionInfo { Ok = false };
            }

            if (token.IsCancellationRequested)
                return;

            if (!info.Ok)
            {
                // A failed lookup (SSL timeout, ipinfo down, etc.) is not proof the tunnel
                // is broken. Retry with backoff; never tear down a live session for this.
                if (connected
                    && !string.Equals(info.Error, "canceled", StringComparison.OrdinalIgnoreCase)
                    && connectionInfoFailureRetries < ConnectionInfoMaxFailureRetries)
                {
                    connectionInfoFailureRetries++;
                    textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Checking");
                    textInfoVerdict.Foreground = Brushes.Gray;
                    infoStatusDot.Fill = Brushes.Gray;
                    ApplyGoidaSummary();
                    ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(4));
                    return;
                }

                textInfoIp.Text = Loc("Lang.ConnectionInfo.Unknown");
                ClearConnectionInfoDetails();
                textInfoVerdict.Text = string.IsNullOrWhiteSpace(info.Error)
                    ? Loc("Lang.ConnectionInfo.Error")
                    : $"{Loc("Lang.ConnectionInfo.Error")} ({info.Error})";
                textInfoVerdict.Foreground = Brushes.Gray;
                infoStatusDot.Fill = Brushes.Gray;
                ApplyGoidaSummary();
                return;
            }

            connectionInfoFailureRetries = 0;

            textInfoIp.Text = info.Ip;
            ApplyConnectionInfoDetails(info);

            ApplyVerdict(connected, info.Ip);
            ApplyGoidaSummary();

            if (isConnected && lastTunnelCheckOk == false)
                RegisterTunnelFailure();
            else
                consecutiveTunnelFailures = 0;
        }

        private bool IsGoidaProfileActive()
        {
            GoidaMainPresentation presentation = getGoidaPresentation?.Invoke();
            return presentation != null && !string.IsNullOrWhiteSpace(presentation.Summary);
        }

        private void EnsureBaselineBeforeConnect()
        {
            if (!string.IsNullOrWhiteSpace(baselineIp))
                return;

            try
            {
                System.Threading.Tasks.Task<ConnectionInfo> lookup = connectionInfoService
                    .LookupAsync(null, CancellationToken.None);
                if (!lookup.Wait(TimeSpan.FromSeconds(8)))
                    return;

                ConnectionInfo info = lookup.Result;
                if (info.Ok && !string.IsNullOrWhiteSpace(info.Ip))
                    baselineIp = info.Ip;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainWindow.EnsureBaselineBeforeConnect", ex);
            }
        }

        private void RegisterTunnelFailure()
        {
            if (DateTime.UtcNow < connectionCheckGraceUntil)
            {
                consecutiveTunnelFailures = 0;
                return;
            }

            if (IsGoidaProfileActive() && DateTime.UtcNow < goidaSwitchGraceUntil)
            {
                consecutiveTunnelFailures = 0;
                return;
            }

            consecutiveTunnelFailures++;

            // After grace, one failed check switches; otherwise confirm twice.
            int requiredFailures = IsGoidaProfileActive() ? 1 : 2;
            if (consecutiveTunnelFailures < requiredFailures)
            {
                ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(IsGoidaProfileActive() ? 3 : 2));
                return;
            }

            consecutiveTunnelFailures = 0;

            if (onTunnelBroken == null)
                return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    bool switched = onTunnelBroken.Invoke();
                    if (switched)
                    {
                        goidaSwitchGraceUntil = DateTime.UtcNow.AddSeconds(25);
                        Dispatcher.BeginInvoke(new Action(() =>
                            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(10))));
                    }
                    else if (isConnected)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(5))));
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("MainWindow.OnTunnelBroken", ex);
                }
            });
        }

        private void ClearConnectionInfoDetails()
        {
            textInfoFlag.Text = "🌐";
            imageInfoFlag.Source = null;
            imageInfoFlag.Visibility = Visibility.Collapsed;
            textInfoLocation.Text = "—";
            textInfoCountry.Text = string.Empty;
            textInfoOrg.Text = "—";
        }

        private void ApplyConnectionInfoDetails(ConnectionInfo info)
        {
            string place = info.PlaceLine;
            string country = info.CountryName;

            textInfoLocation.Text = !string.IsNullOrWhiteSpace(place)
                ? place
                : !string.IsNullOrWhiteSpace(country) ? country : "—";

            textInfoCountry.Text = !string.IsNullOrWhiteSpace(place) && !string.IsNullOrWhiteSpace(country)
                ? country
                : string.Empty;

            textInfoOrg.Text = string.IsNullOrWhiteSpace(info.Org) ? "—" : info.Org;
            ApplyCountryFlag(info);
        }

        private void SetModeBadge(bool connected, string modeText)
        {
            if (connected && !string.IsNullOrEmpty(modeText))
            {
                textInfoMode.Text = $"{Loc("Lang.ConnectionInfo.Mode")} {modeText}";
                borderInfoMode.Visibility = Visibility.Visible;
                return;
            }

            textInfoMode.Text = string.Empty;
            borderInfoMode.Visibility = Visibility.Collapsed;
        }

        private void ApplyCountryFlag(ConnectionInfo info)
        {
            imageInfoFlag.Source = null;
            imageInfoFlag.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(info.FlagImageUrl))
            {
                try
                {
                    BitmapImage flag = new BitmapImage();
                    flag.BeginInit();
                    flag.UriSource = new Uri(info.FlagImageUrl, UriKind.Absolute);
                    flag.CacheOption = BitmapCacheOption.OnLoad;
                    flag.EndInit();
                    imageInfoFlag.Source = flag;
                    imageInfoFlag.Visibility = Visibility.Visible;
                    textInfoFlag.Text = string.Empty;
                    return;
                }
                catch
                {
                    // Fall back to emoji when the CDN image cannot be loaded.
                }
            }

            textInfoFlag.Text = !string.IsNullOrWhiteSpace(info.FlagEmoji) ? info.FlagEmoji : "🌐";
        }

        private void ApplyVerdict(bool connected, string currentIp)
        {
            if (!connected)
            {
                // Remember the unprotected (real) exit as a baseline to detect leaks later.
                baselineIp = currentIp;
                textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Idle");
                textInfoVerdict.Foreground = Brushes.Gray;
                infoStatusDot.Fill = Brushes.Gray;
                lastTunnelCheckOk = null;
                return;
            }

            bool exposed = !string.IsNullOrEmpty(baselineIp) &&
                string.Equals(baselineIp, currentIp, StringComparison.OrdinalIgnoreCase);
            // No pre-connect baseline: a successful lookup through the tunnel path
            // still proves egress works. With a baseline, same IP = leak.
            lastTunnelCheckOk = string.IsNullOrEmpty(baselineIp) || !exposed;

            if (exposed)
            {
                textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Exposed");
                textInfoVerdict.Foreground = (Brush)TryFindResource("Color.Red") ?? Brushes.OrangeRed;
                infoStatusDot.Fill = (Brush)TryFindResource("Color.Red") ?? Brushes.OrangeRed;
            }
            else
            {
                bool torActive = getUserSettings?.Invoke()?.GetTorSettings()?.GetEnabled() == true;
                textInfoVerdict.Text = Loc(torActive ? "Lang.ConnectionInfo.ProtectedTor" : "Lang.ConnectionInfo.Protected");
                textInfoVerdict.Foreground = (Brush)TryFindResource("Color.Green") ?? Brushes.LimeGreen;
                infoStatusDot.Fill = (Brush)TryFindResource("Color.Green") ?? Brushes.LimeGreen;
            }
        }

        private string Loc(string key)
        {
            return TryFindResource(key) as string ?? key;
        }
    }
}
