using System;
using System.Collections.Generic;

namespace InvisibleGorillaXRay.Handlers.SmartInput
{
    using Models;

    /// <summary>
    /// Result of applying a <see cref="PastedInputResult"/> to the app: how many
    /// servers/subscriptions/bridges were imported and any per-item failures.
    /// </summary>
    public class SmartImportOutcome
    {
        public int ServersAdded;
        public int SubscriptionsAdded;
        public int BridgesAdded;
        public int Failures;
        public bool BridgesUpdated;
        public BridgeType BridgeType = BridgeType.NONE;

        public int TotalAdded => ServersAdded + SubscriptionsAdded + BridgesAdded;
        public bool AnyAdded => TotalAdded > 0;
    }

    /// <summary>
    /// Applies a classified paste blob through the platform-supplied delegates.
    /// Keeps the routing logic identical across Windows / Mac / Android / Linux.
    /// </summary>
    public static class SmartImportService
    {
        public static SmartImportOutcome Apply(
            PastedInputResult classified,
            Func<string, Status> convertLinkToConfig,
            Action<string, string> onCreateConfig,
            Func<string, string, Status> convertLinkToSubscription,
            Action<string, string, string> onCreateSubscription,
            Func<List<string>, BridgeType, bool> onAddBridges)
        {
            var outcome = new SmartImportOutcome();
            if (classified == null)
                return outcome;

            foreach (string link in classified.ServerLinks)
            {
                try
                {
                    Status status = convertLinkToConfig?.Invoke(link);
                    if (status == null || status.Code == Code.ERROR)
                    {
                        outcome.Failures++;
                        continue;
                    }

                    if (status.Content is string[] config && config.Length >= 2)
                    {
                        onCreateConfig?.Invoke(config[0], config[1]);
                        outcome.ServersAdded++;
                    }
                    else
                    {
                        outcome.Failures++;
                    }
                }
                catch
                {
                    outcome.Failures++;
                }
            }

            int subIndex = 1;
            foreach (string url in classified.SubscriptionLinks)
            {
                try
                {
                    string remark = PastedInputClassifier.SuggestSubscriptionRemark(url, subIndex++);
                    Status status = convertLinkToSubscription?.Invoke(remark, url);
                    if (status == null || status.Code == Code.ERROR)
                    {
                        outcome.Failures++;
                        continue;
                    }

                    if (status.Content is string[] sub && sub.Length >= 2)
                    {
                        onCreateSubscription?.Invoke(sub[0], url, sub[1]);
                        outcome.SubscriptionsAdded++;
                    }
                    else
                    {
                        outcome.Failures++;
                    }
                }
                catch
                {
                    outcome.Failures++;
                }
            }

            if (classified.HasBridges && onAddBridges != null)
            {
                try
                {
                    bool ok = onAddBridges.Invoke(classified.BridgeLines, classified.DetectedBridgeType);
                    if (ok)
                    {
                        outcome.BridgesAdded = classified.BridgeLines.Count;
                        outcome.BridgesUpdated = true;
                        outcome.BridgeType = classified.DetectedBridgeType;
                    }
                    else
                    {
                        outcome.Failures++;
                    }
                }
                catch
                {
                    outcome.Failures++;
                }
            }

            return outcome;
        }

        /// <summary>
        /// Merge new bridge lines into existing Tor settings (dedup, trim) and set
        /// the bridge family + enable Tor so a pasted bridge "just works".
        /// Returns the updated settings object; caller persists it.
        /// </summary>
        public static TorSettings MergeBridges(TorSettings current, List<string> newLines, BridgeType bridgeType)
        {
            TorSettings tor = current?.Clone() ?? new TorSettings();

            var merged = new List<string>(tor.GetBridgeLines());
            var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);

            if (newLines != null)
            {
                foreach (string raw in newLines)
                {
                    string line = raw?.Trim();
                    if (string.IsNullOrEmpty(line))
                        continue;
                    if (seen.Add(line))
                        merged.Add(line);
                }
            }

            tor.BridgeLines = merged;
            if (bridgeType != BridgeType.NONE)
                tor.BridgeType = bridgeType;
            tor.Enabled = true;

            return tor;
        }
    }
}
