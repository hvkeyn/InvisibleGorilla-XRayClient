using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Handlers
{
    using Models;
    using Values;
    using Utilities;
    using Settings.Startup;

    public class SettingsHandler : Handler
    {
        private UserSettings userSettings;

        public UserSettings UserSettings => userSettings;

        public SettingsHandler()
        {
            this.userSettings = LoadUserSettings();
        }

        public void UpdateUserSettings(UserSettings userSettings)
        {
            this.userSettings.Language = userSettings.Language;
            this.userSettings.Mode = userSettings.Mode;
            this.userSettings.Protocol = userSettings.Protocol;
            this.userSettings.LogLevel = userSettings.LogLevel;
            this.userSettings.IsSystemProxyUse = userSettings.IsSystemProxyUse;
            this.userSettings.IsUdpEnable = userSettings.IsUdpEnable;
            this.userSettings.IsRunningAtStartup = userSettings.IsRunningAtStartup;
            this.userSettings.IsStartHidden = userSettings.IsStartHidden;
            this.userSettings.IsAutoConnect = userSettings.IsAutoConnect;
            this.userSettings.IsSendingAnalytics = userSettings.IsSendingAnalytics;
            this.userSettings.ProxyPort = userSettings.ProxyPort;
            this.userSettings.TunPort = userSettings.TunPort;
            this.userSettings.TestPort = userSettings.TestPort;
            this.userSettings.TunIp = userSettings.TunIp;
            this.userSettings.Dns = userSettings.Dns;
            this.userSettings.LogPath = userSettings.LogPath;
            this.userSettings.AppRulesMode = userSettings.AppRulesMode;
            this.userSettings.AppRules = CloneAppRules(userSettings.AppRules);

            UpdateStartupSetting();
            SaveUserSettings();
        }

        public void GenerateClientId()
        {
            userSettings.ClientId = IdentificationUtility.GenerateClientId();
            SaveUserSettings();
        }

        public void UpdateCurrentConfigPath(string path)
        {
            userSettings.CurrentConfigPath = string.IsNullOrEmpty(path) ? Directory.CONFIGS : path;
            SaveUserSettings();
        }

        public void UpdateMode(Mode mode)
        {
            userSettings.Mode = mode;
            SaveUserSettings();
        }

        private void UpdateStartupSetting()
        {
            IStartupSetting startupSetting = new WindowsStartupSetting();

            if (userSettings.IsRunningAtStartup)
                startupSetting.EnableRunAtStartup();
            else
                startupSetting.DisableRunAtStartup();
        }

        private UserSettings LoadUserSettings()
        {
            if (!File.Exists(Path.USER_SETTINGS))
                return NormalizeRules(new UserSettings());

            string rawSettings = File.ReadAllText(Path.USER_SETTINGS);
            if (!JsonUtility.IsJsonValid(rawSettings))
                return NormalizeRules(new UserSettings());

            return NormalizeRules(JsonConvert.DeserializeObject<UserSettings>(rawSettings));

            UserSettings NormalizeRules(UserSettings settings)
            {
                settings.AppRules ??= new System.Collections.Generic.List<AppRule>();
                settings.AppRules = CloneAppRules(settings.AppRules);
                return settings;
            }
        }

        private void SaveUserSettings()
        {
            string rawSettings = JsonConvert.SerializeObject(userSettings);
            File.WriteAllText(Path.USER_SETTINGS, rawSettings);
        }

        private static System.Collections.Generic.List<AppRule> CloneAppRules(System.Collections.Generic.IEnumerable<AppRule>? appRules)
        {
            if (appRules == null)
                return new System.Collections.Generic.List<AppRule>();

            return appRules
                .Where(rule => rule != null && !string.IsNullOrWhiteSpace(rule.AppId))
                .Select(rule => rule.Clone())
                .ToList();
        }
    }
}