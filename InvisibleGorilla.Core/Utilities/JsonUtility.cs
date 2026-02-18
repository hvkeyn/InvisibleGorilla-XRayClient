using System;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InvisibleGorillaXRay.Utilities
{
    public static class JsonUtility
    {
        public static bool IsJsonValid(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    return doc != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static T ConvertFromJson<T>(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json))
                    return default;
                    
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch
            {
                return default;
            }
        }

        public static string Find(string key, string parent, string jsonString)
        {
            if (string.IsNullOrWhiteSpace(jsonString))
                return null;

            try
            {
                var match = JObject.Parse(jsonString.ToLower())
                    .DescendantsAndSelf().OfType<JProperty>()
                    .FirstOrDefault(x => x.Name.Equals(parent) && x.SelectToken($"$..{key}") != null);

                return match?.SelectToken($"$..{key}")?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
