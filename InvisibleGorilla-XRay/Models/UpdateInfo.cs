using System.Collections.Generic;

namespace InvisibleGorillaXRay.Models
{
    public class UpdateInfo
    {
        public string TagName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public bool IsPrerelease { get; set; }
        public bool IsNewerThanCurrent { get; set; }
        public List<ReleaseAsset> Assets { get; set; } = new List<ReleaseAsset>();
    }
}
