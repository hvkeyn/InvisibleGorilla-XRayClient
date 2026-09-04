using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InvisibleGorillaXRay.Core;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaFetcher
    {
        private static readonly HttpClient SharedClient = CreateClient();

        private static HttpClient CreateClient()
        {
            HttpClientHandler handler = new()
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            HttpClient client = new(handler)
            {
                Timeout = TimeSpan.FromSeconds(90)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "InvisibleGorilla-XRay/3.6.11");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/plain,*/*");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
            return client;
        }

        public async Task<IReadOnlyDictionary<int, string>> FetchListsAsync(
            IEnumerable<int> listIds,
            CancellationToken cancellationToken = default)
        {
            Dictionary<int, string> results = new();
            List<Task> tasks = new();
            using SemaphoreSlim gate = new(3);

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
                string body = await FetchFromMirrorsAsync(listId, cancellationToken).ConfigureAwait(false);
                lock (results)
                {
                    results[listId] = body;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException($"Goida.FetchList.{listId}", ex);
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

        private static async Task<string> FetchFromMirrorsAsync(int listId, CancellationToken cancellationToken)
        {
            Exception? lastError = null;
            foreach (string url in GoidaSourceCatalog.GetListUrls(listId))
            {
                try
                {
                    using HttpResponseMessage response = await SharedClient
                        .GetAsync(url, cancellationToken)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = new HttpRequestException($"HTTP {(int)response.StatusCode} from {url}");
                        continue;
                    }

                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body) || GoidaSubscriptionNormalizer.LooksLikeHtmlError(body))
                    {
                        lastError = new InvalidOperationException($"Empty or HTML body from {url}");
                        continue;
                    }

                    DiagnosticLog.Write("Goida.FetchList", $"List {listId}: {body.Length} bytes from {url}");
                    return body;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (lastError != null)
                DiagnosticLog.WriteException($"Goida.FetchList.{listId}", lastError);

            return string.Empty;
        }
    }
}
