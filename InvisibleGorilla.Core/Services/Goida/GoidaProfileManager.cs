using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaProfileManager : IDisposable
    {
        public const int MaxProbeBatch = 16;
        private readonly GoidaNodeStore store = new();
        private readonly GoidaFetcher fetcher = new();
        private GoidaNodeParser parser;
        private GoidaHealthMonitor monitor;

        private Func<GoidaProfileSettings> getSettings;
        private Action<GoidaProfileSettings> saveSettings;
        private Action<GoidaNode>? onActiveNodeChanged;
        private Func<bool>? pauseNativeForTest;
        private Action? resumeNativeAfterTest;

        private CancellationTokenSource? loopCts;
        private Task? backgroundTask;
        private int refreshInProgress;
        private int probeInProgress;

        public event Action? NodesUpdated;
        public event Action<string>? StatusMessage;

        public IReadOnlyList<GoidaListMeta> Lists => GoidaSourceCatalog.AllLists;

        public void Setup(
            Func<string, Status> convertConfigLinkToV2Ray,
            Func<string, int> testConnection,
            Func<GoidaProfileSettings> getSettings,
            Action<GoidaProfileSettings> saveSettings,
            Action<GoidaNode>? onActiveNodeChanged = null,
            Func<bool>? pauseNativeForTest = null,
            Action? resumeNativeAfterTest = null)
        {
            this.getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            this.saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
            this.onActiveNodeChanged = onActiveNodeChanged;
            this.pauseNativeForTest = pauseNativeForTest;
            this.resumeNativeAfterTest = resumeNativeAfterTest;
            parser = new GoidaNodeParser(convertConfigLinkToV2Ray);
            monitor = new GoidaHealthMonitor(store, testConnection);
            monitor.NodesUpdated += () => NodesUpdated?.Invoke();
            store.EnsureDirectories();
            store.Load();
        }

        public void Start()
        {
            Stop();
            loopCts = new CancellationTokenSource();
            backgroundTask = Task.Run(() => BackgroundLoopAsync(loopCts.Token));
        }

        public void Stop()
        {
            loopCts?.Cancel();
            try
            {
                backgroundTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            loopCts?.Dispose();
            loopCts = null;
            backgroundTask = null;
        }

        public IReadOnlyList<GoidaNode> GetNodesSorted()
        {
            return store.GetNodes()
                .OrderBy(node => node.Status == GoidaNodeStatus.Ok ? 0 : 1)
                .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                .ThenBy(node => node.ListId)
                .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public (int Ok, int Timeout, int Error, int Unknown) GetProbeSummary()
        {
            int ok = 0;
            int timeout = 0;
            int error = 0;
            int unknown = 0;

            foreach (GoidaNode node in store.GetNodes())
            {
                switch (node.Status)
                {
                    case GoidaNodeStatus.Ok:
                        ok++;
                        break;
                    case GoidaNodeStatus.Timeout:
                        timeout++;
                        break;
                    case GoidaNodeStatus.Error:
                        error++;
                        break;
                    default:
                        unknown++;
                        break;
                }
            }

            return (ok, timeout, error, unknown);
        }

        public GoidaNode? GetActiveNode()
        {
            GoidaProfileSettings settings = getSettings().Clone();
            if (string.IsNullOrWhiteSpace(settings.ActiveNodeId))
                return null;

            return store.FindById(settings.ActiveNodeId);
        }

        public bool TryEnsureActiveNode()
        {
            GoidaProfileSettings settings = getSettings().Clone();
            if (!string.IsNullOrWhiteSpace(settings.ActiveNodeId)
                && store.FindById(settings.ActiveNodeId) != null)
            {
                return true;
            }

            GoidaNode? best = GoidaActiveSelector.SelectBestNode(
                settings,
                store.GetNodes(),
                settings.ActiveNodeId);
            if (best == null)
                return false;

            SetActiveNode(best.Id, persistOnly: true);
            return true;
        }

        public async Task RefreshListsAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref refreshInProgress, 1, 0) != 0)
                return;

            try
            {
                GoidaProfileSettings settings = getSettings().Clone();
                store.EnsureDirectories();

                IReadOnlyDictionary<int, string> listData = await fetcher
                    .FetchListsAsync(settings.EnabledListIds ?? Enumerable.Range(1, 26), cancellationToken)
                    .ConfigureAwait(false);

                List<GoidaNode> merged = new();
                Dictionary<string, GoidaNode> existing = store.GetNodes()
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<int, string> pair in listData.OrderBy(entry => entry.Key))
                {
                    try
                    {
                        List<GoidaNode> parsed = parser.ParseList(pair.Key, pair.Value, store.NodesDirectory);
                        foreach (GoidaNode node in parsed)
                        {
                            if (existing.TryGetValue(node.Id, out GoidaNode? previous))
                            {
                                node.LatencyMs = previous.LatencyMs;
                                node.Status = previous.Status;
                                node.LastCheckedUtc = previous.LastCheckedUtc;
                            }

                            merged.Add(node);
                        }
                    }
                    catch
                    {
                    }
                }

                store.ReplaceNodes(merged);

                // Re-fetch settings: the user may have changed them while lists downloaded.
                GoidaProfileSettings latest = getSettings().Clone();
                latest.LastRefreshUtc = DateTime.UtcNow;
                saveSettings(latest);

                StatusMessage?.Invoke("refresh-complete");
                NodesUpdated?.Invoke();
            }
            catch
            {
                StatusMessage?.Invoke("refresh-failed");
            }
            finally
            {
                Interlocked.Exchange(ref refreshInProgress, 0);
            }
        }

        public async Task ProbeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref probeInProgress, 1, 0) != 0)
                return;

            bool pausedNative = false;
            try
            {
                GoidaProfileSettings settings = getSettings().Clone();
                IEnumerable<GoidaNode> targets = FilterProbeTargets(settings, store.GetNodes());

                pausedNative = pauseNativeForTest?.Invoke() ?? false;
                await monitor.ProbeNodesAsync(targets, cancellationToken).ConfigureAwait(false);
                await EvaluateAutoSwitchAsync(settings).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                if (pausedNative)
                {
                    try { resumeNativeAfterTest?.Invoke(); } catch { }
                }

                Interlocked.Exchange(ref probeInProgress, 0);
            }
        }

        public void SetActiveNode(string nodeId, bool persistOnly = false)
        {
            GoidaNode? node = store.FindById(nodeId);
            if (node == null)
                return;

            GoidaProfileSettings settings = getSettings().Clone();
            settings.ActiveNodeId = node.Id;
            saveSettings(settings);
            onActiveNodeChanged?.Invoke(node);
            NodesUpdated?.Invoke();
        }

        public void UpdateSettings(GoidaProfileSettings settings)
        {
            saveSettings(settings.Clone());
            NodesUpdated?.Invoke();
        }

        public void Dispose()
        {
            Stop();
        }

        private IEnumerable<GoidaNode> FilterProbeTargets(
            GoidaProfileSettings settings,
            IReadOnlyList<GoidaNode> nodes)
        {
            HashSet<int> enabledLists = settings.EnabledListIds?.ToHashSet()
                ?? Enumerable.Range(1, 26).ToHashSet();
            IEnumerable<GoidaNode> filtered = nodes
                .Where(node => enabledLists.Contains(node.ListId));

            if (settings.SelectionMode == GoidaSelectionMode.ManualFixed
                && !string.IsNullOrWhiteSpace(settings.PinnedNodeId))
            {
                return filtered.Where(node =>
                    string.Equals(node.Id, settings.PinnedNodeId, StringComparison.OrdinalIgnoreCase));
            }

            if (settings.SelectionMode == GoidaSelectionMode.ManualPool
                && settings.ManualPoolNodeIds?.Count > 0)
            {
                HashSet<string> pool = settings.ManualPoolNodeIds
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return filtered.Where(node => pool.Contains(node.Id));
            }

            string? activeId = settings.ActiveNodeId;
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                GoidaNode? active = filtered.FirstOrDefault(node =>
                    string.Equals(node.Id, activeId, StringComparison.OrdinalIgnoreCase));
                if (active != null)
                {
                    return filtered
                        .OrderByDescending(node => string.Equals(node.Id, activeId, StringComparison.OrdinalIgnoreCase))
                        .Take(Math.Min(filtered.Count(), MaxProbeBatch));
                }
            }

            return filtered.Take(MaxProbeBatch);
        }

        private async Task BackgroundLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                GoidaProfileSettings settings = getSettings().Clone();
                if (settings.Enabled && settings.LastRefreshUtc == default)
                    await RefreshListsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }

            DateTime lastRefresh = DateTime.UtcNow;
            DateTime lastProbe = DateTime.UtcNow;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    GoidaProfileSettings settings = getSettings().Clone();
                    if (!settings.Enabled)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    TimeSpan refreshInterval = TimeSpan.FromMinutes(
                        Math.Max(1, settings.RefreshIntervalMinutes));
                    TimeSpan probeInterval = TimeSpan.FromSeconds(
                        Math.Max(15, settings.ProbeIntervalSeconds));

                    if (DateTime.UtcNow - lastRefresh >= refreshInterval)
                    {
                        await RefreshListsAsync(cancellationToken).ConfigureAwait(false);
                        lastRefresh = DateTime.UtcNow;
                    }

                    if (store.GetNodes().Count > 0 && DateTime.UtcNow - lastProbe >= probeInterval)
                    {
                        await ProbeAsync(cancellationToken).ConfigureAwait(false);
                        lastProbe = DateTime.UtcNow;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }

        private Task EvaluateAutoSwitchAsync(GoidaProfileSettings staleSnapshot)
        {
            // Always operate on the freshest settings: the snapshot was taken before
            // the probe ran and saving it back would clobber any list/mode/checkbox
            // changes the user made through the UI in the meantime.
            GoidaProfileSettings settings = getSettings().Clone();

            GoidaNode? current = string.IsNullOrWhiteSpace(settings.ActiveNodeId)
                ? null
                : store.FindById(settings.ActiveNodeId);
            GoidaNode? best = GoidaActiveSelector.SelectBestNode(
                settings,
                store.GetNodes(),
                settings.ActiveNodeId);

            if (!GoidaActiveSelector.ShouldAutoSwitch(settings, current, best) || best == null)
                return Task.CompletedTask;

            settings.ActiveNodeId = best.Id;
            saveSettings(settings);
            onActiveNodeChanged?.Invoke(best);
            NodesUpdated?.Invoke();
            return Task.CompletedTask;
        }
    }
}
