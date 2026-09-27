using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Builds the game context-menu entries.
    //
    // Split out of the plugin entry point because it is pure declaration: the
    // backgrounds and covers sections are the same five commands differing only
    // by ArtworkKind, so listing them twice by hand invited them to drift.
    public static class GameMenuBuilder
    {
        public static IEnumerable<GameMenuItem> Build(
            IEnumerable<Game> games, ImageMenuHandler handler)
        {
            var selected = games?.ToList() ?? new List<Game>();

            foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                // Captured per iteration: the lambdas below outlive the loop,
                // and closing over the loop variable would give every menu item
                // the last kind.
                ArtworkKind current = kind;
                string section = current == ArtworkKind.Cover
                    ? "ImageRotater|" + Loc.Get("LOCImageRotaterMenuCovers")
                    : "ImageRotater|" + Loc.Get("LOCImageRotaterMenuBackgrounds");

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = current == ArtworkKind.Cover
                        ? Loc.Get("LOCImageRotaterMenuBrowseCovers")
                        : Loc.Get("LOCImageRotaterMenuBrowseBackgrounds"),
                    Action = a => handler.ManageImages(selected.FirstOrDefault(), current)
                };

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = Loc.Get("LOCImageRotaterMenuRemoveAll"),
                    Action = a => handler.ClearImages(selected, current)
                };
            }

            // At the ImageRotater root, not under Backgrounds or Covers: a
            // fragmented video shows as a black tile wherever it is used, and
            // the user chasing one should not have to guess which kind it was.
            yield return new GameMenuItem
            {
                MenuSection = "ImageRotater",
                Description = Loc.Get("LOCImageRotaterMenuRepairVideos"),
                Action = a => handler.RepairVideos(selected)
            };
        }
    }
}
