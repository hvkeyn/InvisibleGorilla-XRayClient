using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Models
{
    public class SubscriptionMeta
    {
        public int UpdateIntervalHours { get; set; }
        public string UserInfo { get; set; }
        public string Announce { get; set; }
        public string SupportUrl { get; set; }
        public string WebPageUrl { get; set; }
        public string FallbackUrl { get; set; }
        public string NewUrl { get; set; }
        public string ProviderId { get; set; }
        public string SubExpire { get; set; }
        public bool HwidInCookie { get; set; }
        public string Notice { get; set; }
        public long UpdatedAtUnix { get; set; }

        public void Apply(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            string text = Decode(value);
            switch (key.Trim().ToLowerInvariant())
            {
                case "profile-update-interval":
                    if (int.TryParse(text, out int hours) && hours > 0)
                        UpdateIntervalHours = hours;
                    break;
                case "subscription-userinfo":
                    UserInfo = text;
                    break;
                case "announce":
                    Announce = text;
                    break;
                case "support-url":
                    SupportUrl = text;
                    break;
                case "profile-web-page-url":
                case "homepage":
                    WebPageUrl = text;
                    break;
                case "fallback-url":
                    FallbackUrl = text;
                    break;
                case "new-url":
                    NewUrl = text;
                    break;
                case "providerid":
                    ProviderId = text;
                    break;
                case "subscription-hwid-in-cookie-enable":
                    HwidInCookie = IsOn(text);
                    break;
            }
        }

        public void AddNotice(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            string trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(Notice) && Notice.IndexOf(trimmed, StringComparison.Ordinal) >= 0)
                return;

            Notice = string.IsNullOrWhiteSpace(Notice) ? trimmed : Notice + "\n" + trimmed;
        }

        public string FormatUserInfo()
        {
            if (string.IsNullOrWhiteSpace(UserInfo) || UserInfo.Trim() == "0")
                return null;

            long upload = 0, download = 0, total = 0, expire = 0;
            foreach (string part in UserInfo.Split(';'))
            {
                string[] pair = part.Split(new[] { '=' }, 2);
                if (pair.Length != 2)
                    continue;
                long.TryParse(pair[1].Trim(), out long number);
                switch (pair[0].Trim().ToLowerInvariant())
                {
                    case "upload": upload = number; break;
                    case "download": download = number; break;
                    case "total": total = number; break;
                    case "expire": expire = number; break;
                }
            }

            string used = FormatBytes(upload + download);
            string limit = total > 0 ? FormatBytes(total) : "∞";
            string until = string.Empty;
            if (expire > 32000000000)
                expire /= 1000;
            if (expire > 0)
            {
                try
                {
                    until = " · " + DateTimeOffset.FromUnixTimeSeconds(expire).LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
                }
                catch
                {
                }
            }

            return used + " / " + limit + until;
        }

        public static SubscriptionMeta Load(string directory)
        {
            try
            {
                string path = Path.Combine(directory, "meta.dat");
                if (!File.Exists(path))
                    return new SubscriptionMeta();
                return JsonConvert.DeserializeObject<SubscriptionMeta>(File.ReadAllText(path)) ?? new SubscriptionMeta();
            }
            catch
            {
                return new SubscriptionMeta();
            }
        }

        public void Save(string directory)
        {
            UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "meta.dat"), JsonConvert.SerializeObject(this));
        }

        public static string Decode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim().Trim('"');
            if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string compact = value.Substring("base64:".Length).Trim().Replace('-', '+').Replace('_', '/');
                    int pad = compact.Length % 4;
                    if (pad > 0)
                        compact += new string('=', 4 - pad);
                    value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(compact));
                }
                catch
                {
                }
            }

            int line = value.IndexOfAny(new[] { '\r', '\n' });
            if (line > 0)
                value = value.Substring(0, line);
            value = value.Trim();
            return value.Length == 0 ? null : value;
        }

        private static bool IsOn(string value)
        {
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024)
                return (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " GB";
        }
    }

    public static class SubscriptionFetchContext
    {
        [ThreadStatic]
        public static SubscriptionMeta Previous;
    }
}
