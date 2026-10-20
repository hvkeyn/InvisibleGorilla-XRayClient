using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.ComponentModel;
using System.Diagnostics;

namespace InvisibleGorillaXRay
{
    using Core;
    using Models;
    using Values;
    using Services;
    using Services.Goida;
    using Services.OpenFlux;
    using Handlers.OpenFlux;
    using Services.Analytics.General;
    using Services.Analytics.MainWindow;

    public partial class MainWindow : Window
    {
        private bool isRerunRequest;
        private bool shutdownRequested;
        private int runGeneration;

        private readonly ConnectionInfoService connectionInfoService = new ConnectionInfoService();
        private DispatcherTimer connectionInfoTimer;
        private CancellationTokenSource connectionInfoCts;
        private bool connectionInfoInFlight;
        private bool isConnected;
        private string baselineIp;

        // null = not checked yet / VPN idle, true = tunnel passes traffic, false = confirmed IP leak.
        private bool? lastTunnelCheckOk;
        private int consecutiveTunnelFailures;
        private int connectionInfoFailureRetries;
        private const int ConnectionInfoMaxFailureRetries = 1;
        private DateTime goidaSwitchGraceUntil = DateTime.MinValue;
        private DateTime connectionCheckGraceUntil = DateTime.MinValue;
        private readonly Queue<string> homeActivityLines = new Queue<string>();
        private DispatcherTimer homeSpeedTimer;
        private long homeSpeedRx;
        private long homeSpeedTx;
        private bool homeSpeedReady;
        private int homeSpeedBusy;
        private readonly object homeSpeedLock = new object();
        private PerformanceCounter homeRxCounter;
        private PerformanceCounter homeTxCounter;

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
        private Action<OpenFluxProfile> onSaveOpenFlux;
        private Func<string, Status> onApplyOpenFluxUrl;
        private Func<OpenFluxManager> getOpenFluxManager;
        private Action ensureOpenFluxProxyMode;
        private Action<string> onRunServer;
        private Action onCancelServer;
        private Action onStopServer;
        private Action onDisableMode;
        private Action onGenerateClientId;
        private Action onGitHubClick;
        private Action onBugReportingClick;
        private Action<string> onCustomLinkClick;
        private Func<bool> onTunnelBroken;
        private Func<bool> trySwitchLiveServer;
        private int liveServerSwitchSteps;
        private bool openFluxApplyBusy;
        private string pendingOpenFluxUrl;
        private bool isApplyingOpenFluxUi;
        private bool openFluxTransportReady;
        private bool openFluxUrlHooked;
        private DispatcherTimer openFluxCheckTimer;
        private int lastOpenFluxPingMs = -1;

        private BackgroundWorker runWorker;
        private BackgroundWorker updateWorker;
        private BackgroundWorker broadcastWorker;

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();
        private AnalyticsService AnalyticsService => ServiceLocator.Get<AnalyticsService>();

        public MainWindow()
        {
            InitializeComponent();
            FitDesktopWindow();
            TextSelectionAssist.Attach(this);
#if DEBUG
            Title = "Invisible Gorilla XRay · debug";
#endif
            InitializeRunWorker();
            InitializeUpdateWorker();
            InitializeBroadcastWorker();

            updateWorker.RunWorkerAsync();
            broadcastWorker.RunWorkerAsync();

            InitializeConnectionInfoTimer();
            InitializeNetworkMonitoring();

            // Windows logoff/shutdown must be able to close the window even though a user close
            // is now always minimized.
            if (Application.Current != null)
                Application.Current.SessionEnding += (_, __) => shutdownRequested = true;
            LoadHomeLog();

            void InitializeNetworkMonitoring()
            {
                networkChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                networkChangeDebounce.Tick += (sender, e) =>
                {
                    networkChangeDebounce.Stop();
                    if (shutdownRequested || !runWorker.IsBusy)
                        return;

                    if (DateTime.UtcNow < suppressNetworkRestartUntilUtc)
                    {
                        DiagnosticLog.Write("Network", "Change ignored: the session was just (re)started.");
                        return;
                    }

                    if (HasActiveAppTunnel())
                    {
                        DiagnosticLog.Write("Network", "Change ignored: it comes from the app's own TUN adapter.");
                        return;
                    }

                    // Wi-Fi/Ethernet switch or a wake-up: the live session usually keeps
                    // pointing at a dead route, and the app had no reaction at all before.
                    DiagnosticLog.Write("Network", "Network change detected; restarting the session.");
                    TryRerun();
                };

                NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
                Closed += OnMainWindowClosed;

                homeStateWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                homeStateWatchdog.Tick += (sender, e) =>
                {
                    if (!homeWaitingForRun || runWorker.IsBusy || isRerunRequest)
                        return;

                    DiagnosticLog.Write(
                        "Home",
                        "Stuck 'starting' state healed: no worker is running, returning to the stopped state.");
                    ShowStopStatus();
                };
                homeStateWatchdog.Start();
            }

            void InitializeConnectionInfoTimer()
            {
                connectionInfoTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(20)
                };
                connectionInfoTimer.Tick += (sender, e) =>
                {
                    if (!connectionInfoInFlight)
                        RefreshConnectionInfo();
                };
            }

            void InitializeRunWorker()
            {
                runWorker = new BackgroundWorker();

                runWorker.RunWorkerCompleted += (sender, e) => {
                    if (isRerunRequest && !shutdownRequested)
                    {
                        isRerunRequest = false;
                        int token = Interlocked.Increment(ref runGeneration);
                        runWorker.RunWorkerAsync(token);
                    }
                };

                runWorker.DoWork += (sender, e) => {
                    int runToken = e.Argument is int token ? token : Volatile.Read(ref runGeneration);
                    bool RunAlive() => runToken == Volatile.Read(ref runGeneration);
                    if (!RunAlive())
                        return;

                    EnsureOpenFluxProxyMode();
                    EnsureBaselineBeforeConnect();

                    Dispatcher.BeginInvoke(new Action(delegate {
                        if (!RunAlive())
                            return;
                        ShowWaitForRunStatus();
                        SetHomeProgress(25, Loc("Lang.Home.Step.Starting"));
                    }));

                    Status configStatus = loadConfig.Invoke();

                    if (!RunAlive())
                        return;

                    if (configStatus.Code == Code.ERROR)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate {
                            if (!RunAlive())
                                return;
                        NoteHome(configStatus.Content?.ToString());
                        HandleError();
                        ShowStopStatus();
                        }));

                        return;
                    }

                    Status modeStatus = enableMode.Invoke();

                    if (!RunAlive())
                        return;

                    if (modeStatus.Code == Code.ERROR)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate {
                            if (!RunAlive())
                                return;
                            NoteHome(modeStatus.Content?.ToString());
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
                                if (!RunAlive())
                                    return;
                                ShowStopStatus();
                            }));

                            return;
                        }
                    }

                    try
                    {
                        if (!RunAlive())
                            return;

                        if (IsOpenFluxProfileActive())
                        {
                            string docUrl = OpenFluxUrl.Trim(getUserSettings?.Invoke()?.GetOpenFluxProfile()?.DocUrl);

                            // Fail fast with a precise message instead of waiting up to 75 s for
                            // the WebView capture and then reporting a generic "document" error.
                            OpenFluxUrlCheckResult preflight = OpenFluxUrlCheck.Inspect(docUrl);
                            DiagnosticLog.Write(
                                "OpenFlux.Preflight",
                                $"format={preflight.FormatOk} reachable={preflight.Reachable} doc={preflight.LooksLikeDocument} error='{preflight.Error}'");

                            if (!preflight.FormatOk)
                                throw new InvalidOperationException("Lang.OpenFlux.Error.BadUrl");

                            if (string.Equals(preflight.Error, "login", StringComparison.Ordinal))
                                throw new InvalidOperationException("Lang.OpenFlux.Error.Login");

                            if (string.Equals(preflight.Error, "http", StringComparison.Ordinal))
                                throw new InvalidOperationException("Lang.OpenFlux.Error.Http");

                            if (string.Equals(preflight.Error, "timeout", StringComparison.Ordinal))
                                throw new InvalidOperationException("Lang.OpenFlux.Error.Timeout");

                            if (!preflight.LooksLikeDocument)
                                throw new InvalidOperationException("Lang.OpenFlux.Error.Document");

                            Dispatcher.BeginInvoke(new Action(() => SetHomeProgress(40, Loc("Lang.OpenFlux.Status.Opening"))));
                            bool captured = false;
                            try
                            {
                                Task<bool> capture = Dispatcher.InvokeAsync(
                                    () => WindowsOpenFluxDocumentCapture.CaptureAsync(this, docUrl)).Task.Unwrap();
                                DateTime deadline = DateTime.UtcNow.AddSeconds(75);
                                while (!capture.IsCompleted && DateTime.UtcNow < deadline)
                                {
                                    if (!RunAlive())
                                    {
                                        Dispatcher.BeginInvoke(new Action(CloseOpenFluxCaptureWindow));
                                        return;
                                    }
                                    capture.Wait(250);
                                }
                                captured = capture.Status == TaskStatus.RanToCompletion && capture.Result;
                            }
                            catch (Exception captureEx)
                            {
                                DiagnosticLog.WriteException("OpenFlux.Capture", captureEx);
                            }

                            if (!RunAlive())
                                return;

                            string snapshotPath = System.IO.Path.Combine(Values.Directory.OPENFLUX, "doc.html");
                            bool haveSnapshot = false;
                            try
                            {
                                haveSnapshot = File.Exists(snapshotPath)
                                    && File.ReadAllText(snapshotPath).IndexOf("id=\"client-config\"", StringComparison.Ordinal) >= 0;
                            }
                            catch
                            {
                                haveSnapshot = false;
                            }

                            if (!captured && !haveSnapshot)
                                throw new InvalidOperationException("Lang.OpenFlux.Error.Document");
                        }

                        if (!RunAlive())
                            return;

                        Dispatcher.BeginInvoke(new Action(() => SetHomeProgress(70, Loc("Lang.Home.Step.Tunnel"))));
                        onRunServer.Invoke(configStatus.Content.ToString());
                    }
                    catch (Exception ex)
                    {
                        if (!RunAlive())
                            return;

                        string message = ex.GetBaseException().Message ?? "";
                        Dispatcher.BeginInvoke(new Action(delegate {
                            if (!RunAlive())
                                return;
                            if (message.StartsWith("Lang.", StringComparison.Ordinal))
                                message = Loc(message);
                            if (string.IsNullOrWhiteSpace(message))
                                message = Loc("Lang.OpenFlux.Error.Generic");
                            NoteHome(message);
                            MessageBox.Show(
                                this,
                                message,
                                Caption.ERROR,
                                MessageBoxButton.OK,
                                MessageBoxImage.Error
                            );
                            ShowStopStatus();
                        }));

                        return;
                    }

                    if (!RunAlive())
                        return;

                    Dispatcher.BeginInvoke(new Action(delegate {
                        if (!RunAlive())
                            return;
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
            Func<bool> onTunnelBroken = null,
            Func<bool> trySwitchLiveServer = null,
            Action<OpenFluxProfile> onSaveOpenFlux = null,
            Func<string, Status> onApplyOpenFluxUrl = null,
            Func<OpenFluxManager> getOpenFluxManager = null,
            Action ensureOpenFluxProxyMode = null)
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
            this.onSaveOpenFlux = onSaveOpenFlux;
            this.onApplyOpenFluxUrl = onApplyOpenFluxUrl;
            this.getOpenFluxManager = getOpenFluxManager;
            this.ensureOpenFluxProxyMode = ensureOpenFluxProxyMode;
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
            this.trySwitchLiveServer = trySwitchLiveServer;

            EnsureOpenFluxTransportCombo();
            HookOpenFluxStatus();
            UpdateUI();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            TryOpenPolicyWindow();
            TryStartHidden();
            TryAutoConnect();

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
            ApplyOpenFluxPanel();
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

            Brush statusBrush = Brushes.Gray;
            try
            {
                if (!string.IsNullOrWhiteSpace(colorHex))
                    statusBrush = (Brush)new BrushConverter().ConvertFromString(colorHex) ?? Brushes.Gray;
            }
            catch
            {
                statusBrush = Brushes.Gray;
            }

            textGoidaSummary.Text = presentation.Summary;
            textGoidaSignalLabel.Text = Loc(qualityLabel);
            textGoidaSignalLabel.Foreground = statusBrush;
            textGoidaLatency.Text = latencyText;
            wifiGoidaSignal?.SetSignal(signalLevel, statusBrush);
            panelGoidaDetails.Visibility = Visibility.Visible;
        }

        public void RequestGracefulShutdown()
        {
            if (shutdownRequested)
                return;

            shutdownRequested = true;
            isRerunRequest = false;
            InvalidateRun();
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
                        // Always tear down OS routing before touching native Xray. StopServer
                        // can block/crash inside XRayCore.dll; routes must not be left behind.
                        onDisableMode.Invoke();
                        onStopServer.Invoke();
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
            if (!shutdownRequested)
            {
                // Shift+close is the explicit exit; the tray menu has the same "Закрыть" item.
                if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift)
                {
                    shutdownRequested = true;
                    e.Cancel = true;
                    DiagnosticLog.Write("MainWindow.OnClosing", "Shift+close: shutting down.");
                    RequestGracefulShutdown();
                    return;
                }

                // Closing hides the window into the tray, as it always did. The safety net below
                // only kicks in when the tray icon could not be created: hiding the window then
                // makes the app unreachable and the single-instance guard blocks a relaunch.
                if (!Handlers.NotifyHandler.IsIconVisible)
                {
                    e.Cancel = true;
                    if (WindowState == WindowState.Minimized)
                        return;

                    WindowState = WindowState.Minimized;
                    DiagnosticLog.Write(
                        "MainWindow.OnClosing",
                        "No tray icon; kept in the taskbar instead of hiding. Shift+close exits.");
                    return;
                }

                e.Cancel = true;
                Hide();
                DiagnosticLog.Write("MainWindow.OnClosing", "Window hidden to the tray; Shift+close or the tray menu exits.");
                return;
            }

            base.OnClosing(e);
        }

        private DispatcherTimer networkChangeDebounce;

        // True while the home screen shows "starting" (the CANCEL button). A failed start path
        // (e.g. a broken OpenFlux document) used to leave the UI there forever; the watchdog
        // below returns it to the stopped state when no worker is actually running.
        private bool homeWaitingForRun;

        private DispatcherTimer homeStateWatchdog;

        // The app's own TUN adapter raises NetworkAddressChanged. Without this guard the
        // monitor treated that as a real network switch and restarted the session it had
        // just started, which killed OpenFlux/Tor sessions and kept the UI on "Opening".
        private DateTime suppressNetworkRestartUntilUtc = DateTime.MinValue;

        private void OnMainWindowClosed(object sender, EventArgs e)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            networkChangeDebounce?.Stop();
            homeStateWatchdog?.Stop();
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e) => ScheduleNetworkRestart();

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e) => ScheduleNetworkRestart();

        private static bool HasActiveAppTunnel()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    if ((nic.Name ?? string.Empty).IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;

                    if ((nic.Description ?? string.Empty).IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private void ScheduleNetworkRestart()
        {
            if (shutdownRequested || networkChangeDebounce == null)
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ScheduleNetworkRestart));
                return;
            }

            // One change raises several events; collapse them into a single restart.
            networkChangeDebounce.Stop();
            networkChangeDebounce.Start();
        }

        public void TryRerun()
        {
            if (!runWorker.IsBusy)
                return;

            InvalidateRun();
            isRerunRequest = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { onDisableMode.Invoke(); } catch (Exception ex) { DiagnosticLog.WriteException("MainWindow.TryRerun.DisableMode", ex); }
                try { onStopServer.Invoke(); } catch (Exception ex) { DiagnosticLog.WriteException("MainWindow.TryRerun.StopServer", ex); }
            });
        }

        public void TryDisableModeAndRerun()
        {
            if (!runWorker.IsBusy)
                return;

            InvalidateRun();
            isRerunRequest = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                onDisableMode.Invoke();
                onStopServer.Invoke();
            });
        }

        public bool IsServerRunning => runWorker.IsBusy;

        private bool resumeServerAfterNativeTest;

        public bool TryPauseForNativeTest()
        {
            if (!runWorker.IsBusy)
                return false;

            // This used to sleep on the UI thread. The window froze, and Stop
            // could not finish because the dispatcher was blocked.
            if (Dispatcher.CheckAccess())
                return false;

            resumeServerAfterNativeTest = true;
            InvalidateRun();
            onDisableMode.Invoke();
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
            int token = Interlocked.Increment(ref runGeneration);
            runWorker.RunWorkerAsync(token);
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
                InvalidateRun();
                isRerunRequest = true;
                ShowWaitForRunStatus();
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        onDisableMode.Invoke();
                        onStopServer.Invoke();
                    }
                    catch (Exception ex) { DiagnosticLog.WriteException("MainWindow.OnRunButtonClick", ex); }
                });
                return;
            }

            int token = Interlocked.Increment(ref runGeneration);
            ResetHomeLog();
            runWorker.RunWorkerAsync(token);
            AnalyticsService.SendEvent(new RunButtonClickedEvent());
        }

        private void ResetHomeLog()
        {
            // The home log persisted across sessions, so errors from a previous attempt
            // ("No configurations were found", failed OpenFlux document) stayed on screen and
            // looked like the current connection was resetting. Each manual start begins clean.
            homeActivityLines.Clear();
            try
            {
                if (File.Exists(HomeLogPath))
                    File.Delete(HomeLogPath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Home.Log.Reset", ex);
            }

            if (homeActivityLog != null)
                homeActivityLog.Text = string.Empty;
        }

        private void OnStopButtonClick(object sender, RoutedEventArgs e)
        {
            isRerunRequest = false;
            InvalidateRun();
            connectionInfoCts?.Cancel();
            ShowStopStatus();
            AnalyticsService.SendEvent(new StopButtonClickedEvent());

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    onDisableMode.Invoke();
                    onStopServer.Invoke();
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
            InvalidateRun();
            ShowStopStatus();

            System.Threading.Tasks.Task.Run(() =>
            {
                try { onCancelServer?.Invoke(); }
                catch (Exception ex) { DiagnosticLog.WriteException("MainWindow.OnCancelButtonClick", ex); }
                try { onStopServer?.Invoke(); }
                catch (Exception ex) { DiagnosticLog.WriteException("MainWindow.OnCancelButtonClick.Stop", ex); }
            });
        }

        private void InvalidateRun()
        {
            Interlocked.Increment(ref runGeneration);
            suppressNetworkRestartUntilUtc = DateTime.UtcNow.AddSeconds(30);
            if (Dispatcher.CheckAccess())
                CloseOpenFluxCaptureWindow();
            else
                Dispatcher.BeginInvoke(new Action(CloseOpenFluxCaptureWindow));
        }

        private static void CloseOpenFluxCaptureWindow()
        {
            if (Application.Current == null)
                return;

            foreach (Window window in Application.Current.Windows.OfType<Window>().ToList())
            {
                if (window.Title == "OpenFlux")
                    window.Close();
            }
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
            serverWindow.BindOpenFluxEditor(panelOpenFlux, ApplyOpenFluxPanel);
            serverWindow.Closing += (_, _) => serverWindow.ReleaseOpenFluxEditor(openFluxEditorHost);
            serverWindow.ShowDialog();
            serverWindow.ReleaseOpenFluxEditor(openFluxEditorHost);
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

        public void NotifyReady()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(NotifyReady);
                return;
            }

            ShowRunStatus();
        }

        private void ShowRunStatus()
        {
            homeWaitingForRun = false;
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
                : ActiveTunnelSession.IsOpenFlux
                    ? TimeSpan.FromSeconds(45)
                    : TimeSpan.FromSeconds(20);
            if (!connectionInfoTimer.IsEnabled)
                connectionInfoTimer.Start();
            SetHomeProgress(100, Loc("Lang.Home.Step.Running"));
            UpdateHomeLinkBadge();
            StartHomeSpeed();
            // Give the tunnel/proxy a moment to take over routing before probing the exit IP.
            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(3));
        }

        private void ShowStopStatus()
        {
            homeWaitingForRun = false;
            statusStop.Visibility = Visibility.Visible;
            statusRun.Visibility = Visibility.Hidden;
            statusWaitForRun.Visibility = Visibility.Hidden;

            buttonRun.Visibility = Visibility.Visible;
            buttonCancel.Visibility = Visibility.Hidden;
            buttonStop.Visibility = Visibility.Hidden;

            isConnected = false;
            if (homeLinkBadge != null)
                homeLinkBadge.Visibility = Visibility.Collapsed;
            consecutiveTunnelFailures = 0;
            connectionInfoTimer.Stop();
            StopHomeSpeed();
            if (homeConnectProgress != null)
            {
                homeConnectProgress.Value = 0;
                homeConnectProgress.Visibility = Visibility.Collapsed;
            }
            if (homeConnectStep != null)
                homeConnectStep.Text = "";
            if (homeTrafficPanel != null)
                homeTrafficPanel.Visibility = Visibility.Collapsed;
            if (homeSpeedTrace != null)
                homeSpeedTrace.Visibility = Visibility.Collapsed;
            if (homeTrafficText != null)
                homeTrafficText.Visibility = Visibility.Collapsed;
            ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(1));
        }

        private void ShowWaitForRunStatus()
        {
            homeWaitingForRun = true;
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

        private string lastOpenFluxGeoIp = "";
        private ConnectionInfo lastOpenFluxGeo;

        private async Task<ConnectionInfo> LookupOpenFluxExitIpAsync(CancellationToken token)
        {
            OpenFluxManager manager = getOpenFluxManager?.Invoke();
            int port = manager?.BoundSocksPort ?? 0;
            if (manager == null || !manager.IsRunning || port <= 0)
                return new ConnectionInfo { Ok = false, Error = "openflux socks down" };

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            string body = await Task.Run(
                () => Socks5Http.GetHttpsBody("127.0.0.1", port, "api.ipify.org", "/", 8000),
                token).ConfigureAwait(true);
            watch.Stop();

            string ip = (body ?? "").Trim();
            if (ip.Length < 7 || ip.IndexOf('.') < 0)
                return new ConnectionInfo { Ok = false, Error = "openflux probe empty" };

            ConnectionInfo info = new ConnectionInfo
            {
                Ok = true,
                Ip = ip,
                LatencyMs = (int)Math.Max(1, watch.ElapsedMilliseconds)
            };
            return await EnrichOpenFluxGeoAsync(info, token).ConfigureAwait(true);
        }

        private async Task<ConnectionInfo> EnrichOpenFluxGeoAsync(ConnectionInfo info, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(lastOpenFluxGeoIp)
                && string.Equals(lastOpenFluxGeoIp, info.Ip, StringComparison.Ordinal)
                && lastOpenFluxGeo != null)
            {
                return new ConnectionInfo
                {
                    Ok = true,
                    Ip = lastOpenFluxGeo.Ip,
                    LatencyMs = info.LatencyMs,
                    City = lastOpenFluxGeo.City,
                    Region = lastOpenFluxGeo.Region,
                    CountryName = lastOpenFluxGeo.CountryName,
                    CountryCode = lastOpenFluxGeo.CountryCode,
                    Org = lastOpenFluxGeo.Org
                };
            }

            try
            {
                using SocketsHttpHandler handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(3)
                };
                using HttpClient client = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(3)
                };
                string json = await client.GetStringAsync("https://ipwho.is/" + info.Ip, token).ConfigureAwait(true);
                JObject root = JObject.Parse(json);
                if (root.Value<bool?>("success") == false)
                    return info;

                JObject connection = root["connection"] as JObject;
                ConnectionInfo geo = new ConnectionInfo
                {
                    Ok = true,
                    Ip = info.Ip,
                    LatencyMs = info.LatencyMs,
                    City = (string)root["city"] ?? "",
                    Region = (string)root["region"] ?? "",
                    CountryName = (string)root["country"] ?? "",
                    CountryCode = (string)root["country_code"] ?? "",
                    Org = (string)connection?["org"] ?? (string)connection?["isp"] ?? ""
                };
                lastOpenFluxGeoIp = info.Ip;
                lastOpenFluxGeo = geo;
                return geo;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("ConnectionInfo.OpenFluxGeo", ex.Message);
                return info;
            }
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
            if (shutdownRequested || !IsLoaded || textInfoIp == null || infoStatusDot == null)
                return;

            connectionInfoCts?.Cancel();
            CancellationTokenSource probeCts = new CancellationTokenSource();
            connectionInfoCts = probeCts;
            CancellationToken token = probeCts.Token;

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

            bool openFluxProbe = connected && ActiveTunnelSession.IsOpenFlux;
            bool torProbe = connected && settings?.GetTorSettings()?.GetEnabled() == true;
            TimeSpan requestTimeout = TimeSpan.FromSeconds(openFluxProbe || torProbe ? 30 : 12);

            ConnectionInfo info;
            connectionInfoInFlight = true;
            try
            {
                // OpenFlux runs one manual SOCKS ipify request, like the Android build.
                // Several parallel HTTPS probes through the document channel starve it.
                info = openFluxProbe
                    ? await LookupOpenFluxExitIpAsync(token)
                    : await connectionInfoService.LookupAsync(probeProxy, token, requestTimeout);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("ConnectionInfo", "Probe error: " + ex.Message);
                info = new ConnectionInfo { Ok = false, Error = ex.Message };
            }
            finally
            {
                if (ReferenceEquals(connectionInfoCts, probeCts))
                    connectionInfoInFlight = false;
            }

            if (token.IsCancellationRequested)
                return;

            if (connected != isConnected)
            {
                ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(1));
                return;
            }

            DiagnosticLog.Write("ConnectionInfo", info.Ok
                ? $"Probe ok via {(probeProxy == null ? "route" : openFluxProbe ? "OpenFlux SOCKS" : "local proxy")} in {info.LatencyMs} ms"
                : $"Probe failed: {info.Error}");

            if (!info.Ok)
            {
                // "HTTP 429" means the exit answered us: the tunnel works and only the lookup
                // service is throttling. SSL/timeout/all-endpoints-failed is the opposite —
                // the probe cannot reach the exit through the tunnel at all.
                bool hardProbeFailure = string.Equals(info.Error, "SSL", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.Error, "timeout", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.Error, "all endpoints failed", StringComparison.OrdinalIgnoreCase);

                if (!hardProbeFailure
                    && connected
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

                if (connected && hardProbeFailure)
                {
                    // Subscription with several servers: switch to the next one first.
                    if (liveServerSwitchSteps < 4
                        && trySwitchLiveServer != null
                        && trySwitchLiveServer())
                    {
                        liveServerSwitchSteps++;
                        connectionInfoFailureRetries = 0;
                        textInfoVerdict.Text = Loc("Lang.ConnectionInfo.Switching");
                        textInfoVerdict.Foreground = Brushes.Gray;
                        infoStatusDot.Fill = Brushes.Gray;
                        ApplyGoidaSummary();
                        return;
                    }

                    // Otherwise let the failover logic decide (Goida applies its own
                    // threshold and grace window). The counter used to be bumped only after a
                    // *successful* probe with a leaked IP, so a blocked node — the most common
                    // failure — could never trigger a switch.
                    RegisterTunnelFailure();
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
            liveServerSwitchSteps = 0;

            textInfoIp.Text = info.LatencyMs > 0
                ? $"{info.Ip}  ·  {info.LatencyMs} ms"
                : info.Ip;
            if (info.LatencyMs > 0)
                lastOpenFluxPingMs = info.LatencyMs;
            ApplyConnectionInfoDetails(info);

            ApplyVerdict(connected, info.Ip);
            ApplyGoidaSummary();
            ApplyOpenFluxPanel();

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
            textInfoFlag.Text = string.Empty;
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

            DiagnosticLog.Write(
                "ConnectionInfo",
                $"details: code='{info.CountryCode}', country='{country}', place='{place}', flag='{info.FlagImageUrl}'");

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

                    // A captive portal or a filtered proxy can answer with a tiny placeholder
                    // image; it used to leave an empty box on the card instead of the code.
                    if (flag.PixelWidth >= 10 && flag.PixelHeight >= 6)
                    {
                        imageInfoFlag.Source = flag;
                        imageInfoFlag.Visibility = Visibility.Visible;
                        textInfoFlag.Text = string.Empty;
                        return;
                    }
                }
                catch
                {
                    // The CDN is not always reachable through the tunnel; fall back below.
                }
            }

            // Windows ships no flag glyphs in Segoe UI Emoji, so "🇩🇪" renders as a tofu
            // box. Show the ISO code instead - it always renders and stays readable.
            textInfoFlag.Text = string.IsNullOrWhiteSpace(info.CountryCode)
                ? string.Empty
                : info.CountryCode.Trim().ToUpperInvariant();
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

        private bool IsOpenFluxProfileActive()
        {
            return OpenFluxProfilePaths.IsMarker(getUserSettings?.Invoke()?.GetCurrentConfigPath());
        }

        private void EnsureOpenFluxTransportCombo()
        {
            if (comboOpenFluxTransport == null || openFluxTransportReady)
                return;

            comboOpenFluxTransport.Items.Clear();
            comboOpenFluxTransport.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = Loc("Lang.OpenFlux.Transport.Auto"),
                Tag = OpenFluxTransportMode.Auto
            });
            comboOpenFluxTransport.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = Loc("Lang.OpenFlux.Transport.Vyandex"),
                Tag = OpenFluxTransportMode.Vyandex
            });
            comboOpenFluxTransport.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = Loc("Lang.OpenFlux.Transport.Yandex"),
                Tag = OpenFluxTransportMode.Yandex
            });
            comboOpenFluxTransport.SelectedIndex = 0;
            comboOpenFluxTransport.SelectionChanged += (_, _) => SaveOpenFluxFromUi(restart: false);
            if (textOpenFluxUrl != null && !openFluxUrlHooked)
            {
                openFluxUrlHooked = true;
                textOpenFluxUrl.TextChanged += OnOpenFluxUrlTextChanged;
                textOpenFluxUrl.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter)
                    {
                        e.Handled = true;
                        OnOpenFluxApplyClick(null, null);
                    }
                };
            }
            openFluxTransportReady = true;
        }

        private void OnOpenFluxUrlTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (isApplyingOpenFluxUi)
                return;
            RefreshOpenFluxDerivedKey();
            ScheduleOpenFluxUrlCheck();
        }

        private void RefreshOpenFluxDerivedKey()
        {
            if (textOpenFluxKey == null)
                return;
            string key = OpenFluxUrl.DeriveKey(textOpenFluxUrl?.Text);
            if (textOpenFluxKey.Text != key)
                textOpenFluxKey.Text = key;
        }

        private void HookOpenFluxStatus()
        {
            OpenFluxManager manager = getOpenFluxManager?.Invoke();
            if (manager == null)
                return;
            manager.StatusChanged += (status, detail) =>
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyOpenFluxStatusText(status, detail, lastTunnelCheckOk)));
            };
        }

        private void ApplyOpenFluxPanel()
        {
            if (panelOpenFlux == null)
                return;

            EnsureOpenFluxTransportCombo();
            if (openFluxEditorHost != null)
                openFluxEditorHost.Visibility = Visibility.Collapsed;
            panelOpenFlux.Visibility = Visibility.Visible;

            UserSettings settings = getUserSettings?.Invoke();
            OpenFluxProfile profile = settings?.GetOpenFluxProfile() ?? new OpenFluxProfile();
            isApplyingOpenFluxUi = true;
            try
            {
                string canonicalUrl = OpenFluxUrl.Trim(profile.DocUrl);
                if (textOpenFluxUrl != null && textOpenFluxUrl.Text != canonicalUrl)
                    textOpenFluxUrl.Text = canonicalUrl;
                RefreshOpenFluxDerivedKey();
                SelectOpenFluxTransport(profile.Transport);
            }
            finally
            {
                isApplyingOpenFluxUi = false;
            }

            OpenFluxManager manager = getOpenFluxManager?.Invoke();
            if (isConnected)
                ApplyOpenFluxStatusText(manager?.Status ?? OpenFluxClientStatus.Stopped, manager?.StatusDetail, lastTunnelCheckOk);
            else if (lastOpenFluxPingMs > 0)
                SetOpenFluxStatusMessage(string.Format(Loc("Lang.OpenFlux.Status.UrlOk"), lastOpenFluxPingMs));
        }

        private void SelectOpenFluxTransport(OpenFluxTransportMode mode)
        {
            if (comboOpenFluxTransport == null)
                return;
            for (int i = 0; i < comboOpenFluxTransport.Items.Count; i++)
            {
                if (comboOpenFluxTransport.Items[i] is System.Windows.Controls.ComboBoxItem item
                    && item.Tag is OpenFluxTransportMode tagged
                    && tagged == mode)
                {
                    comboOpenFluxTransport.SelectedIndex = i;
                    return;
                }
            }
            comboOpenFluxTransport.SelectedIndex = 0;
        }

        private OpenFluxTransportMode ReadOpenFluxTransport()
        {
            if (comboOpenFluxTransport?.SelectedItem is System.Windows.Controls.ComboBoxItem item
                && item.Tag is OpenFluxTransportMode mode)
                return mode;
            return OpenFluxTransportMode.Auto;
        }

        private OpenFluxProfile ReadOpenFluxFromUi()
        {
            UserSettings settings = getUserSettings?.Invoke();
            OpenFluxProfile profile = settings?.GetOpenFluxProfile().Clone() ?? new OpenFluxProfile();
            profile.DocUrl = OpenFluxUrl.Trim(textOpenFluxUrl?.Text);
            profile.Transport = ReadOpenFluxTransport();
            profile.EncryptionKey = OpenFluxUrl.DeriveKey(profile.DocUrl);
            profile.ConfigPath = OpenFluxProfilePaths.MarkerPath;
            return profile;
        }

        private static string ResolveOpenFluxKey(string typed, string stored)
        {
            return OpenFluxUrl.DeriveKey(!string.IsNullOrWhiteSpace(typed) ? typed : stored);
        }

        private void EnsureOpenFluxProxyMode()
        {
            ensureOpenFluxProxyMode?.Invoke();
        }

        private void SaveOpenFluxFromUi(bool restart)
        {
            if (isApplyingOpenFluxUi || onSaveOpenFlux == null)
                return;
            OpenFluxProfile profile = ReadOpenFluxFromUi();
            onSaveOpenFlux(profile);
        }

        private async void OnOpenFluxApplyClick(object sender, RoutedEventArgs e)
        {
            pendingOpenFluxUrl = OpenFluxUrl.Trim(textOpenFluxUrl?.Text);
            if (openFluxApplyBusy)
                return;

            openFluxApplyBusy = true;
            if (buttonOpenFluxApply != null)
                buttonOpenFluxApply.IsEnabled = false;

            try
            {
                while (true)
                {
                    string url = pendingOpenFluxUrl;
                    pendingOpenFluxUrl = null;
                    OpenFluxProfile profile = ReadOpenFluxFromUi();
                    profile.DocUrl = url;
                    profile.EncryptionKey = OpenFluxUrl.DeriveKey(url);

                    if (textOpenFluxUrl != null && textOpenFluxUrl.Text != url)
                        textOpenFluxUrl.Text = url;
                    RefreshOpenFluxDerivedKey();

                    SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Status.Connecting"));
                    OpenFluxUrlCheckResult check = await OpenFluxUrlCheck.InspectAsync(url, CancellationToken.None);
                    lastOpenFluxPingMs = check.LooksLikeDocument ? check.LatencyMs : -1;
                    if (!check.FormatOk || !check.LooksLikeDocument)
                    {
                        SetOpenFluxStatusMessage(Loc(MapOpenFluxUiError(check.Error)));
                        break;
                    }

                    onSaveOpenFlux?.Invoke(profile);

                    string ping = string.Format(Loc("Lang.OpenFlux.Status.UrlOk"), check.LatencyMs);
                    if (onApplyOpenFluxUrl != null)
                    {
                        Status result = await System.Threading.Tasks.Task.Run(() => onApplyOpenFluxUrl(url));
                        if (result.Code != Code.SUCCESS)
                        {
                            SetOpenFluxStatusMessage(ping + " · " + Loc(MapOpenFluxUiError(result.Content?.ToString())));
                            break;
                        }

                        string register = result.Content?.ToString() == "register-fail"
                            ? Loc("Lang.OpenFlux.Status.RegisterFail")
                            : Loc("Lang.OpenFlux.Status.Registered");
                        SetOpenFluxStatusMessage(ping + " · " + register);
                    }
                    else
                    {
                        SetOpenFluxStatusMessage(ping);
                    }

                    if (pendingOpenFluxUrl == null)
                        break;
                }
            }
            finally
            {
                openFluxApplyBusy = false;
                if (buttonOpenFluxApply != null)
                    buttonOpenFluxApply.IsEnabled = true;
                if (isConnected)
                    ScheduleConnectionInfoRefresh(TimeSpan.FromSeconds(2));
            }
        }

        private void ScheduleOpenFluxUrlCheck()
        {
            if (openFluxCheckTimer == null)
            {
                openFluxCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                openFluxCheckTimer.Tick += async (_, _) =>
                {
                    openFluxCheckTimer.Stop();
                    if (openFluxApplyBusy || isConnected)
                        return;
                    string url = OpenFluxUrl.Trim(textOpenFluxUrl?.Text);
                    if (!OpenFluxUrl.TryValidate(url, out _))
                    {
                        lastOpenFluxPingMs = -1;
                        SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Error.BadUrl"));
                        return;
                    }
                    SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Status.Connecting"));
                    OpenFluxUrlCheckResult check = await OpenFluxUrlCheck.InspectAsync(url, CancellationToken.None);
                    if (openFluxApplyBusy)
                        return;
                    if (!check.LooksLikeDocument)
                    {
                        lastOpenFluxPingMs = -1;
                        SetOpenFluxStatusMessage(Loc(MapOpenFluxUiError(check.Error)));
                        return;
                    }
                    lastOpenFluxPingMs = check.LatencyMs;
                    SetOpenFluxStatusMessage(string.Format(Loc("Lang.OpenFlux.Status.UrlOk"), check.LatencyMs));
                };
            }
            openFluxCheckTimer.Stop();
            openFluxCheckTimer.Start();
        }

        private void ApplyOpenFluxStatusText(OpenFluxClientStatus status, string detail, bool? tunnelOk)
        {
            if (!IsOpenFluxProfileActive() || textOpenFluxStatus == null)
                return;

            if (status == OpenFluxClientStatus.Error)
            {
                SetOpenFluxStatusMessage(Loc(MapOpenFluxUiError(detail)));
                return;
            }

            if (status == OpenFluxClientStatus.Stopped)
            {
                SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Status.Stopped"));
                return;
            }

            if (status == OpenFluxClientStatus.Connecting)
            {
                SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Status.Connecting"));
                return;
            }

            if (status == OpenFluxClientStatus.WaitingPeer
                || (status == OpenFluxClientStatus.Connected && !isConnected))
            {
                if (string.Equals(detail, "peer", StringComparison.OrdinalIgnoreCase) || tunnelOk == false)
                    SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Status.PeerMissing"));
                else
                    SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Status.WaitingPeer"));
                return;
            }

            if (status == OpenFluxClientStatus.Connected)
            {
                if (tunnelOk == false)
                {
                    SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Status.PeerMissing"));
                    return;
                }
                SetOpenFluxStatusMessage(lastOpenFluxPingMs > 0
                    ? string.Format(Loc("Lang.OpenFlux.Status.LiveMs"), lastOpenFluxPingMs)
                    : Loc("Lang.OpenFlux.Status.Live"));
                return;
            }

            SetOpenFluxStatusMessage(Loc("Lang.OpenFlux.Status.Stopped"));
        }

        private void SetOpenFluxStatusMessage(string text)
        {
            if (textOpenFluxStatus != null)
                textOpenFluxStatus.Text = text;
            if (!string.IsNullOrWhiteSpace(text) && IsOpenFluxProfileActive())
                NoteHome(text);
        }

        private void UpdateHomeLinkBadge()
        {
            if (homeLinkBadge == null || homeLinkText == null)
                return;

            string label = Handlers.Tor.TorManager.LatestLinkLabel;
            UserSettings settings = getUserSettings?.Invoke();
            bool tor = Services.Tor.TorProfilePaths.IsMarker(settings?.GetCurrentConfigPath())
                || settings?.GetTorSettings()?.GetEnabled() == true;
            if (!tor || string.IsNullOrWhiteSpace(label))
            {
                homeLinkBadge.Visibility = Visibility.Collapsed;
                return;
            }

            homeLinkText.Text = string.Format(Loc("Lang.Tor.Link.Badge"), label);
            homeLinkBadge.Visibility = Visibility.Visible;
            NoteHome(homeLinkText.Text);
        }

        private void SetHomeProgress(double percent, string step)
        {
            if (panelHomeProgress == null)
                return;

            panelHomeProgress.Visibility = Visibility.Visible;
            if (homeConnectProgress != null)
            {
                homeConnectProgress.Visibility = Visibility.Visible;
                homeConnectProgress.Value = Math.Max(0, Math.Min(100, percent));
                homeConnectProgress.Foreground = percent >= 100
                    ? new SolidColorBrush(Color.FromRgb(0x6D, 0xCC, 0x8E))
                    : new SolidColorBrush(Color.FromRgb(0x6D, 0xCC, 0x8E));
            }
            if (homeConnectStep != null)
                homeConnectStep.Text = step ?? "";
            NoteHome(step);
        }

        private const int HomeLogVisible = 8;

        private static string HomeLogPath =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(Values.Directory.LOGS, "home-activity.log"));

        private void LoadHomeLog()
        {
            try
            {
                if (!File.Exists(HomeLogPath) || homeActivityLog == null)
                    return;

                string[] lines = File.ReadAllLines(HomeLogPath);
                int start = Math.Max(0, lines.Length - HomeLogVisible);
                homeActivityLines.Clear();
                for (int i = start; i < lines.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                        homeActivityLines.Enqueue(lines[i].Trim());
                }

                if (homeActivityLines.Count == 0)
                    return;

                if (panelHomeProgress != null)
                    panelHomeProgress.Visibility = Visibility.Visible;
                homeActivityLog.Text = string.Join(Environment.NewLine, homeActivityLines);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Home.Log.Load", ex);
            }
        }

        private void NoteHome(string message)
        {
            if (homeActivityLog == null || string.IsNullOrWhiteSpace(message))
                return;

            string line = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            if (homeActivityLines.Count > 0 && homeActivityLines.Last().EndsWith(line, StringComparison.Ordinal))
                return;

            homeActivityLines.Enqueue(stamped);
            while (homeActivityLines.Count > HomeLogVisible)
                homeActivityLines.Dequeue();

            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HomeLogPath));
                File.AppendAllText(HomeLogPath, stamped + Environment.NewLine);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Home.Log.Save", ex);
            }

            if (panelHomeProgress != null)
                panelHomeProgress.Visibility = Visibility.Visible;
            homeActivityLog.Text = string.Join(Environment.NewLine, homeActivityLines);
            homeActivityLog.ScrollToEnd();
        }

        private const int HomeSpeedSlotCount = 18;
        private readonly long[] homeSpeedSamples = new long[HomeSpeedSlotCount];
        private readonly bool[] homeSpeedFaults = new bool[HomeSpeedSlotCount];
        private Border[] homeSpeedDots;
        private int homeSpeedWrite;
        private int homeSpeedFilled;

        private void StartHomeSpeed()
        {
            homeSpeedReady = false;
            homeSpeedWrite = 0;
            homeSpeedFilled = 0;
            EnsureHomeSpeedDots();
            if (homeTrafficPanel != null)
                homeTrafficPanel.Visibility = Visibility.Visible;
            if (homeSpeedTrace != null)
                homeSpeedTrace.Visibility = Visibility.Visible;
            if (homeDownText != null)
            {
                homeDownText.Text = "0 B/s";
                homeDownText.Foreground = MintBrush();
            }
            if (homeUpText != null)
            {
                homeUpText.Text = "0 B/s";
                homeUpText.Foreground = MintBrush();
            }
            PaintHomeSpeedDots();
            if (homeSpeedTimer == null)
            {
                homeSpeedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                homeSpeedTimer.Tick += (_, _) => RefreshHomeSpeed();
            }
            if (!homeSpeedTimer.IsEnabled)
                homeSpeedTimer.Start();
        }

        private void StopHomeSpeed()
        {
            homeSpeedTimer?.Stop();
            Interlocked.Exchange(ref homeSpeedBusy, 0);
            DisposeSpeedCounters();
        }

        private void RefreshHomeSpeed()
        {
            if (!isConnected)
                return;
            if (Interlocked.CompareExchange(ref homeSpeedBusy, 1, 0) != 0)
                return;

            Task.Run(() =>
            {
                long down = 0;
                long up = 0;
                bool have = false;
                try
                {
                    have = TryReadTunnelSpeed(out down, out up);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("Home.Speed", ex);
                    DisposeSpeedCounters();
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!isConnected)
                            return;
                        ApplyHomeSpeed(have ? down : 0, have ? up : 0);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref homeSpeedBusy, 0);
                    }
                }));
            });
        }

        private bool TryReadTunnelSpeed(out long down, out long up)
        {
            if (HasTunnelAdapter())
            {
                if (TryReadPerfSpeed(out down, out up))
                    return true;
                if (TryReadNicSpeed(out down, out up))
                    return true;
            }

            return TryReadBridgeSpeed(out down, out up);
        }

        private static bool HasTunnelAdapter()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Any(item =>
                    item.OperationalStatus == OperationalStatus.Up
                    && (item.Name.IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0
                        || item.Description.IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0
                        || item.Description.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0));
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadBridgeSpeed(out long down, out long up)
        {
            down = 0;
            up = 0;
            OpenFluxHttpBridge.Shared.ReadTraffic(out long rx, out long tx);
            lock (homeSpeedLock)
            {
                if (!homeSpeedReady)
                {
                    homeSpeedRx = rx;
                    homeSpeedTx = tx;
                    homeSpeedReady = true;
                    return rx > 0 || tx > 0;
                }

                down = Math.Max(0, rx - homeSpeedRx);
                up = Math.Max(0, tx - homeSpeedTx);
                homeSpeedRx = rx;
                homeSpeedTx = tx;
                return true;
            }
        }

        private bool TryReadPerfSpeed(out long down, out long up)
        {
            down = 0;
            up = 0;
            lock (homeSpeedLock)
            {
                if (homeRxCounter == null)
                    BindSpeedCounters();
                if (homeRxCounter == null || homeTxCounter == null)
                    return false;

                down = (long)Math.Max(0, homeRxCounter.NextValue());
                up = (long)Math.Max(0, homeTxCounter.NextValue());
                return true;
            }
        }

        private void BindSpeedCounters()
        {
            string[] names;
            try
            {
                names = new PerformanceCounterCategory("Network Interface").GetInstanceNames();
            }
            catch
            {
                return;
            }

            string match = names.FirstOrDefault(name =>
                    name.IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? names.FirstOrDefault(name =>
                    name.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0);
            if (string.IsNullOrEmpty(match))
                return;

            try
            {
                homeRxCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", match, true);
                homeTxCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", match, true);
            }
            catch
            {
                try { homeRxCounter?.Dispose(); } catch { }
                try { homeTxCounter?.Dispose(); } catch { }
                homeRxCounter = null;
                homeTxCounter = null;
            }
        }

        private bool TryReadNicSpeed(out long down, out long up)
        {
            down = 0;
            up = 0;
            NetworkInterface[] adapters;
            try
            {
                adapters = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return false;
            }

            NetworkInterface adapter = adapters
                .Where(item => item.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(item =>
                    item.Name.IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.Description.IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault(item =>
                    item.Name.IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.Description.IndexOf("InvisibleGorilla", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.Description.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0);
            if (adapter == null)
                return false;

            IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics();
            long rx = stats.BytesReceived;
            long tx = stats.BytesSent;
            lock (homeSpeedLock)
            {
                if (!homeSpeedReady)
                {
                    homeSpeedRx = rx;
                    homeSpeedTx = tx;
                    homeSpeedReady = true;
                    return true;
                }

                down = Math.Max(0, rx - homeSpeedRx);
                up = Math.Max(0, tx - homeSpeedTx);
                homeSpeedRx = rx;
                homeSpeedTx = tx;
                return true;
            }
        }

        private void DisposeSpeedCounters()
        {
            lock (homeSpeedLock)
            {
                try { homeRxCounter?.Dispose(); } catch { }
                try { homeTxCounter?.Dispose(); } catch { }
                homeRxCounter = null;
                homeTxCounter = null;
                homeSpeedReady = false;
            }
        }

        private void ApplyHomeSpeed(long down, long up)
        {
            if (homeDownText != null)
            {
                homeDownText.Text = FormatRate(down);
                homeDownText.Foreground = RateBrush(down);
            }
            if (homeUpText != null)
            {
                homeUpText.Text = FormatRate(up);
                homeUpText.Foreground = RateBrush(up);
            }
            PushHomeSpeedSample(down + up, false);
        }

        private void FitDesktopWindow()
        {
            Rect area = SystemParameters.WorkArea;
            Thickness frame = SystemParameters.WindowNonClientFrameThickness;
            const double designWidth = 400;
            const double designHeight = 720;
            double maxWidth = Math.Max(320, area.Width - 8);
            double maxHeight = Math.Max(420, area.Height - 8);
            double outerWidth = designWidth + frame.Left + frame.Right;
            double outerHeight = designHeight + frame.Top + frame.Bottom;
            double scale = Math.Min(1, Math.Min(maxWidth / outerWidth, maxHeight / outerHeight));
            MinWidth = 0;
            MinHeight = 0;
            Width = outerWidth * scale;
            Height = outerHeight * scale;
            MinWidth = Math.Min(320, Width);
            MinHeight = Math.Min(420, Height);
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        }

        private void EnsureHomeSpeedDots()
        {
            if (homeSpeedTrace == null || homeSpeedDots != null)
                return;

            homeSpeedDots = new Border[HomeSpeedSlotCount];
            homeSpeedTrace.ColumnDefinitions.Clear();
            homeSpeedTrace.Children.Clear();
            for (int i = 0; i < HomeSpeedSlotCount; i++)
            {
                homeSpeedTrace.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
                var dot = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    HorizontalAlignment = i == 0
                        ? HorizontalAlignment.Left
                        : i == HomeSpeedSlotCount - 1
                            ? HorizontalAlignment.Right
                            : HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dot, i);
                homeSpeedDots[i] = dot;
                homeSpeedTrace.Children.Add(dot);
            }
        }

        private void PushHomeSpeedSample(long bytesPerSec, bool fault)
        {
            homeSpeedSamples[homeSpeedWrite] = bytesPerSec;
            homeSpeedFaults[homeSpeedWrite] = fault;
            homeSpeedWrite = (homeSpeedWrite + 1) % HomeSpeedSlotCount;
            if (homeSpeedFilled < HomeSpeedSlotCount)
                homeSpeedFilled++;
            PaintHomeSpeedDots();
        }

        private void PaintHomeSpeedDots()
        {
            if (homeSpeedDots == null)
                return;

            for (int visual = 0; visual < HomeSpeedSlotCount; visual++)
            {
                int ageFromNewest = HomeSpeedSlotCount - 1 - visual;
                Border dot = homeSpeedDots[visual];
                if (ageFromNewest >= homeSpeedFilled)
                {
                    StyleHomeSpeedDot(dot, Color.FromRgb(0x3A, 0x3A, 0x3A), fault: false);
                    continue;
                }

                int index = (homeSpeedWrite - 1 - ageFromNewest + HomeSpeedSlotCount * 2) % HomeSpeedSlotCount;
                if (homeSpeedFaults[index])
                    StyleHomeSpeedDot(dot, Color.FromRgb(0xFF, 0x2D, 0x2D), fault: true);
                else
                    StyleHomeSpeedDot(dot, ((SolidColorBrush)RateBrush(homeSpeedSamples[index])).Color, fault: false);
            }
        }

        private static void StyleHomeSpeedDot(Border dot, Color color, bool fault)
        {
            dot.Width = fault ? 10 : 8;
            dot.Height = fault ? 10 : 8;
            dot.CornerRadius = new CornerRadius(fault ? 5 : 2);
            dot.Background = new SolidColorBrush(color);
        }

        private static SolidColorBrush MintBrush()
        {
            return new SolidColorBrush(Color.FromRgb(0x6D, 0xCC, 0x8E));
        }

        private static Brush RateBrush(long bytesPerSec)
        {
            if (bytesPerSec < 1024)
                return MintBrush();
            return SpeedBrush(bytesPerSec);
        }

        private static string FormatRate(long bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024)
                return (bytesPerSec / (1024d * 1024d)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB/s";
            if (bytesPerSec >= 1024)
                return (bytesPerSec / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KB/s";
            return bytesPerSec.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B/s";
        }

        private static SolidColorBrush SpeedBrush(long bytesPerSec)
        {
            // A quiet but live tunnel is amber. Red is reserved for a failed check.
            if (bytesPerSec < 32 * 1024)
                return new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x3A));
            if (bytesPerSec < 256 * 1024)
                return new SolidColorBrush(Color.FromRgb(0xD6, 0xC4, 0x4A));
            return new SolidColorBrush(Color.FromRgb(0x6D, 0xCC, 0x8E));
        }

        private static string MapOpenFluxUiError(string detail)
        {
            return detail switch
            {
                "empty" or "scheme" or "host" => "Lang.OpenFlux.Error.BadUrl",
                "key-short" => "Lang.OpenFlux.Error.KeyShort",
                "document" => "Lang.OpenFlux.Error.Document",
                "captcha" => "Lang.OpenFlux.Error.Captcha",
                "transport" => "Lang.OpenFlux.Error.Transport",
                "listen" => "Lang.OpenFlux.Error.Listen",
                "login" => "Lang.OpenFlux.Error.Login",
                "timeout" => "Lang.OpenFlux.Error.Timeout",
                "http" => "Lang.OpenFlux.Error.Http",
                "register-fail" => "Lang.OpenFlux.Status.RegisterFail",
                "peer" => "Lang.OpenFlux.Status.PeerMissing",
                _ => "Lang.OpenFlux.Error.Generic"
            };
        }
    }
}
