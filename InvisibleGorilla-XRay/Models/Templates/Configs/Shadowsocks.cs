using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InvisibleGorillaXRay.Models.Templates.Configs
{
    using Values;

    public class Shadowsocks : Template
    {
        private string remark = string.Empty;
        private string address = string.Empty;
        private int port;
        private string security = string.Empty;
        private string id = string.Empty;

        private static readonly HashSet<string> validSecurity = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "aes-256-gcm",
            "aes-128-gcm",
            "chacha20-poly1305",
            "chacha20-ietf-poly1305",
            "xchacha20-poly1305",
            "xchacha20-ietf-poly1305",
            "none",
            "plain",
            "2022-blake3-aes-128-gcm",
            "2022-blake3-aes-256-gcm",
            "2022-blake3-chacha20-poly1305"
        };

        public override Status FetchDataFromLink(string link)
        {
            try
            {
                return TryParse(link)
                    ? new Status(Code.SUCCESS, SubCode.SUCCESS, null)
                    : Fail();
            }
            catch (Exception)
            {
                return Fail();
            }
        }

        private bool TryParse(string link)
        {
            remark = string.Empty;
            address = string.Empty;
            port = 0;
            security = string.Empty;
            id = string.Empty;

            if (string.IsNullOrWhiteSpace(link))
                return false;

            string body = link.Trim();
            if (!body.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
                return false;

            // The fragment is optional in SIP002; the previous parser indexed [1] and
            // rejected every ss:// link without a remark.
            int hash = body.IndexOf('#');
            if (hash >= 0)
            {
                string rawRemark = body.Substring(hash + 1);
                body = body.Substring(0, hash);
                try { remark = Uri.UnescapeDataString(rawRemark); } catch { remark = rawRemark; }
            }

            body = body.Substring("ss://".Length);

            // Plugins (?plugin=...) are not supported by the core config used here.
            int query = body.IndexOf('?');
            if (query >= 0)
                body = body.Substring(0, query);

            body = body.TrimEnd('/');
            if (body.Length == 0)
                return false;

            int at = body.LastIndexOf('@');
            string userInfo;
            string hostPort;

            if (at >= 0)
            {
                userInfo = body.Substring(0, at);
                hostPort = body.Substring(at + 1);
            }
            else
            {
                // Legacy form: ss://BASE64(method:password@host:port)
                string decoded = DecodeBase64(body);
                if (string.IsNullOrEmpty(decoded))
                    return false;

                int legacyAt = decoded.LastIndexOf('@');
                if (legacyAt < 0)
                    return false;

                userInfo = decoded.Substring(0, legacyAt);
                hostPort = decoded.Substring(legacyAt + 1);
            }

            // SIP002 allows both BASE64 and plain (percent-encoded) userinfo.
            string credentials = DecodeBase64(userInfo) ?? SafeUnescape(userInfo);
            if (string.IsNullOrEmpty(credentials))
                return false;

            int colon = credentials.IndexOf(':');
            if (colon <= 0 || colon == credentials.Length - 1)
                return false;

            security = credentials.Substring(0, colon).Trim();
            // Everything after the first colon is the password: a ':' inside it used to be
            // truncated and produced authentication failures against the server.
            id = credentials.Substring(colon + 1);

            if (!Uri.TryCreate("http://" + hostPort, UriKind.Absolute, out Uri endpoint))
                return false;

            address = endpoint.Host;
            port = endpoint.Port;

            return !string.IsNullOrWhiteSpace(security)
                && !string.IsNullOrWhiteSpace(id)
                && !string.IsNullOrWhiteSpace(address)
                && port > 0 && port <= 65535;
        }

        private Status Fail() =>
            new Status(
                code: Code.ERROR,
                subCode: SubCode.INVALID_CONFIG,
                content: LocalizationService.GetTerm(Localization.INVALID_CONFIG)
            );

        private static string DecodeBase64(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string compact = value.Trim()
                .Replace('-', '+')
                .Replace('_', '/');

            int padding = compact.Length % 4;
            if (padding > 0)
                compact += new string('=', 4 - padding);

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(compact));
            }
            catch
            {
                return null;
            }
        }

        private static string SafeUnescape(string value)
        {
            try { return Uri.UnescapeDataString(value); }
            catch { return value; }
        }

        protected override Adapter Adapter => new Adapter() {
            type = "shadowsocks",
            remark = remark,
            address = address,
            port = port,
            id = id,
            security = security
        };

        protected override V2Ray.Outbound.Settings OutboundSettings => new V2Ray.Outbound.Settings() {
            servers = new V2Ray.Outbound.Settings.Server[] {
                new V2Ray.Outbound.Settings.Server() {
                    address = Adapter.address,
                    port = Adapter.port,
                    password = Adapter.id,
                    ota = false,
                    level = 1,
                    // An unknown method is passed through: silently downgrading to "none"
                    // would disable encryption instead of surfacing a config error.
                    method = validSecurity.Contains(Adapter.security) ? Adapter.security : Adapter.security
                }
            }
        };
    }
}
