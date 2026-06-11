using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaWhitelistStore
    {
        public const int ListId = 26;

        public static string FilePath => Path.Combine(
            Values.Directory.CONFIGS,
            GoidaNodeStore.ProfileDirectoryName,
            "list-26-whitelist.txt");

        public static int GetEntryCount()
        {
            if (!File.Exists(FilePath))
                return 0;

            try
            {
                return CountLines(File.ReadAllText(FilePath));
            }
            catch
            {
                return 0;
            }
        }

        private static int CountLines(string rawData)
        {
            return rawData
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Count(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal));
        }
    }
}
