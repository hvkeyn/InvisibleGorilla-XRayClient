using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Models
{
    /// <summary>
    /// How user traffic is routed when Tor is enabled.
    /// ONLY_TOR     - all traffic egresses through the local Tor SOCKS port (Orbot-style).
    /// XRAY_OVER_TOR - the selected Xray server is reached *through* Tor (chaining via dialerProxy),
    ///                useful to defeat censorship on the path to the Xray entry point.
    /// </summary>
    public enum TorMode { ONLY_TOR, XRAY_OVER_TOR }

    /// <summary>
    /// Pluggable-transport family used to reach the Tor network.
    /// NONE      - connect directly to public relays (no bridges).
    /// OBFS4     - obfs4 bridges (default or manual lines), via lyrebird/obfs4proxy.
    /// SNOWFLAKE - snowflake transport (no bridge lines required).
    /// MEEK_AZURE- meek domain-fronted bridge.
    /// WEBTUNNEL - webtunnel bridges (manual lines).
    /// </summary>
    public enum BridgeType { NONE, OBFS4, SNOWFLAKE, MEEK_AZURE, WEBTUNNEL }

    /// <summary>
    /// Tor + bridges configuration. Persisted as a nested object inside Settings.json.
    /// </summary>
    public class TorSettings
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool Enabled;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public TorMode Mode;

        // Local listeners exposed by the bundled tor daemon. Non-standard ports to avoid
        // clashing with a system-wide Tor / Tor Browser instance on 9050/9051.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int SocksPort;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int ControlPort;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public BridgeType BridgeType;

        // Manual or fetched obfs4 / meek / webtunnel bridge lines (one per entry).
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public List<string> BridgeLines;

        public TorSettings()
        {
            Enabled = false;
            Mode = TorMode.ONLY_TOR;
            SocksPort = 9250;
            ControlPort = 9251;
            BridgeType = BridgeType.NONE;
            BridgeLines = new List<string>();
        }

        public bool GetEnabled() => Enabled;

        public TorMode GetMode() => Mode;

        public int GetSocksPort() => SocksPort > 0 ? SocksPort : 9250;

        public int GetControlPort() => ControlPort > 0 ? ControlPort : 9251;

        public BridgeType GetBridgeType() => BridgeType;

        public List<string> GetBridgeLines()
        {
            if (BridgeLines == null)
                return new List<string>();

            return BridgeLines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();
        }

        public TorSettings Clone()
        {
            return new TorSettings
            {
                Enabled = Enabled,
                Mode = Mode,
                SocksPort = GetSocksPort(),
                ControlPort = GetControlPort(),
                BridgeType = BridgeType,
                BridgeLines = GetBridgeLines()
            };
        }
    }

    /// <summary>
    /// A reusable, switchable Tor bridge profile that shows up in the server list like a VLESS
    /// config. Selecting it activates Tor with these bridges; it can be availability/speed checked
    /// just like a normal server. The matching <see cref="ConfigPath"/> points at a minimal Tor-only
    /// Xray config file so the existing "select + run" pipeline works unchanged.
    /// </summary>
    public class TorProfile
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Name;

        // Path of the generated Tor-only Xray config file that represents this profile in the list.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string ConfigPath;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public TorMode Mode;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public BridgeType BridgeType;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public List<string> BridgeLines;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int SocksPort;

        // Latency (ms) of the last successful bridge check, or -1 when never/failed.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int LastLatencyMs;

        public TorProfile()
        {
            Name = "";
            ConfigPath = "";
            Mode = TorMode.ONLY_TOR;
            BridgeType = BridgeType.OBFS4;
            BridgeLines = new List<string>();
            SocksPort = 9250;
            LastLatencyMs = -1;
        }

        public List<string> GetBridgeLines()
        {
            if (BridgeLines == null)
                return new List<string>();

            return BridgeLines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();
        }

        public int GetSocksPort() => SocksPort > 0 ? SocksPort : 9250;

        public TorProfile Clone()
        {
            return new TorProfile
            {
                Name = Name ?? "",
                ConfigPath = ConfigPath ?? "",
                Mode = Mode,
                BridgeType = BridgeType,
                BridgeLines = GetBridgeLines(),
                SocksPort = GetSocksPort(),
                LastLatencyMs = LastLatencyMs
            };
        }
    }
}
