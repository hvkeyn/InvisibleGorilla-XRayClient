using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace InvisibleGorillaXRay.Mac
{
    using Managers;

    public partial class App : Application
    {
        private MacAppManager? appManager;
        private bool _cleanedUp;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                appManager = new MacAppManager(desktop.Args ?? Array.Empty<string>());
                appManager.Initialize();

                var mainWindow = appManager.WindowFactory.CreateMainWindow();
                desktop.MainWindow = mainWindow;

                desktop.ShutdownRequested += (s, e) => CleanupBeforeExit();
                AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanupBeforeExit();
                AppDomain.CurrentDomain.UnhandledException += (s, e) => CleanupBeforeExit();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void CleanupBeforeExit()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;

            var task = Task.Run(() =>
            {
                try { appManager?.Core?.Stop(); } catch { }
                try { appManager?.Core?.DisableMode(); } catch { }
            });
            task.Wait(TimeSpan.FromSeconds(3));
        }
    }
}
