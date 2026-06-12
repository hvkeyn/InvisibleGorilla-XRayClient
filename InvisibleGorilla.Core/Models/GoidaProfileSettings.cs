using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Models
{
    public sealed class GoidaProfileSettings
    {
        public const int DefaultRefreshMinutes = 9;
        public const int DefaultProbeSeconds = 60;
        public const int DefaultAutoSwitchLatencyMs = 3000;
        public const int DefaultAutoPoolSize = 100;
        public const int MaxVerifiedNodes = 100;
        // Manual "verify" runs on demand and is cancellable, so it may natively VLESS-verify a much
        // larger slice of the selected lists than the lightweight background probe. The TCP phase
        // already covers every selected node; this only caps the serial native verification.
        public const int MaxManualVerifyNodes = 500;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool Enabled { get; set; }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool AutoSwitchOnFly { get; set; } = true;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int RefreshIntervalMinutes { get; set; } = DefaultRefreshMinutes;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int ProbeIntervalSeconds { get; set; } = DefaultProbeSeconds;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int AutoSwitchLatencyMs { get; set; } = DefaultAutoSwitchLatencyMs;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public GoidaSelectionMode SelectionMode { get; set; } = GoidaSelectionMode.AutoBest;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public List<int> EnabledListIds { get; set; } = Enumerable.Range(1, 25).ToList();

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string ActiveNodeId { get; set; } = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string PinnedNodeId { get; set; } = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public List<string> ManualPoolNodeIds { get; set; } = new();

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public DateTime LastRefreshUtc { get; set; }

        public GoidaProfileSettings Clone()
        {
            return new GoidaProfileSettings
            {
                Enabled = Enabled,
                AutoSwitchOnFly = AutoSwitchOnFly,
                RefreshIntervalMinutes = RefreshIntervalMinutes > 0 ? RefreshIntervalMinutes : DefaultRefreshMinutes,
                ProbeIntervalSeconds = ProbeIntervalSeconds > 0 ? ProbeIntervalSeconds : DefaultProbeSeconds,
                AutoSwitchLatencyMs = AutoSwitchLatencyMs > 0 ? AutoSwitchLatencyMs : DefaultAutoSwitchLatencyMs,
                SelectionMode = SelectionMode,
                EnabledListIds = EnabledListIds?.ToList() ?? Enumerable.Range(1, 25).ToList(),
                ActiveNodeId = ActiveNodeId ?? string.Empty,
                PinnedNodeId = PinnedNodeId ?? string.Empty,
                ManualPoolNodeIds = ManualPoolNodeIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? new List<string>(),
                LastRefreshUtc = LastRefreshUtc
            };
        }

        public bool ShouldShowInServerList()
        {
            return Enabled
                || !string.IsNullOrWhiteSpace(ActiveNodeId)
                || LastRefreshUtc != default;
        }
    }
}
