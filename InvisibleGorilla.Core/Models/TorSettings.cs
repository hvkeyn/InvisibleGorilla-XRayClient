using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        // Last successful bridge check for the built-in Tor card, or -1 when never checked.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(-1)]
        public int LastLatencyMs;

        // Empty means any exit, so the circuit can assemble. A two-letter code
        // is optional and then Tor may use only that country.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string ExitCountry;

        // Several saved address lists. The active one is what a start and a check use.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public List<TorBridgeSet> Sets;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string ActiveSetId;

        public TorSettings()
        {
            Enabled = false;
            Mode = TorMode.ONLY_TOR;
            SocksPort = 9250;
            ControlPort = 9251;
            BridgeType = BridgeType.NONE;
            BridgeLines = new List<string>();
            LastLatencyMs = -1;
            ExitCountry = "";
            Sets = new List<TorBridgeSet>();
            ActiveSetId = "";
        }

        public bool GetEnabled() => Enabled;

        public TorMode GetMode() => Mode;

        public int GetSocksPort() => SocksPort > 0 ? SocksPort : 9250;

        public int GetControlPort() => ControlPort > 0 ? ControlPort : 9251;

        public BridgeType GetBridgeType() => BridgeType;

        public string GetExitCountry()
        {
            if (string.IsNullOrWhiteSpace(ExitCountry))
                return string.Empty;

            string code = ExitCountry.Trim().ToLowerInvariant();
            if (code.Length == 2 && code[0] >= 'a' && code[0] <= 'z' && code[1] >= 'a' && code[1] <= 'z')
                return code;

            return string.Empty;
        }

        public List<string> GetBridgeLines()
        {
            if (BridgeLines == null)
                return new List<string>();

            var lines = new List<string>();
            foreach (string raw in BridgeLines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                foreach (string part in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = part.Trim();
                    if (line.Length > 0)
                        lines.Add(line);
                }
            }

            return lines;
        }

        public TorSettings Clone()
        {
            EnsureSets();
            return new TorSettings
            {
                Enabled = Enabled,
                Mode = Mode,
                SocksPort = GetSocksPort(),
                ControlPort = GetControlPort(),
                BridgeType = BridgeType,
                BridgeLines = GetBridgeLines(),
                LastLatencyMs = LastLatencyMs,
                ExitCountry = GetExitCountry(),
                ActiveSetId = ActiveSetId ?? "",
                Sets = Sets.Select(set => set.Clone()).ToList()
            };
        }

        public void EnsureSets()
        {
            if (Sets == null)
                Sets = new List<TorBridgeSet>();

            Sets.RemoveAll(set => set == null || string.IsNullOrWhiteSpace(set.Id));
            if (Sets.Count == 0)
            {
                List<string> migratedLines = GetBridgeLines();
                BridgeType migratedType = TorBridgeSet.InferType(migratedLines);
                TorBridgeSet migrated = new TorBridgeSet
                {
                    Id = Guid.NewGuid().ToString("N"),
                    BridgeType = migratedType != BridgeType.NONE ? migratedType : BridgeType,
                    BridgeLines = migratedLines
                };
                Sets.Add(migrated);
                ActiveSetId = migrated.Id;
            }

            if (FindSet(ActiveSetId) == null)
                ActiveSetId = Sets[0].Id;

            RepairSetTypes();
            PublishActive();
        }

        public List<TorBridgeSet> GetSets()
        {
            EnsureSets();
            return Sets;
        }

        public TorBridgeSet FindSet(string id)
        {
            if (Sets == null || string.IsNullOrWhiteSpace(id))
                return null;

            return Sets.LastOrDefault(set => set != null && string.Equals(set.Id, id, StringComparison.Ordinal));
        }

        public TorBridgeSet GetActiveSet()
        {
            EnsureSets();
            return FindSet(ActiveSetId);
        }

        public void ApplyToSet(string setId, BridgeType type, IList<string> lines)
        {
            EnsureSets();
            TorBridgeSet set = FindSet(setId) ?? GetActiveSet();
            List<string> copy = TorBridgeSet.CopyLines(lines);

            // An empty editor must never erase saved bridges: the panel is rebuilt on every
            // screen switch and used to wipe the WebTunnel line this way.
            if (copy.Count == 0 && set.GetBridgeLines().Count > 0)
            {
                if (string.Equals(set.Id, ActiveSetId, StringComparison.Ordinal))
                    PublishActive();
                return;
            }

            BridgeType inferred = TorBridgeSet.InferType(copy);
            set.BridgeType = inferred != BridgeType.NONE ? inferred : type;
            set.BridgeLines = copy;
            if (string.Equals(set.Id, ActiveSetId, StringComparison.Ordinal))
                PublishActive();
        }

        public void SelectSet(string setId)
        {
            EnsureSets();
            TorBridgeSet set = FindSet(setId);
            if (set == null)
                return;

            ActiveSetId = set.Id;
            PublishActive();
        }

        public TorBridgeSet AddSet(BridgeType type, IList<string> lines)
        {
            EnsureSets();
            TorBridgeSet set = new TorBridgeSet
            {
                Id = Guid.NewGuid().ToString("N"),
                BridgeType = type,
                BridgeLines = TorBridgeSet.CopyLines(lines)
            };
            Sets.Add(set);
            ActiveSetId = set.Id;
            PublishActive();
            return set;
        }

        /// <summary>
        /// Opens the saved list for this transport. A missing list is created from the built-in lines.
        /// An existing list is left as the user saved it.
        /// </summary>
        public TorBridgeSet OpenOrCreateOrigin(string origin, BridgeType type, IList<string> builtinLines)
        {
            EnsureSets();
            if (!string.IsNullOrWhiteSpace(origin))
            {
                TorBridgeSet existing = Sets.FirstOrDefault(set =>
                    string.Equals(set.Origin, origin, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    SelectSet(existing.Id);
                    return existing;
                }
            }

            TorBridgeSet created = AddSet(type, builtinLines);
            created.Origin = origin ?? "";
            return created;
        }

        public void RemoveActiveSet()
        {
            EnsureSets();
            TorBridgeSet active = GetActiveSet();
            if (Sets.Count <= 1)
            {
                active.BridgeLines = new List<string>();
                PublishActive();
                return;
            }

            int index = Sets.IndexOf(active);
            Sets.Remove(active);
            TorBridgeSet next = Sets[Math.Min(Math.Max(index, 0), Sets.Count - 1)];
            ActiveSetId = next.Id;
            PublishActive();
        }

        public int SetOrdinal(TorBridgeSet set)
        {
            EnsureSets();
            if (set == null)
                return 0;

            int same = 0;
            int ordinal = 0;
            foreach (TorBridgeSet other in Sets)
            {
                if (other.BridgeType != set.BridgeType)
                    continue;

                same++;
                if (string.Equals(other.Id, set.Id, StringComparison.Ordinal))
                    ordinal = same;
            }

            return same > 1 ? ordinal : 0;
        }

        public void RepairSetTypes()
        {
            if (Sets == null)
                return;

            foreach (TorBridgeSet set in Sets)
            {
                if (set == null)
                    continue;
                BridgeType inferred = TorBridgeSet.InferType(set.GetBridgeLines());
                if (inferred != BridgeType.NONE)
                    set.BridgeType = inferred;
            }
        }

        private void PublishActive()
        {
            TorBridgeSet active = FindSet(ActiveSetId) ?? (Sets.Count > 0 ? Sets[0] : null);
            if (active == null)
                return;

            ActiveSetId = active.Id;
            BridgeType = active.BridgeType;
            BridgeLines = active.GetBridgeLines();
        }
    }

    /// <summary>
    /// One saved list of bridge addresses. Several lists can share a transport.
    /// </summary>
    public class TorBridgeSet
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Id;

        // User-facing name of the saved list; empty keeps the generated description.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Name;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public BridgeType BridgeType;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public List<string> BridgeLines;

        // Built-in shortcut that owns this list, such as "snowflake" or "snowflake-amp".
        // Empty for a list the user saved themselves.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Origin;

        public TorBridgeSet()
        {
            Id = "";
            Name = "";
            BridgeType = BridgeType.OBFS4;
            BridgeLines = new List<string>();
            Origin = "";
        }

        public List<string> GetBridgeLines()
        {
            return CopyLines(BridgeLines);
        }

        public TorBridgeSet Clone()
        {
            return new TorBridgeSet
            {
                Id = Id ?? "",
                Name = Name ?? "",
                BridgeType = BridgeType,
                BridgeLines = GetBridgeLines(),
                Origin = Origin ?? ""
            };
        }

        public static BridgeType InferType(IList<string> lines)
        {
            if (lines == null)
                return BridgeType.NONE;

            foreach (string raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string trimmed = raw.Trim();
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

        public static List<string> CopyLines(IList<string> lines)
        {
            var copy = new List<string>();
            if (lines == null)
                return copy;

            foreach (string raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                foreach (string part in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = part.Trim();
                    if (line.Length > 0)
                        copy.Add(line);
                }
            }

            return copy;
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

            var lines = new List<string>();
            foreach (string raw in BridgeLines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                foreach (string part in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = part.Trim();
                    if (line.Length > 0)
                        lines.Add(line);
                }
            }

            return lines;
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
