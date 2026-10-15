using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    using Values;

    /// <summary>
    /// Fresh bridge lines from Moat. Orbot keeps this cache for 48 hours and prefers it
    /// over the bridges compiled into the app.
    /// </summary>
    public static class BridgeCatalog
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(48);

        public static List<string> Read(string transport)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(transport))
                return lines;

            try
            {
                string path = CatalogPath();
                if (!File.Exists(path))
                    return lines;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Ttl)
                    return lines;

                JObject root = JObject.Parse(File.ReadAllText(path));
                AddLines(root[transport] as JArray, lines);
            }
            catch
            {
            }

            return lines;
        }

        public static void Store(JObject builtin)
        {
            if (builtin == null)
                return;

            try
            {
                JObject root = LoadOrCreate();
                bool any = false;
                foreach (JProperty property in builtin.Properties())
                {
                    if (property.Value is JArray array && AddLines(array, new List<string>()) > 0)
                    {
                        root[property.Name] = array;
                        any = true;
                    }
                }

                if (any)
                    Save(root);
            }
            catch
            {
            }
        }

        public static void StoreLines(string transport, IList<string> lines)
        {
            if (string.IsNullOrWhiteSpace(transport) || lines == null || lines.Count == 0)
                return;

            var array = new JArray();
            foreach (string line in lines)
            {
                if (LooksLikeBridge(line))
                    array.Add(line.Trim());
            }
            if (array.Count == 0)
                return;

            try
            {
                JObject root = LoadOrCreate();
                root[transport] = array;
                Save(root);
            }
            catch
            {
            }
        }

        private static JObject LoadOrCreate()
        {
            try
            {
                string path = CatalogPath();
                if (File.Exists(path))
                    return JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
            }
            return new JObject();
        }

        private static void Save(JObject root)
        {
            string path = CatalogPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? Directory.TOR_DATA);
            File.WriteAllText(path, root.ToString());
        }

        private static string CatalogPath() => System.IO.Path.Combine(Directory.TOR_DATA, "updated-bridges.json");

        private static int AddLines(JArray array, List<string> lines)
        {
            if (array == null)
                return 0;
            int added = 0;
            foreach (JToken token in array)
            {
                string line = (string)token;
                if (!LooksLikeBridge(line))
                    continue;
                lines.Add(line.Trim());
                added++;
            }
            return added;
        }

        private static bool LooksLikeBridge(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;
            string trimmed = line.TrimStart();
            return trimmed.StartsWith("obfs4 ", StringComparison.Ordinal)
                || trimmed.StartsWith("snowflake ", StringComparison.Ordinal)
                || trimmed.StartsWith("meek", StringComparison.Ordinal)
                || trimmed.StartsWith("webtunnel ", StringComparison.Ordinal)
                || trimmed.StartsWith("dnstt ", StringComparison.Ordinal);
        }
    }
}
