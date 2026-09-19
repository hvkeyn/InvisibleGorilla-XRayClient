using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace InvisibleGorillaXRay.Services.OpenFlux
{
    using Models;

    public sealed class OpenFluxUrlCheckResult
    {
        public bool FormatOk;
        public bool Reachable;
        public bool LooksLikeDocument;
        public int LatencyMs;
        public string CanonicalUrl = "";
        public string Key = "";
        public string Error = "";
    }

    public static class OpenFluxUrlCheck
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
        }

        public static OpenFluxUrlCheckResult Inspect(string url)
        {
            OpenFluxUrlCheckResult result = new OpenFluxUrlCheckResult();
            string canonical = OpenFluxUrl.Trim(url);
            result.CanonicalUrl = canonical;
            result.Key = OpenFluxUrl.DeriveKey(canonical);

            if (!OpenFluxUrl.TryValidate(canonical, out string error))
            {
                result.Error = string.IsNullOrWhiteSpace(error) ? "empty" : error;
                return result;
            }

            result.FormatOk = true;
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using HttpResponseMessage response = Http.GetAsync(
                    canonical,
                    HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                watch.Stop();
                result.LatencyMs = (int)Math.Max(1, watch.ElapsedMilliseconds);
                result.Reachable = true;

                string finalHost = (response.RequestMessage?.RequestUri?.Host ?? "").ToLowerInvariant();
                if (finalHost.IndexOf("passport.yandex", StringComparison.Ordinal) >= 0)
                {
                    result.Error = "login";
                    return result;
                }

                int code = (int)response.StatusCode;
                if (code >= 400)
                {
                    result.Error = "http";
                    return result;
                }

                result.LooksLikeDocument = true;
                return result;
            }
            catch (Exception)
            {
                watch.Stop();
                result.LatencyMs = (int)watch.ElapsedMilliseconds;
                result.Error = "timeout";
                return result;
            }
        }

        public static Task<OpenFluxUrlCheckResult> InspectAsync(string url, CancellationToken token)
        {
            return Task.Run(() => Inspect(url), token);
        }
    }
}
