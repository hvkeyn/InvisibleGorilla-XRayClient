using System;
using System.Globalization;

namespace InvisibleGorillaXRay.Services
{
    internal static class CountryDisplay
    {
        public static string GetFlagEmoji(string? countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
                return string.Empty;

            char first = char.ToUpperInvariant(countryCode[0]);
            char second = char.ToUpperInvariant(countryCode[1]);
            if (first is < 'A' or > 'Z' || second is < 'A' or > 'Z')
                return string.Empty;

            return string.Concat(
                char.ConvertFromUtf32(0x1F1E6 + (first - 'A')),
                char.ConvertFromUtf32(0x1F1E6 + (second - 'A')));
        }

        public static string GetCountryName(string? countryCode, string? fallbackName = null)
        {
            if (!string.IsNullOrWhiteSpace(fallbackName) && fallbackName.Trim().Length > 2)
                return fallbackName.Trim();

            if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
                return fallbackName?.Trim() ?? string.Empty;

            try
            {
                return new RegionInfo(countryCode.ToUpperInvariant()).DisplayName;
            }
            catch
            {
                return countryCode.ToUpperInvariant();
            }
        }

        public static string BuildPlaceLine(string city, string region)
        {
            if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(region))
                return $"{city.Trim()}, {region.Trim()}";

            if (!string.IsNullOrWhiteSpace(city))
                return city.Trim();

            if (!string.IsNullOrWhiteSpace(region))
                return region.Trim();

            return string.Empty;
        }
    }
}
