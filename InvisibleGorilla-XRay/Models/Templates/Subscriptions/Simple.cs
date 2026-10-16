using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace InvisibleGorillaXRay.Models.Templates.Subscriptions
{
    using Utilities;
    using Values;

    public class Simple : Template
    {
        public override bool IsValid(string link)
        {
            return true;
        }

        public override Status FetchDataFromLink(string link)
        {
            try
            {
                string fetchUrl = StripFragment(link, out string fragment);
                ApplyFragmentTitle(fragment);
                if (!TryDownload(fetchUrl, out string body, out WebHeaderCollection headers, out int status))
                {
                    string fallback = Meta.FallbackUrl;
                    if (string.IsNullOrWhiteSpace(fallback) || !TryDownload(fallback, out body, out headers, out status) || status >= 300)
                        throw new Exception();
                }

                if (status >= 300)
                    throw new Exception();

                ApplyResponseHeaders(headers);
                if (!string.IsNullOrWhiteSpace(headers["encrypt-tag"]))
                {
                    return new Status(
                        Code.ERROR,
                        SubCode.UNSUPPORTED_LINK,
                        "Lang.Android.Status.EncryptedSubscription");
                }

                string retryAgent = headers["change-user-agent"];
                if (!string.IsNullOrWhiteSpace(retryAgent)
                    && TryDownload(fetchUrl, out string retried, out WebHeaderCollection retriedHeaders, out int retriedStatus, retryAgent)
                    && retriedStatus < 300
                    && string.IsNullOrWhiteSpace(retriedHeaders["encrypt-tag"]))
                {
                    body = retried;
                    headers = retriedHeaders;
                    ApplyResponseHeaders(headers);
                }

                Data = body;
                if (!IsAnyDataExisits())
                    throw new Exception();

                if (string.IsNullOrWhiteSpace(ProfileTitle))
                    ProfileTitle = SubscriptionMeta.Decode(headers["profile-title"]);

                return new Status(Code.SUCCESS, SubCode.SUCCESS, null);
            }
            catch
            {
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.UNSUPPORTED_LINK,
                    content: LocalizationService.GetTerm(Localization.UNSUPPORTED_SUBSCRIPTION_LINK)
                );
            }
        }

        private bool TryDownload(string url, out string body, out WebHeaderCollection headers, out int status, string userAgent = null)
        {
            body = null;
            headers = new WebHeaderCollection();
            status = 0;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 9000;
                request.ReadWriteTimeout = 9000;
                request.UserAgent = string.IsNullOrWhiteSpace(userAgent) ? "Happ/3.23.0" : userAgent;
                request.Accept = "*/*";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                string hwid = SubscriptionClientProfile.GetOrCreateHwid();
                if (Meta.HwidInCookie)
                    request.Headers["Cookie"] = hwid;
                else
                    request.Headers["x-hwid"] = hwid;
                request.Headers["x-device-os"] = SubscriptionClientProfile.OperatingSystem;
                request.Headers["x-ver-os"] = SubscriptionClientProfile.OsVersion;
                request.Headers["x-device-model"] = SubscriptionClientProfile.Model;
                request.Headers["x-device-locale"] = System.Globalization.CultureInfo.CurrentUICulture.Name;
                request.Headers["x-api-version"] = "1.0";
                request.Headers["x-app-version"] = "3.6.56";
                if (!string.IsNullOrWhiteSpace(Meta.ProviderId))
                    request.Headers["x-provider-id"] = Meta.ProviderId;
                if (!string.IsNullOrWhiteSpace(Meta.SubExpire))
                    request.Headers["x-sub-expire"] = Meta.SubExpire;
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                    request.Headers["x-domain-hash"] = DomainHash(uri.Host);

                using HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                status = (int)response.StatusCode;
                headers = response.Headers;
                using StreamReader reader = new StreamReader(response.GetResponseStream());
                body = reader.ReadToEnd();
                return true;
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse error)
            {
                status = (int)error.StatusCode;
                headers = error.Headers;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyResponseHeaders(WebHeaderCollection headers)
        {
            if (headers == null)
                return;

            foreach (string key in headers.AllKeys)
                Meta.Apply(key, headers[key]);
        }

        private static string DomainHash(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return string.Empty;

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(host.Trim().ToLowerInvariant()));
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            foreach (byte part in hash)
                builder.Append(part.ToString("x2"));
            return builder.ToString();
        }

        private void ApplyFragmentTitle(string fragment)
        {
            if (string.IsNullOrWhiteSpace(fragment) || !string.IsNullOrWhiteSpace(ProfileTitle))
                return;

            foreach (string part in fragment.Split('#'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = part.Substring(0, eq).Trim();
                if (!key.Equals("profile-title", StringComparison.OrdinalIgnoreCase))
                    continue;
                ProfileTitle = DecodeHeaderText(Uri.UnescapeDataString(part.Substring(eq + 1)));
                return;
            }
        }

        private static string StripFragment(string link, out string fragment)
        {
            fragment = null;
            if (string.IsNullOrWhiteSpace(link))
                return link;

            int hash = link.IndexOf('#');
            if (hash < 0)
                return link;

            fragment = link.Substring(hash + 1);
            return link.Substring(0, hash);
        }

        private static string DecodeHeaderText(string value)
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
                    value = Encoding.UTF8.GetString(Convert.FromBase64String(compact));
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
    }
}