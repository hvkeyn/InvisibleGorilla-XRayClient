using System;
using System.Text.RegularExpressions;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaNodeDisplay
    {
        private static readonly Regex CountryCodeRegex = new(
            @"(?<![A-Za-z0-9])([A-Z]{2})(?![A-Za-z0-9])",
            RegexOptions.Compiled);

        public static string ExtractCountry(string displayName, string endpoint)
        {
            string source = $"{displayName} {endpoint}".Trim();
            if (string.IsNullOrWhiteSpace(source))
                return "-";

            Match match = CountryCodeRegex.Match(source);
            if (match.Success)
                return match.Groups[1].Value;

            return "-";
        }

        public static string ExtractProtocol(string configJson)
        {
            if (string.IsNullOrWhiteSpace(configJson))
                return "-";

            string lower = configJson.ToLowerInvariant();
            bool xhttp = lower.Contains("\"xhttp\"") || lower.Contains("type=xhttp");
            if (lower.Contains("\"vless\"") || lower.Contains("vless://"))
                return xhttp ? "VLESS/XHTTP" : "VLESS";
            if (lower.Contains("\"vmess\"") || lower.Contains("vmess://"))
                return "VMess";
            if (lower.Contains("\"trojan\"") || lower.Contains("trojan://"))
                return "Trojan";
            if (lower.Contains("\"shadowsocks\"") || lower.Contains("ss://"))
                return "SS";

            return "-";
        }

        public static string FormatLastChecked(DateTime lastCheckedUtc)
        {
            if (lastCheckedUtc == default)
                return "-";

            return lastCheckedUtc.ToLocalTime().ToString("dd.MM HH:mm");
        }

        public static string FormatLatencyShort(int latencyMs)
        {
            return latencyMs >= 0 ? $"{latencyMs} ms" : "—";
        }

        public static string BuildMainSummary(GoidaNode? node)
        {
            if (node == null)
                return string.Empty;

            string protocol = string.IsNullOrWhiteSpace(node.Protocol) ? "-" : node.Protocol;
            string country = string.IsNullOrWhiteSpace(node.Country) ? "-" : node.Country;
            string endpoint = string.IsNullOrWhiteSpace(node.Endpoint) ? "-" : node.Endpoint;

            return $"{country} · {protocol} · {endpoint} · List {node.ListId}";
        }

        public static GoidaMainPresentation BuildMainPresentation(GoidaNode? node)
        {
            if (node == null)
                return new GoidaMainPresentation();

            (int level, string label, string color) = DescribeSignal(
                node.LatencyMs, node.Status, node.VlessVerified);
            return new GoidaMainPresentation
            {
                Summary = BuildMainSummary(node),
                SignalLevel = level,
                QualityLabel = label,
                ColorHex = color,
                LatencyText = FormatLatencyShort(node.LatencyMs)
            };
        }

        public static (int Level, string Label, string ColorHex) DescribeSignal(
            int latencyMs, GoidaNodeStatus status, bool vlessVerified = false)
        {
            if (status is GoidaNodeStatus.Timeout or GoidaNodeStatus.Error)
                return (0, "Lang.Goida.Signal.Offline", "#E85D5D");

            if (!vlessVerified || status != GoidaNodeStatus.Ok)
                return (0, "Lang.Goida.Signal.Unverified", "#9AA0A6");

            if (latencyMs < 0)
                return (0, "Lang.Goida.Signal.Unknown", "#9AA0A6");

            if (latencyMs <= 150)
                return (4, "Lang.Goida.Signal.Excellent", "#5FD38D");

            if (latencyMs <= 350)
                return (3, "Lang.Goida.Signal.Good", "#9AD9B0");

            if (latencyMs <= 700)
                return (2, "Lang.Goida.Signal.Fair", "#E8C547");

            return (1, "Lang.Goida.Signal.Slow", "#E8845D");
        }
    }
}
