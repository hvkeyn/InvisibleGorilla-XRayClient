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
        public string Region { get; init; } = string.Empty;
        public string CountryCode { get; init; } = string.Empty;
        public string CountryName { get; init; } = string.Empty;
        public string Org { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;

        public string FlagEmoji => CountryDisplay.GetFlagEmoji(CountryCode);

        public string FlagImageUrl =>
            string.IsNullOrWhiteSpace(CountryCode) || CountryCode.Length != 2
                ? string.Empty
                : $"https://flagcdn.com/w40/{CountryCode.ToLowerInvariant()}.png";

        public string PlaceLine => CountryDisplay.BuildPlaceLine(City, Region);

        public string Location
        {
            get
            {
                string place = PlaceLine;
                if (!string.IsNullOrWhiteSpace(place) && !string.IsNullOrWhiteSpace(CountryName))
                    return $"{place}, {CountryName}";
                if (!string.IsNullOrWhiteSpace(CountryName))
                    return CountryName;
                if (!string.IsNullOrWhiteSpace(place))
                    return place;
                return string.Empty;
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

        /// <summary>
        /// When the app is excluded from its own VPN (Android), callers must pass the local
        /// xray SOCKS proxy. Returns an error instead of falling back to a direct lookup.
        /// </summary>
        public Task<ConnectionInfo> LookupThroughTunnelAsync(IWebProxy proxy, CancellationToken token = default)
        {
            if (proxy == null)
            {
                return Task.FromResult(new ConnectionInfo
                {
                    Ok = false,
                    Error = "Tunnel proxy unavailable"
                });
            }

            return LookupAsync(proxy, token);
        }

        private static async Task<ConnectionInfo> TryLookupAsync(string url, IWebProxy proxy, CancellationToken token)
        {
            using SocketsHttpHandler handler = new SocketsHttpHandler
            {
                UseProxy = proxy != null,
                Proxy = proxy,
                PreAuthenticate = proxy != null,
                AllowAutoRedirect = true,
                ConnectTimeout = TimeSpan.FromSeconds(12)
            };

            using HttpClient client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
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

                string countryCode = FirstNonEmpty(root, "country_iso", "country");
                string countryName = FirstNonEmpty(root, "country_name");
                if (countryCode.Length > 2)
                {
                    countryName = string.IsNullOrWhiteSpace(countryName) ? countryCode : countryName;
                    countryCode = FirstNonEmpty(root, "country_iso");
                }

                countryName = CountryDisplay.GetCountryName(countryCode, countryName);

                return new ConnectionInfo
                {
                    Ok = true,
                    Ip = ip,
                    City = FirstNonEmpty(root, "city"),
                    Region = FirstNonEmpty(root, "region", "regionName"),
                    CountryCode = countryCode,
                    CountryName = countryName,
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
