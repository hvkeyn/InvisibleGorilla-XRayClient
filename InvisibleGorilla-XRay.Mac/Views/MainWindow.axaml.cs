using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;

namespace InvisibleGorillaXRay.Mac.Views
{
    using Models;
    using Values;
    using Services;
    using Services.Analytics.General;
    using Services.Analytics.MainWindow;

    public partial class MainWindow : Window
    {
        private bool isRerunRequest;
        private bool isRunWorkerBusy;

        private Func<bool> isNeedToShowPolicyWindow;
        private Func<bool> shouldStartHidden;
        private Func<bool> isNeedToAutoConnect;
        private Func<Config> getConfig;
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
            TryOpenPolicyWindow();
            TryStartHidden();
            TryAutoConnect();
            RunUpdateCheck();

            AnalyticsService.SendEvent(new AppOpenedEvent());
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

        private void OnManageServersClick(object sender, PointerPressedEventArgs e)
        {
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

        private void OpenServerWindow()
        {
            ServerWindow serverWindow = openServerWindow.Invoke();
            serverWindow.ShowDialog(this);
        }

        private void OpenSettingsWindow()
        {
            SettingsWindow settingsWindow = openSettingsWindow.Invoke();
            settingsWindow.ShowDialog(this);
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
            statusRun.IsVisible = true;
            statusStop.IsVisible = false;
            statusWaitForRun.IsVisible = false;

            buttonStop.IsVisible = true;
            buttonCancel.IsVisible = false;
            buttonRun.IsVisible = false;
        }

        private void ShowStopStatus()
        {
            statusStop.IsVisible = true;
            statusRun.IsVisible = false;
            statusWaitForRun.IsVisible = false;

            buttonRun.IsVisible = true;
            buttonCancel.IsVisible = false;
            buttonStop.IsVisible = false;
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

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
