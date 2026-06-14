using System.Collections.Generic;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaSourceCatalog
    {
        public const string BaseUrl =
            "https://raw.githubusercontent.com/AvenCores/goida-vpn-configs/refs/heads/main/githubmirror";

        public static IReadOnlyList<GoidaListMeta> AllLists { get; } = BuildLists();

        public static string GetListUrl(int listId) => $"{BaseUrl}/{listId}.txt";

        private static IReadOnlyList<GoidaListMeta> BuildLists()
        {
            List<GoidaListMeta> lists = new();
            for (int id = 1; id <= 25; id++)
            {
                lists.Add(new GoidaListMeta
                {
                    Id = id,
                    Title = $"Goida list #{id}",
                    Url = GetListUrl(id)
                });
            }

            lists.Add(new GoidaListMeta
            {
                Id = 26,
                Title = "SNI/CIDR whitelist bypass",
                Url = GetListUrl(26)
            });

            return lists;
        }
    }
}
