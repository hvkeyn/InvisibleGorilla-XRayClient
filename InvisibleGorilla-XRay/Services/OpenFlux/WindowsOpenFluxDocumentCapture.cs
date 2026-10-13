using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Services.OpenFlux
{
    using InvisibleGorillaXRay.Core;

    internal static class WindowsOpenFluxDocumentCapture
    {
        public static Task<bool> CaptureAsync(Window owner, string docUrl)
        {
            var done = new TaskCompletionSource<bool>();
            if (string.IsNullOrWhiteSpace(docUrl) || owner == null)
            {
                done.TrySetResult(false);
                return done.Task;
            }

            string dir = Values.Directory.OPENFLUX;
            Directory.CreateDirectory(dir);
            string htmlPath = Path.Combine(dir, "doc.html");
            string cookiePath = Path.Combine(dir, "doc.cookies");

            var window = new Window
            {
                Owner = owner,
                Width = 440,
                Height = 640,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Title = "OpenFlux",
                Background = System.Windows.Media.Brushes.Black
            };
            var web = new WebView2();
            window.Content = web;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            int ticks = 0;
            bool finished = false;

            void Finish(bool ok)
            {
                if (finished)
                    return;
                finished = true;
                timer.Stop();
                try
                {
                    if (window.IsVisible)
                        window.Close();
                }
                catch
                {
                }
                done.TrySetResult(ok);
            }

            timer.Tick += async (_, _) =>
            {
                if (finished)
                    return;
                ticks++;
                if (ticks > 80)
                {
                    Finish(false);
                    return;
                }

                try
                {
                    if (web.CoreWebView2 == null)
                        return;
                    string raw = await web.ExecuteScriptAsync(
                        "(function(){return document.documentElement?document.documentElement.outerHTML:'';})()");
                    string html = DecodeScriptResult(raw);
                    if (html.IndexOf("id=\"client-config\"", StringComparison.Ordinal) < 0)
                        return;

                    File.WriteAllText(htmlPath, html);
                    try
                    {
                        var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync(docUrl);
                        string header = string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value));
                        if (!string.IsNullOrWhiteSpace(header))
                            File.WriteAllText(cookiePath, header);
                    }
                    catch (Exception cookieEx)
                    {
                        DiagnosticLog.WriteException("OpenFlux.Capture.Cookies", cookieEx);
                    }
                    Finish(true);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("OpenFlux.Capture.Read", ex);
                }
            };

            window.Closed += (_, _) => Finish(false);
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await web.EnsureCoreWebView2Async();
                    web.CoreWebView2.Navigate(docUrl.Trim());
                    timer.Start();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("OpenFlux.Capture", ex);
                    Finish(false);
                }
            };
            window.Show();
            return done.Task;
        }

        private static string DecodeScriptResult(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                return "";
            try
            {
                return JsonConvert.DeserializeObject<string>(raw) ?? "";
            }
            catch
            {
                return raw;
            }
        }
    }
}