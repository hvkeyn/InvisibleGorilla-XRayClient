using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaProfileManager : IDisposable
    {
        public const int MaxBackgroundProbeBatch = 32;
        public const int MaxManualProbeBatch = 512;
        public const int MaxProbeBatch = MaxManualProbeBatch;

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

        private Func<GoidaProfileSettings> getSettings;
        private Action<GoidaProfileSettings> saveSettings;
        private Action<GoidaNode>? onActiveNodeChanged;
        private Func<bool>? pauseNativeForTest;
        private Action? resumeNativeAfterTest;
        private Func<bool>? isVpnSessionActive;

        private CancellationTokenSource? loopCts;
        private Task? backgroundTask;
        private int refreshInProgress;
        private int backgroundProbeInProgress;
        private int manualProbeInProgress;

        // Nodes that recently failed a real tunnel check; excluded from failover
        // for a cooldown so a flapping node isn't re-picked immediately.
        private readonly Dictionary<string, DateTime> recentTunnelFailures = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan TunnelFailureCooldown = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan FailoverDebounce = TimeSpan.FromSeconds(3);
        private DateTime lastFailoverUtc = DateTime.MinValue;

        public event Action? NodesUpdated;
        public event Action<string>? StatusMessage;
        public event Action<GoidaProbeProgress>? ProbeProgress;

        public IReadOnlyList<GoidaListMeta> Lists => GoidaSourceCatalog.AllLists;

        public int CountManualProbeTargets()
        {
            GoidaProfileSettings settings = getSettings().Clone();
            return FilterProbeTargets(settings, store.GetNodes(), manual: true).Count();
        }

        public void Setup(
            Func<string, Status> convertConfigLinkToV2Ray,
            Func<string, int> testConnection,
            Func<GoidaProfileSettings> getSettings,
            Action<GoidaProfileSettings> saveSettings,
            Action<GoidaNode>? onActiveNodeChanged = null,
            Func<bool>? pauseNativeForTest = null,
            Action? resumeNativeAfterTest = null,
            Func<bool>? isVpnSessionActive = null)
        {
            this.getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            this.saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
            this.onActiveNodeChanged = onActiveNodeChanged;
            this.pauseNativeForTest = pauseNativeForTest;
            this.resumeNativeAfterTest = resumeNativeAfterTest;
            this.isVpnSessionActive = isVpnSessionActive;
            parser = new GoidaNodeParser(convertConfigLinkToV2Ray);
            monitor = new GoidaHealthMonitor(store, testConnection);
            monitor.NodesUpdated += () => NodesUpdated?.Invoke();
            monitor.ProbeProgress += progress => ProbeProgress?.Invoke(progress);
            store.EnsureDirectories();
            store.Load();
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

                List<int> fetchListIds = GetVpnListIds(settings);
                if (fetchListIds.Count == 0)
                {
                    StatusMessage?.Invoke("refresh-no-vpn-lists");
                    return;
                }

                IReadOnlyDictionary<int, string> listData = await fetcher
                    .FetchListsAsync(fetchListIds, cancellationToken)
                    .ConfigureAwait(false);

                HashSet<int> refreshedListIds = fetchListIds.ToHashSet();
                Dictionary<string, GoidaNode> mergedById = store.GetNodes()
                    .Where(node => !refreshedListIds.Contains(node.ListId))
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

                Dictionary<string, GoidaNode> existing = store.GetNodes()
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<int, string> pair in listData.OrderBy(entry => entry.Key))
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(pair.Value))
                            continue;

                        List<GoidaNode> parsed = parser.ParseList(pair.Key, pair.Value, store.NodesDirectory);
                        foreach (GoidaNode node in parsed)
                        {
                            if (existing.TryGetValue(node.Id, out GoidaNode? previous))
                            {
                                node.LatencyMs = previous.LatencyMs;
                                node.Status = previous.Status;
                                node.LastCheckedUtc = previous.LastCheckedUtc;
                            }

                            mergedById[node.Id] = node;
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException($"Goida.RefreshList.{pair.Key}", ex);
                    }
                }

                store.ReplaceNodes(mergedById.Values.ToList());
                TryEnsureActiveNode();

                GoidaProfileSettings latest = getSettings().Clone();
                latest.LastRefreshUtc = DateTime.UtcNow;
                saveSettings(latest);

                StatusMessage?.Invoke("refresh-complete");
                NodesUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Goida.RefreshListsAsync", ex);
                StatusMessage?.Invoke("refresh-failed");
            }
            finally
            {
                Interlocked.Exchange(ref refreshInProgress, 0);
            }
        }

        public async Task<GoidaProbeResult> ProbeAsync(
            CancellationToken cancellationToken = default,
            bool manual = false)
        {
            if (manual)
            {
                if (Interlocked.CompareExchange(ref manualProbeInProgress, 1, 0) != 0)
                    return new GoidaProbeResult();

                try
                {
                    return await ProbeAsyncCore(cancellationToken, manual: true).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref manualProbeInProgress, 0);
                }
            }

            if (Interlocked.CompareExchange(ref backgroundProbeInProgress, 1, 0) != 0)
                return new GoidaProbeResult();

            if (isVpnSessionActive?.Invoke() == true)
            {
                Interlocked.Exchange(ref backgroundProbeInProgress, 0);
                return new GoidaProbeResult();
            }

            try
            {
                return await ProbeAsyncCore(cancellationToken, manual: false).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref backgroundProbeInProgress, 0);
            }
        }

        private async Task<GoidaProbeResult> ProbeAsyncCore(CancellationToken cancellationToken, bool manual)
        {
            try
            {
                GoidaProfileSettings settings = getSettings().Clone();
                List<GoidaNode> targets = FilterProbeTargets(settings, store.GetNodes(), manual).ToList();

                GoidaProbeResult result = manual
                    ? await monitor.ProbeNodesFastAsync(targets, cancellationToken).ConfigureAwait(false)
                    : await monitor.ProbeNodesFastAsync(
                        targets.Take(MaxBackgroundProbeBatch).ToList(),
                        cancellationToken).ConfigureAwait(false);

                // A TCP connect only proves the endpoint is reachable, not that VLESS
                // actually works. Verify the fastest candidates with a real xray test
                // so dead "3 ms OK" nodes don't end up at the top and get auto-picked.
                if (manual && !result.Cancelled && result.Ok > 0 && isVpnSessionActive?.Invoke() != true)
                    await VerifyTopNodesAsync(cancellationToken).ConfigureAwait(false);

                if (!result.Cancelled && result.Ok > 0 && settings.AutoSwitchOnFly)
                    await EvaluateAutoSwitchAsync(settings).ConfigureAwait(false);

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new GoidaProbeResult { Cancelled = true };
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Goida.ProbeAsync", ex);
                return new GoidaProbeResult();
            }
        }

        /// <summary>
        /// Called when the running tunnel is detected dead (exit IP check failed or
        /// shows the real IP). Marks the active node as failed and switches to the
        /// next best candidate. Returns true if a switch was initiated.
        /// </summary>
        public bool ReportTunnelFailure()
        {
            GoidaProfileSettings settings = getSettings().Clone();

            if (!settings.AutoSwitchOnFly
                || settings.SelectionMode == GoidaSelectionMode.ManualFixed)
            {
                return false;
            }

            // Debounce: a rerun takes a few seconds; don't stack switches.
            if (DateTime.UtcNow - lastFailoverUtc < FailoverDebounce)
                return false;

            string activeId = settings.ActiveNodeId ?? string.Empty;
            HashSet<string> failedIds;
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                store.UpdateNodeStatus(activeId, Values.Availability.ERROR, GoidaNodeStatus.Error);
                lock (recentTunnelFailures)
                    recentTunnelFailures[activeId] = DateTime.UtcNow;
            }

            failedIds = GetRecentFailedNodeIds();

            GoidaNode? next = GoidaActiveSelector.SelectNextFailoverNode(
                settings,
                store.GetNodes(),
                activeId,
                failedIds);

            if (next == null || string.Equals(next.Id, activeId, StringComparison.OrdinalIgnoreCase))
            {
                // Every node in the pool was tried — clear the short cooldown and
                // walk the list again from the top.
                if (settings.SelectionMode == GoidaSelectionMode.ManualPool)
                {
                    lock (recentTunnelFailures)
                        recentTunnelFailures.Clear();

                    next = GoidaActiveSelector.SelectNextFailoverNode(
                        settings,
                        store.GetNodes(),
                        activeId,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                if (next == null || string.Equals(next.Id, activeId, StringComparison.OrdinalIgnoreCase))
                {
                    NodesUpdated?.Invoke();
                    return false;
                }
            }

            lastFailoverUtc = DateTime.UtcNow;
            DiagnosticLog.Write("Goida.ReportTunnelFailure", $"Switching {activeId} -> {next.Id} ({next.DisplayName})");
            SetActiveNode(next.Id, notifyExternal: true);
            return true;
        }

        private HashSet<string> GetRecentFailedNodeIds()
        {
            DateTime cutoff = DateTime.UtcNow - TunnelFailureCooldown;
            lock (recentTunnelFailures)
            {
                List<string> expired = recentTunnelFailures
                    .Where(pair => pair.Value < cutoff)
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (string id in expired)
                    recentTunnelFailures.Remove(id);

                return recentTunnelFailures.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        private async Task VerifyTopNodesAsync(CancellationToken cancellationToken)
        {
            try
            {
                GoidaProfileSettings settings = getSettings().Clone();
                List<GoidaNode> top = FilterProbeTargets(settings, store.GetNodes(), manual: true)
                    .Where(node => node.Status == GoidaNodeStatus.Ok && node.LatencyMs >= 0)
                    .OrderBy(node => node.LatencyMs)
                    .Take(GoidaProfileSettings.MaxVerifiedNodes)
                    .ToList();

                if (top.Count == 0)
                    return;

                StatusMessage?.Invoke("verify-start");
                await monitor.ProbeNodesAsync(top, cancellationToken).ConfigureAwait(false);
                StatusMessage?.Invoke("verify-complete");
                NodesUpdated?.Invoke();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Goida.VerifyTopNodes", ex);
            }
        }

        public void SetActiveNode(string nodeId, bool persistOnly = false, bool notifyExternal = true)
        {
            GoidaNode? node = store.FindById(nodeId);
            if (node == null)
                return;

            GoidaProfileSettings settings = getSettings().Clone();
            settings.ActiveNodeId = node.Id;
            saveSettings(settings);
            if (notifyExternal && !persistOnly)
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
            bool manual = false)
        {
            int batchLimit = manual ? MaxManualProbeBatch : MaxBackgroundProbeBatch;
            HashSet<int> enabledLists = GetEnabledListSet(settings);
            IEnumerable<GoidaNode> filtered = nodes
                .Where(node => enabledLists.Contains(node.ListId));

            // Manual "Probe all" always scans every enabled list — pool/pinned
            // filters apply only to background probes and auto-switch selection.
            if (!manual)
            {
                if (settings.SelectionMode == GoidaSelectionMode.ManualFixed
                    && !string.IsNullOrWhiteSpace(settings.PinnedNodeId))
                {
                    return filtered
                        .Where(node => string.Equals(node.Id, settings.PinnedNodeId, StringComparison.OrdinalIgnoreCase))
                        .Take(batchLimit);
                }

                if (settings.SelectionMode == GoidaSelectionMode.ManualPool
                    && settings.ManualPoolNodeIds?.Count > 0)
                {
                    HashSet<string> pool = settings.ManualPoolNodeIds
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return filtered
                        .Where(node => pool.Contains(node.Id))
                        .Take(batchLimit);
                }
            }

            List<GoidaNode> materialized = filtered.ToList();
            string? activeId = settings.ActiveNodeId;
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                GoidaNode? active = materialized.FirstOrDefault(node =>
                    string.Equals(node.Id, activeId, StringComparison.OrdinalIgnoreCase));
                if (active != null && !manual)
                {
                    return materialized
                        .OrderByDescending(node => string.Equals(node.Id, activeId, StringComparison.OrdinalIgnoreCase))
                        .Take(Math.Min(materialized.Count, batchLimit));
                }
            }

            return SelectProbeRoundRobin(materialized, batchLimit);
        }

        private static IEnumerable<GoidaNode> SelectProbeRoundRobin(
            IReadOnlyList<GoidaNode> nodes,
            int maxCount)
        {
            if (nodes.Count == 0 || maxCount <= 0)
                yield break;

            List<List<GoidaNode>> byList = nodes
                .GroupBy(node => node.ListId)
                .OrderBy(group => group.Key)
                .Select(group => group
                    .OrderBy(node => node.LastCheckedUtc == default ? 0 : 1)
                    .ThenBy(node => node.Status == GoidaNodeStatus.Unknown ? 0 : 1)
                    .ThenBy(node => node.LatencyMs < 0 ? 0 : 1)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList())
                .ToList();

            int picked = 0;
            int index = 0;
            while (picked < maxCount && byList.Count > 0)
            {
                bool addedInRound = false;
                foreach (List<GoidaNode> listNodes in byList)
                {
                    if (index >= listNodes.Count)
                        continue;

                    yield return listNodes[index];
                    picked++;
                    addedInRound = true;
                    if (picked >= maxCount)
                        yield break;
                }

                if (!addedInRound)
                    yield break;

                index++;
            }
        }

        private async Task BackgroundLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                GoidaProfileSettings settings = getSettings().Clone();
                if (ShouldRunBackground(settings) && settings.LastRefreshUtc == default)
                    await RefreshListsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Goida.BackgroundLoop.InitialRefresh", ex);
            }

            DateTime lastRefresh = DateTime.UtcNow;
            DateTime lastProbe = DateTime.UtcNow;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    GoidaProfileSettings settings = getSettings().Clone();
                    if (!ShouldRunBackground(settings))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // While the VPN session is live, switch to a lightweight watchdog:
                    // ping the active endpoint every 5s and fail over quickly when it dies.
                    if (isVpnSessionActive?.Invoke() == true)
                    {
                        WatchActiveNode(settings);
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
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("Goida.BackgroundLoop", ex);
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

        private int activeNodeFailStreak;

        /// <summary>
        /// Cheap liveness check of the active node while the VPN session is running.
        /// Two consecutive TCP failures trigger a failover to the next best node.
        /// </summary>
        private void WatchActiveNode(GoidaProfileSettings settings)
        {
            try
            {
                if (!settings.AutoSwitchOnFly
                    || settings.SelectionMode == GoidaSelectionMode.ManualFixed
                    || string.IsNullOrWhiteSpace(settings.ActiveNodeId))
                {
                    activeNodeFailStreak = 0;
                    return;
                }

                GoidaNode? active = store.FindById(settings.ActiveNodeId);
                if (active == null)
                    return;

                int latency = GoidaEndpointProbe.ProbeTcp(active.Endpoint, 1500);
                if (latency >= 0)
                {
                    activeNodeFailStreak = 0;
                    // TCP reachability does not mean VLESS works; promoting back to
                    // Ok here was undoing tunnel-failure marks and blocking failover.
                    return;
                }

                activeNodeFailStreak++;
                if (activeNodeFailStreak < 2)
                    return;

                activeNodeFailStreak = 0;
                DiagnosticLog.Write("Goida.WatchActiveNode",
                    $"Active node {active.Id} unreachable twice in a row, failing over");
                ReportTunnelFailure();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Goida.WatchActiveNode", ex);
            }
        }

        private bool ShouldRunBackground(GoidaProfileSettings settings)
        {
            return settings.EnabledListIds?.Count > 0
                || !string.IsNullOrWhiteSpace(settings.ActiveNodeId)
                || settings.LastRefreshUtc != default
                || store.GetNodes().Count > 0;
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
