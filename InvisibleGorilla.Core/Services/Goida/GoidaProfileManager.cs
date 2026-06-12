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
        public const int MaxManualProbeBatch = 512;

        public static List<int> GetVpnListIds(GoidaProfileSettings settings)
        {
            return settings.EnabledListIds?
                .Where(id => id >= 1 && id <= 26)
                .Distinct()
                .OrderBy(id => id)
                .ToList() ?? Enumerable.Range(1, 25).ToList();
        }

        public static bool HasVpnListsEnabled(GoidaProfileSettings settings)
        {
            return GetVpnListIds(settings).Count > 0;
        }

        public static HashSet<int> GetEnabledListSet(GoidaProfileSettings settings)
        {
            return GetVpnListIds(settings).ToHashSet();
        }
        private readonly GoidaNodeStore store = new();
        private readonly GoidaFetcher fetcher = new();
        private GoidaNodeParser parser;
        private GoidaHealthMonitor monitor;
        private GoidaOperationLog? operationLog;

        private Func<GoidaProfileSettings> getSettings;
        private Action<GoidaProfileSettings> saveSettings;
        private Action<GoidaNode>? onActiveNodeChanged;
        private Func<bool>? pauseNativeForTest;
        private Action? resumeNativeAfterTest;
        private Func<bool> canProbe = () => true;

        private CancellationTokenSource? loopCts;
        private Task? backgroundTask;
        private int refreshInProgress;
        private int probeInProgress;

        // When the VPN tunnel is active we must never run a native probe (TestConnection):
        // the probe and the tunnel share the same native xray core, and running both at
        // once crashes the Android process. ProbingSuspended hard-blocks every probe path
        // (manual + background) and CancelActiveProbe aborts an in-flight one.
        private volatile bool probingSuspended;
        private CancellationTokenSource? activeProbeCts;
        private readonly object probeGuard = new();

        public bool ProbingSuspended => probingSuspended;

        public event Action? NodesUpdated;
        public event Action<string>? StatusMessage;
        public event Action<GoidaProbeProgress>? ProbeProgress;

        public IReadOnlyList<GoidaListMeta> Lists => GoidaSourceCatalog.AllLists;

        public int CountManualProbeTargets()
        {
            GoidaProfileSettings settings = getSettings().Clone();
            return FilterProbeTargets(settings, store.GetNodes(), manual: true).Count();
        }

        public int CountVerifyTargets(bool manual = true)
        {
            GoidaProfileSettings settings = getSettings().Clone();
            int limit = manual ? GoidaProfileSettings.MaxVerifiedNodes : MaxProbeBatch;
            return FilterProbeTargets(settings, store.GetNodes(), manual)
                .Take(limit)
                .Count();
        }

        public IReadOnlyList<GoidaNode> GetVisibleNodes()
        {
            GoidaProfileSettings settings = getSettings().Clone();
            HashSet<int> enabledLists = GetEnabledListSet(settings);

            return store.GetNodes()
                .Where(node => enabledLists.Contains(node.ListId))
                .ToList();
        }

        public int CountVisibleNodes()
        {
            GoidaProfileSettings settings = getSettings().Clone();
            return store.CountNodesForLists(GetVpnListIds(settings));
        }

        public void Setup(
            Func<string, Status> convertConfigLinkToV2Ray,
            Func<string, int> testConnection,
            Func<GoidaProfileSettings> getSettings,
            Action<GoidaProfileSettings> saveSettings,
            Action<GoidaNode>? onActiveNodeChanged = null,
            Func<bool>? pauseNativeForTest = null,
            Action? resumeNativeAfterTest = null,
            Func<bool>? canProbe = null)
        {
            this.getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            this.saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
            this.onActiveNodeChanged = onActiveNodeChanged;
            this.pauseNativeForTest = pauseNativeForTest;
            this.resumeNativeAfterTest = resumeNativeAfterTest;
            this.canProbe = canProbe ?? (() => true);
            parser = new GoidaNodeParser(convertConfigLinkToV2Ray);
            monitor = new GoidaHealthMonitor(store, testConnection);
            monitor.NodesUpdated += () => NodesUpdated?.Invoke();
            monitor.ProbeProgress += progress => ProbeProgress?.Invoke(progress);
            store.EnsureDirectories();
            store.Load();
            operationLog = new GoidaOperationLog(
                System.IO.Path.Combine(store.ProfileDirectory, "goida-operations.json"));
        }

        public IReadOnlyList<GoidaOperationEntry> GetRecentOperations(int count = 20)
        {
            return operationLog?.GetRecent(count) ?? new List<GoidaOperationEntry>();
        }

        public void LogOperation(string message)
        {
            operationLog?.Add(message);
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
            return GetVisibleNodes()
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

            foreach (GoidaNode node in GetVisibleNodes())
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
                                node.VlessVerified = previous.VlessVerified;
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

                operationLog?.Add($"Refresh: {merged.Count} nodes");
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

        public void SuspendProbing()
        {
            probingSuspended = true;
            CancelActiveProbe();
        }

        public void ResumeProbing()
        {
            probingSuspended = false;
        }

        public void CancelActiveProbe()
        {
            lock (probeGuard)
            {
                try { activeProbeCts?.Cancel(); }
                catch { }
            }
        }

        public async Task WaitForProbeIdleAsync(int timeoutMs = 9000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Interlocked.CompareExchange(ref probeInProgress, 0, 0) != 0)
            {
                if (DateTime.UtcNow >= deadline)
                    return;

                try { await Task.Delay(50).ConfigureAwait(false); }
                catch { return; }
            }
        }

        public async Task<GoidaProbeResult> ProbeAsync(
            CancellationToken cancellationToken = default,
            bool manual = false)
        {
            if (probingSuspended)
                return new GoidaProbeResult();

            if (Interlocked.CompareExchange(ref probeInProgress, 1, 0) != 0)
                return new GoidaProbeResult();

            bool pausedNative = false;
            CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (probeGuard)
                activeProbeCts = linkedCts;
            CancellationToken probeToken = linkedCts.Token;
            try
            {
                if (probingSuspended)
                    return new GoidaProbeResult { Cancelled = true };

                GoidaProfileSettings settings = getSettings().Clone();
                List<GoidaNode> targets = FilterProbeTargets(settings, store.GetNodes(), manual).ToList();
                if (targets.Count == 0)
                    return new GoidaProbeResult();

                GoidaTcpVlessProbeOptions probeOptions = new()
                {
                    MaxVlessTests = manual
                        ? GoidaProfileSettings.MaxVerifiedNodes
                        : Math.Min(GoidaProfileSettings.MaxVerifiedNodes, MaxProbeBatch),
                    EarlyStopOkCount = manual
                        ? GoidaProfileSettings.DefaultAutoPoolSize
                        : Math.Min(5, MaxProbeBatch),
                    MaxTcpLatencyForVlessMs = settings.AutoSwitchLatencyMs,
                    OnFirstVlessOk = () => _ = EvaluateAutoSwitchAsync(getSettings().Clone())
                };

                pausedNative = pauseNativeForTest?.Invoke() ?? false;

                GoidaProbeResult result;
                store.BeginBulkUpdate();
                try
                {
                    result = await monitor.ProbeTcpThenVlessAsync(
                        targets,
                        probeOptions,
                        probeToken).ConfigureAwait(false);
                }
                finally
                {
                    store.EndBulkUpdate();
                }

                if (!result.Cancelled && result.Ok > 0 && !probingSuspended)
                    await EvaluateAutoSwitchAsync(settings).ConfigureAwait(false);

                if (manual && !result.Cancelled)
                    operationLog?.Add(
                        $"Probe: {result.Ok} ok, {result.Timeout} timeout, {result.Error} error");

                return result;
            }
            catch (OperationCanceledException)
            {
                return new GoidaProbeResult { Cancelled = true };
            }
            catch
            {
                return new GoidaProbeResult();
            }
            finally
            {
                if (pausedNative)
                {
                    try { resumeNativeAfterTest?.Invoke(); } catch { }
                }

                lock (probeGuard)
                {
                    if (ReferenceEquals(activeProbeCts, linkedCts))
                        activeProbeCts = null;
                }
                linkedCts.Dispose();
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
            operationLog?.Add($"Active node: {node.DisplayName}");
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
            IReadOnlyList<GoidaNode> nodes,
            bool manual)
        {
            HashSet<int> enabledLists = settings.EnabledListIds?.Count > 0
                ? settings.EnabledListIds.ToHashSet()
                : Enumerable.Range(1, 26).ToHashSet();
            IEnumerable<GoidaNode> filtered = nodes
                .Where(node => enabledLists.Contains(node.ListId));

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
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(node => pool.Contains(node.Id));
            }

            if (manual)
                return filtered;

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

                    if (!probingSuspended
                        && canProbe()
                        && store.GetNodes().Count > 0
                        && DateTime.UtcNow - lastProbe >= probeInterval)
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
            operationLog?.Add($"Auto-switch: {best.DisplayName}");
            onActiveNodeChanged?.Invoke(best);
            NodesUpdated?.Invoke();
            return Task.CompletedTask;
        }
    }
}
