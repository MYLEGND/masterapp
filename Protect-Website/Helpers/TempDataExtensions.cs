using Microsoft.AspNetCore.Mvc.ViewFeatures;
using System.Text.Json;

namespace LegendCorePlatform.Helpers
{
    public static class TempDataExtensions
    {
        public static void Put<T>(this ITempDataDictionary tempData, string key, T value)
        {
            tempData[key] = JsonSerializer.Serialize(value);
        }

        public static T? Get<T>(this ITempDataDictionary tempData, string key)
        {
            if (!tempData.ContainsKey(key)) return default;
            var json = tempData[key] as string;
            return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json);
        }
    }
}
