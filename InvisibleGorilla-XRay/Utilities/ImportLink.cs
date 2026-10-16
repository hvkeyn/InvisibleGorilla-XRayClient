using System;

namespace InvisibleGorillaXRay.Utilities
{
    /// <summary>
    /// Unwraps INCY and Happ import commands into the subscription URL or server link they carry.
    /// </summary>
    public static class ImportLink
    {
        private static readonly string[] Wrappers =
        {
            "incy://import/",
            "incy://add/",
            "happ://import/",
            "happ://add/"
        };

        public static string Unwrap(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line ?? string.Empty;

            string current = line.Trim();
            for (int i = 0; i < 3; i++)
            {
                string stripped = TryStrip(current);
                if (stripped == null)
                    break;

                current = Uri.UnescapeDataString(stripped).Trim();
            }

            return current;
        }

        private static string TryStrip(string line)
        {
            foreach (string wrapper in Wrappers)
            {
                if (line.StartsWith(wrapper, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(wrapper.Length).Trim();
            }

            return null;
        }
    }
}
