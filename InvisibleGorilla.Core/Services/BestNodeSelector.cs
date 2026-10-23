using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InvisibleGorillaXRay.Services
{
    using Diagnostics;

    /// <summary>
    /// Picks the subscription node with the lowest TCP handshake time so the user does not have
    /// to guess which of the dozens of entries is alive right now.
    /// </summary>
    public static class BestNodeSelector
    {
        public static string Pick(IEnumerable<string> configPaths, int timeoutMs = 900, int parallelism = 8)
        {
            if (configPaths == null)
                return null;

            string[] paths = configPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (paths.Length == 0)
                return null;

            ConcurrentBag<(string Path, int Rtt)> measured = new();

            try
            {
                Parallel.ForEach(
                    paths,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) },
                    path =>
                    {
                        try
                        {
                            string json = System.IO.File.ReadAllText(path);
                            int rtt = ConfigEndpointProbe.MeasureTcpRtt(json, timeoutMs);
                            if (rtt > 0)
                                measured.Add((path, rtt));
                        }
                        catch
                        {
                            // A broken file must not stop the scan; it simply stays out of the ranking.
                        }
                    });
            }
            catch (Exception ex)
            {
                InvisibleGorillaXRay.Core.DiagnosticLog.WriteException("BestNodeSelector.Pick", ex);
            }

            if (measured.IsEmpty)
                return null;

            (string Path, int Rtt) best = measured.OrderBy(item => item.Rtt).First();
            InvisibleGorillaXRay.Core.DiagnosticLog.Write(
                "AutoSelect",
                $"Fastest of {paths.Length} nodes: {System.IO.Path.GetFileNameWithoutExtension(best.Path)} ({best.Rtt} ms)");
            return best.Path;
        }
    }
}
