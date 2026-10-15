using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    /// <summary>
    /// A CAPTCHA challenge returned by the moat (BridgeDB) service. The image must be solved
    /// by the user; the solution is submitted via <see cref="MoatClient.SubmitSolutionAsync"/>.
    /// </summary>
    public sealed class MoatChallenge
    {
        public byte[] ImagePng;
        public string Challenge;
        public string Transport;
    }

    public sealed class MoatResult
    {
        public bool Success;
        public string Error;
        public List<string> Bridges = new List<string>();
        public MoatChallenge Challenge;
        // Transport family recommended by the circumvention API ("obfs4", "snowflake", ...).
        public string Transport;
    }

    /// <summary>
    /// Client for the Tor "moat" / BridgeDB bridge-distribution API (the same mechanism Tor
    /// Browser uses for "Request bridges"). Fetches a CAPTCHA, then exchanges the solution for
    /// fresh bridge lines. Can be routed through a local SOCKS proxy (e.g. a bootstrapped Tor
    /// using Snowflake) to work even when the API is censored.
    /// </summary>
    public sealed class MoatClient
    {
        private const string MoatBaseUrl = "https://bridges.torproject.org/moat";
        private const string JsonApiContentType = "application/vnd.api+json";

        private readonly string socksProxy; // e.g. "socks5://127.0.0.1:9250" or null

        public MoatClient(string socksProxy = null)
        {
            this.socksProxy = socksProxy;
        }

        private HttpClient CreateClient()
        {
            HttpMessageHandler handler;
            if (!string.IsNullOrWhiteSpace(socksProxy))
            {
                var proxyHandler = new HttpClientHandler();
                try
                {
                    proxyHandler.Proxy = new WebProxy(socksProxy);
                    proxyHandler.UseProxy = true;
                }
                catch
                {
                    proxyHandler.UseProxy = false;
                }
                handler = proxyHandler;
            }
            else
            {
                // A live tunnel or system proxy captures bridges.torproject.org and
                // the call sits until HttpClient.Timeout. Direct is the right path.
                handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    Proxy = null,
                    ConnectTimeout = TimeSpan.FromSeconds(6)
                };
            }

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "InvisibleGorilla-XRay/Tor");
            return client;
        }

        private static string ShortError(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException)
                return "timed out";
            string message = ex?.Message ?? "";
            if (message.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0)
                return "timed out";
            return message;
        }

        /// <summary>
        /// Requests a CAPTCHA challenge for the given transport ("obfs4" by default).
        /// </summary>
        public async Task<MoatResult> RequestChallengeAsync(string transport = "obfs4")
        {
            var result = new MoatResult();
            try
            {
                var body = new JObject
                {
                    ["data"] = new JArray
                    {
                        new JObject
                        {
                            ["version"] = "0.1.0",
                            ["type"] = "client-transports",
                            ["supported"] = new JArray { transport }
                        }
                    }
                };

                using var client = CreateClient();
                using var content = new StringContent(body.ToString(), Encoding.UTF8);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(JsonApiContentType);

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{MoatBaseUrl}/fetch") { Content = content };
                request.Headers.TryAddWithoutValidation("Accept", JsonApiContentType);

                var response = await client.SendAsync(request).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                JObject root = JObject.Parse(json);
                JToken data = root["data"]?[0];
                if (data == null)
                {
                    result.Error = "Moat returned no challenge.";
                    return result;
                }

                string image = (string)data["image"];
                string challenge = (string)data["challenge"];
                string returnedTransport = (string)data["transport"] ?? transport;

                if (string.IsNullOrEmpty(image) || string.IsNullOrEmpty(challenge))
                {
                    result.Error = "Moat challenge was incomplete.";
                    return result;
                }

                result.Success = true;
                result.Challenge = new MoatChallenge
                {
                    ImagePng = Convert.FromBase64String(image),
                    Challenge = challenge,
                    Transport = returnedTransport
                };
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ShortError(ex);
                return result;
            }
        }

        /// <summary>
        /// Submits the user's CAPTCHA solution and returns fresh bridge lines on success.
        /// </summary>
        public async Task<MoatResult> SubmitSolutionAsync(MoatChallenge challenge, string solution)
        {
            var result = new MoatResult();
            if (challenge == null)
            {
                result.Error = "No active challenge.";
                return result;
            }

            try
            {
                var body = new JObject
                {
                    ["data"] = new JArray
                    {
                        new JObject
                        {
                            ["id"] = "2",
                            ["version"] = "0.1.0",
                            ["type"] = "moat-solution",
                            ["transport"] = challenge.Transport,
                            ["challenge"] = challenge.Challenge,
                            ["solution"] = solution,
                            ["qrcode"] = "false"
                        }
                    }
                };

                using var client = CreateClient();
                using var content = new StringContent(body.ToString(), Encoding.UTF8);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(JsonApiContentType);

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{MoatBaseUrl}/check") { Content = content };
                request.Headers.TryAddWithoutValidation("Accept", JsonApiContentType);

                var response = await client.SendAsync(request).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                JObject root = JObject.Parse(json);

                JToken errors = root["errors"]?[0];
                if (errors != null)
                {
                    result.Error = (string)errors["detail"] ?? "Incorrect CAPTCHA solution.";
                    return result;
                }

                JToken bridges = root["data"]?[0]?["bridges"];
                if (bridges is JArray bridgeArray && bridgeArray.Count > 0)
                {
                    foreach (JToken bridge in bridgeArray)
                    {
                        string line = (string)bridge;
                        if (!string.IsNullOrWhiteSpace(line))
                            result.Bridges.Add(line.Trim());
                    }
                    result.Success = result.Bridges.Count > 0;
                    if (!result.Success)
                        result.Error = "No bridges returned.";
                    return result;
                }

                result.Error = "Moat returned no bridges (wrong solution?).";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ShortError(ex);
                return result;
            }
        }

        /// <summary>
        /// "Ask Tor" / Smart Connect: asks the circumvention API for the bridges recommended
        /// for the user's location and returns them directly — no CAPTCHA required. When the
        /// server has no country-specific recommendation it falls back to built-in bridges.
        /// </summary>
        /// <param name="country">ISO country code (e.g. "ru"); null lets the server geolocate.</param>
        public async Task<MoatResult> GetCircumventionSettingsAsync(string country = null, string preferredTransport = null)
        {
            var result = new MoatResult();
            try
            {
                var body = new JObject();
                string resolved = ResolveCountry(country);
                if (!string.IsNullOrEmpty(resolved))
                    body["country"] = resolved;
                if (!string.IsNullOrWhiteSpace(preferredTransport))
                    body["transports"] = new JArray { preferredTransport };

                JObject root = await SendCircumventionAsync(HttpMethod.Post, $"{MoatBaseUrl}/circumvention/settings", body).ConfigureAwait(false);
                JToken errors = root?["errors"]?[0];
                if (errors != null)
                {
                    result.Error = (string)errors["detail"] ?? "Circumvention API error.";
                }
                else if (root?["settings"] is JArray settings
                    && TryTakeBridges(settings, preferredTransport, result))
                {
                    BridgeCatalog.StoreLines(result.Transport, result.Bridges);
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Error = ShortError(ex);
            }

            MoatResult builtin = await GetBuiltinBridgesAsync(preferredTransport ?? "obfs4").ConfigureAwait(false);
            if (builtin.Success)
                return builtin;
            if (string.IsNullOrEmpty(builtin.Error))
                builtin.Error = result.Error;
            return builtin;
        }

        private static bool TryTakeBridges(JArray settings, string preferredTransport, MoatResult result)
        {
            foreach (JToken setting in settings)
            {
                JToken bridges = setting["bridges"];
                if (bridges == null)
                    continue;

                string type = (string)bridges["type"];
                if (!TransportMatches(preferredTransport, type))
                    continue;

                if (bridges["bridge_strings"] is JArray strings)
                {
                    foreach (JToken s in strings)
                    {
                        string line = (string)s;
                        if (!string.IsNullOrWhiteSpace(line))
                            result.Bridges.Add(line.Trim());
                    }
                }

                if (result.Bridges.Count > 0)
                {
                    result.Transport = type;
                    result.Success = true;
                    return true;
                }
            }

            return false;
        }

        private static bool TransportMatches(string wanted, string actual)
        {
            if (string.IsNullOrWhiteSpace(wanted))
                return true;

            string left = NormalizeTransport(wanted);
            string right = NormalizeTransport(actual);
            return left.Length > 0 && (left == right || right.StartsWith(left, StringComparison.Ordinal));
        }

        private static string NormalizeTransport(string transport)
        {
            switch ((transport ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "meek-azure":
                case "meek_lite":
                case "meek":
                    return "meek";
                default:
                    return (transport ?? string.Empty).Trim().ToLowerInvariant();
            }
        }

        /// <summary>
        /// Fetches the current built-in bridge lines for a given transport from the
        /// circumvention API (no CAPTCHA). Falls back to bundled defaults on failure.
        /// </summary>
        public async Task<MoatResult> GetBuiltinBridgesAsync(string transport = "obfs4")
        {
            var result = new MoatResult { Transport = transport };
            try
            {
                JObject root = await SendCircumventionAsync(HttpMethod.Get, $"{MoatBaseUrl}/circumvention/builtin", null).ConfigureAwait(false);
                BridgeCatalog.Store(root);
                string key = NormalizeTransport(transport);
                JArray strings = root?[transport] as JArray ?? root?[key] as JArray;
                if (strings == null && key == "meek")
                    strings = root?["meek-azure"] as JArray ?? root?["meek"] as JArray;
                if (strings != null)
                {
                    foreach (JToken s in strings)
                    {
                        string line = (string)s;
                        if (!string.IsNullOrWhiteSpace(line))
                            result.Bridges.Add(line.Trim());
                    }
                }

                result.Success = result.Bridges.Count > 0;
                if (!result.Success)
                    result.Error = "Built-in bridges were unavailable.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ShortError(ex);
                return result;
            }
        }

        private static string ResolveCountry(string country)
        {
            string value = country;
            if (string.IsNullOrWhiteSpace(value))
            {
                try { value = RegionInfo.CurrentRegion.TwoLetterISORegionName; }
                catch { value = null; }
            }

            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim().ToLowerInvariant();
            if (value.Length != 2 || !char.IsLetter(value[0]) || !char.IsLetter(value[1]))
                return null;
            return value;
        }

        private async Task<JObject> SendCircumventionAsync(HttpMethod method, string url, JObject body)
        {
            try
            {
                using (HttpClient direct = CreateClient())
                {
                    direct.Timeout = TimeSpan.FromSeconds(8);
                    return await SendWithClient(direct, method, url, body).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                InvisibleGorillaXRay.Core.DiagnosticLog.Write("Moat", "Direct request failed: " + ex.GetType().Name);
            }

            using (MeekFrontSession meek = MeekFrontSession.TryStart())
            {
                if (meek == null)
                    throw new InvalidOperationException("Circumvention service is unreachable.");

                Exception last = null;
                foreach (string front in MeekFrontSession.Fronts)
                {
                    try
                    {
                        using var client = new HttpClient(CreateMeekHandler(meek, front), true)
                        {
                            Timeout = TimeSpan.FromSeconds(25)
                        };
                        return await SendWithClient(client, method, url, body).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        InvisibleGorillaXRay.Core.DiagnosticLog.Write("Moat", "Meek request failed: " + ex.GetType().Name);
                    }
                }

                throw last ?? new InvalidOperationException("Meek tunnel failed.");
            }
        }

        private static HttpMessageHandler CreateMeekHandler(MeekFrontSession meek, string front)
        {
            return new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                ConnectCallback = (context, _) =>
                {
                    string host = context.DnsEndPoint.Host;
                    int port = context.DnsEndPoint.Port;
                    Stream stream = meek.Open(front, host, port);
                    return new ValueTask<Stream>(stream);
                }
            };
        }

        private static async Task<JObject> SendWithClient(HttpClient client, HttpMethod method, string url, JObject body)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Accept", JsonApiContentType);
            if (body != null && method != HttpMethod.Get)
            {
                var content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                request.Content = content;
            }

            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) || json.TrimStart().StartsWith("<"))
                throw new InvalidOperationException("Circumvention service returned no JSON.");
            if ((int)response.StatusCode >= 500)
                throw new HttpRequestException("Circumvention service status " + (int)response.StatusCode);

            return JObject.Parse(json);
        }

        private async Task<JObject> PostJsonAsync(string url, JObject inner)
        {
            var body = new JObject { ["data"] = new JArray { inner } };

            using var client = CreateClient();
            using var content = new StringContent(body.ToString(), Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(JsonApiContentType);

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Accept", JsonApiContentType);

            var response = await client.SendAsync(request).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            JObject root = JObject.Parse(json);
            // Some deployments wrap the payload in a JSON:API "data" array.
            if (root["data"]?[0] is JObject wrapped && root["settings"] == null && root["errors"] == null)
                return wrapped;
            return root;
        }
    }
}
