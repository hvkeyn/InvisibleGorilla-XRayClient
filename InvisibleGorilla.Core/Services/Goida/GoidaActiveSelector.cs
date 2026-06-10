using System;
using System.Collections.Generic;
using System.Linq;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaActiveSelector
    {
        public static GoidaNode? SelectBestNode(
            GoidaProfileSettings settings,
            IReadOnlyList<GoidaNode> nodes,
            string? currentActiveNodeId = null)
        {
            IEnumerable<GoidaNode> candidates = FilterCandidates(settings, nodes);
            List<GoidaNode> healthy = candidates
                .Where(node => node.Status == GoidaNodeStatus.Ok && node.LatencyMs >= 0)
                .OrderBy(node => node.LatencyMs)
                .ToList();

            if (healthy.Count == 0)
                return null;

            return settings.SelectionMode switch
            {
                GoidaSelectionMode.ManualFixed => healthy.FirstOrDefault(node =>
                    string.Equals(node.Id, settings.PinnedNodeId, StringComparison.OrdinalIgnoreCase)),
                GoidaSelectionMode.ManualPool => SelectFromPool(settings, healthy, currentActiveNodeId),
                _ => healthy[0]
            };
        }

        public static GoidaNode? SelectNextFailoverNode(
            GoidaProfileSettings settings,
            IReadOnlyList<GoidaNode> nodes,
            string? currentActiveNodeId,
            ISet<string> excludeIds)
        {
            if (settings.SelectionMode == GoidaSelectionMode.ManualPool
                && settings.ManualPoolNodeIds?.Count > 0)
            {
                return SelectNextInOrderedPool(settings, nodes, currentActiveNodeId, excludeIds);
            }

            return SelectNextInAutoRoundRobin(settings, nodes, currentActiveNodeId, excludeIds);
        }

        public static bool ShouldAutoSwitch(
            GoidaProfileSettings settings,
            GoidaNode? currentNode,
            GoidaNode? bestNode)
        {
            if (!settings.Enabled || !settings.AutoSwitchOnFly)
                return false;

            if (settings.SelectionMode != GoidaSelectionMode.AutoBest
                && settings.SelectionMode != GoidaSelectionMode.ManualPool)
                return false;

            if (bestNode == null)
                return false;

            if (currentNode == null)
                return true;

            if (string.Equals(currentNode.Id, bestNode.Id, StringComparison.OrdinalIgnoreCase))
                return false;

            if (currentNode.Status is GoidaNodeStatus.Timeout or GoidaNodeStatus.Error)
                return true;

            if (currentNode.LatencyMs < 0 || currentNode.LatencyMs > settings.AutoSwitchLatencyMs)
                return bestNode.LatencyMs >= 0 && bestNode.LatencyMs < currentNode.LatencyMs;

            return false;
        }

        private static IEnumerable<GoidaNode> FilterCandidates(
            GoidaProfileSettings settings,
            IReadOnlyList<GoidaNode> nodes)
        {
            HashSet<int> enabledLists = settings.EnabledListIds?.ToHashSet() ?? new HashSet<int>();
            IEnumerable<GoidaNode> filtered = nodes.Where(node => enabledLists.Contains(node.ListId));

            if (settings.SelectionMode == GoidaSelectionMode.ManualFixed
                && !string.IsNullOrWhiteSpace(settings.PinnedNodeId))
            {
                filtered = filtered.Where(node =>
                    string.Equals(node.Id, settings.PinnedNodeId, StringComparison.OrdinalIgnoreCase));
            }
            else if (settings.SelectionMode == GoidaSelectionMode.ManualPool
                && settings.ManualPoolNodeIds?.Count > 0)
            {
                HashSet<string> pool = settings.ManualPoolNodeIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(node => pool.Contains(node.Id));
            }

            return filtered;
        }

        private static GoidaNode? SelectFromPool(
            GoidaProfileSettings settings,
            List<GoidaNode> healthy,
            string? currentActiveNodeId)
        {
            if (healthy.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(currentActiveNodeId))
            {
                GoidaNode? current = healthy.FirstOrDefault(node =>
                    string.Equals(node.Id, currentActiveNodeId, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                    return current;
            }

            if (settings.ManualPoolNodeIds?.Count > 0)
            {
                Dictionary<string, GoidaNode> healthyById = healthy
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
                foreach (string id in settings.ManualPoolNodeIds)
                {
                    if (healthyById.TryGetValue(id, out GoidaNode? node))
                        return node;
                }
            }

            return healthy[0];
        }

        private static GoidaNode? SelectNextInAutoRoundRobin(
            GoidaProfileSettings settings,
            IReadOnlyList<GoidaNode> nodes,
            string? currentActiveNodeId,
            ISet<string> excludeIds)
        {
            List<GoidaNode> sorted = FilterCandidates(settings, nodes)
                .Where(node => !excludeIds.Contains(node.Id))
                .Where(node => node.Status is not GoidaNodeStatus.Error and not GoidaNodeStatus.Timeout)
                .OrderBy(node => node.Status == GoidaNodeStatus.Ok ? 0 : 1)
                .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                .ThenBy(node => node.ListId)
                .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sorted.Count == 0)
                return null;

            int startIndex = 0;
            if (!string.IsNullOrWhiteSpace(currentActiveNodeId))
            {
                int currentIndex = sorted.FindIndex(node =>
                    string.Equals(node.Id, currentActiveNodeId, StringComparison.OrdinalIgnoreCase));
                if (currentIndex >= 0)
                    startIndex = currentIndex + 1;
            }

            for (int offset = 0; offset < sorted.Count; offset++)
            {
                GoidaNode candidate = sorted[(startIndex + offset) % sorted.Count];
                if (string.Equals(candidate.Id, currentActiveNodeId, StringComparison.OrdinalIgnoreCase))
                    continue;
                return candidate;
            }

            return null;
        }

        private static GoidaNode? SelectNextInOrderedPool(
            GoidaProfileSettings settings,
            IReadOnlyList<GoidaNode> nodes,
            string? currentActiveNodeId,
            ISet<string> excludeIds)
        {
            List<string> pool = settings.ManualPoolNodeIds!
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            if (pool.Count == 0)
                return null;

            Dictionary<string, GoidaNode> byId = nodes
                .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

            int startIndex = 0;
            if (!string.IsNullOrWhiteSpace(currentActiveNodeId))
            {
                int currentIndex = pool.FindIndex(id =>
                    string.Equals(id, currentActiveNodeId, StringComparison.OrdinalIgnoreCase));
                if (currentIndex >= 0)
                    startIndex = currentIndex + 1;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                bool allowUnknown = pass == 1;
                for (int offset = 0; offset < pool.Count; offset++)
                {
                    string id = pool[(startIndex + offset) % pool.Count];
                    if (excludeIds.Contains(id))
                        continue;

                    if (!byId.TryGetValue(id, out GoidaNode? node))
                        continue;

                    if (node.Status is GoidaNodeStatus.Error or GoidaNodeStatus.Timeout)
                        continue;

                    if (node.Status == GoidaNodeStatus.Ok)
                        return node;

                    if (allowUnknown && node.Status == GoidaNodeStatus.Unknown)
                        return node;
                }
            }

            return null;
        }
    }
}
