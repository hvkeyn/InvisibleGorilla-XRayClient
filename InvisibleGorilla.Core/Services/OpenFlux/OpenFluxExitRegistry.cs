using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Services.OpenFlux
{
    using InvisibleGorillaXRay.Core;

    /// <summary>
    /// Tells the Fornex exit to spawn a dedicated process for this document URL.
    /// Goes direct (no system proxy) so a brand-new URL can be registered before the tunnel exists.
    /// </summary>
    public static class OpenFluxExitRegistry
    {
        public const string DefaultRegisterUrl = "http://5.187.4.132:17911/v1/urls";
        private const string Tag = "OpenFlux.Register";
        public static string LastError { get; private set; } = "";

        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null,
                AutomaticDecompression = DecompressionMethods.None
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
        }

        public static int PingExit()
        {
            string health = DefaultRegisterUrl.Replace("/v1/urls", "/v1/health", StringComparison.Ordinal);
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, health);
                using HttpResponseMessage response = http.SendAsync(request, cts.Token).GetAwaiter().GetResult();
                watch.Stop();
                if (!response.IsSuccessStatusCode)
                    return -2;
                return (int)Math.Max(1, watch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                return -1;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(Tag, "exit ping failed: " + ex.GetType().Name);
                return -2;
            }
        }

        public static string ComputeMac(string encryptionKey, string url)
        {
            byte[] key = Encoding.UTF8.GetBytes((encryptionKey ?? "").Trim());
            byte[] msg = Encoding.UTF8.GetBytes("openflux-register-v1|" + (url ?? ""));
            using HMACSHA256 hmac = new HMACSHA256(key);
            byte[] hash = hmac.ComputeHash(msg);
            StringBuilder hex = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        public static void RegisterInBackground(string url, string encryptionKey, string registerUrl = null)
        {
            string secret = ResolveSecret(encryptionKey);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Register(url, secret, registerUrl, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(Tag, "background register failed: " + ex.Message);
                }
            });
        }

        private static string ResolveSecret(string encryptionKey)
        {
            string trimmed = (encryptionKey ?? "").Trim();
            if (trimmed.Length == 0)
                return "";
            try
            {
                if ((trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
                    && System.IO.File.Exists(trimmed))
                    return System.IO.File.ReadAllText(trimmed).Trim();
            }
            catch { }
            return trimmed;
        }

        private static object BuildPayload(string url, string encryptionKey)
        {
            string mac = ComputeMac(encryptionKey, url);
            string openFluxDir = InvisibleGorillaXRay.Values.Directory.OPENFLUX;
            string exitHtml = System.IO.Path.Combine(openFluxDir, "doc-exit.html");
            string exitCookies = System.IO.Path.Combine(openFluxDir, "doc-exit.cookies");
            string htmlPath = System.IO.File.Exists(exitHtml)
                ? exitHtml
                : System.IO.Path.Combine(openFluxDir, "doc.html");
            string cookiePath = System.IO.File.Exists(exitCookies)
                ? exitCookies
                : System.IO.Path.Combine(openFluxDir, "doc.cookies");
            string htmlB64 = "";
            string cookie = "";
            try
            {
                if (System.IO.File.Exists(htmlPath))
                {
                    byte[] raw = System.IO.File.ReadAllBytes(htmlPath);
                    if (raw.Length > 32 && raw.Length <= 2500000
                        && Encoding.UTF8.GetString(raw).IndexOf("client-config", StringComparison.Ordinal) >= 0)
                    {
                        htmlB64 = Convert.ToBase64String(raw);
                    }
                }

                if (htmlB64.Length > 0 && System.IO.File.Exists(cookiePath))
                {
                    cookie = System.IO.File.ReadAllText(cookiePath) ?? "";
                    if (cookie.Length > 16000)
                        cookie = "";
                }
            }
            catch
            {
                htmlB64 = "";
                cookie = "";
            }

            if (htmlB64.Length == 0)
                return new { url, mac };

            DiagnosticLog.Write(Tag, "register snapshot attached");
            return new { url, mac, html_b64 = htmlB64, cookie };
        }

        public static bool Register(string url, string encryptionKey, string registerUrl, CancellationToken token)
        {
            url = (url ?? "").Trim();
            string endpoint = string.IsNullOrWhiteSpace(registerUrl) ? DefaultRegisterUrl : registerUrl.Trim();
            LastError = "";
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(encryptionKey))
            {
                LastError = "empty url or key";
                return false;
            }

            string payload = JsonConvert.SerializeObject(BuildPayload(url, encryptionKey));

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            try
            {
                using HttpResponseMessage response = http.SendAsync(request, token).GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                DiagnosticLog.Write(Tag, $"POST {endpoint} -> {(int)response.StatusCode}");
                if (!response.IsSuccessStatusCode)
                {
                    LastError = $"HTTP {(int)response.StatusCode}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException == null ? ex.Message : ex.Message + " / " + ex.InnerException.Message;
                LastError = detail;
                DiagnosticLog.Write(Tag, "register error: " + detail);
                return false;
            }
        }
    }
}
