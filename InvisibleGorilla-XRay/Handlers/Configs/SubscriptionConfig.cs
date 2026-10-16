using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Handlers.Configs
{
    using Models;
    using Values;
    using Utilities;

    public class SubscriptionConfig : BaseConfig
    {
        private List<Subscription> groups;
        private Func<Config> createConfigModel;

        public SubscriptionConfig() : base()
        {
            this.groups = new List<Subscription>();
        }

        public void Setup(Func<string> getCurrentConfigPath)
        {
            LoadFiles(getCurrentConfigPath.Invoke());
        }
        
        public void LoadGroups()
        {
            groups.Clear();

            DirectoryInfo directoryInfo = new DirectoryInfo(Directory.CONFIGS);
            if (!directoryInfo.Exists)
                return;
            
            DirectoryInfo[] directories = directoryInfo.GetDirectories().Where(
                directory => directory.GetFiles().Any(file => file.Name == "info.dat") &&
                    directory.GetFiles().Count() > 1
            ).OrderBy(
                directory => directory.CreationTime
            ).ToArray();

            foreach(DirectoryInfo directory in directories)
            {
                AddDirectoryToList(directory);
            }
        }

        public List<Subscription> GetAllGroups()
        {
            return groups;
        }

        public void CreateSubscription(string remark, string url, string data)
        {
            remark = string.IsNullOrWhiteSpace(remark) ? "subscription" : remark.Trim();
            string destinationDirectory = $"{Directory.CONFIGS}/{remark}";
            List<string[]> configs = ReadServers(data, out Models.SubscriptionMeta meta);
            string storedUrl = !string.IsNullOrWhiteSpace(meta?.NewUrl) && meta.NewUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? meta.NewUrl
                : url;

            if (!IsAnyConfigExists() && string.IsNullOrWhiteSpace(meta?.Notice) && string.IsNullOrWhiteSpace(meta?.Announce))
                return;

            if (System.IO.Directory.Exists(destinationDirectory))
                System.IO.Directory.Delete(destinationDirectory, true);

            var usedRemarks = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CreateInfoFile();
            foreach(string[] config in configs)
            {
                CreateConfigFile(
                    remark: UniqueRemark(GetConfigRemark(config), usedRemarks), 
                    data: GetConfigData(config)
                );
            }

            bool IsAnyConfigExists() => configs.Count > 0;

            void CreateInfoFile()
            {
                string destinationPath = $"{destinationDirectory}/info.dat";
                SaveToDirectory(destinationPath, storedUrl);
                meta?.Save(destinationDirectory);
            }

            List<string[]> ReadServers(string payload, out Models.SubscriptionMeta parsedMeta)
            {
                parsedMeta = null;
                Newtonsoft.Json.Linq.JToken token = Newtonsoft.Json.Linq.JToken.Parse(payload);
                if (token is Newtonsoft.Json.Linq.JObject envelope && envelope["servers"] != null)
                {
                    parsedMeta = envelope["meta"]?.ToObject<Models.SubscriptionMeta>();
                    return envelope["servers"].ToObject<List<string[]>>() ?? new List<string[]>();
                }

                return token.ToObject<List<string[]>>() ?? new List<string[]>();
            }

            void CreateConfigFile(string remark, string data)
            {
                string destinationPath = $"{destinationDirectory}/{remark}.json";
                SaveToDirectory(destinationPath, data);
                SetFileTime(destinationPath);
                AddConfigToList(CreateConfigModel(destinationPath));

                void SetFileTime(string path) => FileUtility.SetFileTimeToNow(path);
            }

            void SaveToDirectory(string destinationPath, string data)
            {
                FileUtility.CreateDirectory(destinationDirectory);
                FileUtility.SetDirectoryTimeToNow(destinationDirectory);
                File.WriteAllText(destinationPath, data);
            }

            string UniqueRemark(string name, System.Collections.Generic.HashSet<string> used)
            {
                string baseName = string.IsNullOrWhiteSpace(name) ? "server" : name.Trim();
                string unique = baseName;
                int index = 2;
                while (!used.Add(unique))
                    unique = baseName + "-" + index++;
                return unique;
            }

            string GetConfigRemark(string[] config) => config[0];

            string GetConfigData(string[] config) => config[1];
        }

        public void DeleteSubscription(Subscription subscription)
        {
            System.IO.Directory.Delete(subscription.Directory.FullName, true);
            groups.Remove(subscription);
        }

        public override void LoadFiles(string path)
        {
            LoadGroups();
            configs.Clear();

            if(!IsAnyGroupExists())
                return;

            DirectoryInfo directoryInfo = new DirectoryInfo(GetConfigDirectory());

            if (!IsValidDirectory())
                directoryInfo = groups.Last().Directory;

            FileInfo[] files = directoryInfo.GetFiles().Where(
                file => file.Extension != ".dat"
            ).OrderBy(file => file.CreationTime).ToArray();

            foreach(FileInfo file in files)
            {
                AddConfigToList(CreateConfigModel(file.FullName));
            }

            bool IsAnyGroupExists() => groups.Count > 0;

            bool IsValidDirectory() => directoryInfo.Exists && GetConfigDirectory() != GetRootConfigDirectory();

            string GetConfigDirectory()
            {
                string directory = path;
                if (!FileUtility.IsDirectory(path))
                    directory = FileUtility.GetDirectory(path);

                path = $"{directory}/info.dat";
                if (!FileUtility.IsFileExists(path))
                    directory = Directory.CONFIGS;
                
                return FileUtility.GetFullPath(directory);
            }

            string GetRootConfigDirectory()
            {
                return FileUtility.GetFullPath(Directory.CONFIGS);
            }
        }

        public override Config CreateConfigModel(string path)
        {
            return new Config(
                path: $"{GetDirectory(path)}/{GetFileName(path)}",
                name: GetFileName(path),
                type: ConfigType.FILE,
                group: GroupType.SUBSCRIPTION,
                updateTime: GetFileUpdateTime(path)
            );
        }

        private void AddDirectoryToList(DirectoryInfo directory)
        {
            groups.Add(new Subscription(FetchUrlFromDirectory(), directory));

            string FetchUrlFromDirectory() => File.ReadAllText($"{directory.FullName}/info.dat");
        }
    }
}