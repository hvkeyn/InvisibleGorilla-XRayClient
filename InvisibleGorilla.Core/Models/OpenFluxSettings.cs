using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Models
{
    public enum OpenFluxTransportMode
    {
        Auto,
        Vyandex,
        Yandex
    }

    /// <summary>
    /// One Yandex-document tunnel profile. Shown in the server list like Goida.
    /// EncryptionKey is stored in Settings.json (user data) and never logged.
    /// </summary>
    public class OpenFluxProfile
    {
        public const int DefaultSocksPort = 11080;
        public const string DefaultCodec = "batched";

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Name;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string DocUrl;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public OpenFluxTransportMode Transport;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Codec;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string EncryptionKey;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int SocksPort;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int LastLatencyMs;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string ConfigPath;

        public OpenFluxProfile()
        {
            Name = "OpenFlux";
            DocUrl = "";
            Transport = OpenFluxTransportMode.Auto;
            Codec = DefaultCodec;
            EncryptionKey = "";
            SocksPort = DefaultSocksPort;
            LastLatencyMs = -1;
            ConfigPath = "";
        }

        public int GetSocksPort() => SocksPort > 0 ? SocksPort : DefaultSocksPort;

        public string GetCodec() => string.IsNullOrWhiteSpace(Codec) ? DefaultCodec : Codec.Trim();

        public OpenFluxProfile Clone()
        {
            return new OpenFluxProfile
            {
                Name = Name ?? "OpenFlux",
                DocUrl = DocUrl ?? "",
                Transport = Transport,
                Codec = GetCodec(),
                EncryptionKey = EncryptionKey ?? "",
                SocksPort = GetSocksPort(),
                LastLatencyMs = LastLatencyMs,
                ConfigPath = ConfigPath ?? ""
            };
        }
    }

    public static class OpenFluxUrl
    {
        public static string Trim(string url) => Canonicalize((url ?? "").Trim());

        public static string DeriveKey(string url)
        {
            string canonical = Trim(url);
            if (string.IsNullOrWhiteSpace(canonical))
                return "";
            byte[] data = Encoding.UTF8.GetBytes("OpenFlux document key v1\0" + canonical);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }


        public static string Canonicalize(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "";

            string cleaned = string.Concat(url.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!Uri.TryCreate(cleaned, UriKind.Absolute, out Uri uri))
                return cleaned;

            string host = (uri.Host ?? "").ToLowerInvariant();
            string path = uri.AbsolutePath ?? "";
            bool editDoc = path.IndexOf("/edit/d/", StringComparison.OrdinalIgnoreCase) >= 0;
            if (editDoc && (host == "docs.yandex.ru" || host.EndsWith(".docs.yandex.ru")
                || host == "volga.yandex.ru" || host.EndsWith(".volga.yandex.ru")))
            {
                var builder = new UriBuilder(uri)
                {
                    Host = "disk.yandex.ru",
                    Scheme = "https",
                    Port = -1
                };
                return builder.Uri.ToString();
            }

            return uri.ToString();
        }

        public static bool TryValidate(string url, out string error)
        {
            error = "";
            string trimmed = Trim(url);
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                error = "empty";
                return false;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "scheme";
                return false;
            }

            string host = uri.Host ?? "";
            if (!host.EndsWith("disk.yandex.ru", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("docs.yandex.ru", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith("docs.yandex.ru", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith("volga.yandex.ru", StringComparison.OrdinalIgnoreCase))
            {
                error = "host";
                return false;
            }

            return true;
        }

        public static string ResolveTransport(string url, OpenFluxTransportMode mode)
        {
            if (mode == OpenFluxTransportMode.Vyandex)
                return "vyandex";
            if (mode == OpenFluxTransportMode.Yandex)
                return "yandex";

            if (!Uri.TryCreate(Trim(url), UriKind.Absolute, out Uri uri))
                return "vyandex";

            string host = uri.Host ?? "";
            string path = uri.AbsolutePath ?? "";
            if (host.IndexOf("disk.yandex.ru", StringComparison.OrdinalIgnoreCase) >= 0
                || host.IndexOf("volga.yandex.ru", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("/edit/d/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "vyandex";
            }

            if (host.IndexOf("docs.yandex.ru", StringComparison.OrdinalIgnoreCase) >= 0)
                return "yandex";

            return "vyandex";
        }
    }
}
