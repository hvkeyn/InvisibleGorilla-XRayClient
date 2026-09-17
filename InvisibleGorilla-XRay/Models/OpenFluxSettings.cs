using System;
using System.Collections.Generic;
using System.Linq;
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
        public static string Trim(string url) => (url ?? "").Trim();

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
