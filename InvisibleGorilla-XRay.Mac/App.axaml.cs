using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace InvisibleGorillaXRay.Mac
{
    using Managers;
    using Handlers;
    using Factories;

    public partial class App : Application
    {
        private MacAppManager? appManager;

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
            try { appManager?.Core?.Stop(); } catch { }
            try { appManager?.Core?.DisableMode(); } catch { }
        }
    }
}
