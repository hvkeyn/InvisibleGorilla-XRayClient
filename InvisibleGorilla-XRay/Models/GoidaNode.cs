using System;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Models
{
    public sealed class GoidaNode
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Id { get; set; } = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int ListId { get; set; }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string DisplayName { get; set; } = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Endpoint { get; set; } = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string ConfigPath { get; set; } = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Country { get; set; } = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public string Protocol { get; set; } = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int LatencyMs { get; set; } = Values.Availability.NOT_CHECKED;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public GoidaNodeStatus Status { get; set; } = GoidaNodeStatus.Unknown;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public DateTime LastCheckedUtc { get; set; }

        public GoidaNode Clone()
        {
            return new GoidaNode
            {
                Id = Id,
                ListId = ListId,
                DisplayName = DisplayName,
                Endpoint = Endpoint,
                ConfigPath = ConfigPath,
                Country = Country,
                Protocol = Protocol,
                LatencyMs = LatencyMs,
                Status = Status,
                LastCheckedUtc = LastCheckedUtc
            };
        }
    }
}
