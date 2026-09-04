using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Values;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaNodeDisplay
    {
        private static readonly Regex CountryCodeRegex = new(
            @"(?<![A-Za-z0-9])([A-Z]{2})(?![A-Za-z0-9])",
            RegexOptions.Compiled);

        private static readonly Regex BracketCountryRegex = new(
            @"[\[\(](?<code>[A-Za-z]{2})[\]\)]",
            RegexOptions.Compiled);

        private static readonly Regex FlagEmojiRegex = new(
            @"[\uD83C][\uDDE6-\uDDFF][\uD83C][\uDDE6-\uDDFF]",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, string> KeywordCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cloudflare"] = "CF",
            ["cf"] = "CF",
            ["google"] = "US",
            ["amazon"] = "US",
            ["aws"] = "US",
            ["microsoft"] = "US",
            ["azure"] = "US",
            ["germany"] = "DE",
            ["deutschland"] = "DE",
            ["france"] = "FR",
            ["netherlands"] = "NL",
            ["holland"] = "NL",
            ["finland"] = "FI",
            ["sweden"] = "SE",
            ["norway"] = "NO",
            ["poland"] = "PL",
            ["turkey"] = "TR",
            ["iran"] = "IR",
            ["russia"] = "RU",
            ["россия"] = "RU",
            ["kazakhstan"] = "KZ",
            ["japan"] = "JP",
            ["korea"] = "KR",
            ["singapore"] = "SG",
            ["hongkong"] = "HK",
            ["hong kong"] = "HK",
            ["taiwan"] = "TW",
            ["canada"] = "CA",
            ["uk"] = "GB",
            ["united kingdom"] = "GB",
            ["usa"] = "US",
            ["united states"] = "US"
        };

        public static string ExtractCountry(string displayName, string endpoint)
        {
            string source = $"{displayName} {endpoint}".Trim();
            if (string.IsNullOrWhiteSpace(source))
                return "-";

            Match flagMatch = FlagEmojiRegex.Match(source);
            if (flagMatch.Success)
                return flagMatch.Value;

            Match bracketMatch = BracketCountryRegex.Match(source);
            if (bracketMatch.Success)
                return bracketMatch.Groups["code"].Value.ToUpperInvariant();

            Match match = CountryCodeRegex.Match(source);
            if (match.Success)
                return match.Groups[1].Value;

            foreach (KeyValuePair<string, string> pair in KeywordCountries)
            {
                if (source.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return pair.Value;
            }

            return "-";
        }

        public static string FormatCountryDisplay(string country)
        {
            if (string.IsNullOrWhiteSpace(country) || country == "-")
                return "🌐 —";

            if (country.Length == 2 && char.IsLetter(country[0]) && char.IsLetter(country[1]))
                return $"{IsoToFlag(country)} {country.ToUpperInvariant()}";

            if (FlagEmojiRegex.IsMatch(country))
                return country;

            return country;
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
            return latencyMs switch
            {
                Values.Availability.NOT_CHECKED => "-",
                Values.Availability.TIMEOUT => "timeout",
                Values.Availability.ERROR => "error",
                _ when latencyMs >= 0 => $"{latencyMs} ms",
                _ => "-"
            };
        }

        public static string FormatStatusShort(GoidaNodeStatus status)
        {
            return status switch
            {
                GoidaNodeStatus.Ok => "OK",
                GoidaNodeStatus.Timeout => "timeout",
                GoidaNodeStatus.Error => "error",
                _ => "unknown"
            };
        }

        public static string BuildMainSummary(GoidaNode? node)
        {
            if (node == null)
                return string.Empty;

            string protocol = string.IsNullOrWhiteSpace(node.Protocol) ? "-" : node.Protocol;
            string country = FormatCountryDisplay(node.Country);
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

        private static string IsoToFlag(string iso2)
        {
            if (string.IsNullOrWhiteSpace(iso2) || iso2.Length != 2)
                return "🌐";

            char upperA = char.ToUpperInvariant(iso2[0]);
            char upperB = char.ToUpperInvariant(iso2[1]);
            if (upperA is < 'A' or > 'Z' || upperB is < 'A' or > 'Z')
                return "🌐";

            return char.ConvertFromUtf32(0x1F1E6 + (upperA - 'A'))
                + char.ConvertFromUtf32(0x1F1E6 + (upperB - 'A'));
        }
    }
}
