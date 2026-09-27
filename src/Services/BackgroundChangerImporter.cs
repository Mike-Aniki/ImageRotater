using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Copies the artwork a user gave BackgroundChanger into this plugin's
    // store, so switching plugins does not mean rebuilding years of curation.
    //
    // BackgroundChanger's layout, read from its source:
    //
    //   <ExtensionsData>\3afdd02b-db6c-4b60-8faa-2971d6dfad2a\
    //     BackgroundChanger\{gameId}.json   Items: Name, FolderName, IsCover, ...
    //     Images\{FolderName}\{Name}
    //
    // Covers and backgrounds share one folder there, so the JSON is the only
    // thing that tells them apart - walking the folders would file every
    // cover as a background. An item with no FolderName is Playnite's own
    // library file for the game (its "default"), which this plugin already
    // preserves on first rotation, so those are left alone.
    //
    // Copies, never moves: BackgroundChanger keeps working until the user
    // chooses to remove it.
    public static class BackgroundChangerImporter
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        public const string PluginId = "3afdd02b-db6c-4b60-8faa-2971d6dfad2a";

        public class Result
        {
            public int Games { get; set; }
            public int Copied { get; set; }
            public int Skipped { get; set; }
            public int Missing { get; set; }
            public int Failed { get; set; }
            public int WebP { get; set; }
            public string SourcePath { get; set; }

            public string Summary
            {
                get
                {
                    if (Games == 0 && Copied + Skipped + Missing + Failed == 0)
                    {
                        return Loc.Format("LOCImageRotaterNoBcAt", SourcePath);
                    }

                    string text = Loc.Format("LOCImageRotaterImportedFiles", Copied, Games);

                    if (Skipped > 0)
                    {
                        text += Loc.Format("LOCImageRotaterAlreadyPresent", Skipped);
                    }

                    if (Missing > 0)
                    {
                        text += Loc.Format("LOCImageRotaterBcMissingFiles", Missing);
                    }

                    if (Failed > 0)
                    {
                        text += Loc.Format("LOCImageRotaterCopyFailed", Failed);
                    }

                    // WPF has no WebP decoder, so a WebP the user could see in
                    // BackgroundChanger renders blank here until converted.
                    if (WebP > 0)
                    {
                        text += Loc.Format("LOCImageRotaterWebPCopied", WebP);
                    }

                    return text;
                }
            }
        }

        // The slice of BackgroundChanger's per-game record this needs.
        // Newtonsoft ignores the rest.
        private class GameRecord
        {
            public List<Item> Items { get; set; }
        }

        private class Item
        {
            public string Name { get; set; }
            public string FolderName { get; set; }
            public bool IsCover { get; set; }
        }

        // Safe to run again: a file already in the game's folder - by stem, so
        // a WebP the user has since converted to MP4 counts - is skipped.
        public static Result Import(string bcRoot, ISet<Guid> knownGames, GameImageStore store)
        {
            var result = new Result { SourcePath = bcRoot };

            string records = Path.Combine(bcRoot ?? string.Empty, "BackgroundChanger");
            if (string.IsNullOrEmpty(bcRoot) || !Directory.Exists(records))
            {
                return result;
            }

            foreach (string file in Directory.GetFiles(records, "*.json"))
            {
                Guid gameId;
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out gameId)
                    || !knownGames.Contains(gameId))
                {
                    continue;
                }

                List<Item> items;
                try
                {
                    items = JsonConvert.DeserializeObject<GameRecord>(File.ReadAllText(file))?.Items;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"ImageRotater: could not read BackgroundChanger record {file}");
                    result.Failed++;
                    continue;
                }

                if (items == null)
                {
                    continue;
                }

                bool touched = false;
                foreach (ArtworkKind kind in new[] { ArtworkKind.Cover, ArtworkKind.Background })
                {
                    List<Item> ofKind = items
                        .Where(i => i != null
                            && i.IsCover == (kind == ArtworkKind.Cover)
                            && !string.IsNullOrEmpty(i.FolderName)
                            && !string.IsNullOrEmpty(i.Name)
                            && GameImageStore.IsSupported(i.Name))
                        .ToList();

                    if (ofKind.Count == 0)
                    {
                        continue;
                    }

                    string target = store.GetGameFolder(gameId, kind);
                    var present = new HashSet<string>(
                        Directory.Exists(target)
                            ? Directory.GetFiles(target).Select(Path.GetFileNameWithoutExtension)
                            : Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (Item item in ofKind)
                    {
                        string source = Path.Combine(bcRoot, "Images", item.FolderName, item.Name);

                        if (!File.Exists(source))
                        {
                            result.Missing++;
                            continue;
                        }

                        if (present.Contains(Path.GetFileNameWithoutExtension(item.Name)))
                        {
                            result.Skipped++;
                            continue;
                        }

                        if (store.AddImage(gameId, source, kind) == null)
                        {
                            result.Failed++;
                            continue;
                        }

                        result.Copied++;
                        touched = true;

                        if (item.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                        {
                            result.WebP++;
                        }
                    }
                }

                if (touched)
                {
                    result.Games++;
                }
            }

            return result;
        }
    }
}
