using System;
using System.IO;
using System.Linq;

namespace InvisibleGorillaXRay.Utilities
{
    using Services;

    /// <summary>
    /// Removes subscription stubs that landed in the plain configuration list:
    /// placeholder servers, punctuation filenames, and copies of subscription files.
    /// </summary>
    public static class SubscriptionJanitor
    {
        public static int Clean(string configsRoot)
        {
            if (string.IsNullOrWhiteSpace(configsRoot) || !Directory.Exists(configsRoot))
                return 0;

            int removed = 0;
            DirectoryInfo root = new DirectoryInfo(configsRoot);
            foreach (FileInfo file in root.GetFiles())
            {
                if (ShouldDeleteRootFile(root, file))
                {
                    TryDelete(file);
                    removed++;
                }
            }

            foreach (DirectoryInfo directory in root.GetDirectories())
            {
                foreach (FileInfo file in directory.GetFiles("*.json"))
                {
                    if (LooksLikePlaceholder(file))
                    {
                        TryDelete(file);
                        removed++;
                    }
                }
            }

            return removed;
        }

        private static bool ShouldDeleteRootFile(DirectoryInfo root, FileInfo file)
        {
            if (file.Name.Equals("info.dat", StringComparison.OrdinalIgnoreCase)
                || file.Name.Equals("meta.dat", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                return false;

            string stem = Path.GetFileNameWithoutExtension(file.Name);
            if (string.IsNullOrWhiteSpace(stem) || !stem.Any(char.IsLetterOrDigit))
                return true;

            if (LooksLikePlaceholder(file))
                return true;

            foreach (DirectoryInfo directory in root.GetDirectories())
            {
                if (File.Exists(Path.Combine(directory.FullName, file.Name)))
                    return true;
            }

            return false;
        }

        private static bool LooksLikePlaceholder(FileInfo file)
        {
            try
            {
                if (file.Length > 200000)
                    return false;
                return SubscriptionServerEndpoint.IsPlaceholderJson(File.ReadAllText(file.FullName));
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(FileInfo file)
        {
            try
            {
                file.Delete();
            }
            catch
            {
            }
        }
    }
}
