using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Mac.Handlers.Tunnels
{
    using InvisibleGorillaXRay.Core;
    using InvisibleGorillaXRay.Models;

    internal sealed class MacTransparentProxyBridgeConfig
    {
        public string Mode { get; init; } = AppRulesMode.ALL_APPS.ToString();
        public int SocksPort { get; init; }
        public string TunnelAddress { get; init; } = string.Empty;
        public string Dns { get; init; } = string.Empty;
        public List<string> BundleIdentifiers { get; init; } = new();
        public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    }

    internal static class MacAppRulesBridge
    {
        private static string ConfigPath => Path.Combine(Values.Directory.TUN, "mac-transparent-proxy-config.json");
        private static string HelperRoot => Path.Combine(Values.Directory.ROOT, "Native", "TransparentProxyExtension");

        public static Status Prepare(UserSettings settings, int socksPort, string tunnelAddress, string dns)
        {
            try
            {
                Values.Directory.EnsureWritableDirectories();
                AppRulesMode mode = settings.GetEffectiveAppRulesMode();
                List<string> bundleIds = settings.GetEffectiveEnabledAppRules()
                    .Select(rule => rule.AppId?.Trim())
                    .Where(appId => !string.IsNullOrWhiteSpace(appId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()!;

                MacTransparentProxyBridgeConfig config = new()
                {
                    Mode = mode.ToString(),
                    SocksPort = socksPort,
                    TunnelAddress = tunnelAddress ?? string.Empty,
                    Dns = dns ?? string.Empty,
                    BundleIdentifiers = bundleIds
                };

                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
                DiagnosticLog.Write(
                    "MacAppRulesBridge",
                    $"Prepared transparent proxy config with mode={mode} and {bundleIds.Count} bundle ids at {ConfigPath}");

                if (!Directory.Exists(HelperRoot))
                {
                    DiagnosticLog.Write(
                        "MacAppRulesBridge",
                        $"Transparent proxy helper scaffold was not found at {HelperRoot}");
                }

                return new Status(Code.SUCCESS, SubCode.SUCCESS, null);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MacAppRulesBridge.Prepare", ex);
                return new Status(Code.ERROR, SubCode.CANT_TUNNEL, $"Failed to prepare macOS app rules: {ex.Message}");
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    File.Delete(ConfigPath);
                    DiagnosticLog.Write("MacAppRulesBridge", $"Cleared transparent proxy config: {ConfigPath}");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MacAppRulesBridge.Clear", ex);
            }
        }
    }
}
