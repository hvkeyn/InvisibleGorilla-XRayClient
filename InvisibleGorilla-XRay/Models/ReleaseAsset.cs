namespace InvisibleGorillaXRay.Models
{
    public class ReleaseAsset
    {
        public string Name { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public long Size { get; set; }
        public string ContentType { get; set; } = string.Empty;
    }
}
