using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using InvisibleGorillaXRay.Services.Goida;
using InvisibleGorillaXRay.Values;

namespace InvisibleGorillaXRay.Services
{
    /// <summary>
    /// Reads the first proxy address from a saved Xray config so a block can be probed.
    /// </summary>
    public static class SubscriptionServerEndpoint
    {
        public static string Read(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                JObject root = JObject.Parse(json);
                JArray outbounds = root["outbounds"] as JArray;
                if (outbounds == null)
                    return null;

                foreach (JToken outbound in outbounds)
                {
                    string protocol = outbound.Value<string>("protocol");
                    if (protocol == "freedom" || protocol == "blackhole" || protocol == "dns")
                        continue;

                    JToken vnext = outbound["settings"]?["vnext"]?.First;
                    string fromVnext = Format(vnext?["address"]?.ToString(), vnext?["port"]?.ToString());
                    if (fromVnext != null)
                        return fromVnext;

                    JToken server = outbound["settings"]?["servers"]?.First;
                    string fromServer = Format(server?["address"]?.ToString(), server?["port"]?.ToString());
                    if (fromServer != null)
                        return fromServer;
                }
            }
            catch
            {
            }

            return null;
        }

        public static bool IsPlaceholderLink(string link)
        {
            if (string.IsNullOrWhiteSpace(link))
                return true;

            if (link.IndexOf("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            int at = link.LastIndexOf('@');
            if (at < 0 || at + 1 >= link.Length)
                return false;

            string rest = link.Substring(at + 1);
            int cut = rest.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0)
                rest = rest.Substring(0, cut);

            int colon = rest.LastIndexOf(':');
            if (colon <= 0)
                return false;

            string host = rest.Substring(0, colon).Trim().Trim('[', ']');
            if (!int.TryParse(rest.Substring(colon + 1).Trim(), out int port))
                return false;

            return port <= 1
                || host == "0.0.0.0"
                || host == "127.0.0.1"
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        public static string Describe(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                JObject root = JObject.Parse(json);
                if (root["outbounds"] is not JArray outbounds)
                    return null;

                foreach (JToken outbound in outbounds)
                {
                    string protocol = outbound.Value<string>("protocol");
                    if (string.IsNullOrWhiteSpace(protocol)
                        || protocol == "freedom"
                        || protocol == "blackhole"
                        || protocol == "dns")
                        continue;

                    string network = outbound["streamSettings"]?.Value<string>("network");
                    string security = outbound["streamSettings"]?.Value<string>("security");
                    List<string> parts = new();
                    parts.Add(protocol.ToUpperInvariant());
                    if (!string.IsNullOrWhiteSpace(network))
                        parts.Add(network.ToUpperInvariant());
                    if (!string.IsNullOrWhiteSpace(security)
                        && !security.Equals("none", StringComparison.OrdinalIgnoreCase))
                        parts.Add(FormatSecurity(security));
                    return string.Join(" | ", parts);
                }
            }
            catch
            {
            }

            return null;
        }

        public static int ProbeFile(string path, int timeoutMs = 1200)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return Availability.ERROR;

                string json = File.ReadAllText(path);
                if (IsPlaceholderJson(json))
                    return Availability.TIMEOUT;

                string endpoint = Read(json);
                if (string.IsNullOrWhiteSpace(endpoint))
                    return Availability.ERROR;

                return GoidaEndpointProbe.ProbeTcp(endpoint, timeoutMs);
            }
            catch
            {
                return Availability.ERROR;
            }
        }

        public static int[] ProbeFiles(IReadOnlyList<string> paths, int timeoutMs = 1200)
        {
            int[] latencies = new int[paths?.Count ?? 0];
            if (paths == null || paths.Count == 0)
                return latencies;

            Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = 6 }, index =>
            {
                latencies[index] = ProbeFile(paths[index], timeoutMs);
            });
            return latencies;
        }

        public static bool IsPlaceholderJson(string json)
        {
            string endpoint = Read(json);
            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0)
                return false;

            string host = endpoint.Substring(0, colon);
            string port = endpoint.Substring(colon + 1);
            return port == "0" || port == "1" || host == "0.0.0.0" || host == "127.0.0.1";
        }

        /// <summary>
        /// A panel that refuses this device sends advertisement links instead of VPN servers.
        /// The remark is the whole message, so the card title is the notice itself.
        /// </summary>
        public static bool IsProviderNotice(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string text = name.Trim();
            if (text.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - 5);
            text = text.ToLowerInvariant();

            return text.Contains("лимит устройств")
                || text.Contains("device limit")
                || text.Contains("приложение не поддерживается")
                || text.Contains("not supported")
                || text.Contains("свяжитесь с нами")
                || text.Contains("управление устройствами")
                || text.Contains("безлимит на");
        }

        public static bool IsDeviceLimitText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string value = text.ToLowerInvariant();
            return value.Contains("лимит устройств")
                || value.Contains("device limit");
        }

        private static string FormatSecurity(string security)
        {
            if (security.Equals("tls", StringComparison.OrdinalIgnoreCase)
                || security.Equals("xtls", StringComparison.OrdinalIgnoreCase))
                return security.ToUpperInvariant();

            if (security.Length == 1)
                return security.ToUpperInvariant();

            return char.ToUpperInvariant(security[0]) + security.Substring(1).ToLowerInvariant();
        }

        private static string Format(string address, string port)
        {
            if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(port))
                return null;
            if (!int.TryParse(port, out int parsed) || parsed < 1 || parsed > 65535)
                return null;
            return address.Trim() + ":" + parsed;
        }
    }
}
