using System;
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
            this.userSettings.IsAutoUpdate = userSettings.IsAutoUpdate;
            this.userSettings.IsAutoSelectBest = userSettings.IsAutoSelectBest;
            this.userSettings.ProxyPort = userSettings.ProxyPort;
            this.userSettings.TunPort = userSettings.TunPort;
            this.userSettings.TestPort = userSettings.TestPort;
            this.userSettings.TunIp = userSettings.TunIp;
            this.userSettings.Dns = userSettings.Dns;
            this.userSettings.LogPath = userSettings.LogPath;
            this.userSettings.AppRulesMode = userSettings.AppRulesMode;
            this.userSettings.AppRules = CloneAppRules(userSettings.AppRules);
            this.userSettings.AppRuleTemplates = CloneAppRuleTemplates(userSettings.AppRuleTemplates);
            this.userSettings.AppRuleTemplateBindings = CloneAppRuleTemplateBindings(userSettings.AppRuleTemplateBindings);
            this.userSettings.Tor = (userSettings.Tor ?? this.userSettings.Tor ?? new TorSettings()).Clone();
            this.userSettings.Goida = (userSettings.Goida ?? this.userSettings.Goida ?? new GoidaProfileSettings()).Clone();
            this.userSettings.OpenFlux = (userSettings.OpenFlux ?? this.userSettings.OpenFlux ?? new OpenFluxProfile()).Clone();
            this.userSettings.OpenFluxProfiles = CloneOpenFluxProfiles(userSettings.OpenFluxProfiles);

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
            UserSettings? loaded = TryLoad(Path.USER_SETTINGS);
            if (loaded != null)
                return NormalizeRules(loaded);

            // A truncated or hand-edited Settings.json used to reset every setting silently.
            // The backup written by the last successful save is a better starting point.
            UserSettings? backup = TryLoad(Path.USER_SETTINGS + ".bak");
            if (backup != null)
            {
                Core.DiagnosticLog.Write("Settings", "Settings.json is unreadable; restored from Settings.json.bak");
                return NormalizeRules(backup);
            }

            return NormalizeRules(new UserSettings());

            UserSettings? TryLoad(string path)
            {
                try
                {
                    if (!File.Exists(path))
                        return null;

                    string rawSettings = File.ReadAllText(path);
                    if (!JsonUtility.IsJsonValid(rawSettings))
                        return null;

                    // Any valid JSON (null, 123, {"Mode":"TUNNEL"}) passes IsJsonValid, so the
                    // deserializer has to be guarded as well.
                    return JsonConvert.DeserializeObject<UserSettings>(rawSettings);
                }
                catch (Exception ex)
                {
                    Core.DiagnosticLog.WriteException("Settings.Load", ex);
                    return null;
                }
            }

            UserSettings NormalizeRules(UserSettings settings)
            {
                settings.AppRules ??= new System.Collections.Generic.List<AppRule>();
                settings.AppRules = CloneAppRules(settings.AppRules);
                settings.AppRuleTemplates ??= new System.Collections.Generic.List<AppRuleTemplate>();
                settings.AppRuleTemplateBindings ??= new System.Collections.Generic.List<AppRuleTemplateBinding>();
                settings.AppRuleTemplates = CloneAppRuleTemplates(settings.AppRuleTemplates);
                settings.AppRuleTemplateBindings = CloneAppRuleTemplateBindings(settings.AppRuleTemplateBindings);
                settings.Tor = (settings.Tor ?? new TorSettings()).Clone();
                settings.Goida = (settings.Goida ?? new GoidaProfileSettings()).Clone();
                settings.OpenFlux = (settings.OpenFlux ?? new OpenFluxProfile()).Clone();
                settings.OpenFluxProfiles = CloneOpenFluxProfiles(settings.OpenFluxProfiles);
                return settings;
            }
        }

        private void SaveUserSettings()
        {
            try
            {
                string rawSettings = JsonConvert.SerializeObject(userSettings);
                string target = Path.USER_SETTINGS;
                string temp = target + ".tmp";

                File.WriteAllText(temp, rawSettings);

                if (!File.Exists(target))
                {
                    File.Move(temp, target);
                    return;
                }

                // Keep the previous good copy, then swap in place: a crash, a locked file or
                // a full disk must not leave a half-written Settings.json behind.
                try { File.Copy(target, target + ".bak", true); }
                catch (Exception ex) { Core.DiagnosticLog.WriteException("Settings.Backup", ex); }

                try
                {
                    File.Replace(temp, target, null, ignoreMetadataErrors: true);
                }
                catch (Exception ex)
                {
                    Core.DiagnosticLog.WriteException("Settings.Replace", ex);
                    File.Copy(temp, target, true);
                    try { File.Delete(temp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Core.DiagnosticLog.WriteException("Settings.Save", ex);
            }
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

        private static System.Collections.Generic.List<AppRuleTemplate> CloneAppRuleTemplates(System.Collections.Generic.IEnumerable<AppRuleTemplate>? templates)
        {
            if (templates == null)
                return new System.Collections.Generic.List<AppRuleTemplate>();

            return templates
                .Where(template => template != null && !string.IsNullOrWhiteSpace(template.Id))
                .Select(template =>
                {
                    AppRuleTemplate clone = template.Clone();
                    clone.AppRules = CloneAppRules(clone.AppRules);
                    clone.Name = clone.Name?.Trim() ?? string.Empty;
                    return clone;
                })
                .ToList();
        }

        private static System.Collections.Generic.List<AppRuleTemplateBinding> CloneAppRuleTemplateBindings(System.Collections.Generic.IEnumerable<AppRuleTemplateBinding>? bindings)
        {
            if (bindings == null)
                return new System.Collections.Generic.List<AppRuleTemplateBinding>();

            return bindings
                .Where(binding => binding != null
                    && !string.IsNullOrWhiteSpace(binding.ConfigPath)
                    && !string.IsNullOrWhiteSpace(binding.TemplateId))
                .Select(binding => new AppRuleTemplateBinding(
                    configPath: NormalizeConfigPath(binding.ConfigPath),
                    templateId: binding.TemplateId.Trim()))
                .ToList();
        }

        private static System.Collections.Generic.List<OpenFluxProfile> CloneOpenFluxProfiles(System.Collections.Generic.IEnumerable<OpenFluxProfile>? profiles)
        {
            if (profiles == null)
                return new System.Collections.Generic.List<OpenFluxProfile>();

            return profiles
                .Where(profile => profile != null)
                .Select(profile => profile.Clone())
                .ToList();
        }

        private static string NormalizeConfigPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return System.IO.Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Trim();
            }
        }
    }
}