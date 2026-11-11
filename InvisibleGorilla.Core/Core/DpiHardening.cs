using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Core
{
    /// <summary>
    /// Prepares an outbound for hostile mobile networks.
    ///
    /// Two things break a subscription config there:
    ///  * the legacy fragment mask uses string ranges ("100-200") and the current core rejects
    ///    the whole config with "LengthMin can't be 0";
    ///  * every TLS connection is a separate fingerprint, and carriers throttle or reset flows
    ///    with many handshakes, which is what mux with a fixed concurrency fixes.
    /// </summary>
    public static class DpiHardening
    {
        private const string FragmentPackets = "tlshello";
        private const int FragmentLengthFrom = 100;
        private const int FragmentLengthTo = 200;
        private const int FragmentIntervalFrom = 10;
        private const int FragmentIntervalTo = 20;
        private const int MuxConcurrency = 8;

        private static volatile bool mobileNetwork;

        /// <summary>Set by the platform layer; mobile networks get mux and hello splitting.</summary>
        public static bool MobileNetwork
        {
            get => mobileNetwork;
            set => mobileNetwork = value;
        }

        public static string Apply(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json;

            try
            {
                JObject root = JObject.Parse(json);
                if (root["outbounds"] is not JArray outbounds)
                    return json;

                bool changed = false;
                foreach (JToken item in outbounds)
                {
                    if (item is not JObject outbound)
                        continue;

                    string protocol = outbound.Value<string>("protocol") ?? "";
                    if (protocol.Length == 0 || protocol == "freedom" || protocol == "blackhole" || protocol == "dns")
                        continue;

                    changed |= NormalizeMasks(outbound);

                    if (mobileNetwork)
                        changed |= ApplyMobileProfile(outbound);
                }

                return changed ? root.ToString(Newtonsoft.Json.Formatting.None) : json;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("DpiHardening.Apply", ex);
                return json;
            }
        }

        private static bool NormalizeMasks(JObject outbound)
        {
            bool changed = false;

            if (outbound["streamSettings"] is JObject stream
                && stream["sockopt"] is JObject sockopt
                && sockopt["fragment"] is JObject streamFragment)
            {
                changed |= NormalizeFragment(streamFragment);
            }

            if (outbound["settings"] is JObject settings && settings["fragment"] is JObject settingsFragment)
                changed |= NormalizeFragment(settingsFragment);

            // The newer "finalmask" block carries arrays of strings ("lengths": ["10-20"]) that
            // this core cannot build ("LengthMin can't be 0"), which rejected the whole node.
            // Drop it: the plain sockopt.fragment mask covers the same purpose and is accepted.
            if (outbound["streamSettings"] is JObject streamSettings2
                && streamSettings2["finalmask"] != null)
            {
                streamSettings2.Remove("finalmask");
                changed = true;
            }

            return changed;
        }

        private static bool ApplyMobileProfile(JObject outbound)
        {
            bool changed = false;

            // One TLS connection for the whole session: carriers throttle by handshake count.
            if (outbound["mux"] is not JObject mux)
            {
                outbound["mux"] = new JObject
                {
                    ["enabled"] = true,
                    ["concurrency"] = MuxConcurrency
                };
                changed = true;
            }
            else if (mux.Value<bool?>("enabled") != true)
            {
                mux["enabled"] = true;
                mux["concurrency"] = MuxConcurrency;
                changed = true;
            }

            if (outbound["streamSettings"] is not JObject stream)
                return changed;

            string network = stream.Value<string>("network") ?? "tcp";
            string security = stream.Value<string>("security") ?? "none";
            if (!string.Equals(network, "tcp", StringComparison.OrdinalIgnoreCase))
                return changed;
            if (!string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase))
                return changed;

            if (stream["sockopt"] is not JObject sockopt)
            {
                sockopt = new JObject();
                stream["sockopt"] = sockopt;
            }

            if (sockopt["fragment"] != null)
                return changed;

            sockopt["fragment"] = new JObject
            {
                ["packets"] = FragmentPackets,
                ["length"] = new JObject { ["from"] = FragmentLengthFrom, ["to"] = FragmentLengthTo },
                ["interval"] = new JObject { ["from"] = FragmentIntervalFrom, ["to"] = FragmentIntervalTo }
            };

            return true;
        }

        private static bool NormalizeFragment(JObject fragment)
        {
            bool changed = NormalizeRange(fragment, "length");
            changed |= NormalizeRange(fragment, "interval");
            changed |= NormalizeRange(fragment, "maxSplit");
            return changed;
        }

        private static bool NormalizeRange(JObject fragment, string name)
        {
            JToken value = fragment[name];
            if (value == null)
                return false;

            if (value is JObject obj)
            {
                if (obj["from"] != null && obj["to"] != null)
                    return false;

                int from = ReadInt(obj, "left", "from", "min");
                int to = ReadInt(obj, "right", "to", "max");
                if (from <= 0 || to <= 0)
                    return false;

                fragment[name] = new JObject { ["from"] = from, ["to"] = to };
                return true;
            }

            if (!TryParseRange(value.ToString(), out int start, out int end))
                return false;

            fragment[name] = new JObject { ["from"] = start, ["to"] = end };
            return true;
        }

        private static int ReadInt(JObject obj, params string[] names)
        {
            foreach (string name in names)
            {
                JToken token = obj[name];
                if (token != null && int.TryParse(token.ToString(), out int parsed) && parsed > 0)
                    return parsed;
            }

            return 0;
        }

        private static bool TryParseRange(string text, out int start, out int end)
        {
            start = 0;
            end = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] parts = text.Split(new[] { '-', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start <= 0)
                return false;

            if (parts.Length > 1)
                int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out end);

            if (end <= 0)
                end = start;

            return end >= start;
        }
    }
}