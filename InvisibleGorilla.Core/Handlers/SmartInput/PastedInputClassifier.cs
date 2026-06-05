using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace InvisibleGorillaXRay.Handlers.SmartInput
{
    using Models;
    using Values;

    /// <summary>
    /// What a single pasted token represents.
    /// </summary>
    public enum PastedItemKind
    {
        ServerLink,        // vless:// vmess:// trojan:// ss:// ...
        SubscriptionLink,  // http(s):// pointing at a subscription
        Bridge,            // a Tor bridge line (obfs4/webtunnel/snowflake/meek/vanilla)
        Unknown
    }

    /// <summary>
    /// Outcome of classifying a chunk of pasted text. The same blob can mix
    /// server links, subscription URLs and Tor bridge lines; each is sorted here.
    /// </summary>
    public class PastedInputResult
    {
        public List<string> ServerLinks { get; } = new List<string>();
        public List<string> SubscriptionLinks { get; } = new List<string>();
        public List<string> BridgeLines { get; } = new List<string>();
        public List<string> Unknown { get; } = new List<string>();

        // Bridge family inferred from the pasted bridge lines (first wins).
        public BridgeType DetectedBridgeType { get; set; } = BridgeType.NONE;

        public int RecognizedCount => ServerLinks.Count + SubscriptionLinks.Count + BridgeLines.Count;
        public bool HasAny => RecognizedCount > 0;
        public bool HasBridges => BridgeLines.Count > 0;
        public bool HasServers => ServerLinks.Count > 0;
        public bool HasSubscriptions => SubscriptionLinks.Count > 0;
    }

    /// <summary>
    /// Detects, from arbitrary pasted text, whether each line is a server
    /// connection link, a subscription URL, or a Tor bridge line — so the UI can
    /// offer a single "paste anything" box that routes each piece automatically.
    /// </summary>
    public static class PastedInputClassifier
    {
        private static readonly string[] ConfigSchemes =
        {
            "vless://", "vmess://", "trojan://", "ss://", "ssr://", "socks://", "trojan-go://", "hysteria://", "hysteria2://", "hy2://", "tuic://"
        };

        // Pluggable-transport keywords that prefix a Tor bridge line.
        private static readonly Dictionary<string, BridgeType> BridgeTransports =
            new Dictionary<string, BridgeType>(StringComparer.OrdinalIgnoreCase)
            {
                { "obfs4", BridgeType.OBFS4 },
                { "obfs3", BridgeType.OBFS4 },
                { "scramblesuit", BridgeType.OBFS4 },
                { "webtunnel", BridgeType.WEBTUNNEL },
                { "snowflake", BridgeType.SNOWFLAKE },
                { "meek", BridgeType.MEEK_AZURE },
                { "meek_lite", BridgeType.MEEK_AZURE },
            };

        // A "vanilla" (no-transport) bridge: IP:PORT followed by a 40-hex fingerprint.
        private static readonly Regex VanillaBridge = new Regex(
            @"^(?:Bridge\s+)?\d{1,3}(?:\.\d{1,3}){3}:\d{1,5}\s+[0-9A-Fa-f]{40}\b",
            RegexOptions.Compiled);

        // IPv6 vanilla bridge: [addr]:port + fingerprint.
        private static readonly Regex VanillaBridgeV6 = new Regex(
            @"^(?:Bridge\s+)?\[[0-9A-Fa-f:]+\]:\d{1,5}\s+[0-9A-Fa-f]{40}\b",
            RegexOptions.Compiled);

        public static PastedInputResult Classify(string text)
        {
            var result = new PastedInputResult();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            foreach (string rawLine in normalized.Split('\n'))
            {
                string line = StripBridgePrefix(rawLine.Trim());
                if (line.Length == 0)
                    continue;

                // invxray:// deep links exported by the app itself (config / config-data / subscription).
                if (TryNormalizeDeepLink(line, out string deepPayload, out PastedItemKind deepKind))
                {
                    if (deepKind == PastedItemKind.SubscriptionLink)
                        result.SubscriptionLinks.Add(deepPayload);
                    else
                        result.ServerLinks.Add(deepPayload);
                    continue;
                }

                switch (ClassifyLine(line, out BridgeType bridgeType))
                {
                    case PastedItemKind.ServerLink:
                        result.ServerLinks.Add(line);
                        break;
                    case PastedItemKind.SubscriptionLink:
                        result.SubscriptionLinks.Add(line);
                        break;
                    case PastedItemKind.Bridge:
                        result.BridgeLines.Add(line);
                        if (result.DetectedBridgeType == BridgeType.NONE && bridgeType != BridgeType.NONE)
                            result.DetectedBridgeType = bridgeType;
                        break;
                    default:
                        result.Unknown.Add(line);
                        break;
                }
            }

            if (result.HasBridges && result.DetectedBridgeType == BridgeType.NONE)
                result.DetectedBridgeType = BridgeType.OBFS4;

            return result;
        }

        public static PastedItemKind ClassifyLine(string line) => ClassifyLine(line, out _);

        private static PastedItemKind ClassifyLine(string line, out BridgeType bridgeType)
        {
            bridgeType = BridgeType.NONE;
            if (string.IsNullOrWhiteSpace(line))
                return PastedItemKind.Unknown;

            // 1) Tor bridge with a pluggable-transport prefix (obfs4, webtunnel, ...).
            int firstSpace = line.IndexOf(' ');
            if (firstSpace > 0)
            {
                string head = line.Substring(0, firstSpace);
                if (BridgeTransports.TryGetValue(head, out BridgeType bt))
                {
                    bridgeType = bt;
                    return PastedItemKind.Bridge;
                }
            }

            // 2) Vanilla bridge: IP:PORT + fingerprint.
            if (VanillaBridge.IsMatch(line) || VanillaBridgeV6.IsMatch(line))
            {
                bridgeType = BridgeType.NONE;
                return PastedItemKind.Bridge;
            }

            // 3) Known server connection schemes.
            foreach (string scheme in ConfigSchemes)
            {
                if (line.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                    return PastedItemKind.ServerLink;
            }

            // 4) Plain http(s) URL → treated as a subscription source.
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return PastedItemKind.SubscriptionLink;

            return PastedItemKind.Unknown;
        }

        // Unwrap an invxray:// deep link into its underlying payload so the same
        // links exported/shared by the app (older versions included) import cleanly.
        private static bool TryNormalizeDeepLink(string line, out string payload, out PastedItemKind kind)
        {
            payload = null;
            kind = PastedItemKind.Unknown;

            // Check config-data before config: "config-data/" is not a prefix of "config/".
            if (line.StartsWith(DeepLink.CONFIG_DATA, StringComparison.OrdinalIgnoreCase))
            {
                payload = Uri.UnescapeDataString(line.Substring(DeepLink.CONFIG_DATA.Length).Trim());
                kind = PastedItemKind.ServerLink;
                return payload.Length > 0;
            }

            if (line.StartsWith(DeepLink.CONFIG, StringComparison.OrdinalIgnoreCase))
            {
                payload = line.Substring(DeepLink.CONFIG.Length).Trim();
                kind = PastedItemKind.ServerLink;
                return payload.Length > 0;
            }

            if (line.StartsWith(DeepLink.SUBSCRIPTION, StringComparison.OrdinalIgnoreCase))
            {
                payload = line.Substring(DeepLink.SUBSCRIPTION.Length).Trim();
                kind = PastedItemKind.SubscriptionLink;
                return payload.Length > 0;
            }

            return false;
        }

        // Tolerate lines copied with a leading "Bridge " directive (torrc style).
        private static string StripBridgePrefix(string line)
        {
            if (line.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
            {
                string rest = line.Substring("Bridge ".Length).Trim();
                if (rest.Length > 0)
                    return rest;
            }
            return line;
        }

        /// <summary>
        /// Suggest a human-friendly subscription remark derived from its URL host.
        /// </summary>
        public static string SuggestSubscriptionRemark(string url, int fallbackIndex)
        {
            try
            {
                var uri = new Uri(url);
                string host = uri.Host;
                if (!string.IsNullOrWhiteSpace(host))
                    return host;
            }
            catch { }
            return $"Subscription {fallbackIndex}";
        }
    }
}
