namespace InvisibleGorillaXRay.Values
{
    public static class Directory
    {
        private static string root = System.AppContext.BaseDirectory;

        public static string ROOT => root;
        public static string LIBRARIES => System.IO.Path.Combine(ROOT, "Libraries");
        public static string TUN => System.IO.Path.Combine(ROOT, "TUN");
        public static string CONFIGS => System.IO.Path.Combine(ROOT, "Configs");
        public static string LOGS => System.IO.Path.Combine(ROOT, "Logs");
        public static string ASSETS => System.IO.Path.Combine(ROOT, "Assets");
        public static string LOCALIZATION => System.IO.Path.Combine(ASSETS, "Localization");

        public static void SetRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return;

            root = System.IO.Path.GetFullPath(rootPath);
        }

        public static void EnsureWritableDirectories()
        {
            TryCreate(ROOT);
            TryCreate(CONFIGS);
            TryCreate(LOGS);
            TryCreate(TUN);

            static void TryCreate(string path)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(path);
                }
                catch
                {
                }
            }
        }
    }
}