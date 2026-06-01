using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Services
{
    public sealed class ConnectionInfo
    {
        public bool Ok { get; init; }
        public string Ip { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string Country { get; init; } = string.Empty;
        public string Org { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;

        public string Location
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(City) && !string.IsNullOrWhiteSpace(Country))
                    return $"{City}, {Country}";
                if (!string.IsNullOrWhiteSpace(Country))
                    return Country;
                return City;
            }
        }
    }

    public sealed class ConnectionInfoService
    {
        private const string PrimaryEndpoint = "https://ipinfo.io/json";
        private const string FallbackEndpoint = "https://ifconfig.co/json";
        private const string UserAgent = "InvisibleGorilla-XRay";

        public async Task<ConnectionInfo> LookupAsync(IWebProxy proxy = null, CancellationToken token = default)
        {
            ConnectionInfo primary = await TryLookupAsync(PrimaryEndpoint, proxy, token).ConfigureAwait(false);
            if (primary.Ok || token.IsCancellationRequested)
                return primary;

            ConnectionInfo fallback = await TryLookupAsync(FallbackEndpoint, proxy, token).ConfigureAwait(false);
            return fallback.Ok ? fallback : primary;
        }

        private static async Task<ConnectionInfo> TryLookupAsync(string url, IWebProxy proxy, CancellationToken token)
        {
            using HttpClientHandler handler = new HttpClientHandler
            {
                UseProxy = proxy != null,
                Proxy = proxy,
                AllowAutoRedirect = true
            };

            using HttpClient client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new ConnectionInfo { Ok = false, Error = $"HTTP {(int)response.StatusCode}" };

                string payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                JObject root = JObject.Parse(payload);
                string ip = FirstNonEmpty(root, "ip", "query");
                if (string.IsNullOrWhiteSpace(ip))
                    return new ConnectionInfo { Ok = false, Error = "No IP in response" };

                return new ConnectionInfo
                {
                    Ok = true,
                    Ip = ip,
                    City = FirstNonEmpty(root, "city"),
                    Country = FirstNonEmpty(root, "country", "country_iso"),
                    Org = FirstNonEmpty(root, "org", "asn_org", "isp")
                };
            }
            catch (OperationCanceledException)
            {
                return new ConnectionInfo { Ok = false, Error = "Canceled" };
            }
            catch (Exception ex)
            {
                return new ConnectionInfo { Ok = false, Error = ex.Message };
            }
        }

        private static string FirstNonEmpty(JObject root, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = (string)root[key];
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return string.Empty;
        }
    }
}
