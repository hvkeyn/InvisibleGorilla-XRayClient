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
                directory => !IsStagingDirectory(directory.Name) &&
                    directory.GetFiles().Any(file => file.Name == "info.dat") &&
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

        private static bool IsStagingDirectory(string name) =>
            name.EndsWith(".new", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);

        public void CreateSubscription(string remark, string url, string data)
        {
            remark = FileUtility.GetValidFileName(remark);
            if (string.IsNullOrWhiteSpace(remark))
                remark = "subscription";

            string destinationDirectory = System.IO.Path.Combine(Directory.CONFIGS, remark);

            // The remark comes from the subscription server (profile-title) or from user
            // input. A name like ".." used to point the delete below at the application
            // directory itself, so the resolved path must stay inside Configs.
            string configsRoot = System.IO.Path.GetFullPath(Directory.CONFIGS);
            string destinationFullPath = System.IO.Path.GetFullPath(destinationDirectory);
            if (!destinationFullPath.StartsWith(configsRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Core.DiagnosticLog.Write(
                    "SubscriptionConfig",
                    $"Refusing unsafe subscription directory: '{destinationFullPath}' (root '{configsRoot}')");
                return;
            }

            List<string[]> configs = ReadServers(data, out Models.SubscriptionMeta meta);
            string storedUrl = !string.IsNullOrWhiteSpace(meta?.NewUrl) && meta.NewUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? meta.NewUrl
                : url;

            if (!IsAnyConfigExists() && string.IsNullOrWhiteSpace(meta?.Notice) && string.IsNullOrWhiteSpace(meta?.Announce))
                return;

            // Write the new revision into a staging directory and swap it in. The previous
            // "delete the old directory, then write the new one" sequence left the
            // subscription without any nodes when anything failed halfway (disk full,
            // locked file, crash, antivirus).
            string stagingDirectory = destinationFullPath + ".new";
            string backupDirectory = destinationFullPath + ".bak";
            var writtenNames = new List<string>();

            try
            {
                TryDelete(stagingDirectory);
                System.IO.Directory.CreateDirectory(stagingDirectory);

                var usedRemarks = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CreateInfoFile();
                foreach (string[] config in configs)
                {
                    CreateConfigFile(
                        remark: UniqueRemark(GetConfigRemark(config), usedRemarks),
                        data: GetConfigData(config)
                    );
                }
            }
            catch (Exception ex)
            {
                Core.DiagnosticLog.WriteException("SubscriptionConfig.Stage", ex);
                TryDelete(stagingDirectory);
                return;
            }

            if (!TrySwap(stagingDirectory, backupDirectory, destinationFullPath))
                return;

            foreach (string name in writtenNames)
            {
                string finalPath = System.IO.Path.Combine(destinationFullPath, name + ".json");
                if (!System.IO.File.Exists(finalPath))
                    continue;

                FileUtility.SetFileTimeToNow(finalPath);
                AddConfigToList(CreateConfigModel(finalPath));
            }

            bool IsAnyConfigExists() => configs.Count > 0;

            void CreateInfoFile()
            {
                string destinationPath = System.IO.Path.Combine(stagingDirectory, "info.dat");
                SaveToDirectory(destinationPath, storedUrl);
                meta?.Save(stagingDirectory);
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
                // Server-provided names are not trusted either: "..\x" must not escape the
                // subscription directory.
                string safeRemark = FileUtility.GetValidFileName(remark);
                if (string.IsNullOrWhiteSpace(safeRemark))
                    safeRemark = "server";

                string destinationPath = System.IO.Path.Combine(stagingDirectory, safeRemark + ".json");
                SaveToDirectory(destinationPath, data);
                writtenNames.Add(safeRemark);
            }

            void SaveToDirectory(string destinationPath, string data)
            {
                FileUtility.SetDirectoryTimeToNow(stagingDirectory);
                File.WriteAllText(destinationPath, data);
            }

            bool TrySwap(string staging, string backup, string target)
            {
                try
                {
                    TryDelete(backup);

                    bool hadPrevious = System.IO.Directory.Exists(target);
                    if (hadPrevious)
                        System.IO.Directory.Move(target, backup);

                    try
                    {
                        System.IO.Directory.Move(staging, target);
                    }
                    catch
                    {
                        // Put the previous revision back before giving up.
                        if (hadPrevious
                            && !System.IO.Directory.Exists(target)
                            && System.IO.Directory.Exists(backup))
                        {
                            System.IO.Directory.Move(backup, target);
                        }

                        throw;
                    }

                    TryDelete(backup);
                    return true;
                }
                catch (Exception ex)
                {
                    Core.DiagnosticLog.WriteException("SubscriptionConfig.Swap", ex);
                    TryDelete(staging);
                    return false;
                }
            }

            static void TryDelete(string path)
            {
                try
                {
                    if (System.IO.Directory.Exists(path))
                        System.IO.Directory.Delete(path, true);
                }
                catch
                {
                }
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