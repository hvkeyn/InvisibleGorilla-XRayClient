using System;

namespace InvisibleGorillaXRay.Utilities
{
    public static class FileUtility
    {
        private static readonly string[] WindowsReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static string GetValidFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return string.Empty;

            string cleaned = AllowJustAsciiCharacters(RemoveInvalidFileCharacters());

            // Windows silently drops trailing dots and spaces, so ".." and "name." would both
            // survive the character filter while still meaning "parent directory" afterwards.
            cleaned = cleaned.Trim().TrimEnd('.', ' ');

            if (cleaned.Length == 0 || cleaned == "." || cleaned == "..")
                return string.Empty;

            foreach (string reserved in WindowsReservedNames)
            {
                if (string.Equals(cleaned, reserved, StringComparison.OrdinalIgnoreCase))
                    return "_" + cleaned;
            }

            return cleaned;

            string RemoveInvalidFileCharacters()
            {
                return string.Concat(fileName.Split(
                    System.IO.Path.GetInvalidFileNameChars()
                ));
            }

            string AllowJustAsciiCharacters(string name)
            {
                string cleanedName = System.Text.RegularExpressions.Regex.Replace(
                    name,
                    @"[^\p{L}\p{N} ._()|\-]+",
                    " ");
                return System.Text.RegularExpressions.Regex.Replace(cleanedName, @"\s+", " ").Trim();
            }
        }

        public static void SetFileTimeToNow(string path)
        {
            System.IO.File.SetCreationTime(path, DateTime.Now);
            System.IO.File.SetLastWriteTime(path, DateTime.Now);
        }

        public static void SetDirectoryTimeToNow(string path)
        {
            System.IO.Directory.SetCreationTime(path, DateTime.Now);
            System.IO.Directory.SetLastWriteTime(path, DateTime.Now);
        }

        public static string GetFullPath(string path)
        {
            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }

        public static void TryDeleteDirectory(string path)
        {
            try
            {
                if (System.IO.Directory.Exists(path))
                    System.IO.Directory.Delete(path);
            }
            catch
            {
            }
        }

        public static void CreateDirectory(string path) => System.IO.Directory.CreateDirectory(path);

        public static string GetDirectory(string path) => System.IO.Path.GetDirectoryName(path);

        public static string GetFileName(string path) => System.IO.Path.GetFileName(path);

        public static string GetFileUpdateTime(string path) => System.IO.File.GetLastWriteTime(path).ToShortDateString();

        public static bool IsFileExists(string path) => !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);

        public static bool IsDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                return System.IO.Directory.Exists(path) 
                    && System.IO.File.GetAttributes(path).HasFlag(System.IO.FileAttributes.Directory);
            }
            catch
            {
                return false;
            }
        }
    }
}
