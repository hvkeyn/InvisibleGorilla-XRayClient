using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace InvisibleGorillaXRay.Android
{
    using InvisibleGorillaXRay.Android.Managers;
    using InvisibleGorillaXRay.Android.Platforms;
    using InvisibleGorillaXRay.Android.Views;

    public partial class App : Application
    {
        private AndroidAppManager? appManager;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                AndroidAppStorage.ConfigureAppRoot();
                AndroidAppStorage.EnsureRuntimeAssets();

                appManager = new AndroidAppManager();
                appManager.Initialize();

                singleView.MainView = new MainView(appManager);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
