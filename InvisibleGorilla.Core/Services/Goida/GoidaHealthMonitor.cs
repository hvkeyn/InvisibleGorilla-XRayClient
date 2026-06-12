using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaHealthMonitor
    {
        private const int FastProbeConcurrency = 16;
        private const int FastProbeTimeoutMs = 2500;
        private const int NativeProbeTimeoutMs = 7000;
        private const int ProgressNotifyEvery = 4;

        private static readonly SemaphoreSlim NativeTestGate = new(1, 1);

        private readonly GoidaNodeStore store;
        private readonly Func<string, int> testConnection;

        public GoidaHealthMonitor(GoidaNodeStore store, Func<string, int> testConnection)
        {
            this.store = store;
            this.testConnection = testConnection;
        }

        public event Action? NodesUpdated;
        public event Action<GoidaProbeProgress>? ProbeProgress;

        public Task<GoidaProbeResult> ProbeNodesAsync(
            IEnumerable<GoidaNode> nodes,
            CancellationToken cancellationToken = default)
        {
            return ProbeNodesInternalAsync(nodes, useNativeTest: true, cancellationToken);
        }

        public Task<GoidaProbeResult> ProbeNodesFastAsync(
            IEnumerable<GoidaNode> nodes,
            CancellationToken cancellationToken = default)
        {
            return ProbeNodesInternalAsync(nodes, useNativeTest: false, cancellationToken);
        }

        // Session-time liveness refresh. Unlike the normal fast probe it never touches the
        // VlessVerified flag (passes null), so a node that was already VLESS-verified before the
        // tunnel came up keeps that status while we just refresh latency/reachability over TCP.
        // A reachable node stays Ok; an unreachable one is demoted to Timeout/Error so the
        // failover logic can react. No native core is used, so this is safe during an active VPN.
        public async Task ProbeNodesTcpRefreshAsync(
            IReadOnlyList<GoidaNode> targets,
            CancellationToken cancellationToken = default)
        {
            if (targets == null || targets.Count == 0)
                return;

            using SemaphoreSlim gate = new(FastProbeConcurrency, FastProbeConcurrency);
            List<Task> tasks = new(targets.Count);

            foreach (GoidaNode node in targets)
            {
                if (node == null)
                    continue;

                string probeNodeId = node.Id;
                int probeListId = node.ListId;
                string probeEndpoint = node.Endpoint;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        int latency = GoidaEndpointProbe.ProbeTcp(probeEndpoint, FastProbeTimeoutMs);
                        store.UpdateNodeStatus(probeNodeId, latency, MapLatency(latency),
                            vlessVerified: null, listId: probeListId, endpoint: probeEndpoint);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException($"Goida.TcpRefresh.{probeNodeId}", ex);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            NodesUpdated?.Invoke();
        }

        public async Task<GoidaProbeResult> ProbeTcpThenVlessAsync(
            IReadOnlyList<GoidaNode> targets,
            GoidaTcpVlessProbeOptions options,
            CancellationToken cancellationToken = default)
        {
            if (targets.Count == 0)
                return new GoidaProbeResult();

            List<GoidaNode> vlessPending = new();
            object vlessLock = new();
            bool tcpPhaseDone = false;
            int tcpOk = 0;
            int tcpTimeout = 0;
            int tcpError = 0;
            int tcpCompleted = 0;
            int nativeOk = 0;
            int nativeTimeout = 0;
            int nativeError = 0;
            int nativeTested = 0;
            bool cancelled = false;
            GoidaNode? bestNode = null;
            int bestLatency = int.MaxValue;
            int vlessEnqueued = 0;
            int vlessTargetTotal = 0;
            bool vlessPhaseAnnounced = false;

            void EnqueueForVless(GoidaNode? live, int tcpLatency)
            {
                if (live == null
                    || live.VlessVerified
                    || tcpLatency < 0
                    || tcpLatency > options.MaxTcpLatencyForVlessMs)
                    return;

                lock (vlessLock)
                {
                    int insertAt = vlessPending.FindIndex(node => node.LatencyMs > live.LatencyMs);
                    if (insertAt < 0)
                        vlessPending.Add(live);
                    else
                        vlessPending.Insert(insertAt, live);

                    vlessEnqueued++;
                    vlessTargetTotal = Math.Min(options.MaxVlessTests, vlessEnqueued);
                }
            }

            Task nativeWorker = options.MaxVlessTests <= 0
                ? Task.CompletedTask
                : Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    GoidaNode? node = null;
                    lock (vlessLock)
                    {
                        if (vlessPending.Count > 0)
                            node = vlessPending[0];
                        if (node != null)
                            vlessPending.RemoveAt(0);
                        else if (tcpPhaseDone)
                            break;
                    }

                    if (node == null)
                    {
                        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (nativeTested >= options.MaxVlessTests)
                        break;

                    if (nativeOk >= options.EarlyStopOkCount)
                        break;

                    await NativeTestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        int latency = await RunNativeProbeWithDeadline(
                            node,
                            options.NativeTimeoutMs > 0
                                ? options.NativeTimeoutMs
                                : NativeProbeTimeoutMs,
                            cancellationToken).ConfigureAwait(false);

                        ApplyProbeResult(node.Id, node.ListId, node.Endpoint, latency, vlessVerified: true,
                            ref nativeOk, ref nativeTimeout, ref nativeError, ref nativeTested,
                            ref bestNode, ref bestLatency);

                        if (!vlessPhaseAnnounced)
                            vlessPhaseAnnounced = true;

                        NotifyProgress(nativeTested, Math.Max(1, vlessTargetTotal), node.Id, node.DisplayName,
                            latency, vlessVerified: true);

                        if (nativeOk == 1 && latency >= 0
                            && latency != Values.Availability.ERROR
                            && latency != Values.Availability.TIMEOUT)
                            options.OnFirstVlessOk?.Invoke();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException($"Goida.ProbeNode.{node.Id}", ex);
                        store.UpdateNodeStatus(node.Id, Values.Availability.ERROR, GoidaNodeStatus.Error,
                            vlessVerified: false, listId: node.ListId, endpoint: node.Endpoint);
                        nativeTested++;
                        nativeError++;
                        NotifyProgress(nativeTested, Math.Max(1, vlessTargetTotal), node.Id, node.DisplayName,
                            Values.Availability.ERROR, vlessVerified: true);
                    }
                    finally
                    {
                        NativeTestGate.Release();
                    }
                }
            }, cancellationToken);

            if (options.MaxVlessTests <= 0)
                tcpPhaseDone = true;

            int tcpTimeoutMs = options.TcpTimeoutMs > 0 ? options.TcpTimeoutMs : FastProbeTimeoutMs;
            using SemaphoreSlim gate = new(FastProbeConcurrency, FastProbeConcurrency);
            List<Task> tcpTasks = new(targets.Count);
            object tcpLock = new();

            foreach (GoidaNode node in targets)
            {
                string probeNodeId = node.Id;
                int probeListId = node.ListId;
                string probeEndpoint = node.Endpoint;
                string probeDisplayName = node.DisplayName;
                tcpTasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        int latency = GoidaEndpointProbe.ProbeTcp(probeEndpoint, tcpTimeoutMs);

                        lock (tcpLock)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            ApplyProbeResult(probeNodeId, probeListId, probeEndpoint, latency,
                                vlessVerified: false, ref tcpOk, ref tcpTimeout,
                                ref tcpError, ref tcpCompleted, ref bestNode, ref bestLatency);

                            if (!vlessPhaseAnnounced)
                                NotifyProgress(tcpCompleted, targets.Count, probeNodeId, probeDisplayName, latency,
                                    vlessVerified: false);

                            GoidaNode? stored = ResolveStoredNode(probeNodeId, probeListId, probeEndpoint);
                            EnqueueForVless(stored, latency);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException($"Goida.FastProbe.{probeNodeId}", ex);
                        lock (tcpLock)
                        {
                            store.UpdateNodeStatus(probeNodeId, Values.Availability.ERROR, GoidaNodeStatus.Error,
                                listId: probeListId, endpoint: probeEndpoint);
                            tcpCompleted++;
                            tcpError++;
                            if (!vlessPhaseAnnounced)
                                NotifyProgress(tcpCompleted, targets.Count, probeNodeId, probeDisplayName,
                                    Values.Availability.ERROR, vlessVerified: false);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            try
            {
                await Task.WhenAll(tcpTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }

            lock (vlessLock)
                tcpPhaseDone = true;

            try
            {
                await nativeWorker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }

            return new GoidaProbeResult
            {
                Total = targets.Count,
                Completed = tcpCompleted,
                Ok = nativeOk > 0 ? nativeOk : tcpOk,
                Timeout = nativeTested > 0 ? nativeTimeout : tcpTimeout,
                Error = nativeTested > 0 ? nativeError : tcpError,
                Cancelled = cancelled,
                BestNode = bestNode
            };
        }

        private async Task<GoidaProbeResult> ProbeNodesInternalAsync(
            IEnumerable<GoidaNode>? nodes,
            bool useNativeTest,
            CancellationToken cancellationToken)
        {
            List<GoidaNode> targets = nodes?
                .Where(node => node != null)
                .ToList() ?? new List<GoidaNode>();

            if (targets.Count == 0)
                return new GoidaProbeResult();

            if (useNativeTest)
                return await ProbeNativeSequentialAsync(targets, cancellationToken).ConfigureAwait(false);

            return await ProbeFastParallelAsync(targets, cancellationToken).ConfigureAwait(false);
        }

        private async Task<GoidaProbeResult> ProbeNativeSequentialAsync(
            IReadOnlyList<GoidaNode> targets,
            CancellationToken cancellationToken)
        {
            int ok = 0;
            int timeout = 0;
            int error = 0;
            int completed = 0;
            GoidaNode? bestNode = null;
            int bestLatency = int.MaxValue;
            bool cancelled = false;

            for (int index = 0; index < targets.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                GoidaNode node = targets[index];

                await NativeTestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int latency = await RunNativeProbeWithDeadline(node, NativeProbeTimeoutMs, cancellationToken)
                        .ConfigureAwait(false);
                    ApplyProbeResult(node.Id, node.ListId, node.Endpoint, latency, vlessVerified: true,
                        ref ok, ref timeout, ref error, ref completed, ref bestNode, ref bestLatency);

                    NotifyProgress(completed, targets.Count, node.Id, node.DisplayName, latency,
                        vlessVerified: true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException($"Goida.ProbeNode.{node.Id}", ex);
                    store.UpdateNodeStatus(node.Id, Values.Availability.ERROR, GoidaNodeStatus.Error,
                        vlessVerified: false, listId: node.ListId, endpoint: node.Endpoint);
                    completed++;
                    error++;
                    NotifyProgress(completed, targets.Count, node.Id, node.DisplayName,
                        Values.Availability.ERROR, vlessVerified: false);
                }
                finally
                {
                    NativeTestGate.Release();
                }
            }

            return BuildResult(targets.Count, completed, ok, timeout, error, cancelled, bestNode);
        }

        private async Task<GoidaProbeResult> ProbeFastParallelAsync(
            IReadOnlyList<GoidaNode> targets,
            CancellationToken cancellationToken)
        {
            int ok = 0;
            int timeout = 0;
            int error = 0;
            int completed = 0;
            GoidaNode? bestNode = null;
            int bestLatency = int.MaxValue;
            bool cancelled = false;
            object progressLock = new();

            using SemaphoreSlim gate = new(FastProbeConcurrency, FastProbeConcurrency);
            List<Task> tasks = new(targets.Count);

            foreach (GoidaNode node in targets)
            {
                string probeNodeId = node.Id;
                int probeListId = node.ListId;
                string probeEndpoint = node.Endpoint;
                string probeDisplayName = node.DisplayName;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        int latency = GoidaEndpointProbe.ProbeTcp(probeEndpoint, FastProbeTimeoutMs);
                        lock (progressLock)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            ApplyProbeResult(probeNodeId, probeListId, probeEndpoint, latency,
                                vlessVerified: false, ref ok, ref timeout,
                                ref error, ref completed, ref bestNode, ref bestLatency);
                            NotifyProgress(completed, targets.Count, probeNodeId, probeDisplayName, latency,
                                vlessVerified: false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException($"Goida.FastProbe.{probeNodeId}", ex);
                        lock (progressLock)
                        {
                            store.UpdateNodeStatus(probeNodeId, Values.Availability.ERROR, GoidaNodeStatus.Error,
                                listId: probeListId, endpoint: probeEndpoint);
                            completed++;
                            error++;
                            NotifyProgress(completed, targets.Count, probeNodeId, probeDisplayName,
                                Values.Availability.ERROR, vlessVerified: false);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }

            lock (progressLock)
            {
                return BuildResult(targets.Count, completed, ok, timeout, error, cancelled, bestNode);
            }
        }

        public int TestNodeNative(GoidaNode node) =>
            ProbeNodeSafe(node);

        private async Task<int> RunNativeProbeWithDeadline(
            GoidaNode node,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            Task<int> probeTask = Task.Run(() => ProbeNodeSafe(node), cancellationToken);
            try
            {
                Task completed = await Task.WhenAny(
                    probeTask,
                    Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);
                if (completed == probeTask)
                    return probeTask.Result;

                DiagnosticLog.Write("Goida.ProbeNodeNative",
                    $"Deadline {timeoutMs}ms on {node.DisplayName} ({node.Endpoint}), waiting for native test to finish");
                return await probeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException($"Goida.ProbeNodeNative.{node.Id}", ex);
                return Values.Availability.TIMEOUT;
            }
        }

        private GoidaNode? ResolveStoredNode(string nodeId, int listId, string endpoint)
        {
            GoidaNode? stored = store.FindById(nodeId);
            if (stored != null)
                return stored;

            if (listId > 0 && !string.IsNullOrWhiteSpace(endpoint))
            {
                stored = store.GetNodes().FirstOrDefault(candidate =>
                    candidate.ListId == listId
                    && string.Equals(candidate.Endpoint?.Trim(), endpoint.Trim(),
                        StringComparison.OrdinalIgnoreCase));
            }

            return stored;
        }

        private void ApplyProbeResult(
            string nodeId,
            int listId,
            string endpoint,
            int latency,
            bool vlessVerified,
            ref int ok,
            ref int timeout,
            ref int error,
            ref int completed,
            ref GoidaNode? bestNode,
            ref int bestLatency)
        {
            GoidaNodeStatus status = vlessVerified
                ? MapLatency(latency)
                : MapTcpOnlyLatency(latency);
            store.UpdateNodeStatus(nodeId, latency, status, vlessVerified, listId, endpoint);
            completed++;

            GoidaNode? stored = ResolveStoredNode(nodeId, listId, endpoint);

            switch (status)
            {
                case GoidaNodeStatus.Ok:
                    ok++;
                    if (vlessVerified && latency >= 0 && latency < bestLatency)
                    {
                        bestLatency = latency;
                        bestNode = stored ?? store.FindById(nodeId);
                    }
                    break;
                case GoidaNodeStatus.Timeout:
                    timeout++;
                    break;
                default:
                    if (!vlessVerified && latency >= 0)
                        break;
                    error++;
                    break;
            }
        }

        private void NotifyProgress(
            int completed,
            int total,
            string nodeId,
            string displayName,
            int latencyMs,
            bool vlessVerified)
        {
            GoidaNodeStatus status = vlessVerified
                ? MapLatency(latencyMs)
                : MapTcpOnlyLatency(latencyMs);
            if (completed % ProgressNotifyEvery == 0 || completed == total)
                NodesUpdated?.Invoke();

            GoidaNode? node = store.FindById(nodeId);
            if (node == null && !string.IsNullOrWhiteSpace(displayName))
            {
                node = new GoidaNode
                {
                    Id = nodeId,
                    DisplayName = displayName
                };
            }

            ProbeProgress?.Invoke(new GoidaProbeProgress
            {
                Current = completed,
                Total = total,
                Node = node,
                LatencyMs = latencyMs,
                Status = status,
                IsVlessPhase = vlessVerified
            });
        }

        private static GoidaProbeResult BuildResult(
            int total,
            int completed,
            int ok,
            int timeout,
            int error,
            bool cancelled,
            GoidaNode? bestNode)
        {
            return new GoidaProbeResult
            {
                Total = total,
                Completed = completed,
                Ok = ok,
                Timeout = timeout,
                Error = error,
                Cancelled = cancelled,
                BestNode = bestNode
            };
        }

        private int ProbeNodeSafe(GoidaNode node)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(node.ConfigPath))
                    return Values.Availability.ERROR;

                int latency = testConnection(node.ConfigPath);
                if (latency < 0)
                    DiagnosticLog.Write("Goida.ProbeNodeNative",
                        $"{node.DisplayName} ({node.Endpoint}): result={latency}");
                return latency;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException($"Goida.ProbeNodeSafe.{node.Id}", ex);
                return Values.Availability.ERROR;
            }
        }

        private static GoidaNodeStatus MapLatency(int latency)
        {
            return latency switch
            {
                Values.Availability.TIMEOUT => GoidaNodeStatus.Timeout,
                Values.Availability.ERROR => GoidaNodeStatus.Error,
                Values.Availability.NOT_CHECKED => GoidaNodeStatus.Unknown,
                _ when latency >= 0 => GoidaNodeStatus.Ok,
                _ => GoidaNodeStatus.Error
            };
        }

        private static GoidaNodeStatus MapTcpOnlyLatency(int latency)
        {
            return latency switch
            {
                Values.Availability.TIMEOUT => GoidaNodeStatus.Timeout,
                Values.Availability.ERROR => GoidaNodeStatus.Error,
                _ when latency >= 0 => GoidaNodeStatus.Unknown,
                _ => GoidaNodeStatus.Error
            };
        }
    }
}
