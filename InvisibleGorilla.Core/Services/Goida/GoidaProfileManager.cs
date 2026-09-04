using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Values;

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
        private GoidaOperationLog? operationLog;

        private Func<GoidaProfileSettings> getSettings;
        private Action<GoidaProfileSettings> saveSettings;
        private Action<GoidaNode>? onActiveNodeChanged;
        private Func<bool>? pauseNativeForTest;
        private Action? resumeNativeAfterTest;
        private Func<bool>? isVpnSessionActive;
        private Func<bool> canProbe = () => true;

        private readonly Dictionary<string, DateTime> recentTunnelFailures = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan TunnelFailureCooldown = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan FailoverDebounce = TimeSpan.FromSeconds(1);
        private DateTime lastFailoverUtc = DateTime.MinValue;

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

        public int CountManualProbeTargets(int? listIdFilter = null)
        {
            GoidaProfileSettings settings = getSettings().Clone();
            return FilterProbeTargets(settings, store.GetNodes(), manual: true, listIdFilter).Count();
        }

        public int CountVerifyTargets(bool manual = true, int? listIdFilter = null)
        {
            GoidaProfileSettings settings = getSettings().Clone();
            int limit = manual ? GoidaProfileSettings.MaxManualVerifyNodes : MaxProbeBatch;
            return FilterProbeTargets(settings, store.GetNodes(), manual, listIdFilter)
                .Take(limit)
                .Count();
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

        public bool IsRefreshInProgress => Volatile.Read(ref refreshInProgress) != 0;

        public void Setup(
            Func<string, Status> convertConfigLinkToV2Ray,
            Func<string, int> testConnection,
            Func<GoidaProfileSettings> getSettings,
            Action<GoidaProfileSettings> saveSettings,
            Action<GoidaNode>? onActiveNodeChanged = null,
            Func<bool>? pauseNativeForTest = null,
            Action? resumeNativeAfterTest = null,
            Func<bool>? canProbe = null,
            Func<bool>? isVpnSessionActive = null)
        {
            this.getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            this.saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
            this.onActiveNodeChanged = onActiveNodeChanged;
            this.pauseNativeForTest = pauseNativeForTest;
            this.resumeNativeAfterTest = resumeNativeAfterTest;
            this.isVpnSessionActive = isVpnSessionActive;
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

        // Lightweight TCP-only reachability check of the currently active node. It deliberately
        // bypasses the native VLESS test (and the suspend gate), so it is safe to call while the
        // live tunnel is running: it never touches the native xray core and cannot trigger the
        // concurrent-core crash. Used to keep the live signal indicator fresh during a session.
        public async Task<GoidaNode?> ProbeActiveNodeTcpAsync(CancellationToken cancellationToken = default)
        {
            GoidaNode? active = GetActiveNode();
            if (active == null)
                return null;

            try
            {
                await monitor.ProbeNodesTcpRefreshAsync(new[] { active }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Goida.ProbeActiveNodeTcp", ex);
            }

            return store.FindById(active.Id);
        }

        /// <summary>
        /// Called when the running tunnel is detected dead (exit IP check failed or shows the
        /// real IP). Marks the active node as failed and switches to the next candidate. When the
        /// pre-verified pool is exhausted, pauses the tunnel and runs real VLESS tests on the next
        /// candidates until one works. Returns true if a switch was initiated.
        /// </summary>
        public bool ReportTunnelFailure()
        {
            GoidaProfileSettings settings = getSettings().Clone();

            if (!settings.Enabled
                || !settings.AutoSwitchOnFly
                || settings.SelectionMode == GoidaSelectionMode.ManualFixed)
                return false;

            if (DateTime.UtcNow - lastFailoverUtc < FailoverDebounce)
                return false;

            string activeId = settings.ActiveNodeId ?? string.Empty;
            GoidaNode? active = string.IsNullOrWhiteSpace(activeId) ? null : store.FindById(activeId);
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                // Do not overwrite probe status on the node that was live — TCP timeouts during
                // an active session are often false positives.
                lock (recentTunnelFailures)
                    recentTunnelFailures[activeId] = DateTime.UtcNow;
            }

            HashSet<string> failedIds = GetRecentFailedNodeIds();

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
            DiagnosticLog.Write("Goida.ReportTunnelFailure",
                $"Switching {activeId} -> {next.Id} ({next.DisplayName})");
            operationLog?.Add($"Failover: {active?.DisplayName ?? activeId} -> {next.DisplayName}");
            SetActiveNode(next.Id);
            return true;
        }

        private const int MaxLiveFailoverTests = 20;

        private GoidaNode? TryLiveVlessFailover(
            GoidaProfileSettings settings,
            string activeId,
            HashSet<string> failedIds)
        {
            List<GoidaNode> candidates = BuildLiveFailoverCandidates(settings, activeId, failedIds);
            if (candidates.Count == 0)
                return null;

            bool paused = pauseNativeForTest?.Invoke() == true;
            GoidaNode? found = null;
            try
            {
                int tested = 0;
                foreach (GoidaNode node in candidates)
                {
                    if (tested++ >= MaxLiveFailoverTests)
                        break;

                    int latency = monitor.TestNodeNative(node);
                    if (latency >= 0
                        && latency != Availability.ERROR
                        && latency != Availability.TIMEOUT)
                    {
                        store.UpdateNodeStatus(node.Id, latency, GoidaNodeStatus.Ok, vlessVerified: true);
                        DiagnosticLog.Write("Goida.LiveFailover",
                            $"VLESS ok on {node.Id} ({node.DisplayName}) in {latency} ms");
                        found = store.FindById(node.Id);
                        break;
                    }

                    store.UpdateNodeStatus(node.Id, Availability.ERROR, GoidaNodeStatus.Error,
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
                // When a working node was found, SetActiveNode + onActiveNodeChanged will restart
                // the tunnel onto it — resuming the old session here would flash the dead node.
                if (paused && found == null)
                    resumeNativeAfterTest?.Invoke();
            }

            return found;
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

                HashSet<int> enabledLists = GetEnabledListSet(settings);

                IReadOnlyDictionary<int, string> listData = await fetcher
                    .FetchListsAsync(fetchListIds, cancellationToken)
                    .ConfigureAwait(false);

                Dictionary<string, GoidaNode> mergedById = store.GetNodes()
                    .Where(node => enabledLists.Contains(node.ListId))
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

                Dictionary<string, GoidaNode> existing = store.GetNodes()
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, GoidaNode> existingByEndpoint = store.GetNodes()
                    .Where(node => !string.IsNullOrWhiteSpace(node.Endpoint))
                    .GroupBy(node => BuildEndpointKey(node.ListId, node.Endpoint),
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                store.BeginBulkUpdate();
                List<GoidaNode> merged = new();
                try
                {
                foreach (KeyValuePair<int, string> pair in listData.OrderBy(entry => entry.Key))
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(pair.Value))
                        {
                            DiagnosticLog.Write(
                                "Goida.RefreshList",
                                $"List {pair.Key}: empty fetch, keeping previous nodes");
                            continue;
                        }

                        List<GoidaNode> parsed = parser.ParseList(pair.Key, pair.Value, store.NodesDirectory);
                        List<string> staleIds = mergedById
                            .Where(entry => entry.Value.ListId == pair.Key)
                            .Select(entry => entry.Key)
                            .ToList();
                        foreach (string staleId in staleIds)
                            mergedById.Remove(staleId);
                        DiagnosticLog.Write(
                            "Goida.RefreshList",
                            $"List {pair.Key}: fetched {pair.Value.Length} bytes, parsed {parsed.Count} nodes");
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

                if (Interlocked.CompareExchange(ref probeInProgress, 0, 0) != 0)
                {
                    DiagnosticLog.Write("Goida.RefreshListsAsync",
                        "Skipped ReplaceNodes because a probe is in progress");
                    StatusMessage?.Invoke("refresh-deferred");
                    return;
                }

                merged = mergedById.Values.ToList();
                store.ReplaceNodes(merged);
                }
                finally
                {
                    store.EndBulkUpdate();
                }
                TryEnsureActiveNode();

                GoidaProfileSettings latest = getSettings().Clone();
                latest.LastRefreshUtc = DateTime.UtcNow;
                saveSettings(latest);

                operationLog?.Add($"Refresh: {merged.Count} nodes");
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
            bool manual = false,
            int? listIdFilter = null)
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
                List<GoidaNode> targets = FilterProbeTargets(
                    settings,
                    store.GetNodes(),
                    manual,
                    listIdFilter).ToList();
                if (targets.Count == 0)
                    return new GoidaProbeResult();

                bool vpnActive = isVpnSessionActive?.Invoke() == true;
                GoidaTcpVlessProbeOptions probeOptions = new()
                {
                    MaxVlessTests = vpnActive
                        ? 0
                        : manual
                            ? GoidaProfileSettings.MaxManualVerifyNodes
                            : 8,
                    EarlyStopOkCount = vpnActive
                        ? 0
                        : manual
                            ? GoidaProfileSettings.MaxManualVerifyNodes
                            : 3,
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
            bool manual,
            int? listIdFilter = null)
        {
            HashSet<int> enabledLists = settings.EnabledListIds?.Count > 0
                ? settings.EnabledListIds.ToHashSet()
                : Enumerable.Range(1, 26).ToHashSet();
            IEnumerable<GoidaNode> filtered = nodes
                .Where(node => enabledLists.Contains(node.ListId));

            if (listIdFilter is int listId && listId >= 1 && listId <= 26)
            {
                if (!enabledLists.Contains(listId))
                    return Enumerable.Empty<GoidaNode>();

                filtered = filtered.Where(node => node.ListId == listId);
            }

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
            DateTime lastProbe = DateTime.MinValue;

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

        private static string BuildEndpointKey(int listId, string endpoint) =>
            $"{listId}|{endpoint.Trim()}";

        private static void CopyProbeState(GoidaNode source, GoidaNode target)
        {
            target.LatencyMs = source.LatencyMs;
            target.Status = source.Status;
            target.LastCheckedUtc = source.LastCheckedUtc;
            target.VlessVerified = source.VlessVerified;
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
