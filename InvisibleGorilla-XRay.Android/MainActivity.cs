using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using System.Threading;
using System.Threading.Tasks;

namespace InvisibleGorillaXRay.Android
{
    using InvisibleGorillaXRay.Android.Handlers.DeepLinks;
    using InvisibleGorillaXRay.Core;

    [Activity(
        Label = "Invisible Gorilla XRay",
        Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTask,
        WindowSoftInputMode = SoftInput.AdjustResize,
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
        DataScheme = "incy")]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "happ")]
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
        private const int NotificationPermissionRequestCode = 1001;
        private const int VpnPermissionRequestCode = 1002;
        private static readonly object ActivitySync = new();
        private static MainActivity? currentActivity;
        private static TaskCompletionSource<bool>? vpnPermissionRequest;
        private static int globalHandlersRegistered;
        internal static bool IsInForeground { get; private set; } = true;
        internal static DateTime SuppressForegroundChangedUntilUtc { get; set; }
        internal static event Action<bool>? ForegroundChanged;

        internal static MainActivity? Current
        {
            get
            {
                lock (ActivitySync)
                    return currentActivity;
            }
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            RegisterGlobalExceptionHandlersOnce();

            try
            {
                base.OnCreate(savedInstanceState);
                SetCurrentActivity(this);
                RequestNotificationPermissionIfNeeded();
                DispatchIncomingIntent(Intent);
                DiagnosticLog.Write("MainActivity", $"OnCreate completed, intent={Intent?.Action ?? "<null>"}");
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainActivity.OnCreate", ex);
                throw;
            }
        }

        private static void RegisterGlobalExceptionHandlersOnce()
        {
            if (Interlocked.Exchange(ref globalHandlersRegistered, 1) == 1)
                return;

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                try
                {
                    Exception? ex = args.ExceptionObject as Exception;
                    if (ex != null)
                        DiagnosticLog.WriteException("AppDomain.UnhandledException", ex);
                    else
                        DiagnosticLog.Write("AppDomain.UnhandledException", $"Unknown error: {args.ExceptionObject}");
                }
                catch
                {
                    // Crash handler must never throw.
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                try
                {
                    DiagnosticLog.WriteException("TaskScheduler.UnobservedTaskException", args.Exception);
                    args.SetObserved();
                }
                catch
                {
                    // Crash handler must never throw.
                }
            };

            try
            {
                global::Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaUncaughtExceptionHandler();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainActivity.RegisterJavaUncaughtHandler", ex);
            }

            DiagnosticLog.Write("MainActivity", "Global exception handlers installed");
        }

        private sealed class JavaUncaughtExceptionHandler : global::Java.Lang.Object, global::Java.Lang.Thread.IUncaughtExceptionHandler
        {
            private readonly global::Java.Lang.Thread.IUncaughtExceptionHandler? previous;

            public JavaUncaughtExceptionHandler()
            {
                previous = global::Java.Lang.Thread.DefaultUncaughtExceptionHandler;
            }

            public void UncaughtException(global::Java.Lang.Thread? t, global::Java.Lang.Throwable? e)
            {
                try
                {
                    string threadName = t?.Name ?? "<unknown>";
                    string typeName = e?.GetType().FullName ?? "<unknown>";
                    string message = e?.Message ?? string.Empty;
                    DiagnosticLog.Write("Java.UncaughtException", $"thread={threadName}, type={typeName}, message={message}");
                    if (e != null)
                        DiagnosticLog.Write("Java.UncaughtException", $"stack={e.ToString()}");
                }
                catch
                {
                    // Crash handler must never throw.
                }

                previous?.UncaughtException(t, e);
            }
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            SetCurrentActivity(this);

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

        protected override void OnResume()
        {
            base.OnResume();
            SetCurrentActivity(this);
            IsInForeground = true;
            // FocusEvent runs on this looper. Any Avalonia work in the same turn
            // ANRs when the user comes back from Chrome or the ANR dialog.
            new Handler(Looper.MainLooper).PostDelayed(() =>
            {
                if (!IsInForeground)
                    return;
                if (DateTime.UtcNow < SuppressForegroundChangedUntilUtc)
                    return;
                try { ForegroundChanged?.Invoke(true); } catch { }
            }, 2000);
        }

        protected override void OnPause()
        {
            IsInForeground = false;
            try { ForegroundChanged?.Invoke(false); } catch { }
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            lock (ActivitySync)
            {
                if (ReferenceEquals(currentActivity, this))
                    currentActivity = null;
            }

            base.OnDestroy();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            if (requestCode == VpnPermissionRequestCode)
            {
                TaskCompletionSource<bool>? completion;
                lock (ActivitySync)
                {
                    completion = vpnPermissionRequest;
                    vpnPermissionRequest = null;
                }

                bool granted = resultCode == Result.Ok || global::Android.Net.VpnService.Prepare(this) == null;
                completion?.TrySetResult(granted);
                return;
            }

            base.OnActivityResult(requestCode, resultCode, data);
        }

        internal static Task<bool> EnsureVpnPreparedAsync()
        {
            MainActivity? activity = GetCurrentActivity();
            if (activity == null)
                return Task.FromResult(false);

            Intent? intent = global::Android.Net.VpnService.Prepare(activity);
            if (intent == null)
                return Task.FromResult(true);

            lock (ActivitySync)
            {
                if (vpnPermissionRequest != null)
                    return vpnPermissionRequest.Task;

                vpnPermissionRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            activity.RunOnUiThread(() =>
            {
                try
                {
                    activity.StartActivityForResult(intent, VpnPermissionRequestCode);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("MainActivity.StartVpnConsent", ex);

                    lock (ActivitySync)
                    {
                        vpnPermissionRequest?.TrySetResult(false);
                        vpnPermissionRequest = null;
                    }
                }
            });

            return vpnPermissionRequest.Task;
        }

        private static void DispatchIncomingIntent(Intent? intent)
        {
            if (intent == null)
                return;

            // Diagnostic helper: set the OpenFlux document from adb and start the session.
            string openFluxUrl = intent.GetStringExtra("openFluxUrl");
            if (!string.IsNullOrWhiteSpace(openFluxUrl))
            {
                try
                {
                    var handler = new InvisibleGorillaXRay.Handlers.SettingsHandler(
                        () => new InvisibleGorillaXRay.Android.Handlers.Settings.AndroidStartup());
                    var settings = handler.UserSettings;
                    var profile = settings.GetOpenFluxProfile();
                    profile.DocUrl = openFluxUrl.Trim();
                    profile.EncryptionKey = InvisibleGorillaXRay.Models.OpenFluxUrl.DeriveKey(profile.DocUrl);
                    settings.OpenFlux = profile;
                    handler.UpdateUserSettings(settings);
                    InvisibleGorillaXRay.Core.DiagnosticLog.Write("MainActivity",
                        $"OpenFlux document set from adb; key fingerprint {profile.EncryptionKey.Substring(0, Math.Min(12, profile.EncryptionKey.Length))}");
                }
                catch (Exception ex)
                {
                    InvisibleGorillaXRay.Core.DiagnosticLog.WriteException("MainActivity.OpenFluxUrl", ex);
                }
            }

            // Diagnostic helper: make OpenFlux the selected configuration.
            if (intent.GetIntExtra("selectOpenFlux", 0) == 1 || intent.GetIntExtra("startOpenFlux", 0) == 1)
            {
                try
                {
                    var selectHandler = new InvisibleGorillaXRay.Handlers.SettingsHandler(
                        () => new InvisibleGorillaXRay.Android.Handlers.Settings.AndroidStartup());
                    string marker = System.IO.Path.Combine(
                        System.IO.Path.GetFullPath(InvisibleGorillaXRay.Values.Directory.CONFIGS),
                        "__OpenFluxProfile__.json");
                    selectHandler.UpdateCurrentConfigPath(marker);
                    InvisibleGorillaXRay.Core.DiagnosticLog.Write("MainActivity", "OpenFlux selected from adb");
                }
                catch (Exception ex)
                {
                    InvisibleGorillaXRay.Core.DiagnosticLog.WriteException("MainActivity.SelectOpenFlux", ex);
                }
            }

            // Diagnostic helper: probe every subscription node over the current network.
            if (intent.GetIntExtra("sweepMobile", 0) is int sweepDelay && sweepDelay > 0)
            {
                RunMobileSweep(sweepDelay);
                return;
            }

            // Diagnostic helper: adb can ask the app to copy its configs somewhere readable.
            if (string.Equals(intent.GetStringExtra("exportConfigs"), "1", StringComparison.Ordinal))
            {
                ExportConfigsForDiagnostics();
                return;
            }

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

        private static void RunMobileSweep(int delaySeconds)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);

                    string root = System.IO.Path.Combine(InvisibleGorillaXRay.Values.Directory.ROOT, "Configs");
                    if (!System.IO.Directory.Exists(root))
                        return;

                    var configs = new System.Collections.Generic.List<string>();
                    foreach (string file in System.IO.Directory.GetFiles(root, "*.json", System.IO.SearchOption.AllDirectories))
                    {
                        if (file.IndexOf("Goida", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        if (new System.IO.FileInfo(file).Length < 20)
                            continue;
                        configs.Add(file);
                    }

                    if (configs.Count == 0)
                        return;

                    DiagnosticLog.Write("Sweep", $"Probing {configs.Count} node(s) over the current network");
                    int[] latencies = InvisibleGorillaXRay.Services.SubscriptionServerEndpoint.ProbeFiles(configs, 2000);

                    var report = new System.Text.StringBuilder();
                    report.AppendLine("node\tms");
                    int alive = 0;
                    for (int i = 0; i < configs.Count && i < latencies.Length; i++)
                    {
                        bool ok = latencies[i] >= 0;
                        if (ok)
                            alive++;
                        report.AppendLine(System.IO.Path.GetFileName(configs[i]) + "\t" + latencies[i]);
                    }

                    string summary = $"Sweep done: {alive}/{configs.Count} reachable";
                    DiagnosticLog.Write("Sweep", summary);
                    global::Android.Content.Context context = global::Android.App.Application.Context;
                    InvisibleGorillaXRay.Android.Services.AndroidLogShareService.SaveTextToDownloads(
                        context, "sweep-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", summary + "\n" + report);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("MainActivity.RunMobileSweep", ex);
                }
            });
        }
        private static void ExportConfigsForDiagnostics()
        {
            try
            {
                string source = System.IO.Path.Combine(InvisibleGorillaXRay.Values.Directory.ROOT, "Configs");
                string target = System.IO.Path.Combine(
                    global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath ?? "/sdcard",
                    "export");

                if (!System.IO.Directory.Exists(source))
                    return;

                if (System.IO.Directory.Exists(target))
                    System.IO.Directory.Delete(target, true);

                System.IO.Directory.CreateDirectory(target);

                // ASCII names only: adb cannot open the Cyrillic and "|"-laden file names that
                // subscription nodes use, and the index keeps the mapping.
                // adb cannot read /sdcard/Android/data, so publish through MediaStore into
                // Download/InvisibleGorilla-XRay/config-export/ with ASCII names.
                int copied = 0;
                var index = new System.Text.StringBuilder();
                global::Android.Content.Context context = global::Android.App.Application.Context;
                foreach (string file in System.IO.Directory.GetFiles(source, "*", System.IO.SearchOption.AllDirectories))
                {
                    // The Goida cache holds hundreds of thousands of nodes; skip it.
                    if (file.IndexOf("Goida", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    copied++;
                    string name = "config-export/node-" + copied.ToString("D4") + ".json";
                    string text;
                    try { text = System.IO.File.ReadAllText(file); }
                    catch { continue; }

                    InvisibleGorillaXRay.Android.Services.AndroidLogShareService.SaveTextToDownloads(context, name, text);
                    index.AppendLine("node-" + copied.ToString("D4") + ".json\t" + System.IO.Path.GetRelativePath(source, file));
                }

                InvisibleGorillaXRay.Android.Services.AndroidLogShareService.SaveTextToDownloads(
                    context, "config-export/index.txt", index.ToString());
                System.IO.File.WriteAllText(System.IO.Path.Combine(target, "_done.txt"), copied.ToString());
                DiagnosticLog.Write("Export", $"Exported {copied} config file(s) to {target}");
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainActivity.ExportConfigs", ex);
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

        private void RequestNotificationPermissionIfNeeded()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
                return;

            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted)
                return;

            RequestPermissions(
                new[] { global::Android.Manifest.Permission.PostNotifications },
                NotificationPermissionRequestCode);
        }

        private static MainActivity? GetCurrentActivity()
        {
            lock (ActivitySync)
            {
                return currentActivity;
            }
        }

        internal static MainActivity? CurrentActivity => GetCurrentActivity();

        private static void SetCurrentActivity(MainActivity activity)
        {
            lock (ActivitySync)
            {
                currentActivity = activity;
            }
        }
    }
}
