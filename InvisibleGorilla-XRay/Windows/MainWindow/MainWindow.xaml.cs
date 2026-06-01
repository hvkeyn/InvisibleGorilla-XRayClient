using System;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;

namespace InvisibleGorillaXRay
{
    using Models;
    using Values;
    using Services;
    using Services.Analytics.General;
    using Services.Analytics.MainWindow;

    public partial class MainWindow : Window
    {
        private bool isRerunRequest;

        private readonly ConnectionInfoService connectionInfoService = new ConnectionInfoService();
        private DispatcherTimer connectionInfoTimer;
        private CancellationTokenSource connectionInfoCts;
        private bool isConnected;
        private string baselineIp;

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
        private Action<string> onRunServer;
        private Action onCancelServer;
        private Action onStopServer;
        private Action onDisableMode;
        private Action onGenerateClientId;
        private Action onGitHubClick;
        private Action onBugReportingClick;
        private Action<string> onCustomLinkClick;

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
            this.checkForBroadcast = checkForBroadcast;
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
            Config config = getConfig.Invoke();

            if (config == null)
            {
                textServerConfig.Text = LocalizationService.GetTerm(Localization.NO_SERVER_CONFIGURATION);
                return;
            }
            
            textServerConfig.Text = config.Name;
        }

        public void TryRerun()
        {
            if (!runWorker.IsBusy)
                return;
            
            onStopServer.Invoke();
            isRerunRequest = true;
        }

        public void TryDisableModeAndRerun()
        {
            if (!runWorker.IsBusy)
                return;

            System.Threading.Tasks.Task.Run(() => onDisableMode.Invoke());
            onStopServer.Invoke();
            isRerunRequest = true;
        }

        private void OnManageServersClick(object sender, RoutedEventArgs e)
        {
            OpenServerWindow();
            AnalyticsService.SendEvent(new ManageServersButtonClickedEvent());
        }

        private void OnRunButtonClick(object sender, RoutedEventArgs e)
        {
            if (runWorker.IsBusy)
                return;

            runWorker.RunWorkerAsync();
            AnalyticsService.SendEvent(new RunButtonClickedEvent());
        }

        private void OnStopButtonClick(object sender, RoutedEventArgs e)
        {
            onStopServer.Invoke();
            // DisableMode is handled by Run() after server thread completes,
            // so we don't block the UI thread here.
            System.Threading.Tasks.Task.Run(() => onDisableMode.Invoke());
            isRerunRequest = false;
            AnalyticsService.SendEvent(new StopButtonClickedEvent());
        }

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            if (!runWorker.IsBusy)
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
            // Give the tunnel/proxy a moment to take over routing before probing the exit IP.
            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(2));
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
            textInfoLocation.Text = string.Empty;
            textInfoOrg.Text = string.Empty;
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

            textInfoMode.Text = connected && !string.IsNullOrEmpty(modeText)
                ? $"{Loc("Lang.ConnectionInfo.Mode")} {modeText}"
                : string.Empty;

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
                textInfoIp.Text = Loc("Lang.ConnectionInfo.Unknown");
                textInfoLocation.Text = string.Empty;
                textInfoOrg.Text = string.Empty;
                textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Error");
                textInfoVerdict.Foreground = Brushes.Gray;
                infoStatusDot.Fill = Brushes.Gray;
                return;
            }

            textInfoIp.Text = info.Ip;
            textInfoLocation.Text = info.Location;
            textInfoOrg.Text = info.Org;

            ApplyVerdict(connected, info.Ip);
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
                return;
            }

            bool exposed = !string.IsNullOrEmpty(baselineIp) &&
                string.Equals(baselineIp, currentIp, StringComparison.OrdinalIgnoreCase);

            if (exposed)
            {
                textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Exposed");
                textInfoVerdict.Foreground = (Brush)TryFindResource("Color.Red") ?? Brushes.OrangeRed;
                infoStatusDot.Fill = (Brush)TryFindResource("Color.Red") ?? Brushes.OrangeRed;
            }
            else
            {
                textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Protected");
                textInfoVerdict.Foreground = (Brush)TryFindResource("Color.Green") ?? Brushes.LimeGreen;
                infoStatusDot.Fill = (Brush)TryFindResource("Color.Green") ?? Brushes.LimeGreen;
            }
        }

        private string Loc(string key)
        {
            return TryFindResource(key) as string ?? key;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }
    }
}
