using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using InvisibleGorillaXRay.Values;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaEndpointProbe
    {
        private static readonly Regex HostPortRegex = new(
            @"^(?<host>\[[^\]]+\]|[^:]+):(?<port>\d{1,5})$",
            RegexOptions.Compiled);

        public static int ProbeTcp(string endpoint, int timeoutMs = 1500)
        {
            if (!TryParseEndpoint(endpoint, out string host, out int port))
                return Availability.ERROR;

            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using TcpClient client = new();
                IAsyncResult connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
                    return Availability.TIMEOUT;

                client.EndConnect(connect);
                watch.Stop();
                return (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue);
            }
            catch (SocketException)
            {
                return Availability.TIMEOUT;
            }
            catch
            {
                return Availability.ERROR;
            }
        }

        public static string Resolve(string endpoint, string configPath)
        {
            if (TryParseEndpoint(endpoint, out _, out _))
                return endpoint ?? string.Empty;

            string fromFile = FromConfigFile(configPath);
            return TryParseEndpoint(fromFile, out _, out _) ? fromFile : endpoint ?? string.Empty;
        }

        public static string FromConfigJson(string configJson)
        {
            if (string.IsNullOrWhiteSpace(configJson))
                return string.Empty;

            try
            {
                JObject root = JObject.Parse(configJson);
                if (root["outbounds"] is not JArray outbounds)
                    return string.Empty;

                foreach (JToken outboundToken in outbounds)
                {
                    if (outboundToken is not JObject outbound)
                        continue;

                    if (outbound["settings"] is not JObject settings)
                        continue;

                    if (TryFirstEndpoint(settings["vnext"] as JArray, out string endpoint))
                        return endpoint;

                    if (TryFirstEndpoint(settings["servers"] as JArray, out endpoint))
                        return endpoint;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public static string FromConfigFile(string configPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                    return string.Empty;

                return FromConfigJson(File.ReadAllText(configPath));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryFirstEndpoint(JArray entries, out string endpoint)
        {
            endpoint = string.Empty;
            if (entries == null || entries.Count == 0 || entries[0] is not JObject first)
                return false;

            string address = (string)first["address"];
            if (string.IsNullOrWhiteSpace(address))
                return false;

            string port = first["port"]?.ToString();
            if (address.Contains(':') && !address.StartsWith("["))
                address = $"[{address}]";

            endpoint = string.IsNullOrWhiteSpace(port) ? address : address + ":" + port;
            return true;
        }

        public static bool TryParseEndpoint(string endpoint, out string host, out int port)
        {
            host = string.Empty;
            port = 0;

            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            string trimmed = endpoint.Trim();
            Match match = HostPortRegex.Match(trimmed);
            if (!match.Success)
                return false;

            host = match.Groups["host"].Value.Trim();
            if (host.StartsWith("[") && host.EndsWith("]") && host.Length > 2)
                host = host.Substring(1, host.Length - 2);
            if (!int.TryParse(match.Groups["port"].Value, out port) || port is < 1 or > 65535)
                return false;

            return !string.IsNullOrWhiteSpace(host);
        }
    }
}
