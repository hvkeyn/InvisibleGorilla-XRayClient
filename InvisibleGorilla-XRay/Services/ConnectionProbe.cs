using System;
using System.IO;
using System.Net;

namespace InvisibleGorillaXRay.Services
{
    using Models;
    using Utilities;
    using Values;

    /// <summary>
    /// Helpers that make the live connection-info widget reflect the *actual* traffic path
    /// instead of a naive direct request.
    ///
    /// The core problem the widget had: in proxy mode xray exposes a local SOCKS/HTTP
    /// listener and the OS proxy points other apps at it, but a plain HttpClient does not
    /// honour a SOCKS system proxy - so the probe leaked the real ISP IP even while the
    /// browser correctly exited through the VPN. Here we route the probe through the same
    /// local listener the user's apps use, so the reported IP matches reality.
    /// </summary>
    public static class ConnectionProbe
    {
        /// <summary>
        /// Build the proxy the IP lookup should flow through so it mirrors user traffic:
        /// - disconnected: direct (null) so we capture the real baseline IP.
        /// - TUN: direct (null) because routing is captured at the network layer already.
        /// - proxy mode: the local xray listener (socks5/http on 127.0.0.1:proxyPort).
        /// </summary>
        public static IWebProxy BuildExitProxy(bool connected, Mode mode, Protocol protocol, int proxyPort)
        {
            if (!connected)
                return null;

            if (mode == Mode.TUN)
                return null;

            if (proxyPort <= 0)
                return null;

            string scheme = protocol == Protocol.SOCKS ? "socks5" : "http";
            return new WebProxy($"{scheme}://{Global.LOCAL_HOST}:{proxyPort}");
        }

        /// <summary>
        /// Compact, language-neutral description of the active path, e.g.
        /// "VLESS · Proxy/SOCKS", "Trojan · TUN", "Tor", "VLESS over Tor (obfs4) · Proxy/SOCKS".
        /// UI layers prefix it with a localized "Mode:" label.
        /// </summary>
        public static string DescribeMode(Mode mode, Protocol protocol, TorSettings tor, string outboundProtocol)
        {
            string egress;
            string protocolLabel = string.IsNullOrWhiteSpace(outboundProtocol) ? "Xray" : outboundProtocol;

            if (tor != null && tor.GetEnabled())
            {
                if (tor.GetMode() == TorMode.ONLY_TOR)
                    egress = "Tor";
                else
                    egress = $"{protocolLabel} over Tor";

                string bridge = DescribeBridge(tor.GetBridgeType());
                if (!string.IsNullOrEmpty(bridge))
                    egress += $" ({bridge})";
            }
            else
            {
                egress = protocolLabel;
            }

            string routing = mode == Mode.TUN ? "TUN" : "Proxy";
            string listener = protocol == Protocol.SOCKS ? "SOCKS" : "HTTP";

            return $"{egress} · {routing}/{listener}";
        }

        /// <summary>
        /// Best-effort outbound protocol (VLESS / VMESS / Trojan / Shadowsocks) read from the
        /// selected config file. Returns an empty string when it cannot be determined.
        /// </summary>
        public static string DetectOutboundProtocol(string configPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                    return string.Empty;

                string json = File.ReadAllText(configPath);
                string protocol = JsonUtility.Find(key: "protocol", parent: "outbounds", jsonString: json);
                return NormalizeProtocol(protocol);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeProtocol(string protocol)
        {
            if (string.IsNullOrWhiteSpace(protocol))
                return string.Empty;

            switch (protocol.Trim().ToLowerInvariant())
            {
                case "vless": return "VLESS";
                case "vmess": return "VMess";
                case "trojan": return "Trojan";
                case "shadowsocks": return "Shadowsocks";
                case "socks": return "SOCKS";
                case "http": return "HTTP";
                case "wireguard": return "WireGuard";
                default: return protocol.Trim();
            }
        }

        private static string DescribeBridge(BridgeType bridgeType)
        {
            switch (bridgeType)
            {
                case BridgeType.OBFS4: return "obfs4";
                case BridgeType.SNOWFLAKE: return "snowflake";
                case BridgeType.MEEK_AZURE: return "meek-azure";
                case BridgeType.WEBTUNNEL: return "webtunnel";
                default: return string.Empty;
            }
        }
    }
}
