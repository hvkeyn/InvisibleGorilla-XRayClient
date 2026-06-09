using System;
using System.IO;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaProfilePaths
    {
        public const string MarkerFileName = "__GoidaProfile__.json";
        public const string MarkerPath = "./Configs/" + MarkerFileName;

        public static bool IsMarker(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string normalizedPath = NormalizePath(path);
            return string.Equals(normalizedPath, NormalizePath(MarkerPath), StringComparison.OrdinalIgnoreCase)
                || normalizedPath.EndsWith("/" + MarkerFileName, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.EndsWith("\\" + MarkerFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Replace('\\', '/').TrimEnd('/');
            }
        }
    }
}
