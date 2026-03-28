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
    using InvisibleGorillaXRay.Values;

    public sealed class AndroidTunnel : ITunnel
    {
        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();

        public Status Enable(string ip, int port, string address, string server, string dns)
        {
            DiagnosticLog.Write(
                "AndroidTunnel",
                $"TUN mode requested for proxy={ip}:{port}, address={address}, server={server}, dns={dns}");

            Status startStatus = AndroidVpnServiceController.Start(new AndroidVpnStartOptions
            {
                ProxyPort = port,
                UdpEnabled = true,
                TunAddress = address,
                Dns = dns,
                SessionName = "Invisible Gorilla XRay",
                BypassPackages = GetBypassPackages()
            });

            if (startStatus.Code == Code.ERROR)
            {
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
            AndroidVpnServiceController.Stop();
        }

        public void Cancel()
        {
            DiagnosticLog.Write("AndroidTunnel", "Cancel requested");
            AndroidVpnServiceController.Stop();
        }

        private static string[] GetBypassPackages()
        {
            SettingsHandler settingsHandler = new(() => new AndroidStartup());
            UserSettings settings = settingsHandler.UserSettings;
            if (settings.GetAppRulesMode() != AppRulesMode.BYPASS_SELECTED_APPS)
                return Array.Empty<string>();

            return settings.GetEnabledAppRules()
                .Select(rule => rule.AppId?.Trim())
                .Where(appId => !string.IsNullOrWhiteSpace(appId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }
    }
}
