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
            IEnumerable<string> bridgeLines,
            string exitCountry = null)
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

            // tor is started with WorkingDirectory = Tor data folder, so every path in the
            // file has to be absolute: a relative "./Tor/geoip" resolved to "<app>\Tor\Tor\geoip".
            if (File.Exists(Path.TOR_GEOIP))
                sb.AppendLine($"GeoIPFile {Quote(System.IO.Path.GetFullPath(Path.TOR_GEOIP))}");
            if (File.Exists(Path.TOR_GEOIP6))
                sb.AppendLine($"GeoIPv6File {Quote(System.IO.Path.GetFullPath(Path.TOR_GEOIP6))}");

            string exit = NormalizeExitCountry(exitCountry);
            if (exit != null)
            {
                sb.AppendLine($"ExitNodes {{{exit}}}");
                sb.AppendLine("StrictNodes 1");
            }

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
            return "\"" + SanitizeConfValue(value).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// A control-port command is a single line. A bridge line pasted from a panel (or an
        /// answer from Moat) must not be able to inject further commands or torrc directives,
        /// so everything after the first line break is dropped and control characters are
        /// removed. The same rule protects torrc generation: a bridge line is one line too.
        /// </summary>
        public static string SanitizeConfValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            int breakIndex = value.IndexOfAny(new[] { '\r', '\n' });
            string head = breakIndex >= 0 ? value.Substring(0, breakIndex) : value;

            StringBuilder sb = new StringBuilder(head.Length);
            foreach (char c in head)
            {
                if (!char.IsControl(c))
                    sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        private static string NormalizeExitCountry(string country)
        {
            if (string.IsNullOrWhiteSpace(country))
                return null;

            string code = country.Trim().ToLowerInvariant();
            if (code.Length == 2 && code[0] >= 'a' && code[0] <= 'z' && code[1] >= 'a' && code[1] <= 'z')
                return code;

            return null;
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
                    string cleaned = SanitizeConfValue(line);
                    if (cleaned.Length > 0)
                        lines.Add(cleaned);
                }
            }

            if (lines.Count == 0)
                lines = DefaultBridges.ForType(bridgeType) ?? new List<string>();
            return lines;
        }

        /// <summary>
        /// Lyrebird speaks every bundled transport. Registering only the UI type made a
        /// webtunnel or obfs4 line hang when the selected method was still snowflake.
        /// </summary>
        private static string PluginBody(BridgeType bridgeType)
        {
            if (bridgeType == BridgeType.NONE)
                return null;

            string pt = ExecPath(Path.PLUGGABLE_TRANSPORT_EXE);
            string snowflake = File.Exists(Path.SNOWFLAKE_EXE) ? ExecPath(Path.SNOWFLAKE_EXE) : pt;
            if (!string.Equals(snowflake, pt, StringComparison.OrdinalIgnoreCase))
            {
                if (bridgeType == BridgeType.SNOWFLAKE)
                    return $"snowflake exec {snowflake}";
                return $"obfs4,meek_lite,webtunnel exec {pt}";
            }

            return $"obfs4,meek_lite,webtunnel,snowflake exec {pt}";
        }

        public static BridgeType InferBridgeType(IEnumerable<string> bridgeLines)
        {
            if (bridgeLines == null)
                return BridgeType.NONE;

            foreach (string line in bridgeLines)
            {
                string trimmed = SanitizeConfValue(line);
                if (trimmed.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring("Bridge ".Length).TrimStart();

                if (trimmed.StartsWith("snowflake ", StringComparison.OrdinalIgnoreCase))
                    return BridgeType.SNOWFLAKE;
                if (trimmed.StartsWith("meek", StringComparison.OrdinalIgnoreCase))
                    return BridgeType.MEEK_AZURE;
                if (trimmed.StartsWith("webtunnel ", StringComparison.OrdinalIgnoreCase))
                    return BridgeType.WEBTUNNEL;
                if (trimmed.StartsWith("obfs4 ", StringComparison.OrdinalIgnoreCase))
                    return BridgeType.OBFS4;
            }

            return BridgeType.NONE;
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

            if (line.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
                line = line.Substring("Bridge ".Length).TrimStart();

            if (HasTransportPrefix(line))
                return line;

            if (string.IsNullOrEmpty(transport))
                return line;

            return $"{transport} {line}";
        }

        private static bool HasTransportPrefix(string line)
        {
            return line.StartsWith("obfs4 ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("webtunnel ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("snowflake ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("meek_lite ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("meek ", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExecPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string full = System.IO.Path.GetFullPath(path).Replace("\\", "/");
            if (full.IndexOf(' ') < 0)
                return full;

            // tor does not strip quotes inside ClientTransportPlugin: a quoted path reaches
            // CreateProcess with the quote characters and the PT aborts (0xC0000409).
            // A space-free 8.3 short path is the only form tor accepts when the application
            // folder contains spaces.
            string shortPath = TryGetShortPath(full);
            return string.IsNullOrEmpty(shortPath) ? full : shortPath.Replace("\\", "/");
        }

        private static string TryGetShortPath(string path)
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
            {
                return null;
            }

            try
            {
                var buffer = new StringBuilder(1024);
                int length = GetShortPathName(path, buffer, buffer.Capacity);
                return length > 0 ? buffer.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

        private static string Quote(string path) => $"\"{path?.Replace("\\", "/")}\"";
    }
}
