using System;
using System.Text;
using System.Collections.Generic;

namespace InvisibleGorillaXRay.Models.Templates.Subscriptions
{
    using Services;
    using Utilities;

    public abstract class Template
    {
        protected string Data;
        public string ProfileTitle { get; protected set; }
        public SubscriptionMeta Meta { get; } = new SubscriptionMeta();

        protected LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();

        public abstract bool IsValid(string link);

        public abstract Status FetchDataFromLink(string link);

        public string GetValidRemark(string remark) => FileUtility.GetValidFileName(remark);

        public List<string[]> ConvertToV2RayList(Func<string, Status> convertConfigLinkToV2Ray)
        {
            string data;
            List<string[]> v2RayList = new List<string[]>();

            TryDecode();
            TryConvert();
            return v2RayList;

            void TryDecode()
            {
                TryDecodeAsBase64();
                if (IsDecodeSucceeded())
                    return;
                
                TryDecodeAsStringArray();
                if (IsDecodeSucceeded())
                    return;

                void TryDecodeAsBase64()
                {
                    try
                    {
                        string compact = Data.Replace("\r", string.Empty)
                            .Replace("\n", string.Empty)
                            .Replace(" ", string.Empty)
                            .Replace('-', '+')
                            .Replace('_', '/');
                        int pad = compact.Length % 4;
                        if (pad > 0)
                            compact += new string('=', 4 - pad);

                        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(compact));
                        if (decoded.IndexOf("://", StringComparison.Ordinal) >= 0
                            || decoded.IndexOf("[Interface]", StringComparison.Ordinal) >= 0)
                            data = decoded;
                        else
                            data = null;
                    }
                    catch
                    {
                        data = null;
                    }
                }

                void TryDecodeAsStringArray()
                {
                    data = Data;
                }

                bool IsDecodeSucceeded() => !string.IsNullOrEmpty(data);
            }

            void TryConvert()
            {
                string trimmed = data.TrimStart();
                if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    TryConvertJson(data, v2RayList);
                    return;
                }

                string currentBlock = null;
                foreach (string rawLine in data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string link = Utilities.ImportLink.Unwrap(rawLine.Trim());
                    if (string.IsNullOrWhiteSpace(link))
                        continue;

                    if (link.StartsWith("#", StringComparison.Ordinal))
                    {
                        NoteMetadata(link, ref currentBlock);
                        continue;
                    }

                    if (IsRoutingLine(link))
                        continue;

                    if (Services.SubscriptionServerEndpoint.IsPlaceholderLink(link))
                    {
                        Meta.AddNotice(RemarkFromLink(link));
                        continue;
                    }

                    try
                    {
                        Status convertingStatus = convertConfigLinkToV2Ray.Invoke(link);
                        if (convertingStatus.Code == Code.SUCCESS)
                        {
                            string[] config = GetConfig(convertingStatus);
                            string remark = GetConfigRemark(config);
                            if (!string.IsNullOrWhiteSpace(currentBlock)
                                && remark.IndexOf(currentBlock, StringComparison.Ordinal) < 0)
                                remark = currentBlock + " · " + remark;
                            v2RayList.Add(new[] { remark, GetConfigData(config) });
                        }
                    }
                    catch
                    {
                    }
                }

                string[] GetConfig(Status configStatus) => (string[])configStatus.Content;

                string GetConfigRemark(string[] config) => config[0];

                string GetConfigData(string[] config) => config[1];
            }
        }

        protected bool IsAnyDataExisits() => !string.IsNullOrEmpty(Data);

        private void TryConvertJson(string json, List<string[]> v2RayList)
        {
            try
            {
                Newtonsoft.Json.Linq.JToken token = Newtonsoft.Json.Linq.JToken.Parse(json);
                if (token is Newtonsoft.Json.Linq.JArray array)
                {
                    foreach (Newtonsoft.Json.Linq.JToken item in array)
                        AddJsonServer(item, v2RayList);
                    return;
                }

                if (token is Newtonsoft.Json.Linq.JObject obj)
                    AddJsonServer(obj, v2RayList);
            }
            catch
            {
            }
        }

        private void AddJsonServer(Newtonsoft.Json.Linq.JToken item, List<string[]> v2RayList)
        {
            if (item == null)
                return;

            string json = item.ToString(Newtonsoft.Json.Formatting.None);
            string remark = item.Value<string>("remarks")
                ?? item.Value<string>("ps")
                ?? item["meta"]?.Value<string>("serverDescription")
                ?? "server";
            remark = FileUtility.GetValidFileName(remark);
            if (string.IsNullOrWhiteSpace(remark))
                remark = "server";

            if (Services.SubscriptionServerEndpoint.IsPlaceholderJson(json))
            {
                Meta.AddNotice(remark);
                return;
            }

            v2RayList.Add(new[] { remark, json });
        }

        private static string RemarkFromLink(string link)
        {
            int hash = link.LastIndexOf('#');
            if (hash < 0 || hash + 1 >= link.Length)
                return "server";

            string remark = Uri.UnescapeDataString(link.Substring(hash + 1)).Trim();
            int extra = remark.IndexOf('?');
            if (extra > 0)
                remark = remark.Substring(0, extra);
            remark = FileUtility.GetValidFileName(remark);
            return string.IsNullOrWhiteSpace(remark) ? "server" : remark;
        }

        private static bool IsRoutingLine(string link)
        {
            return link.StartsWith("://", StringComparison.Ordinal)
                || link.StartsWith("happ://routing", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("incy://routing", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("happ://autorouting", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("incy://autorouting", StringComparison.OrdinalIgnoreCase);
        }

        private void NoteMetadata(string line, ref string currentBlock)
        {
            string body = line.TrimStart('#').Trim();
            if (body.Length == 0)
                return;

            int sep = body.IndexOfAny(new[] { ':', ' ' });
            string key = sep > 0 ? body.Substring(0, sep).Trim() : body;
            string value = sep > 0 ? body.Substring(sep + 1).Trim() : string.Empty;
            if (IsMetadataKey(key))
            {
                Meta.Apply(key, value);
                if (string.IsNullOrWhiteSpace(ProfileTitle)
                    && key.Equals("profile-title", StringComparison.OrdinalIgnoreCase)
                    && value.Length > 0)
                    ProfileTitle = SubscriptionMeta.Decode(value);
                return;
            }

            if (body.Length <= 48)
                currentBlock = body;
        }

        private static bool IsMetadataKey(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "announce":
                case "announce-url":
                case "profile-title":
                case "profile-update-interval":
                case "profile-web-page-url":
                case "support-url":
                case "subscription-userinfo":
                case "routing":
                case "autorouting":
                case "new-url":
                case "new-domain":
                case "fallback-url":
                case "providerid":
                case "hide-settings":
                    return true;
                default:
                    return false;
            }
        }
    }
}