using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
            if (host.StartsWith('[') && host.EndsWith(']') && host.Length > 2)
                host = host[1..^1];
            if (!int.TryParse(match.Groups["port"].Value, out port) || port is < 1 or > 65535)
                return false;

            return !string.IsNullOrWhiteSpace(host);
        }
    }
}
