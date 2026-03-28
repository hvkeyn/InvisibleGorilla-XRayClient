using System.ComponentModel;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Models
{
    public enum AppRulesMode { DISABLED, BYPASS_SELECTED_APPS }

    public class AppRule
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("")]
        public string AppId;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("")]
        public string DisplayName;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue("")]
        public string IconRef;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
        public bool Enabled;

        public AppRule()
        {
            AppId = "";
            DisplayName = "";
            IconRef = "";
            Enabled = true;
        }

        public AppRule(string appId, string displayName, string iconRef, bool enabled = true)
        {
            AppId = appId ?? "";
            DisplayName = displayName ?? "";
            IconRef = iconRef ?? "";
            Enabled = enabled;
        }

        public AppRule Clone() => new(AppId, DisplayName, IconRef, Enabled);
    }
}
