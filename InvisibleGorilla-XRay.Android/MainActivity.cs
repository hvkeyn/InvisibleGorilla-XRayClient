using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace InvisibleGorillaXRay.Android
{
    using InvisibleGorillaXRay.Android.Handlers.DeepLinks;
    using InvisibleGorillaXRay.Core;

    [Activity(
        Label = "Invisible Gorilla XRay",
        Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTask,
        ConfigurationChanges =
            ConfigChanges.Orientation |
            ConfigChanges.ScreenSize |
            ConfigChanges.UiMode |
            ConfigChanges.ScreenLayout |
            ConfigChanges.SmallestScreenSize |
            ConfigChanges.Density)]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "invxray")]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "vless")]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "vmess")]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "trojan")]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "ss")]
    [IntentFilter(
        new[] { Intent.ActionSend },
        Categories = new[] { Intent.CategoryDefault },
        DataMimeType = "text/plain")]
    public class MainActivity : AvaloniaMainActivity<App>
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            DispatchIncomingIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);

            if (intent == null)
                return;

            Intent = intent;
            DispatchIncomingIntent(intent);
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
                .LogToTrace();
        }

        private static void DispatchIncomingIntent(Intent? intent)
        {
            if (intent == null)
                return;

            try
            {
                if (TryDispatchViewIntent(intent))
                    return;

                TryDispatchSharedText(intent);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainActivity.DispatchIncomingIntent", ex);
            }
        }

        private static bool TryDispatchViewIntent(Intent intent)
        {
            if (!string.Equals(intent.Action, Intent.ActionView, StringComparison.Ordinal))
                return false;

            return AndroidDeepLinkDispatcher.DispatchExternalValue(intent.DataString);
        }

        private static bool TryDispatchSharedText(Intent intent)
        {
            if (!string.Equals(intent.Action, Intent.ActionSend, StringComparison.Ordinal))
                return false;

            string? sharedText = intent.GetStringExtra(Intent.ExtraText);
            return AndroidDeepLinkDispatcher.DispatchExternalValue(sharedText);
        }
    }
}
