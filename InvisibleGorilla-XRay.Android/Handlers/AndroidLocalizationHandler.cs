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
        private bool isLanguageLoaded;
        private ResourceDictionary? currentLangDict;

        public void Setup(Func<string> getCurrentLanguage)
        {
            this.getCurrentLanguage = getCurrentLanguage;
            TryApplyCurrentLanguage();
        }

        public string GetTerm(string key)
        {
            EnsureLanguageLoaded();

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
                isLanguageLoaded = true;
            }
            catch
            {
                ApplyLanguage(Localization.DEFAULT_LANGUAGE);
                isLanguageLoaded = true;
            }
        }

        public void MergeInto(Control target)
        {
            EnsureLanguageLoaded();

            if (currentLangDict == null)
                return;

            // The active language dictionary is registered at the Application level in
            // ApplyLanguage, so DynamicResource lookups in every control already resolve through
            // it. A ResourceDictionary can only have a single owner in Avalonia, so trying to also
            // attach the same instance to a control's MergedDictionaries throws
            // "The ResourceDictionary already has a parent" and previously aborted MainView.Setup,
            // leaving the view half-initialized (e.g. the Goida nav button stayed dead).
            if (Application.Current != null &&
                Application.Current.Resources.MergedDictionaries.Contains(currentLangDict))
                return;

            try
            {
                if (!target.Resources.MergedDictionaries.Contains(currentLangDict))
                    target.Resources.MergedDictionaries.Add(currentLangDict);
            }
            catch (InvalidOperationException)
            {
                // Already owned elsewhere; app-level registration is sufficient for resolution.
            }
        }

        private void EnsureLanguageLoaded()
        {
            if (isLanguageLoaded)
                return;

            TryApplyCurrentLanguage();
        }

        private void ApplyLanguage(string language)
        {
            terms.Clear();

            try
            {
                ResourceDictionary dict = LoadDictionary(language);

                if (currentLangDict != null && Application.Current != null)
                    Application.Current.Resources.MergedDictionaries.Remove(currentLangDict);

                foreach (KeyValuePair<object, object> pair in dict)
                {
                    if (pair.Key is string key && pair.Value is string term)
                        terms[key] = term;
                }

                currentLangDict = dict;

                if (Application.Current != null)
                    Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch
            {
            }
        }

        private static ResourceDictionary LoadDictionary(string language)
        {
            Exception? lastException = null;

            string[] candidates =
            {
                $"avares://InvisibleGorilla-XRay.Android/Assets/Localization/{language}.axaml",
                $"avares://InvisibleGorillaXRay.Android/Assets/Localization/{language}.axaml",
                $"avares://InvisibleGorilla-XRay.Mac/Assets/Localization/{language}.axaml",
                $"avares://InvisibleGorillaXRay.Mac/Assets/Localization/{language}.axaml"
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    return (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri(candidate));
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            throw lastException ?? new InvalidOperationException("Localization resource dictionary not found.");
        }
    }
}
