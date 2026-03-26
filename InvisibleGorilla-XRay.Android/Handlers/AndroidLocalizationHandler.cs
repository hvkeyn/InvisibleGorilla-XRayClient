using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InvisibleGorillaXRay.Android.Handlers
{
    using InvisibleGorillaXRay.Handlers;
    using InvisibleGorillaXRay.Values;

    public sealed class AndroidLocalizationHandler : Handler
    {
        private Func<string>? getCurrentLanguage;
        private readonly Dictionary<string, string> terms = new();
        private ResourceDictionary? currentLangDict;

        public void Setup(Func<string> getCurrentLanguage)
        {
            this.getCurrentLanguage = getCurrentLanguage;
            TryApplyCurrentLanguage();
        }

        public string GetTerm(string key)
        {
            if (terms.TryGetValue(key, out string? value))
                return value;

            if (Application.Current != null &&
                Application.Current.TryFindResource(key, out object? res) &&
                res is string str)
            {
                terms[key] = str;
                return str;
            }

            return key;
        }

        public void TryApplyCurrentLanguage()
        {
            try
            {
                ApplyLanguage(getCurrentLanguage?.Invoke() ?? Localization.DEFAULT_LANGUAGE);
            }
            catch
            {
                ApplyLanguage(Localization.DEFAULT_LANGUAGE);
            }
        }

        private void ApplyLanguage(string language)
        {
            terms.Clear();

            try
            {
                if (currentLangDict != null && Application.Current != null)
                    Application.Current.Resources.MergedDictionaries.Remove(currentLangDict);

                Uri uri = new($"avares://InvisibleGorilla-XRay.Android/Assets/Localization/{language}.axaml");
                ResourceDictionary dict = (ResourceDictionary)AvaloniaXamlLoader.Load(uri);

                foreach (KeyValuePair<object, object?> kv in dict)
                {
                    if (kv.Key is string key && kv.Value is string value)
                        terms[key] = value;
                }

                currentLangDict = dict;
                if (Application.Current != null)
                    Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch
            {
            }
        }
    }
}
