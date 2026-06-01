using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Services
{
    using Core;

    /// <summary>
    /// Result of a public-IP / geolocation lookup. Used by the live connection-info
    /// block so the user can verify on the fly whether their traffic actually exits
    /// through the VPN (the externally visible IP / country changes) or not.
    /// </summary>
    public sealed class ConnectionInfo
    {
        public bool Ok { get; init; }
        public string Ip { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string Region { get; init; } = string.Empty;
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
                if (!string.IsNullOrWhiteSpace(City))
                    return City;
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Fetches the externally visible IP address and geolocation. By default the request
    /// follows the system route (and the system proxy on Windows), so when the tunnel is
    /// active the reported IP reflects the VPN exit, and when routing is broken it reveals
    /// the real ISP address - exactly the signal needed to tell "working or not" live.
    /// </summary>
    public sealed class ConnectionInfoService
    {
        private const string PrimaryEndpoint = "https://ipinfo.io/json";
        private const string FallbackEndpoint = "https://ifconfig.co/json";
        private const string UserAgent = "InvisibleGorilla-XRay";

        public async Task<ConnectionInfo> LookupAsync(IWebProxy proxy, CancellationToken token = default)
        {
            ConnectionInfo result = await TryLookupAsync(PrimaryEndpoint, proxy, token).ConfigureAwait(false);
            if (result.Ok)
                return result;

            if (token.IsCancellationRequested)
                return result;

            ConnectionInfo fallback = await TryLookupAsync(FallbackEndpoint, proxy, token).ConfigureAwait(false);
            return fallback.Ok ? fallback : result;
        }

        private static async Task<ConnectionInfo> TryLookupAsync(string url, IWebProxy proxy, CancellationToken token)
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                UseProxy = proxy != null,
                Proxy = proxy,
                AllowAutoRedirect = true,
                ConnectTimeout = TimeSpan.FromSeconds(8)
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
                {
                    DiagnosticLog.Write("ConnectionInfo", $"GET {url} returned {(int)response.StatusCode}");
                    return new ConnectionInfo { Ok = false, Error = $"HTTP {(int)response.StatusCode}" };
                }

                string payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                JObject root = JObject.Parse(payload);

                string ip = FirstNonEmpty(root, "ip", "query");
                if (string.IsNullOrWhiteSpace(ip))
                    return new ConnectionInfo { Ok = false, Error = "no ip" };

                return new ConnectionInfo
                {
                    Ok = true,
                    Ip = ip,
                    City = FirstNonEmpty(root, "city"),
                    Region = FirstNonEmpty(root, "region", "regionName"),
                    Country = FirstNonEmpty(root, "country", "country_iso"),
                    Org = FirstNonEmpty(root, "org", "asn_org", "isp")
                };
            }
            catch (OperationCanceledException)
            {
                return new ConnectionInfo { Ok = false, Error = "canceled" };
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("ConnectionInfo", $"Lookup {url} failed: {ex.Message}");
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
