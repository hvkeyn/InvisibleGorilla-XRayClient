using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Services
{
    using Core;
    using Models;

    public class GitHubReleaseService
    {
        private const string DefaultOwner = "hvkeyn";
        private const string DefaultRepo = "InvisibleGorilla-XRayClient";
        private const string UserAgent = "InvisibleGorilla-XRay-Updater";

        private static readonly HttpClient httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public async Task<UpdateInfo> GetLatestReleaseAsync(CancellationToken token = default)
        {
            string apiUrl = $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases/latest";

            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync(apiUrl, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    DiagnosticLog.Write("GitHubReleaseService", $"GET {apiUrl} returned {(int)response.StatusCode}");
                    return null;
                }

                string payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                JObject root = JObject.Parse(payload);

                UpdateInfo info = new UpdateInfo
                {
                    TagName = (string)root["tag_name"] ?? string.Empty,
                    Name = (string)root["name"] ?? string.Empty,
                    Body = (string)root["body"] ?? string.Empty,
                    HtmlUrl = (string)root["html_url"] ?? string.Empty,
                    IsPrerelease = (bool?)root["prerelease"] ?? false
                };
                info.Version = NormalizeVersion(info.TagName);

                if (root["assets"] is JArray assets)
                {
                    foreach (JToken asset in assets)
                    {
                        info.Assets.Add(new ReleaseAsset
                        {
                            Name = (string)asset["name"] ?? string.Empty,
                            DownloadUrl = (string)asset["browser_download_url"] ?? string.Empty,
                            Size = (long?)asset["size"] ?? 0L,
                            ContentType = (string)asset["content_type"] ?? string.Empty
                        });
                    }
                }

                return info;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("GitHubReleaseService.GetLatestReleaseAsync", ex);
                return null;
            }
        }

        public static string NormalizeVersion(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return string.Empty;
            string t = tagName.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase) || t.StartsWith("V", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(1);
            return t;
        }

        public async Task<bool> DownloadAssetAsync(
            ReleaseAsset asset,
            string destinationPath,
            IProgress<double> progress = null,
            CancellationToken token = default)
        {
            if (asset == null || string.IsNullOrEmpty(asset.DownloadUrl))
                return false;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir))
                    System.IO.Directory.CreateDirectory(dir);

                using HttpResponseMessage response = await httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    DiagnosticLog.Write("GitHubReleaseService", $"Download {asset.DownloadUrl} returned {(int)response.StatusCode}");
                    return false;
                }

                long? totalBytes = response.Content.Headers.ContentLength;
                long readSoFar = 0L;

                using Stream sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using FileStream destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                int read;
                while ((read = await sourceStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await destStream.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                    readSoFar += read;

                    if (progress != null && totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double ratio = (double)readSoFar / totalBytes.Value;
                        if (ratio > 1.0)
                            ratio = 1.0;
                        progress.Report(ratio);
                    }
                }

                if (progress != null)
                    progress.Report(1.0);

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("GitHubReleaseService.DownloadAssetAsync", ex);
                try
                {
                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);
                }
                catch
                {
                    // Best-effort cleanup; failures here must never propagate over the original error.
                }
                return false;
            }
        }
    }
}
