using System;
using System.Collections.Generic;
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
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(socksProxy))
            {
                try
                {
                    handler.Proxy = new WebProxy(socksProxy);
                    handler.UseProxy = true;
                }
                catch
                {
                    handler.UseProxy = false;
                }
            }

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "InvisibleGorilla-XRay/Tor");
            return client;
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
                result.Error = ex.Message;
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
                result.Error = ex.Message;
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
                var inner = new JObject();
                if (!string.IsNullOrWhiteSpace(country))
                    inner["country"] = country.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(preferredTransport))
                    inner["transports"] = new JArray { preferredTransport };

                JObject root = await PostJsonAsync($"{MoatBaseUrl}/circumvention/settings", inner).ConfigureAwait(false);
                if (root == null)
                {
                    result.Error = "No response from the circumvention service.";
                    return result;
                }

                JToken errors = root["errors"]?[0];
                if (errors != null)
                {
                    result.Error = (string)errors["detail"] ?? "Circumvention API error.";
                    return result;
                }

                if (root["settings"] is JArray settings && settings.Count > 0)
                {
                    foreach (JToken setting in settings)
                    {
                        JToken bridges = setting["bridges"];
                        if (bridges == null)
                            continue;

                        string type = (string)bridges["type"];
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
                            return result;
                        }
                    }
                }

                // No location-specific recommendation: fall back to built-in bridges.
                return await GetBuiltinBridgesAsync(preferredTransport ?? "obfs4").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
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
                JObject root = await PostJsonAsync($"{MoatBaseUrl}/circumvention/builtin", new JObject()).ConfigureAwait(false);
                if (root != null && root[transport] is JArray strings && strings.Count > 0)
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
                result.Error = ex.Message;
                return result;
            }
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
