using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    using Models;
    using Values;

    /// <summary>
    /// Generates a torrc file for the bundled tor daemon. Used both for the main session
    /// and for throwaway bridge-checking instances (with isolated ports / data dirs).
    /// </summary>
    public static class TorrcBuilder
    {
        public static string Build(
            int socksPort,
            int controlPort,
            string dataDirectory,
            string cookieFile,
            string logFile,
            BridgeType bridgeType,
            IEnumerable<string> bridgeLines)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"SocksPort 127.0.0.1:{socksPort}");
            sb.AppendLine($"ControlPort 127.0.0.1:{controlPort}");
            sb.AppendLine("CookieAuthentication 1");
            sb.AppendLine($"CookieAuthFile {Quote(cookieFile)}");
            sb.AppendLine($"DataDirectory {Quote(dataDirectory)}");
            sb.AppendLine("ClientOnly 1");
            sb.AppendLine("AvoidDiskWrites 1");
            sb.AppendLine("SocksTimeout 60");

            if (!string.IsNullOrWhiteSpace(logFile))
            {
                // Tor parses "Log ... file" itself and keeps quote characters in the
                // filename, so a quoted path fails with "No such file or directory".
                string logPath = logFile.Replace("\\", "/");
                if (logPath.IndexOf(' ') >= 0 || logPath.IndexOf('"') >= 0)
                    sb.AppendLine("Log notice stdout");
                else
                    sb.AppendLine($"Log notice file {logPath}");
            }

            if (File.Exists(Path.TOR_GEOIP))
                sb.AppendLine($"GeoIPFile {Quote(Path.TOR_GEOIP)}");
            if (File.Exists(Path.TOR_GEOIP6))
                sb.AppendLine($"GeoIPv6File {Quote(Path.TOR_GEOIP6)}");

            AppendBridges(sb, bridgeType, bridgeLines);

            return sb.ToString();
        }

        /// <summary>
        /// Resolves the transport plugin and bridge statements for a live SETCONF.
        /// A non-direct type with no lines fails closed: callers must not omit UseBridges.
        /// </summary>
        public static bool TryGetRuntimeBridge(
            BridgeType bridgeType,
            IEnumerable<string> bridgeLines,
            out string clientTransportPlugin,
            out List<string> bridgeStatements,
            out string error)
        {
            clientTransportPlugin = null;
            bridgeStatements = new List<string>();
            error = null;

            if (bridgeType == BridgeType.NONE)
                return true;

            List<string> lines = ResolveLines(bridgeType, bridgeLines);
            if (lines.Count == 0)
            {
                error = $"No bridge lines for {bridgeType}. Refusing a direct connection.";
                return false;
            }

            clientTransportPlugin = PluginBody(bridgeType);
            if (string.IsNullOrEmpty(clientTransportPlugin))
            {
                error = $"No pluggable transport for {bridgeType}.";
                return false;
            }

            foreach (string line in lines)
                bridgeStatements.Add(NormalizeBridgeLine(bridgeType, line));
            return true;
        }

        public static string QuoteConfValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void AppendBridges(StringBuilder sb, BridgeType bridgeType, IEnumerable<string> bridgeLines)
        {
            if (!TryGetRuntimeBridge(bridgeType, bridgeLines, out string plugin, out List<string> bridges, out string error))
                throw new InvalidOperationException(error);

            if (bridgeType == BridgeType.NONE)
                return;

            sb.AppendLine("UseBridges 1");
            sb.AppendLine($"ClientTransportPlugin {plugin}");
            foreach (string line in bridges)
                sb.AppendLine($"Bridge {line}");
        }

        private static List<string> ResolveLines(BridgeType bridgeType, IEnumerable<string> bridgeLines)
        {
            List<string> lines = new List<string>();
            if (bridgeLines != null)
            {
                foreach (string line in bridgeLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(line.Trim());
                }
            }

            if (lines.Count == 0)
                lines = DefaultBridges.ForType(bridgeType) ?? new List<string>();
            return lines;
        }

        private static string PluginBody(BridgeType bridgeType)
        {
            string pt = ExecPath(Path.PLUGGABLE_TRANSPORT_EXE);
            // Tor Expert Bundle 14.5+ has no snowflake-client. Lyrebird speaks the
            // snowflake transport when the plugin name is "snowflake", not "obfs4".
            string snowflake = File.Exists(Path.SNOWFLAKE_EXE) ? ExecPath(Path.SNOWFLAKE_EXE) : pt;

            switch (bridgeType)
            {
                case BridgeType.OBFS4:
                    return $"obfs4 exec {pt}";
                case BridgeType.MEEK_AZURE:
                    return $"meek_lite exec {pt}";
                case BridgeType.WEBTUNNEL:
                    return $"webtunnel exec {pt}";
                case BridgeType.SNOWFLAKE:
                    return $"snowflake exec {snowflake}";
                default:
                    return null;
            }
        }

        private static string NormalizeBridgeLine(BridgeType bridgeType, string line)
        {
            string transport = bridgeType switch
            {
                BridgeType.OBFS4 => "obfs4",
                BridgeType.MEEK_AZURE => "meek_lite",
                BridgeType.WEBTUNNEL => "webtunnel",
                BridgeType.SNOWFLAKE => "snowflake",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(transport))
                return line;

            // If the line already begins with the transport keyword, keep it as-is.
            if (line.StartsWith(transport + " ") || line.StartsWith("Bridge "))
                return line.StartsWith("Bridge ") ? line.Substring("Bridge ".Length) : line;

            return $"{transport} {line}";
        }

        private static string ExecPath(string path)
        {
            string normalized = path?.Replace("\\", "/") ?? string.Empty;
            // Same parser quirk as Log: a quoted exec path is launched with the quote
            // characters included, so lyrebird exits immediately.
            if (normalized.IndexOf(' ') >= 0)
                return Quote(normalized);
            return normalized;
        }

        private static string Quote(string path) => $"\"{path?.Replace("\\", "/")}\"";
    }
}
