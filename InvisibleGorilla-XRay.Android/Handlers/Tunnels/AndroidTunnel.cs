using System;
using System.Linq;

namespace InvisibleGorillaXRay.Android.Handlers.Tunnels
{
    using InvisibleGorillaXRay.Core;
    using InvisibleGorillaXRay.Handlers;
    using InvisibleGorillaXRay.Android.Handlers.Settings;
    using InvisibleGorillaXRay.Android.Services;
    using InvisibleGorillaXRay.Handlers.Tunnels;
    using InvisibleGorillaXRay.Models;
    using InvisibleGorillaXRay.Services;
    using InvisibleGorillaXRay.Services.OpenFlux;
    using InvisibleGorillaXRay.Handlers.OpenFlux;
    using InvisibleGorillaXRay.Values;

    public sealed class AndroidTunnel : ITunnel
    {
        internal const int OpenFluxHttpProxyPort = 18080;

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();

        public Status Enable(string ip, int port, string address, string server, string dns, LocalProxyCredentials localProxyCredentials)
        {
            DiagnosticLog.Write(
                "AndroidTunnel",
                $"TUN mode requested for proxy={ip}:{port}, address={address}, server={server}, dns={dns}, authEnabled={localProxyCredentials?.HasValue == true}");

            (AppRulesMode appRulesMode, string[] appPackages) = GetAppRulePackages();
            bool openFlux = IsOpenFluxConfig();
            int httpProxyPort = 0;
            if (openFlux)
            {
                DiagnosticLog.Write(
                    "AndroidTunnel",
                    "OpenFlux TUN: IPv4-only, UDP associate disabled (Yandex mux dies on SOCKS UDP/IPv6 floods)");
                httpProxyPort = OpenFluxHttpProxyPort;
            }

            Status startStatus = AndroidVpnServiceController.Start(new AndroidVpnStartOptions
            {
                ProxyPort = port,
                HttpProxyPort = httpProxyPort,
                ProxyUsername = localProxyCredentials?.Username ?? string.Empty,
                ProxyPassword = localProxyCredentials?.Password ?? string.Empty,
                UdpEnabled = !openFlux,
                EnableIpv6 = !openFlux,
                TunAddress = address,
                // DNS must not be the TUN address itself: packets to 10.0.236.10
                // never appear on the TUN fd, so names never resolve.
                Dns = openFlux ? "1.1.1.1" : dns,
                SessionName = "Invisible Gorilla XRay",
                AppRulesMode = appRulesMode,
                AppPackages = appPackages
            });

            if (startStatus.Code == Code.ERROR)
            {
                try { OpenFluxHttpBridge.Shared.Stop(); } catch { }
                string detail = startStatus.Content?.ToString()
                    ?? AndroidVpnServiceController.LastError
                    ?? string.Empty;

                if (detail.Contains("permission", StringComparison.OrdinalIgnoreCase))
                {
                    return new Status(
                        code: Code.ERROR,
                        subCode: SubCode.CANT_TUNNEL,
                        content: LocalizationService.GetTerm("Lang.Android.Status.VpnPermissionDenied"));
                }

                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.CANT_TUNNEL,
                    content: string.Format(
                        LocalizationService.GetTerm("Lang.Android.Status.VpnStartFailed"),
                        detail));
            }

            return new Status(
                code: Code.SUCCESS,
                subCode: SubCode.SUCCESS,
                content: string.Empty);
        }

        public void Disable()
        {
            DiagnosticLog.Write("AndroidTunnel", "Disable requested");
            try { OpenFluxHttpBridge.Shared.Stop(); } catch { }
            AndroidVpnServiceController.Stop();
        }

        public void Cancel()
        {
            DiagnosticLog.Write("AndroidTunnel", "Cancel requested");
            try { OpenFluxHttpBridge.Shared.Stop(); } catch { }
            AndroidVpnServiceController.Stop();
        }

        private static bool IsOpenFluxConfig()
        {
            try
            {
                SettingsHandler settingsHandler = new(() => new AndroidStartup());
                return OpenFluxProfilePaths.IsMarker(settingsHandler.UserSettings.GetCurrentConfigPath());
            }
            catch
            {
                return false;
            }
        }

        private static (AppRulesMode Mode, string[] Packages) GetAppRulePackages()
        {
            SettingsHandler settingsHandler = new(() => new AndroidStartup());
            UserSettings settings = settingsHandler.UserSettings;

            string configPath = settings.GetCurrentConfigPath();
            string boundTemplateId = settings.GetBoundAppRuleTemplateId();
            AppRulesMode mode = settings.GetEffectiveAppRulesMode();

            DiagnosticLog.Write($"[AppRules] GetAppRulePackages: configPath={configPath}, boundTemplate={boundTemplateId}, mode={mode}");

            if (mode == AppRulesMode.ALL_APPS)
            {
                DiagnosticLog.Write("[AppRules] Mode=ALL_APPS → no packages to pass");
                return (mode, Array.Empty<string>());
            }

            string[] packages = settings.GetEffectiveEnabledAppRules()
                .Select(rule => rule.AppId?.Trim())
                .Where(appId => !string.IsNullOrWhiteSpace(appId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

            DiagnosticLog.Write($"[AppRules] Mode={mode}, packages count={packages.Length}: [{string.Join(", ", packages)}]");

            return (mode, packages);
        }
    }
}
