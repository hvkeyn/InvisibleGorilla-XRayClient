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
        public const int MaxBackgroundVerifyBatch = 25;

        public static List<int> GetVpnListIds(GoidaProfileSettings settings)
        {
            return settings.EnabledListIds?
                .Where(id => id >= 1 && id <= 26)
                .Distinct()
                .OrderBy(id => id)
                .ToList() ?? Enumerable.Range(1, 26).ToList();
        }

        public static bool HasVpnListsEnabled(GoidaProfileSettings settings)
        {
            return GetVpnListIds(settings).Count > 0;
        }

        public static int? ResolveActiveListFilter(string? filterText, GoidaProfileSettings settings)
        {
            string filter = filterText?.Trim() ?? string.Empty;
            if (int.TryParse(filter, out int listId) && listId >= 1 && listId <= 26)
                return listId;

            List<int> enabled = GetVpnListIds(settings);
            return enabled.Count == 1 ? enabled[0] : null;
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
        private static readonly TimeSpan FailoverDebounce = TimeSpan.FromSeconds(1);
        private DateTime lastFailoverUtc = DateTime.MinValue;

        public event Action? NodesUpdated;
        public event Action<string>? StatusMessage;
        public event Action<GoidaProbeProgress>? ProbeProgress;

        public IReadOnlyList<GoidaListMeta> Lists => GoidaSourceCatalog.AllLists;

        public int CountManualProbeTargets(int? listIdFilter = null)
        {
            GoidaProfileSettings settings = getSettings().Clone();
            return FilterProbeTargets(settings, store.GetNodes(), manual: true, listIdFilter).Count();
        }

        public int CountVerifyTargets(bool manual = true)
        {
            GoidaProfileSettings settings = getSettings().Clone();
            int limit = manual ? GoidaProfileSettings.MaxVerifiedNodes : MaxBackgroundVerifyBatch;
            return BuildVerifyTargetList(settings, limit).Count;
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

        public IReadOnlyList<GoidaNode> GetVisibleNodes(int? listIdFilter = null)
        {
            GoidaProfileSettings settings = getSettings().Clone();
            HashSet<int> enabledLists = GetEnabledListSet(settings);

            IEnumerable<GoidaNode> nodes = store.GetNodes()
                .Where(node => enabledLists.Contains(node.ListId));

            if (listIdFilter is int listId && listId >= 1 && listId <= 26)
                nodes = nodes.Where(node => node.ListId == listId);

            return nodes.ToList();
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

        public (int Ok, int Timeout, int Error, int Unknown) GetProbeSummary(int? listIdFilter = null)
        {
            int ok = 0;
            int timeout = 0;
            int error = 0;
            int unknown = 0;

            foreach (GoidaNode node in GetVisibleNodes(listIdFilter))
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
            if (manualProbeInProgress != 0 || backgroundProbeInProgress != 0)
                return;

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

                HashSet<int> enabledLists = GetEnabledListSet(settings);

                IReadOnlyDictionary<int, string> listData = await fetcher
                    .FetchListsAsync(fetchListIds, cancellationToken)
                    .ConfigureAwait(false);

                HashSet<int> refreshedListIds = fetchListIds.ToHashSet();
                Dictionary<string, GoidaNode> mergedById = store.GetNodes()
                    .Where(node => enabledLists.Contains(node.ListId)
                        && !refreshedListIds.Contains(node.ListId))
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

                Dictionary<string, GoidaNode> existing = store.GetNodes()
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, GoidaNode> existingByEndpoint = store.GetNodes()
                    .Where(node => !string.IsNullOrWhiteSpace(node.Endpoint))
                    .GroupBy(node => BuildEndpointKey(node.ListId, node.Endpoint),
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<int, string> pair in listData.OrderBy(entry => entry.Key))
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(pair.Value))
                            continue;

                        List<GoidaNode> parsed = parser.ParseList(pair.Key, pair.Value, store.NodesDirectory);
                        foreach (GoidaNode node in parsed)
                        {
                            GoidaNode? previous = null;
                            if (existing.TryGetValue(node.Id, out previous)
                                || existingByEndpoint.TryGetValue(
                                    BuildEndpointKey(node.ListId, node.Endpoint), out previous))
                            {
                                CopyProbeState(previous, node);
                            }

                            mergedById[node.Id] = node;
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException($"Goida.RefreshList.{pair.Key}", ex);
                    }
                }

                if (manualProbeInProgress != 0 || backgroundProbeInProgress != 0)
                {
                    DiagnosticLog.Write("Goida.RefreshListsAsync",
                        "Skipped ReplaceNodes because a probe is in progress");
                    StatusMessage?.Invoke("refresh-deferred");
                    return;
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
            bool manual = false,
            int? listIdFilter = null)
        {
            if (manual)
            {
                if (Interlocked.CompareExchange(ref manualProbeInProgress, 1, 0) != 0)
                    return new GoidaProbeResult();

                try
                {
                    return await ProbeAsyncCore(cancellationToken, manual: true, listIdFilter).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref manualProbeInProgress, 0);
                }
            }

            if (Interlocked.CompareExchange(ref backgroundProbeInProgress, 1, 0) != 0)
                return new GoidaProbeResult();

            if (manualProbeInProgress != 0)
            {
                Interlocked.Exchange(ref backgroundProbeInProgress, 0);
                return new GoidaProbeResult();
            }

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

        private async Task<GoidaProbeResult> ProbeAsyncCore(
            CancellationToken cancellationToken,
            bool manual,
            int? listIdFilter = null)
        {
            try
            {
                GoidaProfileSettings settings = getSettings().Clone();
                List<GoidaNode> targets = ResolveLiveProbeTargets(
                    FilterProbeTargets(settings, store.GetNodes(), manual, listIdFilter).ToList());

                int maxVless = manual ? GoidaProfileSettings.MaxVerifiedNodes : MaxBackgroundVerifyBatch;
                int earlyStopOk = manual
                    ? GoidaProfileSettings.DefaultAutoPoolSize
                    : Math.Min(5, MaxBackgroundVerifyBatch);

                bool paused = false;
                if (isVpnSessionActive?.Invoke() == true)
                {
                    paused = pauseNativeForTest?.Invoke() == true;
                    if (!paused)
                    {
                        maxVless = 0;
                        StatusMessage?.Invoke("verify-skipped-vpn");
                    }
                }

                GoidaTcpVlessProbeOptions probeOptions = new()
                {
                    MaxVlessTests = maxVless,
                    EarlyStopOkCount = earlyStopOk,
                    MaxTcpLatencyForVlessMs = settings.AutoSwitchLatencyMs,
                    OnFirstVlessOk = () => _ = EvaluateAutoSwitchAsync(getSettings().Clone())
                };

                GoidaProbeResult result;
                try
                {
                    store.BeginBulkUpdate();
                    try
                    {
                        IReadOnlyList<GoidaNode> probeTargets = manual
                            ? targets
                            : targets.Take(MaxBackgroundProbeBatch).ToList();
                        result = await monitor.ProbeTcpThenVlessAsync(
                            probeTargets,
                            probeOptions,
                            cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        store.EndBulkUpdate();
                    }
                }
                finally
                {
                    if (paused)
                        resumeNativeAfterTest?.Invoke();
                }

                if (!result.Cancelled
                    && result.Ok > 0
                    && settings.AutoSwitchOnFly
                    && isVpnSessionActive?.Invoke() != true)
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
                // Do not overwrite probe status (Ok/VlessVerified) on the node that was
                // live — TCP/timeouts during an active session are often false positives.
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
                lock (recentTunnelFailures)
                    recentTunnelFailures.Clear();

                next = GoidaActiveSelector.SelectNextFailoverNode(
                    settings,
                    store.GetNodes(),
                    activeId,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            if (next == null || string.Equals(next.Id, activeId, StringComparison.OrdinalIgnoreCase))
                next = TryLiveVlessFailover(settings, activeId, failedIds);

            if (next == null || string.Equals(next.Id, activeId, StringComparison.OrdinalIgnoreCase))
            {
                NodesUpdated?.Invoke();
                return false;
            }

            lastFailoverUtc = DateTime.UtcNow;
            DiagnosticLog.Write("Goida.ReportTunnelFailure", $"Switching {activeId} -> {next.Id} ({next.DisplayName})");
            SetActiveNode(next.Id, notifyExternal: true);
            return true;
        }

        private const int MaxLiveFailoverTests = 20;

        /// <summary>
        /// When the pre-verified pool is exhausted, pause the live session and run
        /// real VLESS tests on the next candidates until one works.
        /// </summary>
        private GoidaNode? TryLiveVlessFailover(
            GoidaProfileSettings settings,
            string activeId,
            HashSet<string> failedIds)
        {
            List<GoidaNode> candidates = BuildLiveFailoverCandidates(settings, activeId, failedIds);
            if (candidates.Count == 0)
                return null;

            bool paused = pauseNativeForTest?.Invoke() == true;
            try
            {
                int tested = 0;
                foreach (GoidaNode node in candidates)
                {
                    if (tested++ >= MaxLiveFailoverTests)
                        break;

                    int latency = monitor.TestNodeNative(node);
                    if (latency >= 0
                        && latency != Values.Availability.ERROR
                        && latency != Values.Availability.TIMEOUT)
                    {
                        store.UpdateNodeStatus(node.Id, latency, GoidaNodeStatus.Ok, vlessVerified: true);
                        DiagnosticLog.Write("Goida.LiveFailover",
                            $"VLESS ok on {node.Id} ({node.DisplayName}) in {latency} ms");
                        return store.FindById(node.Id);
                    }

                    store.UpdateNodeStatus(node.Id, Values.Availability.ERROR, GoidaNodeStatus.Error,
                        vlessVerified: false);
                    lock (recentTunnelFailures)
                        recentTunnelFailures[node.Id] = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Goida.TryLiveVlessFailover", ex);
            }
            finally
            {
                if (paused)
                    resumeNativeAfterTest?.Invoke();
            }

            return null;
        }

        private List<GoidaNode> BuildLiveFailoverCandidates(
            GoidaProfileSettings settings,
            string activeId,
            HashSet<string> failedIds)
        {
            IReadOnlyList<GoidaNode> all = store.GetNodes();

            if (settings.SelectionMode == GoidaSelectionMode.ManualPool
                && settings.ManualPoolNodeIds?.Count > 0)
            {
                Dictionary<string, GoidaNode> byId = all
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
                int start = 0;
                int currentIndex = settings.ManualPoolNodeIds.FindIndex(id =>
                    string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase));
                if (currentIndex >= 0)
                    start = currentIndex + 1;

                List<GoidaNode> ordered = new();
                for (int i = 0; i < settings.ManualPoolNodeIds.Count; i++)
                {
                    string id = settings.ManualPoolNodeIds[(start + i) % settings.ManualPoolNodeIds.Count];
                    if (failedIds.Contains(id))
                        continue;
                    if (byId.TryGetValue(id, out GoidaNode? node)
                        && !string.Equals(node.Id, activeId, StringComparison.OrdinalIgnoreCase))
                        ordered.Add(node);
                }

                return ordered;
            }

            return all
                .Where(node => GetEnabledListSet(settings).Contains(node.ListId))
                .Where(node => !failedIds.Contains(node.Id))
                .Where(node => !string.Equals(node.Id, activeId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => node.VlessVerified ? 0 : 1)
                .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                .Take(MaxLiveFailoverTests)
                .ToList();
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

        private async Task RunVlessVerificationAsync(
            CancellationToken cancellationToken,
            bool manual,
            IReadOnlyList<GoidaNode>? tcpBatch = null)
        {
            GoidaProfileSettings settings = getSettings().Clone();
            int limit = manual ? GoidaProfileSettings.MaxVerifiedNodes : MaxBackgroundVerifyBatch;
            List<GoidaNode> top = tcpBatch?.Count > 0
                ? BuildVerifyListFromTcpBatch(tcpBatch, limit)
                : BuildVerifyTargetList(settings, limit);
            if (top.Count == 0)
            {
                DiagnosticLog.Write("Goida.VerifyTopNodes",
                    $"No VLESS verify targets (tcpBatch={tcpBatch?.Count ?? 0}, manual={manual})");
                return;
            }

            DiagnosticLog.Write("Goida.VerifyTopNodes",
                $"Starting VLESS verify on {top.Count} nodes (manual={manual})");

            bool paused = false;
            if (isVpnSessionActive?.Invoke() == true)
            {
                paused = pauseNativeForTest?.Invoke() == true;
                if (!paused)
                {
                    DiagnosticLog.Write("Goida.VerifyTopNodes",
                        "Skipped VLESS verify: live VPN session could not be paused");
                    StatusMessage?.Invoke("verify-skipped-vpn");
                    return;
                }
            }

            try
            {
                StatusMessage?.Invoke("verify-start");
                store.BeginBulkUpdate();
                try
                {
                    await monitor.ProbeNodesAsync(top, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    store.EndBulkUpdate();
                }

                int verifiedOk = top.Count(node =>
                    store.FindById(node.Id) is GoidaNode live
                    && live.VlessVerified
                    && live.Status == GoidaNodeStatus.Ok);
                DiagnosticLog.Write("Goida.VerifyTopNodes",
                    $"VLESS verify finished: {verifiedOk}/{top.Count} OK");

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
            finally
            {
                if (paused)
                    resumeNativeAfterTest?.Invoke();
            }
        }

        private List<GoidaNode> BuildVerifyTargetList(GoidaProfileSettings settings, int limit)
        {
            return ResolveLiveProbeTargets(
                FilterProbeTargets(settings, store.GetNodes(), manual: true)
                    .Where(node => !node.VlessVerified && node.LatencyMs >= 0)
                    .OrderBy(node => node.LatencyMs)
                    .Take(limit)
                    .ToList());
        }

        /// <summary>
        /// After a TCP batch, FilterProbeTargets returns a different node set because
        /// sort keys (LastCheckedUtc, Status) changed — verify must use the batch we
        /// actually probed, not a freshly re-filtered list.
        /// </summary>
        private List<GoidaNode> BuildVerifyListFromTcpBatch(IReadOnlyList<GoidaNode> tcpBatch, int limit)
        {
            Dictionary<string, GoidaNode> liveByEndpoint = store.GetNodes()
                .Where(node => !string.IsNullOrWhiteSpace(node.Endpoint))
                .GroupBy(node => BuildEndpointKey(node.ListId, node.Endpoint),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            List<GoidaNode> candidates = new(tcpBatch.Count);
            foreach (GoidaNode target in tcpBatch)
            {
                GoidaNode? live = null;
                if (liveByEndpoint.TryGetValue(
                    BuildEndpointKey(target.ListId, target.Endpoint), out GoidaNode? fromEndpoint))
                    live = fromEndpoint;
                else
                    live = store.FindById(target.Id);

                if (live == null || live.VlessVerified || live.LatencyMs < 0)
                    continue;

                candidates.Add(live);
            }

            return candidates
                .OrderBy(node => node.LatencyMs)
                .Take(limit)
                .ToList();
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
            bool manual = false,
            int? listIdFilter = null)
        {
            int batchLimit = manual ? MaxManualProbeBatch : MaxBackgroundProbeBatch;
            HashSet<int> enabledLists = GetEnabledListSet(settings);
            IEnumerable<GoidaNode> filtered = nodes
                .Where(node => enabledLists.Contains(node.ListId));

            if (listIdFilter is int listId && listId >= 1 && listId <= 26)
            {
                if (!enabledLists.Contains(listId))
                    return Enumerable.Empty<GoidaNode>();

                filtered = filtered.Where(node => node.ListId == listId);
            }

            // Never TCP-probe the live active node — it often times out while traffic
            // flows through it and would get wrongly marked failed / auto-replaced.
            if (!string.IsNullOrWhiteSpace(settings.ActiveNodeId))
            {
                filtered = filtered.Where(node =>
                    !string.Equals(node.Id, settings.ActiveNodeId, StringComparison.OrdinalIgnoreCase));
            }

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

                    // While VPN is live, skip background probes — tunnel health is
                    // checked from the main window (exit IP). TCP to the active
                    // endpoint during a session produced false timeouts.
                    if (isVpnSessionActive?.Invoke() == true)
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

        private static string BuildEndpointKey(int listId, string endpoint) =>
            $"{listId}|{endpoint.Trim()}";

        private List<GoidaNode> ResolveLiveProbeTargets(IReadOnlyList<GoidaNode> targets)
        {
            if (targets.Count == 0)
                return new List<GoidaNode>();

            IReadOnlyList<GoidaNode> liveNodes = store.GetNodes();
            Dictionary<string, GoidaNode> byId = liveNodes
                .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, GoidaNode> byEndpoint = liveNodes
                .Where(node => !string.IsNullOrWhiteSpace(node.Endpoint))
                .GroupBy(node => BuildEndpointKey(node.ListId, node.Endpoint),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            List<GoidaNode> resolved = new(targets.Count);
            foreach (GoidaNode target in targets)
            {
                if (byId.TryGetValue(target.Id, out GoidaNode? live))
                {
                    resolved.Add(live);
                    continue;
                }

                string endpointKey = BuildEndpointKey(target.ListId, target.Endpoint);
                if (byEndpoint.TryGetValue(endpointKey, out live))
                {
                    resolved.Add(live);
                    continue;
                }

                resolved.Add(target);
            }

            return resolved;
        }

        private static void CopyProbeState(GoidaNode source, GoidaNode target)
        {
            target.LatencyMs = source.LatencyMs;
            target.Status = source.Status;
            target.LastCheckedUtc = source.LastCheckedUtc;
            target.VlessVerified = source.VlessVerified;
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
