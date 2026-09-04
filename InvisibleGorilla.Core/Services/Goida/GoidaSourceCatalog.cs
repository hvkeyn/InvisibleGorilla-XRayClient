using System.Collections.Generic;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaSourceCatalog
    {
        public const int MinListId = 1;
        public const int MaxListId = 26;

        // Official README links go through github.com/raw, which 302s to
        // raw.githubusercontent.com. Keep several hosts so RU/blocked paths still refresh.
        private static readonly string[] UrlTemplates =
        {
            "https://raw.githubusercontent.com/AvenCores/goida-vpn-configs/main/githubmirror/{0}.txt",
            "https://github.com/AvenCores/goida-vpn-configs/raw/refs/heads/main/githubmirror/{0}.txt",
            "https://cdn.jsdelivr.net/gh/AvenCores/goida-vpn-configs@main/githubmirror/{0}.txt",
            "https://fastly.jsdelivr.net/gh/AvenCores/goida-vpn-configs@main/githubmirror/{0}.txt",
            "https://raw.gitmirror.com/AvenCores/goida-vpn-configs/main/githubmirror/{0}.txt"
        };

        private static readonly string[] ListTitles =
        {
            "",
            "OpenRay",
            "5ubscrpt10n",
            "proxy-minging",
            "AutoVPN",
            "V2RayCFGDumper",
            "openproxylist",
            "v2ray-configs/trojan",
            "ConfigForge VLESS",
            "telegram-vless",
            "mheidari98 vless",
            "V2rayCollector IR",
            "V.O.I.D Bypass",
            "daily_free_vpn",
            "Mineral",
            "Config-Collector IR",
            "Pawdroid",
            "V2rayCollector_Py",
            "free18",
            "V2rayCollector mix",
            "Proxy-List",
            "kamaji",
            "xray-config-toolkit",
            "vpn-configs-for-russia",
            "vify VLESS",
            "V2RayRoot",
            "SNI/CIDR whitelist"
        };

        public static IReadOnlyList<GoidaListMeta> AllLists { get; } = BuildLists();

        public static string GetListUrl(int listId) => GetListUrls(listId)[0];

        public static IReadOnlyList<string> GetListUrls(int listId)
        {
            List<string> urls = new(UrlTemplates.Length);
            foreach (string template in UrlTemplates)
                urls.Add(string.Format(template, listId));
            return urls;
        }

        private static IReadOnlyList<GoidaListMeta> BuildLists()
        {
            List<GoidaListMeta> lists = new();
            for (int id = MinListId; id <= MaxListId; id++)
            {
                lists.Add(new GoidaListMeta
                {
                    Id = id,
                    Title = id < ListTitles.Length ? ListTitles[id] : $"Goida list #{id}",
                    Url = GetListUrl(id)
                });
            }

            return lists;
        }
    }
}
