namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaMainPresentation
    {
        public string Summary { get; init; } = string.Empty;
        public string QualityLabel { get; init; } = string.Empty;
        public string ColorHex { get; init; } = "#9AA0A6";

        /// <summary>Signal strength 0..4 (0 = offline/unknown, 4 = excellent).</summary>
        public int SignalLevel { get; init; }

        public string LatencyText { get; init; } = string.Empty;
    }
}
