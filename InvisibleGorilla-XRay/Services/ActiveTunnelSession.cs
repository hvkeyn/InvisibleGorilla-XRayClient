using System.Net;

namespace InvisibleGorillaXRay.Services
{
    using Models;
    using Values;

    /// <summary>
    /// Holds the SOCKS credentials for the current xray session so UI probes can
    /// reach the local listener without relying on the TUN capture path.
    /// </summary>
    public static class ActiveTunnelSession
    {
        private static LocalProxyCredentials credentials = LocalProxyCredentials.None;
        private static Mode mode = Mode.PROXY;

        private static int openFluxSocksPort;
        private static int openFluxHttpPort;

        public static void Set(Mode activeMode, LocalProxyCredentials sessionCredentials)
        {
            mode = activeMode;
            credentials = sessionCredentials ?? LocalProxyCredentials.None;
            openFluxSocksPort = 0;
            openFluxHttpPort = 0;
        }

        public static void SetOpenFlux(int socksPort, int httpPort = 0)
        {
            mode = Mode.PROXY;
            credentials = LocalProxyCredentials.None;
            openFluxSocksPort = socksPort;
            openFluxHttpPort = httpPort;
        }

        public static bool IsOpenFlux => openFluxSocksPort > 0 || openFluxHttpPort > 0;

        public static void Clear()
        {
            mode = Mode.PROXY;
            credentials = LocalProxyCredentials.None;
            openFluxSocksPort = 0;
            openFluxHttpPort = 0;
        }

        public static IWebProxy? BuildProbeProxy(bool connected, Mode settingsMode, int proxyPort)
        {
            if (!connected)
                return null;

            if (openFluxSocksPort > 0)
                return new WebProxy($"socks5://{Global.LOCAL_HOST}:{openFluxSocksPort}");

            if (openFluxHttpPort > 0)
                return new WebProxy($"http://{Global.LOCAL_HOST}:{openFluxHttpPort}");

            if (settingsMode == Mode.TUN && credentials.HasValue)
            {
                // .NET SOCKS5 auth comes from WebProxy.Credentials, not URI userinfo.
                return new WebProxy($"socks5://{Global.LOCAL_HOST}:{proxyPort}")
                {
                    Credentials = new NetworkCredential(credentials.Username, credentials.Password)
                };
            }

            return null;
        }
    }
}
