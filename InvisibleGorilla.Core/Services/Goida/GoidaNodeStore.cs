using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
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
                SaveLocked();
            }
        }

        public void UpdateNodeStatus(string nodeId, int latencyMs, GoidaNodeStatus status)
        {
            lock (sync)
            {
                GoidaNode? node = nodes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, nodeId, StringComparison.OrdinalIgnoreCase));
                if (node == null)
                    return;

                node.LatencyMs = latencyMs;
                node.Status = status;
                node.LastCheckedUtc = DateTime.UtcNow;
                SaveLocked();
            }
        }

        public GoidaNode? FindById(string nodeId)
        {
            lock (sync)
            {
                return nodes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, nodeId, StringComparison.OrdinalIgnoreCase))?.Clone();
            }
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
