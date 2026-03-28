using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Models
{
    public enum Mode { PROXY, TUN }

    public enum Protocol { HTTP, SOCKS }

    public enum LogLevel { NONE, DEBUG, INFO, WARNING, ERROR }

    public class UserSettings
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("")]
        public string ClientId;

        [JsonProperty(PropertyName = "language", DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("en-US")]
        public string Language;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("./Configs")]
        public string CurrentConfigPath;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(Mode.PROXY)]
        public Mode Mode;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(Protocol.HTTP)]
        public Protocol Protocol;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
        public bool IsSystemProxyUse;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
        public bool IsUdpEnable;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(false)]
        public bool IsRunningAtStartup;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(false)]
        public bool IsStartHidden;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(false)]
        public bool IsAutoConnect;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
        public bool IsSendingAnalytics;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(10801)]
        public int ProxyPort;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(10802)]
        public int TunPort;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(10803)]
        public int TestPort;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("10.0.236.10")]
        public string TunIp;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("8.8.8.8")]
        public string Dns;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(LogLevel.NONE)]
        public LogLevel LogLevel;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("./Logs")]
        public string LogPath;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(AppRulesMode.DISABLED)]
        public AppRulesMode AppRulesMode;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public List<AppRule> AppRules;

        public UserSettings()
        {
            this.ClientId = "";
            this.Language = "en-US";
            this.CurrentConfigPath = "./Configs";
            this.Mode = Mode.PROXY;
            this.Protocol = Protocol.HTTP;
            this.IsSystemProxyUse = true;
            this.IsUdpEnable = true;
            this.IsRunningAtStartup = false;
            this.IsStartHidden = false;
            this.IsAutoConnect = false;
            this.IsSendingAnalytics = true;
            this.TunIp = "10.0.236.10";
            this.ProxyPort = 10801;
            this.TunPort = 10802;
            this.TestPort = 10803;
            this.Dns = "8.8.8.8";
            this.LogLevel = LogLevel.NONE;
            this.LogPath = "./Logs";
            this.AppRulesMode = AppRulesMode.DISABLED;
            this.AppRules = new List<AppRule>();
        }

        public UserSettings(
            string language,
            Mode mode,
            Protocol protocol,
            LogLevel logLevel,
            bool isUdpEnable,
            bool isSystemProxyUse,
            bool isRunningAtStartup,
            bool isStartHidden,
            bool isAutoConnect,
            bool isSendingAnalytics,
            int proxyPort,
            int tunPort,
            int testPort,
            string tunIp,
            string dns,
            string logPath,
            AppRulesMode appRulesMode = AppRulesMode.DISABLED,
            List<AppRule>? appRules = null
        )
        {
            this.Language = language;
            this.Mode = mode;
            this.Protocol = protocol;
            this.LogLevel = logLevel;
            this.IsSystemProxyUse = isSystemProxyUse;
            this.IsUdpEnable = isUdpEnable;
            this.IsRunningAtStartup = isRunningAtStartup;
            this.IsStartHidden = isStartHidden;
            this.IsAutoConnect = isAutoConnect;
            this.IsSendingAnalytics = isSendingAnalytics;
            this.ProxyPort = proxyPort;
            this.TunPort = tunPort;
            this.TestPort = testPort;
            this.TunIp = tunIp;
            this.Dns = dns;
            this.LogPath = logPath;
            this.AppRulesMode = appRulesMode;
            this.AppRules = NormalizeAppRules(appRules);
        }

        public string GetClientId() => ClientId;

        public string GetLanguage() => Language;

        public string GetCurrentConfigPath() => CurrentConfigPath;

        public Mode GetMode() => Mode;

        public Protocol GetProtocol() => Protocol;

        public bool GetSystemProxyUsed() => IsSystemProxyUse;

        public bool GetUdpEnabled() => IsUdpEnable;

        public bool GetRunningAtStartupEnabled() => IsRunningAtStartup;

        public bool GetStartHiddenEnabled() => IsStartHidden;

        public bool GetAutoConnectEnabled() => IsAutoConnect;

        public bool GetSendingAnalyticsEnabled() => IsSendingAnalytics;

        public int GetProxyPort() => ProxyPort;

        public int GetTunPort() => TunPort;

        public int GetTestPort() => TestPort;

        public string GetTunIp() => TunIp;

        public string GetDns() => Dns;

        public LogLevel GetLogLevel() => LogLevel;

        public string GetLogPath() => LogPath;

        public AppRulesMode GetAppRulesMode() => AppRulesMode;

        public List<AppRule> GetAppRules() => NormalizeAppRules(AppRules);

        public List<AppRule> GetEnabledAppRules() => NormalizeAppRules(AppRules).Where(rule => rule.Enabled).ToList();

        private static List<AppRule> NormalizeAppRules(List<AppRule>? appRules)
        {
            if (appRules == null)
                return new List<AppRule>();

            return appRules
                .Where(rule => rule != null && !string.IsNullOrWhiteSpace(rule.AppId))
                .Select(rule => rule.Clone())
                .ToList();
        }
    }
}