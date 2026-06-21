using System;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Utilities
{
    public static class JsonUtility
    {
        public static bool IsJsonValid(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    return doc != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static T ConvertFromJson<T>(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json))
                    return default;
                    
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch
            {
                return default;
            }
        }

        public static string Find(string key, string parent, string jsonString)
        {
            if (string.IsNullOrWhiteSpace(jsonString))
                return null;

            try
            {
                var match = JObject.Parse(jsonString.ToLower())
                    .DescendantsAndSelf().OfType<JProperty>()
                    .FirstOrDefault(x => x.Name.Equals(parent) && x.SelectToken($"$..{key}") != null);

                return match?.SelectToken($"$..{key}")?.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the remote host from the first real proxy outbound (VLESS/VMess/Trojan/etc.).
        /// The native runtime config often omits or reshapes fields, so callers should fall
        /// back to the on-disk user config when this returns empty.
        /// </summary>
        public static string ExtractOutboundServerAddress(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            try
            {
                if (JToken.Parse(json) is not JObject root)
                    return string.Empty;

                if (root["outbounds"] is not JArray outbounds)
                    return string.Empty;

                foreach (JToken outbound in outbounds)
                {
                    string protocol = outbound["protocol"]?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
                    if (protocol is "freedom" or "blackhole" or "direct" or "dns" or "loopback")
                        continue;

                    JToken? settings = outbound["settings"];
                    if (settings == null)
                        continue;

                    string? address = settings["vnext"]?[0]?["address"]?.ToString()
                        ?? settings["servers"]?[0]?["address"]?.ToString()
                        ?? settings["address"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(address))
                        return address.Trim();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public static string SanitizeRuntimeManagedSections(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json;

            try
            {
                JObject root = JObject.Parse(json);
                bool changed = false;

                // Drop runtime-managed and "management" sections. The local inbound is
                // always rebuilt by the native wrapper, and the api/stats/policy machinery
                // (the Xray gRPC HandlerService) is never used by this client. Leaving it
                // on disk would expose a localhost gRPC API that anti-VPN probes treat as
                // an instant detection signal, so we strip it from every persisted config.
                changed |= RemoveTopLevelProperty(root, "api");
                changed |= RemoveTopLevelProperty(root, "stats");
                changed |= RemoveTopLevelProperty(root, "policy");
                changed |= RemoveTopLevelProperty(root, "inbounds");

                return changed ? root.ToString(Formatting.Indented) : json;
            }
            catch
            {
                return json;
            }
        }

        private static bool RemoveTopLevelProperty(JObject root, string propertyName)
        {
            JProperty property = root.Properties()
                .FirstOrDefault(candidate => candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));

            if (property == null)
                return false;

            property.Remove();
            return true;
        }
    }
}
