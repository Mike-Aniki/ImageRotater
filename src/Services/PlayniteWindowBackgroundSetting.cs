using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Desktop mode's "Use game background image as window background".
    //
    // Playnite honours it by leaving the window's PART_ImageBackground empty,
    // not by hiding it. Themes such as Harmony keep that element visible, so
    // without this check the native background injection filled the empty
    // element and the background came back although the user had turned it off.
    //
    // The SDK does not expose the setting. The live value is read from
    // PlayniteApplication.Current.AppSettings; if that internal shape ever
    // changes, config.json is read instead. When neither works the answer is
    // "enabled", which is the behaviour before this check existed.
    internal static class PlayniteWindowBackgroundSetting
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        internal const string SettingName = "ShowBackgroundImageOnWindow";
        private static readonly Regex ConfigPattern =
            new Regex("\"" + SettingName + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);

        private static bool reflectionResolved;
        private static PropertyInfo currentProperty;
        private static PropertyInfo appSettingsProperty;
        private static PropertyInfo settingProperty;

        private static string configPath;
        private static DateTime configStamp;
        private static bool configValue = true;
        private static bool loggedSource;

        internal static void SetConfigurationDirectory(string directory)
        {
            configPath = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "config.json");
        }

        internal static bool IsEnabled()
        {
            try
            {
                bool? live = ReadLive();
                if (live.HasValue) { LogSource("live settings"); return live.Value; }
            }
            catch (Exception ex) { Logger.Debug(ex, "ImageRotater: live window background setting unreadable"); }

            try
            {
                bool? stored = ReadConfig();
                if (stored.HasValue) { LogSource("config.json"); return stored.Value; }
            }
            catch (Exception ex) { Logger.Debug(ex, "ImageRotater: config.json window background setting unreadable"); }

            return true;
        }

        private static void LogSource(string source)
        {
            if (loggedSource) return;
            loggedSource = true;
            Logger.Info("ImageRotater: reading Playnite's window background setting from " + source);
        }

        private static bool? ReadLive()
        {
            if (!reflectionResolved)
            {
                reflectionResolved = true;
                Type app = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Playnite.PlayniteApplication", false))
                    .FirstOrDefault(t => t != null);
                currentProperty = app?.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                appSettingsProperty = app?.GetProperty("AppSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                settingProperty = appSettingsProperty?.PropertyType.GetProperty(SettingName);
            }
            if (currentProperty == null || appSettingsProperty == null || settingProperty == null) return null;

            object current = currentProperty.GetValue(null);
            object appSettings = current == null ? null : appSettingsProperty.GetValue(current);
            return appSettings == null ? null : settingProperty.GetValue(appSettings) as bool?;
        }

        private static bool? ReadConfig()
        {
            if (configPath == null || !File.Exists(configPath)) return null;
            DateTime stamp = File.GetLastWriteTimeUtc(configPath);
            if (stamp != configStamp)
            {
                bool? parsed = Parse(File.ReadAllText(configPath));
                if (!parsed.HasValue) return null;
                configValue = parsed.Value;
                configStamp = stamp;
            }
            return configValue;
        }

        internal static bool? Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match match = ConfigPattern.Match(json);
            if (!match.Success) return null;
            return string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
