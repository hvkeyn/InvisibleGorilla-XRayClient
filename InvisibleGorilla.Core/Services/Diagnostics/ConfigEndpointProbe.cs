using System;
using System.Diagnostics;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Services.Diagnostics
{
    /// <summary>
    /// Measures the real network round-trip to the server of an Xray config.
    /// The native TestConnection reports the time of the whole TLS/VLESS handshake, which users
    /// read as "ping" (it showed 737 ms for a 99 ms server). This probe reports the TCP connect
    /// time instead; availability still comes from the native handshake test.
    /// </summary>
    public static class ConfigEndpointProbe
    {
        public static int MeasureTcpRtt(string configJson, int timeoutMs = 3000)
        {
            if (!TryReadEndpoint(configJson, out string host, out int port))
            {
                InvisibleGorillaXRay.Core.DiagnosticLog.Write("Latency", "endpoint not found in config");
                return -1;
            }

            int best = -1;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int ms = MeasureOnce(host, port, timeoutMs);
                if (ms > 0 && (best < 0 || ms < best))
                    best = ms;

                if (ms > 0 && attempt >= 1)
                    break;
            }

            InvisibleGorillaXRay.Core.DiagnosticLog.Write("Latency", $"tcp rtt {host}:{port} -> {best} ms");
            return best;
        }

        private static int MeasureOnce(string host, int port, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using TcpClient client = new TcpClient();
                IAsyncResult async = client.BeginConnect(host, port, null, null);
                if (!async.AsyncWaitHandle.WaitOne(timeoutMs))
                    return -1;

                client.EndConnect(async);
                watch.Stop();
                return (int)Math.Max(1, watch.ElapsedMilliseconds);
            }
            catch
            {
                return -1;
            }
        }

        private static bool TryReadEndpoint(string configJson, out string host, out int port)
        {
            host = null;
            port = 0;

            if (string.IsNullOrWhiteSpace(configJson))
                return false;

            try
            {
                JObject root = JObject.Parse(configJson);
                if (!(root["outbounds"] is JArray outbounds))
                    return false;

                foreach (JToken token in outbounds)
                {
                    if (!(token is JObject outbound))
                        continue;

                    string protocol = (string)outbound["protocol"] ?? string.Empty;
                    if (protocol == "freedom" || protocol == "blackhole" || protocol == "dns" || protocol == "loopback")
                        continue;

                    if (!(outbound["settings"] is JObject settings))
                        continue;

                    if (settings["vnext"] is JArray vnext && vnext.Count > 0 && vnext[0] is JObject first)
                    {
                        host = (string)first["address"];
                        port = (int?)first["port"] ?? 0;
                    }
                    else if (settings["servers"] is JArray servers && servers.Count > 0 && servers[0] is JObject server)
                    {
                        host = (string)server["address"];
                        port = (int?)server["port"] ?? 0;
                    }

                    if (!string.IsNullOrWhiteSpace(host) && port > 0 && port <= 65535)
                        return true;

                    host = null;
                    port = 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }
}
