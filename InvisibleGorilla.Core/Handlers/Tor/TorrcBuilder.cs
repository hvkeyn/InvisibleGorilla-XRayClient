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
                sb.AppendLine($"Log notice file {Quote(logFile)}");

            if (File.Exists(Path.TOR_GEOIP))
                sb.AppendLine($"GeoIPFile {Quote(Path.TOR_GEOIP)}");
            if (File.Exists(Path.TOR_GEOIP6))
                sb.AppendLine($"GeoIPv6File {Quote(Path.TOR_GEOIP6)}");

            AppendBridges(sb, bridgeType, bridgeLines);

            return sb.ToString();
        }

        private static void AppendBridges(StringBuilder sb, BridgeType bridgeType, IEnumerable<string> bridgeLines)
        {
            if (bridgeType == BridgeType.NONE)
                return;

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
                lines = DefaultBridges.ForType(bridgeType);

            if (lines.Count == 0)
                return;

            sb.AppendLine("UseBridges 1");

            string pt = Path.PLUGGABLE_TRANSPORT_EXE;
            // Newer Tor Expert Bundles (14.5+) no longer ship a standalone snowflake-client;
            // snowflake is served by lyrebird. Fall back to the bundled PT binary when the
            // dedicated snowflake-client binary is absent.
            string snowflake = File.Exists(Path.SNOWFLAKE_EXE) ? Path.SNOWFLAKE_EXE : Path.PLUGGABLE_TRANSPORT_EXE;

            switch (bridgeType)
            {
                case BridgeType.OBFS4:
                    sb.AppendLine($"ClientTransportPlugin obfs4 exec {Quote(pt)}");
                    break;
                case BridgeType.MEEK_AZURE:
                    sb.AppendLine($"ClientTransportPlugin meek_lite exec {Quote(pt)}");
                    break;
                case BridgeType.WEBTUNNEL:
                    sb.AppendLine($"ClientTransportPlugin webtunnel exec {Quote(pt)}");
                    break;
                case BridgeType.SNOWFLAKE:
                    sb.AppendLine($"ClientTransportPlugin snowflake exec {Quote(snowflake)}");
                    break;
            }

            foreach (string line in lines)
            {
                // Bridge lines may already start with the transport name; tor expects
                // "Bridge <transport> <addr> ...". Prefix the transport if missing.
                sb.AppendLine($"Bridge {NormalizeBridgeLine(bridgeType, line)}");
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

        private static string Quote(string path) => $"\"{path?.Replace("\\", "/")}\"";
    }
}
