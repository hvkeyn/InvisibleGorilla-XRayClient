using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    /// <summary>
    /// Produces the Xray JSON used when Tor is active. Tor runs as a local SOCKS daemon and
    /// is expressed as an Xray outbound, so the rest of the connection pipeline (local inbound,
    /// system-proxy / TUN front-end, app rules) is reused unchanged.
    /// </summary>
    public static class TorConfigBuilder
    {
        public const string TorOutboundTag = "tor-out";

        /// <summary>
        /// ONLY_TOR mode: a minimal config whose single outbound is the local Tor SOCKS port.
        /// </summary>
        public static string BuildTorOnlyConfig(int torSocksPort)
        {
            var config = new JObject
            {
                ["outbounds"] = new JArray
                {
                    new JObject
                    {
                        ["tag"] = "proxy",
                        ["protocol"] = "socks",
                        ["settings"] = new JObject
                        {
                            ["servers"] = new JArray
                            {
                                new JObject
                                {
                                    ["address"] = "127.0.0.1",
                                    ["port"] = torSocksPort
                                }
                            }
                        }
                    }
                }
            };

            return config.ToString(Formatting.None);
        }

        /// <summary>
        /// XRAY_OVER_TOR mode: adds a SOCKS outbound pointing at Tor and routes the primary
        /// outbound's dialer through it (dialerProxy chaining), so Xray reaches its server via Tor.
        /// </summary>
        public static string WrapConfigOverTor(string configJson, int torSocksPort)
        {
            JObject root;
            try
            {
                root = JObject.Parse(configJson);
            }
            catch
            {
                // If the incoming config can't be parsed, fall back to plain Tor egress.
                return BuildTorOnlyConfig(torSocksPort);
            }

            if (!(root["outbounds"] is JArray outbounds) || outbounds.Count == 0)
                return BuildTorOnlyConfig(torSocksPort);

            // Append the Tor SOCKS outbound.
            outbounds.Add(new JObject
            {
                ["tag"] = TorOutboundTag,
                ["protocol"] = "socks",
                ["settings"] = new JObject
                {
                    ["servers"] = new JArray
                    {
                        new JObject
                        {
                            ["address"] = "127.0.0.1",
                            ["port"] = torSocksPort
                        }
                    }
                }
            });

            // Route the primary outbound's dialer through Tor.
            if (outbounds[0] is JObject primary)
            {
                JObject streamSettings = primary["streamSettings"] as JObject;
                if (streamSettings == null)
                {
                    streamSettings = new JObject();
                    primary["streamSettings"] = streamSettings;
                }

                JObject sockopt = streamSettings["sockopt"] as JObject;
                if (sockopt == null)
                {
                    sockopt = new JObject();
                    streamSettings["sockopt"] = sockopt;
                }

                sockopt["dialerProxy"] = TorOutboundTag;
            }

            return root.ToString(Formatting.None);
        }
    }
}
