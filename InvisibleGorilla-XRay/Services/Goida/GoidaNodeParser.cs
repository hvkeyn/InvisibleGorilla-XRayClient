using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Models.Templates.Subscriptions;
using InvisibleGorillaXRay.Utilities;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaNodeParser
    {
        private readonly Func<string, Status> convertConfigLinkToV2Ray;

        public GoidaNodeParser(Func<string, Status> convertConfigLinkToV2Ray)
        {
            this.convertConfigLinkToV2Ray = convertConfigLinkToV2Ray
                ?? throw new ArgumentNullException(nameof(convertConfigLinkToV2Ray));
        }

        public List<GoidaNode> ParseList(int listId, string rawData, string nodesDirectory)
        {
            if (string.IsNullOrWhiteSpace(rawData))
                return new List<GoidaNode>();

            Directory.CreateDirectory(nodesDirectory);
            List<string[]> v2RayList = ConvertRawToV2RayList(rawData);
            Dictionary<string, GoidaNode> nodesById = new(StringComparer.OrdinalIgnoreCase);

            foreach (string[] entry in v2RayList)
            {
                if (entry == null || entry.Length < 2)
                    continue;

                string remark = entry[0]?.Trim() ?? string.Empty;
                string configJson = entry[1];
                if (string.IsNullOrWhiteSpace(configJson))
                    continue;

                string sanitized = JsonUtility.SanitizeRuntimeManagedSections(configJson);
                string endpoint = ExtractEndpoint(remark, sanitized);
                string nodeId = ComputeNodeId(listId, remark, sanitized);
                string fileName = $"{listId}_{nodeId}.json";
                string configPath = Path.Combine(nodesDirectory, fileName);
                File.WriteAllText(configPath, sanitized);

                nodesById[nodeId] = new GoidaNode
                {
                    Id = nodeId,
                    ListId = listId,
                    DisplayName = string.IsNullOrWhiteSpace(remark) ? $"List {listId}" : remark,
                    Endpoint = endpoint,
                    ConfigPath = configPath,
                    Country = GoidaNodeDisplay.ExtractCountry(remark, endpoint),
                    Protocol = GoidaNodeDisplay.ExtractProtocol(sanitized),
                    LatencyMs = Values.Availability.NOT_CHECKED,
                    Status = GoidaNodeStatus.Unknown
                };
            }

            return nodesById.Values.ToList();
        }

        private List<string[]> ConvertRawToV2RayList(string rawData)
        {
            try
            {
                Simple template = new Simple();
                System.Reflection.FieldInfo? dataField = typeof(Template).GetField(
                    "Data",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (dataField == null)
                    return new List<string[]>();

                dataField.SetValue(template, rawData);
                return template.ConvertToV2RayList(convertConfigLinkToV2Ray) ?? new List<string[]>();
            }
            catch
            {
                return new List<string[]>();
            }
        }

        private static string ComputeNodeId(int listId, string remark, string configJson)
        {
            string source = $"{listId}|{remark}|{configJson.Length}|{configJson.GetHashCode()}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        }

        private static string ExtractEndpoint(string remark, string configJson)
        {
            try
            {
                dynamic parsed = JsonConvert.DeserializeObject(configJson);
                if (parsed?.outbounds != null)
                {
                    foreach (dynamic outbound in parsed.outbounds)
                    {
                        string address = outbound?.settings?.vnext?[0]?.address
                            ?? outbound?.settings?.servers?[0]?.address
                            ?? outbound?.settings?.address;
                        object port = outbound?.settings?.vnext?[0]?.port
                            ?? outbound?.settings?.servers?[0]?.port
                            ?? outbound?.settings?.port;
                        if (!string.IsNullOrWhiteSpace(address?.ToString()))
                            return $"{address}:{port}";
                    }
                }
            }
            catch
            {
            }

            return remark;
        }
    }
}
