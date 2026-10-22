using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace InvisibleGorillaXRay.Handlers.Templates
{
    using Services;
    using Models;
    using Models.Templates.Subscriptions;
    using Values;

    public class SubscriptionTemplate : ITemplate
    {
        private List<Type> templates;
        private Func<string, Status> convertConfigLinkToV2Ray;

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();

        public SubscriptionTemplate()
        {
            this.templates = new List<Type>();
        }

        public void Setup(Func<string, Status> convertConfigLinkToV2Ray)
        {
            this.convertConfigLinkToV2Ray = convertConfigLinkToV2Ray;
        }

        public void RegisterTemplates()
        {
            templates.Add(typeof(Jwt));
            templates.Add(typeof(Simple));
        }

        public Status ConvertLinkToSubscription(string remark, string link)
        {
            Template template = FindTemplate();
            if (template == null)
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.UNSUPPORTED_LINK,
                    content: LocalizationService.GetTerm(Localization.UNSUPPORTED_SUBSCRIPTION_LINK)
                );

            if (SubscriptionFetchContext.Previous != null)
            {
                template.Meta.FallbackUrl = SubscriptionFetchContext.Previous.FallbackUrl;
                template.Meta.HwidInCookie = SubscriptionFetchContext.Previous.HwidInCookie;
                template.Meta.ProviderId = SubscriptionFetchContext.Previous.ProviderId;
                template.Meta.SubExpire = SubscriptionFetchContext.Previous.SubExpire;
                SubscriptionFetchContext.Previous = null;
            }

            Status fetchingStatus = template.FetchDataFromLink(link);
            if (fetchingStatus.Code == Code.ERROR)
                return fetchingStatus;
            
            List<string[]> v2RayList = template.ConvertToV2RayList(convertConfigLinkToV2Ray);
            bool hasNotice = !string.IsNullOrWhiteSpace(template.Meta.Notice)
                || !string.IsNullOrWhiteSpace(template.Meta.Announce);

            // Providers often answer an unsupported client with a stub: placeholder nodes such as
            // 0.0.0.0:1 plus a "contact support" note. Those entries are filtered out above, so an
            // empty list here means there is nothing to connect to - say so instead of storing an
            // empty group that silently falls back to the real IP.
            if (Isv2RayListEmpty())
            {
                string notice = !string.IsNullOrWhiteSpace(template.Meta.Notice)
                    ? template.Meta.Notice
                    : template.Meta.Announce;
                string baseMessage = LocalizationService.GetTerm(Localization.INVALID_SUBSCRIPTION);
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.INVALID_CONFIG,
                    content: string.IsNullOrWhiteSpace(notice)
                        ? baseMessage
                        : $"{baseMessage} {notice.Trim()}"
                );
            }

            string title = string.IsNullOrWhiteSpace(template.ProfileTitle) ? remark : template.ProfileTitle;
            if (string.IsNullOrWhiteSpace(template.GetValidRemark(title)))
                title = "subscription";

            var envelope = new Newtonsoft.Json.Linq.JObject
            {
                ["servers"] = Newtonsoft.Json.Linq.JArray.FromObject(v2RayList),
                ["meta"] = Newtonsoft.Json.Linq.JObject.FromObject(template.Meta)
            };
            return new Status(
                code: Code.SUCCESS,
                subCode: SubCode.SUCCESS,
                content: new string[] { 
                    template.GetValidRemark(title), 
                    envelope.ToString(Newtonsoft.Json.Formatting.None)
                }
            );

            Template FindTemplate()
            {
                foreach(Type type in templates)
                {
                    Template template = Activator.CreateInstance(type) as Template;
                    if (template.IsValid(link))
                        return template;
                }

                return null;
            }

            bool Isv2RayListEmpty() => v2RayList.Count == 0;
        }
    }
}