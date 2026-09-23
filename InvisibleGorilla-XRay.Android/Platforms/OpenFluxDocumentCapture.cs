using System;
using System.IO;
using System.Threading;
using Android.App;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Android.Platforms
{
    using InvisibleGorillaXRay.Core;

    /// <summary>
    /// Loads the Yandex document in the system WebView so the page can pass a browser check,
    /// then stores the HTML and cookies for the OpenFlux sidecar. The address is never logged.
    /// </summary>
    internal static class OpenFluxDocumentCapture
    {
        public static bool TryCapture(string docUrl)
        {
            if (string.IsNullOrWhiteSpace(docUrl))
                return false;

            string dir = InvisibleGorillaXRay.Values.Directory.OPENFLUX;
            Directory.CreateDirectory(dir);
            string htmlPath = Path.Combine(dir, "doc.html");
            string cookiePath = Path.Combine(dir, "doc.cookies");
            TryDelete(htmlPath);
            TryDelete(cookiePath);

            Activity? activity = MainActivity.Current;
            if (activity == null)
                return false;

            var done = new ManualResetEventSlim(false);
            bool ok = false;
            activity.RunOnUiThread(() => BeginCapture(activity, docUrl.Trim(), htmlPath, cookiePath, success =>
            {
                ok = success;
                done.Set();
            }));

            if (!done.Wait(TimeSpan.FromSeconds(70)))
                return false;

            return ok && File.Exists(htmlPath);
        }

        private static void BeginCapture(Activity activity, string docUrl, string htmlPath, string cookiePath, Action<bool> done)
        {
            CookieManager cookies = CookieManager.Instance;
            cookies.SetAcceptCookie(true);
            cookies.Flush();

            var web = new WebView(activity);
            web.Settings.JavaScriptEnabled = true;
            web.Settings.DomStorageEnabled = true;
            cookies.SetAcceptThirdPartyCookies(web, true);

            var dialog = new Dialog(activity);
            dialog.SetContentView(web);
            dialog.SetCancelable(true);

            string latestHtml = "";
            bool completed = false;
            var handler = new Handler(Looper.MainLooper);
            int ticks = 0;

            void Finish(bool success)
            {
                if (completed)
                    return;
                completed = true;
                handler.RemoveCallbacksAndMessages(null);
                try
                {
                    if (dialog.IsShowing)
                        dialog.Dismiss();
                }
                catch { }
                try
                {
                    web.StopLoading();
                    web.Destroy();
                }
                catch { }
                done(success);
            }

            void Poll()
            {
                if (completed)
                    return;

                ticks++;
                if (latestHtml.Contains("client-config", StringComparison.Ordinal))
                {
                    try
                    {
                        File.WriteAllText(htmlPath, latestHtml);
                        string header = cookies.GetCookie(docUrl) ?? "";
                        if (!string.IsNullOrWhiteSpace(header))
                            File.WriteAllText(cookiePath, header);
                        cookies.Flush();
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException("OpenFlux.Document", ex);
                        Finish(false);
                        return;
                    }
                    Finish(true);
                    return;
                }

                if (ticks >= 80)
                {
                    Finish(false);
                    return;
                }

                if (!dialog.IsShowing && ticks >= 10)
            {
                dialog.Show();
                dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            }

                web.EvaluateJavascript(
                    "(function(){return document.documentElement?document.documentElement.outerHTML:'';})()",
                    new HtmlCallback(raw =>
                    {
                        if (!completed)
                            latestHtml = Unquote(raw);
                    }));
                handler.PostDelayed(Poll, 700);
            }

            dialog.CancelEvent += (_, _) => Finish(false);
            web.SetWebViewClient(new CaptureClient(() => { }));
            web.LoadUrl(docUrl);
            handler.PostDelayed(Poll, 700);
        }

        private static string Unquote(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                return "";

            try
            {
                if (raw.StartsWith("\"", StringComparison.Ordinal))
                    return JsonConvert.DeserializeObject<string>(raw) ?? "";
            }
            catch
            {
            }

            return raw;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private sealed class CaptureClient : WebViewClient
        {
            private readonly Action onPage;

            public CaptureClient(Action onPage)
            {
                this.onPage = onPage;
            }

            public override void OnPageFinished(WebView? view, string? url)
            {
                base.OnPageFinished(view, url);
                onPage();
            }
        }

        private sealed class HtmlCallback : Java.Lang.Object, IValueCallback
        {
            private readonly Action<string> onValue;

            public HtmlCallback(Action<string> onValue)
            {
                this.onValue = onValue;
            }

            public void OnReceiveValue(Java.Lang.Object? value)
            {
                onValue(value?.ToString() ?? "");
            }
        }
    }
}
