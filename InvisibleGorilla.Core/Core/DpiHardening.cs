using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Core
{
    /// <summary>
    /// Normalises the "fragment" mask in stream settings. Subscription configs often carry the
    /// legacy string form ("length": "100-200") which current xray rejects with
    /// "LengthMin can't be 0", so the whole node looked unsupported. The core wants numeric
    /// ranges, so rewrite them before the native loader sees the file.
    /// </summary>
    public static class DpiHardening
    {
        public static string Apply(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.IndexOf("\"fragment\"", StringComparison.Ordinal) < 0)
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

                    // The mask can live in stream settings (sockopt.fragment) or, for the freedom
                    // outbound used as a detour, in settings.fragment. Both take the same ranges.
                    if (outbound["streamSettings"] is JObject stream
                        && stream["sockopt"] is JObject sockopt
                        && sockopt["fragment"] is JObject streamFragment)
                    {
                        changed |= NormalizeFragment(streamFragment);
                    }

                    if (outbound["settings"] is JObject settings
                        && settings["fragment"] is JObject settingsFragment)
                    {
                        changed |= NormalizeFragment(settingsFragment);
                    }
                }

                return changed ? root.ToString(Newtonsoft.Json.Formatting.None) : json;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("DpiHardening.Apply", ex);
                return json;
            }
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

            string text = value.ToString();
            if (!TryParseRange(text, out int start, out int end))
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

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
                return false;

            if (parts.Length > 1)
                int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out end);

            if (end <= 0)
                end = start;

            return start > 0 && end >= start;
        }
    }
}