using System;
using System.Windows;
using Microsoft.Win32;

namespace InvisibleGorillaXRay
{
    using Managers;
    using Handlers;
    using Factories;
    using Handlers.Tunnels;

    public partial class App : Application
    {
        private AppManager appManager;
        private WindowFactory windowFactory;
        private int cleanupDone;

        private EventHandler processExitHandler;
        private SessionEndedEventHandler sessionEndedHandler;

        protected override void OnStartup(StartupEventArgs e)
        {
            WindowsStaleTunCleanup.TryDisableStaleTunnel();
            InitializeAppManager();
            InitializeNotifyIcon();
            InitializeWindowFactory();
            InitializeMainWindow();
            HandlePipes();
            HandleExitingEvents();

            void InitializeAppManager()
            {
                appManager = new AppManager(e.Args);
                appManager.Initialize();
            }

            void InitializeNotifyIcon()
            {
                appManager.HandlersManager.GetHandler<NotifyHandler>().InitializeNotifyIcon();
            }

            void InitializeWindowFactory()
            {
                windowFactory = appManager.WindowFactory;
            }

            void InitializeMainWindow()
            {
                MainWindow mainWindow = windowFactory.CreateMainWindow();
                mainWindow.Show();
            }

            void HandlePipes()
            {
                if (IsThereAnyArg())
                    PipeManager.SignalThisApp(e.Args);
                
                PipeManager.ListenForPipes();
            }

            void HandleExitingEvents()
            {
                processExitHandler = (sender, args) => CleanupBeforeExit();
                sessionEndedHandler = (sender, args) => CleanupBeforeExit();

                AppDomain.CurrentDomain.ProcessExit += processExitHandler;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                DispatcherUnhandledException += OnDispatcherUnhandledException;
                SystemEvents.SessionEnded += sessionEndedHandler;
            }

            bool IsThereAnyArg() => e.Args.Length != 0;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            CleanupBeforeExit();

            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            SystemEvents.SessionEnded -= sessionEndedHandler;

            base.OnExit(e);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                    Core.DiagnosticLog.WriteException("App.UnhandledException", ex);
                else
                    Core.DiagnosticLog.Write("App.UnhandledException", e.ExceptionObject?.ToString() ?? "unknown");
            }
            catch { }

            CleanupBeforeExit();
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Core.DiagnosticLog.WriteException("App.DispatcherUnhandledException", e.Exception);
            }
            catch { }

            // Keep the idle UI alive when a lookup/probe throws on the dispatcher.
            e.Handled = true;
        }

        private void CleanupBeforeExit()
        {
            // Run at most once: this is reachable from OnExit, ProcessExit,
            // SessionEnded and UnhandledException, and a second Core.Stop()
            // can block forever on a native lock held by the first call.
            if (System.Threading.Interlocked.CompareExchange(ref cleanupDone, 1, 0) != 0)
                return;

            try
            {
                appManager?.HandlersManager?.GetHandler<GoidaProfileHandler>()?.StopBackground();
            }
            catch { }

            try
            {
                // Never block shutdown on the native stop call.
                System.Threading.Tasks.Task stopTask = System.Threading.Tasks.Task.Run(() =>
                {
                    // On crash/exit, routes must be removed first. Native StopServer can
                    // hang or crash inside XRayCore.dll, and then TUN would be left active.
                    try { appManager?.Core?.DisableMode(); } catch { }
                    try { WindowsStaleTunCleanup.TryDisableStaleTunnel(); } catch { }
                    try { appManager?.Core?.Stop(); } catch { }
                });
                stopTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
    }
}
