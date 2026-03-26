using System;
using System.IO;
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
        private Func<IStartupSetting> startupFactory;

        public UserSettings UserSettings => userSettings;

        public SettingsHandler(Func<IStartupSetting> startupFactory)
        {
            this.startupFactory = startupFactory;
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
            this.userSettings.LogPath = NormalizePath(userSettings.LogPath, Values.Directory.LOGS);

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
            userSettings.CurrentConfigPath = NormalizePath(path, Values.Directory.CONFIGS);
            SaveUserSettings();
        }

        public void UpdateMode(Mode mode)
        {
            userSettings.Mode = mode;
            SaveUserSettings();
        }

        private void UpdateStartupSetting()
        {
            IStartupSetting startupSetting = startupFactory();

            if (userSettings.IsRunningAtStartup)
                startupSetting.EnableRunAtStartup();
            else
                startupSetting.DisableRunAtStartup();
        }

        private UserSettings LoadUserSettings()
        {
            Values.Directory.EnsureWritableDirectories();

            if (!File.Exists(Path.USER_SETTINGS))
                return NormalizePaths(new UserSettings());

            string rawSettings = File.ReadAllText(Path.USER_SETTINGS);
            if (!JsonUtility.IsJsonValid(rawSettings))
                return NormalizePaths(new UserSettings());

            return NormalizePaths(JsonConvert.DeserializeObject<UserSettings>(rawSettings));

            UserSettings NormalizePaths(UserSettings settings)
            {
                settings.CurrentConfigPath = NormalizePath(settings.CurrentConfigPath, Values.Directory.CONFIGS);
                settings.LogPath = NormalizePath(settings.LogPath, Values.Directory.LOGS);
                return settings;
            }
        }

        private void SaveUserSettings()
        {
            Values.Directory.EnsureWritableDirectories();
            string rawSettings = JsonConvert.SerializeObject(userSettings);
            File.WriteAllText(Path.USER_SETTINGS, rawSettings);
        }

        private static string NormalizePath(string path, string fallback)
        {
            if (string.IsNullOrWhiteSpace(path))
                return fallback;

            if (!System.IO.Path.IsPathRooted(path))
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(Values.Directory.ROOT, path));

            return path;
        }
    }
}
