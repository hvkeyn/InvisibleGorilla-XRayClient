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
        private const int FastProbeTimeoutMs = 1500;
        private const int ProgressNotifyEvery = 8;

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
                    int latency = await Task.Run(
                        () => ProbeNodeSafe(node),
                        cancellationToken).ConfigureAwait(false);
                    ApplyProbeResult(node.Id, latency, ref ok, ref timeout, ref error, ref completed,
                        ref bestNode, ref bestLatency);

                    NotifyProgress(completed, targets.Count, node.Id, latency);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException($"Goida.ProbeNode.{node.Id}", ex);
                    store.UpdateNodeStatus(node.Id, Values.Availability.ERROR, GoidaNodeStatus.Error);
                    completed++;
                    error++;
                    NotifyProgress(completed, targets.Count, node.Id, Values.Availability.ERROR);
                }
                finally
                {
                    NativeTestGate.Release();
                }

                if (index < targets.Count - 1 && !cancellationToken.IsCancellationRequested)
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
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
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        int latency = GoidaEndpointProbe.ProbeTcp(node.Endpoint, FastProbeTimeoutMs);
                        lock (progressLock)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            ApplyProbeResult(node.Id, latency, ref ok, ref timeout, ref error, ref completed,
                                ref bestNode, ref bestLatency);
                            NotifyProgress(completed, targets.Count, node.Id, latency);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException($"Goida.FastProbe.{node.Id}", ex);
                        lock (progressLock)
                        {
                            store.UpdateNodeStatus(node.Id, Values.Availability.ERROR, GoidaNodeStatus.Error);
                            completed++;
                            error++;
                            NotifyProgress(completed, targets.Count, node.Id, Values.Availability.ERROR);
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

        private void ApplyProbeResult(
            string nodeId,
            int latency,
            ref int ok,
            ref int timeout,
            ref int error,
            ref int completed,
            ref GoidaNode? bestNode,
            ref int bestLatency)
        {
            GoidaNodeStatus status = MapLatency(latency);
            store.UpdateNodeStatus(nodeId, latency, status);
            completed++;

            switch (status)
            {
                case GoidaNodeStatus.Ok:
                    ok++;
                    if (latency >= 0 && latency < bestLatency)
                    {
                        bestLatency = latency;
                        bestNode = store.FindById(nodeId);
                    }
                    break;
                case GoidaNodeStatus.Timeout:
                    timeout++;
                    break;
                default:
                    error++;
                    break;
            }
        }

        private void NotifyProgress(int completed, int total, string nodeId, int latencyMs)
        {
            GoidaNodeStatus status = MapLatency(latencyMs);
            if (completed % ProgressNotifyEvery == 0 || completed == total)
                NodesUpdated?.Invoke();

            ProbeProgress?.Invoke(new GoidaProbeProgress
            {
                Current = completed,
                Total = total,
                Node = store.FindById(nodeId),
                LatencyMs = latencyMs,
                Status = status
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

                return testConnection(node.ConfigPath);
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
    }
}
