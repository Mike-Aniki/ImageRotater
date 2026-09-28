using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // How the search dialog looked when it was last closed, per artwork kind.
    //
    // Covers and backgrounds are remembered separately: a cover search wants
    // "1:1 - Square", a background search wants a hero shape, and one preset
    // would have each undo the other.
    public class SearchPreset
    {
        // TabItem.Name of the source tab: SteamTab, SteamGridDbTab, WebTab,
        // YouTubeTab. The name rather than an index so a reordered tab strip
        // does not silently switch sources.
        public string Tab { get; set; }

        // Aspect group labels ("1:1 - Square") and individual dimensions
        // ("600x900"). Labels come from the result set, so a remembered label
        // that no result matches simply ticks nothing.
        public List<string> Groups { get; set; } = new List<string>();
        public List<string> Dimensions { get; set; } = new List<string>();
        public List<string> Styles { get; set; } = new List<string>();

        public bool ShowAnimated { get; set; } = true;
        public bool ShowNsfw { get; set; }
        public bool ShowHumor { get; set; }
        public bool ShowEpilepsy { get; set; }
        public bool ConvertGifs { get; set; } = true;
    }

    // Stores the presets in search-presets.json beside the plugin's images.
    //
    // A side-car file rather than plugin settings: the dialog has no path to
    // SavePluginSettings, and this is what the user last DID rather than a
    // choice they made on a settings page - it should not appear there, and
    // must not be lost when the settings window is cancelled.
    public static class SearchPresets
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const string FileName = "search-presets.json";

        // Null when nothing has been remembered for this kind, or the file
        // cannot be read - the dialog then opens as it always did.
        public static SearchPreset Load(string pluginUserDataPath, ArtworkKind kind)
        {
            try
            {
                Dictionary<ArtworkKind, SearchPreset> all = ReadAll(pluginUserDataPath);
                SearchPreset preset;
                return all != null && all.TryGetValue(kind, out preset) ? preset : null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not read search presets");
                return null;
            }
        }

        public static void Save(string pluginUserDataPath, ArtworkKind kind, SearchPreset preset)
        {
            if (string.IsNullOrEmpty(pluginUserDataPath) || preset == null)
            {
                return;
            }

            try
            {
                Dictionary<ArtworkKind, SearchPreset> all;
                try
                {
                    all = ReadAll(pluginUserDataPath);
                }
                catch (Exception)
                {
                    // A corrupt file is replaced rather than left to block
                    // every future save.
                    all = null;
                }

                all = all ?? new Dictionary<ArtworkKind, SearchPreset>();
                all[kind] = preset;

                string path = Path.Combine(pluginUserDataPath, FileName);
                Directory.CreateDirectory(pluginUserDataPath);

                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(all, Formatting.Indented));

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not save search presets");
            }
        }

        private static Dictionary<ArtworkKind, SearchPreset> ReadAll(string pluginUserDataPath)
        {
            if (string.IsNullOrEmpty(pluginUserDataPath))
            {
                return null;
            }

            string path = Path.Combine(pluginUserDataPath, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<Dictionary<ArtworkKind, SearchPreset>>(
                File.ReadAllText(path));
        }
    }
}
