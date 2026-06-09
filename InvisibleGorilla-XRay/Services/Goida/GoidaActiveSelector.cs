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

        public static bool ShouldAutoSwitch(
            GoidaProfileSettings settings,
            GoidaNode? currentNode,
            GoidaNode? bestNode)
        {
            if (!settings.AutoSwitchOnFly)
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

            if (string.IsNullOrWhiteSpace(currentActiveNodeId))
                return healthy[0];

            GoidaNode? current = healthy.FirstOrDefault(node =>
                string.Equals(node.Id, currentActiveNodeId, StringComparison.OrdinalIgnoreCase));
            if (current != null && current.Status == GoidaNodeStatus.Ok)
                return current;

            return healthy[0];
        }
    }
}
