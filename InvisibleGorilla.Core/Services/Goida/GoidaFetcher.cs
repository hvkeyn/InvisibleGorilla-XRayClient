using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaFetcher
    {
        private static readonly HttpClient SharedClient = new()
        {
            Timeout = TimeSpan.FromSeconds(45)
        };

        public async Task<IReadOnlyDictionary<int, string>> FetchListsAsync(
            IEnumerable<int> listIds,
            CancellationToken cancellationToken = default)
        {
            Dictionary<int, string> results = new();
            List<Task> tasks = new();
            using SemaphoreSlim gate = new(4);

            foreach (int listId in listIds)
            {
                tasks.Add(FetchOneAsync(listId, results, gate, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results;
        }

        private static async Task FetchOneAsync(
            int listId,
            Dictionary<int, string> results,
            SemaphoreSlim gate,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string url = GoidaSourceCatalog.GetListUrl(listId);
                using HttpResponseMessage response = await SharedClient
                    .GetAsync(url, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                lock (results)
                {
                    results[listId] = body ?? string.Empty;
                }
            }
            catch
            {
                lock (results)
                {
                    results[listId] = string.Empty;
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
