using System.Globalization;
using Playnite.SDK;

namespace ImageRotater
{
    internal static class Loc
    {
        public static string Get(string key)
        {
            return ResourceProvider.GetString(key) ?? key;
        }

        public static string Format(string key, params object[] values)
        {
            return string.Format(CultureInfo.CurrentCulture, Get(key), values);
        }
    }
}
