using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaHealthMonitor
    {
        private static readonly SemaphoreSlim NativeTestGate = new(1, 1);

        private readonly GoidaNodeStore store;
        private readonly Func<string, int> testConnection;

        public GoidaHealthMonitor(GoidaNodeStore store, Func<string, int> testConnection)
        {
            this.store = store;
            this.testConnection = testConnection;
        }

        public event Action? NodesUpdated;

        public async Task ProbeNodesAsync(
            IEnumerable<GoidaNode> nodes,
            CancellationToken cancellationToken = default)
        {
            List<GoidaNode> targets = nodes?.ToList() ?? new List<GoidaNode>();
            if (targets.Count == 0)
                return;

            using SemaphoreSlim gate = new(1);
            List<Task> tasks = new();

            foreach (GoidaNode node in targets)
            {
                tasks.Add(ProbeOneAsync(node, gate, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            NodesUpdated?.Invoke();
        }

        private async Task ProbeOneAsync(
            GoidaNode node,
            SemaphoreSlim gate,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await NativeTestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int latency = await Task.Run(() => testConnection(node.ConfigPath), cancellationToken)
                        .ConfigureAwait(false);
                    GoidaNodeStatus status = MapLatency(latency);
                    store.UpdateNodeStatus(node.Id, latency, status);
                }
                finally
                {
                    NativeTestGate.Release();
                }
            }
            catch
            {
                store.UpdateNodeStatus(node.Id, Values.Availability.ERROR, GoidaNodeStatus.Error);
            }
            finally
            {
                gate.Release();
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
