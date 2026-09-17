using System;
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
                Timeout = TimeSpan.FromSeconds(8)
            };
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

        public static bool Register(string url, string encryptionKey, string registerUrl, CancellationToken token)
        {
            url = (url ?? "").Trim();
            string endpoint = string.IsNullOrWhiteSpace(registerUrl) ? DefaultRegisterUrl : registerUrl.Trim();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(encryptionKey))
                return false;

            string payload = JsonConvert.SerializeObject(new
            {
                url,
                mac = ComputeMac(encryptionKey, url)
            });

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            try
            {
                using HttpResponseMessage response = http.SendAsync(request, token).GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                DiagnosticLog.Write(Tag, $"POST {endpoint} -> {(int)response.StatusCode}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(Tag, "register error: " + ex.Message);
                return false;
            }
        }
    }
}
