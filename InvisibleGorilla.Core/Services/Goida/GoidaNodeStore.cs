using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaNodeStore
    {
        public const string ProfileDirectoryName = "Goida";
        public const string NodesSubDirectory = "nodes";
        public const string StateFileName = "goida-state.json";

        private readonly object sync = new();
        private List<GoidaNode> nodes = new();
        private int bulkUpdateDepth;

        public string ProfileDirectory => Path.Combine(Values.Directory.CONFIGS, ProfileDirectoryName);

        public string NodesDirectory => Path.Combine(ProfileDirectory, NodesSubDirectory);

        public string StateFilePath => Path.Combine(ProfileDirectory, StateFileName);

        public IReadOnlyList<GoidaNode> GetNodes()
        {
            lock (sync)
            {
                return nodes.Select(node => node.Clone()).ToList();
            }
        }

        public void Load()
        {
            lock (sync)
            {
                if (!File.Exists(StateFilePath))
                {
                    nodes = new List<GoidaNode>();
                    return;
                }

                try
                {
                    string json = File.ReadAllText(StateFilePath);
                    nodes = JsonConvert.DeserializeObject<List<GoidaNode>>(json) ?? new List<GoidaNode>();
                }
                catch
                {
                    nodes = new List<GoidaNode>();
                }
            }
        }

        public void ReplaceNodes(IEnumerable<GoidaNode> newNodes)
        {
            lock (sync)
            {
                nodes = newNodes
                    .Where(node => node != null && !string.IsNullOrWhiteSpace(node.Id))
                    .Select(node => node.Clone())
                    .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .ToList();
                if (bulkUpdateDepth == 0)
                    SaveLocked();
            }
        }

        public void BeginBulkUpdate()
        {
            lock (sync)
                bulkUpdateDepth++;
        }

        public void EndBulkUpdate()
        {
            lock (sync)
            {
                if (bulkUpdateDepth > 0)
                    bulkUpdateDepth--;
                if (bulkUpdateDepth == 0)
                    SaveLocked();
            }
        }

        public void UpdateNodeStatus(
            string nodeId,
            int latencyMs,
            GoidaNodeStatus status,
            bool? vlessVerified = null,
            int listId = 0,
            string? endpoint = null)
        {
            lock (sync)
            {
                GoidaNode? node = FindNodeLocked(nodeId, listId, endpoint);
                if (node == null)
                {
                    DiagnosticLog.Write("GoidaNodeStore.UpdateNodeStatus",
                        $"Node not found: id={nodeId}, list={listId}, endpoint={endpoint}");
                    return;
                }

                node.LatencyMs = latencyMs;
                node.Status = status;
                node.LastCheckedUtc = DateTime.UtcNow;
                if (vlessVerified.HasValue)
                    node.VlessVerified = vlessVerified.Value;
                if (bulkUpdateDepth == 0)
                    SaveLocked();
            }
        }

        public GoidaNode? FindById(string nodeId)
        {
            lock (sync)
            {
                return FindNodeLocked(nodeId, 0, null)?.Clone();
            }
        }

        private GoidaNode? FindNodeLocked(string nodeId, int listId, string? endpoint)
        {
            GoidaNode? node = nodes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, nodeId, StringComparison.OrdinalIgnoreCase));
            if (node != null)
                return node;

            if (listId > 0 && !string.IsNullOrWhiteSpace(endpoint))
            {
                string normalizedEndpoint = endpoint.Trim();
                node = nodes.FirstOrDefault(candidate =>
                    candidate.ListId == listId
                    && string.Equals(candidate.Endpoint?.Trim(), normalizedEndpoint,
                        StringComparison.OrdinalIgnoreCase));
            }

            return node;
        }

        public void EnsureDirectories()
        {
            System.IO.Directory.CreateDirectory(NodesDirectory);
        }

        private void SaveLocked()
        {
            EnsureDirectories();
            File.WriteAllText(StateFilePath, JsonConvert.SerializeObject(nodes, Formatting.Indented));
        }
    }
}
