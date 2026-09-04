using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaSubscriptionNormalizer
    {
        private static readonly string[] ConvertiblePrefixes =
        {
            "vmess://", "vless://", "trojan://", "ss://"
        };

        private static readonly Regex GluedProtocolRegex = new(
            @"(?=(?:vmess|vless|trojan|ss|ssr|socks5|socks4|hysteria2|hysteria|hy2|tuic)://)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex InsecureRegex = new(
            @"(?:[?&;])(allowinsecure|allow_insecure|insecure)=(?:1|true|yes)(?:[&;#]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Normalize(string rawData)
        {
            if (string.IsNullOrWhiteSpace(rawData))
                return string.Empty;

            string data = rawData.Trim();
            if (LooksLikeHtmlError(data))
                return string.Empty;

            data = TryDecodeBase64(data);
            data = WebUtility.HtmlDecode(data);
            data = SplitGluedProtocols(data);

            List<string> lines = new();
            foreach (string rawLine in data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (!HasConvertiblePrefix(line))
                    continue;

                if (InsecureRegex.IsMatch(line))
                    continue;

                lines.Add(line);
            }

            return string.Join("\n", lines);
        }

        public static bool LooksLikeHtmlError(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return false;

            string trimmed = body.TrimStart();
            if (!trimmed.StartsWith("<", StringComparison.Ordinal)
                && trimmed.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) < 0
                && trimmed.IndexOf("<html", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return !HasConvertiblePrefix(body) && TryDecodeBase64(body) == body;
        }

        public static string TryDecodeBase64(string data)
        {
            if (string.IsNullOrWhiteSpace(data) || HasConvertiblePrefix(data))
                return data;

            try
            {
                string compact = Regex.Replace(data, @"\s+", string.Empty);
                int pad = compact.Length % 4;
                if (pad > 0)
                    compact += new string('=', 4 - pad);

                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(compact));
                return HasConvertiblePrefix(decoded) ? decoded : data;
            }
            catch
            {
                return data;
            }
        }

        private static string SplitGluedProtocols(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return data;

            return GluedProtocolRegex.Replace(data, "\n").Trim();
        }

        private static bool HasConvertiblePrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (string prefix in ConvertiblePrefixes)
            {
                if (value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }
    }
}