using System;
using System.Web;
using System.Collections.Specialized;

namespace InvisibleGorillaXRay.Models.Templates.Configs
{
    using Values;

    public class Vless : Template
    {
        public class Data
        {
            public Uri uri;
            public NameValueCollection query;

            public Data(string url)
            {
                this.uri = new Uri(url);
                this.query = HttpUtility.ParseQueryString(uri.Query);
            }
        }

        private Data data;

        public override Status FetchDataFromLink(string link) 
        {
            try
            {
                data = new Data(NormalizeLink(link));
            }
            catch
            {
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.INVALID_CONFIG,
                    content: LocalizationService.GetTerm(Localization.INVALID_CONFIG)
                );
            }

            if (IsInvalidLink())
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.INVALID_CONFIG,
                    content: LocalizationService.GetTerm(Localization.INVALID_CONFIG)
                );

            return new Status(Code.SUCCESS, SubCode.SUCCESS, null);

            bool IsInvalidLink() =>
                data?.uri == null
                || string.IsNullOrWhiteSpace(data.uri.IdnHost)
                || data.uri.Port <= 0
                || string.IsNullOrWhiteSpace(data.uri.UserInfo);
        }

        protected override Adapter Adapter
        {
            get
            {
                Adapter adapter = new Adapter() {
                    type = "vless",
                    remark = SafeFragment(data.uri),
                    address = data.uri.IdnHost,
                    port = data.uri.Port,
                    id = data.uri.UserInfo,
                    security = data.query["encryption"] ?? "none",
                    streamNetwork = data.query["type"] ?? "tcp",
                    streamSecurity = data.query["security"] ?? "",
                    flow = data.query["flow"] ?? "",
                    sni = data.query["sni"] ?? "",
                    alpn = HttpUtility.UrlDecode(data.query["alpn"] ?? ""),
                    allowInsecure = HttpUtility.UrlDecode(data.query["allowInsecure"] ?? "") == "1" ? true : false,
                    fingerprint = HttpUtility.UrlDecode(data.query["fp"] ?? ""),
                    publicKey = HttpUtility.UrlDecode(data.query["pbk"] ?? ""),
                    shortId = HttpUtility.UrlDecode(data.query["sid"] ?? ""),
                    spiderX = HttpUtility.UrlDecode(data.query["spx"] ?? ""),
                };

                switch (adapter.streamNetwork)
                {
                    case "tcp":
                        adapter.headerType = data.query["headerType"] ?? "none";
                        adapter.requestHost = HttpUtility.UrlDecode(data.query["host"] ?? "");
                        break;
                    case "kcp":
                        adapter.headerType = data.query["headerType"] ?? "none";
                        adapter.path = HttpUtility.UrlDecode(data.query["seed"] ?? "");
                        break;
                    case "ws":
                        adapter.requestHost = HttpUtility.UrlDecode(data.query["host"] ?? "");
                        adapter.path = HttpUtility.UrlDecode(data.query["path"] ?? "/");
                        break;
                    case "http":
                    case "h2":
                        adapter.streamNetwork = "h2";
                        adapter.requestHost = HttpUtility.UrlDecode(data.query["host"] ?? "");
                        adapter.path = HttpUtility.UrlDecode(data.query["path"] ?? "/");
                        break;
                    case "quic":
                        adapter.headerType = data.query["headerType"] ?? "none";
                        adapter.requestHost = data.query["quicSecurity"] ?? "none";
                        adapter.path = HttpUtility.UrlDecode(data.query["key"] ?? "");
                        break;
                    case "grpc":
                        adapter.path = HttpUtility.UrlDecode(data.query["serviceName"] ?? "");
                        adapter.headerType = HttpUtility.UrlDecode(data.query["mode"] ?? "gun");
                        break;
                    case "xhttp":
                    case "splithttp":
                        adapter.streamNetwork = Global.StreamNetwork.XHTTP;
                        adapter.requestHost = HttpUtility.UrlDecode(data.query["host"] ?? "");
                        adapter.path = HttpUtility.UrlDecode(data.query["path"] ?? "/");
                        adapter.headerType = HttpUtility.UrlDecode(data.query["mode"] ?? "auto");
                        break;
                    case "httpupgrade":
                        adapter.streamNetwork = Global.StreamNetwork.HTTPUPGRADE;
                        adapter.requestHost = HttpUtility.UrlDecode(data.query["host"] ?? "");
                        adapter.path = HttpUtility.UrlDecode(data.query["path"] ?? "/");
                        break;
                    default:
                        break;
                }

                return adapter;
            }
        }

        protected override V2Ray.Outbound.Settings OutboundSettings
        {
            get
            {
                // Adapter is a computed property: every access returns a new object, so the
                // old `Adapter.flow = …` defaults were written to a throw-away instance and
                // lost. Compute the effective flow once and use it below.
                Adapter adapter = Adapter;
                string flow = adapter.flow;

                if (adapter.streamSecurity == "reality")
                {
                    if (string.IsNullOrEmpty(flow))
                        flow = "xtls-rprx-vision";
                }
                else if (adapter.streamSecurity == "xtls")
                {
                    flow = string.IsNullOrEmpty(flow)
                        ? "xtls-rprx-origin"
                        : flow.Replace("splice", "direct");
                }

                return new V2Ray.Outbound.Settings() {
                    vnext = new V2Ray.Outbound.Settings.Vnext[] {
                        new V2Ray.Outbound.Settings.Vnext() {
                            address = adapter.address,
                            port = adapter.port,
                            users = new V2Ray.Outbound.Settings.Vnext.User[] {
                                new V2Ray.Outbound.Settings.Vnext.User() {
                                    id = adapter.id,
                                    flow = flow,
                                    email = Global.DEFAULT_EMAIL,
                                    encryption = adapter.security
                                }
                            }
                        }
                    }
                };
            }
        }

        private static string NormalizeLink(string link)
        {
            string value = (link ?? string.Empty).Trim().Trim('"', '\'');
            if (value.Length == 0)
                return value;

            int hash = value.IndexOf('#');
            string fragment = hash >= 0 ? value.Substring(hash + 1) : null;
            string head = hash >= 0 ? value.Substring(0, hash) : value;

            int scheme = head.IndexOf("://", StringComparison.Ordinal);
            int at = scheme >= 0 ? head.IndexOf('@', scheme + 3) : -1;
            if (at > 0)
            {
                int query = head.IndexOf('?', at);
                int hostEnd = query >= 0 ? query : head.Length;
                string hostPort = head.Substring(at + 1, hostEnd - (at + 1));
                bool hasPort;
                if (hostPort.StartsWith("[", StringComparison.Ordinal))
                {
                    int bracket = hostPort.IndexOf(']');
                    hasPort = bracket >= 0 && hostPort.IndexOf(':', bracket + 1) > bracket;
                }
                else
                {
                    hasPort = hostPort.IndexOf(':') >= 0;
                }

                if (!hasPort && hostPort.Length > 0)
                    head = head.Insert(hostEnd, ":443");
            }

            if (fragment == null)
                return head;

            string decoded = fragment;
            try
            {
                decoded = Uri.UnescapeDataString(fragment);
            }
            catch
            {
            }

            return head + "#" + Uri.EscapeDataString(decoded);
        }

        private static string SafeFragment(Uri uri)
        {
            try
            {
                return uri?.GetComponents(UriComponents.Fragment, UriFormat.Unescaped) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}