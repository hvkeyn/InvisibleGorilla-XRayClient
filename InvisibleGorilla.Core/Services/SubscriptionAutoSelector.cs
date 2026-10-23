using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InvisibleGorillaXRay.Services
{
    using Models;

    /// <summary>
    /// Shared automatic node pick: every platform filters the subscription by the user's
    /// scope (any/foreign/RU) and picks the reachable node with the lowest latency.
    /// </summary>
    public static class SubscriptionAutoSelector
    {
        public static bool TryPick(string currentPath, UserSettings settings, out string best)
        {
            best = null;

            try
            {
                if (settings == null
                    || !settings.GetAutoSelectBest()
                    || string.IsNullOrWhiteSpace(currentPath)
                    || !File.Exists(currentPath))
                    return false;

                string directory = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "info.dat")))
                    return false;

                int scope = settings.GetServerScope();
                List<string> candidates = Directory.GetFiles(directory, "*.json")
                    .Where(path => ServerScope.Matches(Path.GetFileNameWithoutExtension(path), scope))
                    .ToList();

                if (candidates.Count < 2)
                    return false;

                string pick = BestNodeSelector.Pick(candidates);
                if (string.IsNullOrWhiteSpace(pick)
                    || string.Equals(pick, currentPath, StringComparison.OrdinalIgnoreCase))
                    return false;

                best = pick;
                return true;
            }
            catch (Exception ex)
            {
                InvisibleGorillaXRay.Core.DiagnosticLog.WriteException("AutoSelect.Pick", ex);
                return false;
            }
        }
    }
}