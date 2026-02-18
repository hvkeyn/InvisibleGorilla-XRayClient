using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InvisibleGorillaXRay.Mac.Handlers
{
    using InvisibleGorillaXRay.Handlers;
    using InvisibleGorillaXRay.Values;

    public class MacLocalizationHandler : Handler
    {
        private Func<string> getCurrentLanguage;
        private Dictionary<string, string> terms = new();

        public void Setup(Func<string> getCurrentLanguage)
        {
            this.getCurrentLanguage = getCurrentLanguage;
            TryApplyCurrentLanguage();
        }

        public string GetTerm(string key)
        {
            return terms.TryGetValue(key, out var value) ? value : key;
        }

        public void TryApplyCurrentLanguage()
        {
            try { ApplyLanguage(getCurrentLanguage.Invoke()); }
            catch { ApplyLanguage(Localization.DEFAULT_LANGUAGE); }
        }

        private void ApplyLanguage(string language)
        {
            terms.Clear();
            try
            {
                var uri = new Uri($"avares://InvisibleGorilla-XRay.Mac/Assets/Localization/{language}.axaml");
                var dict = (ResourceDictionary)AvaloniaXamlLoader.Load(uri);
                foreach (var kv in dict)
                {
                    if (kv.Key is string key && kv.Value is string val)
                        terms[key] = val;
                }
                if (Application.Current != null)
                    Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
    }
}
