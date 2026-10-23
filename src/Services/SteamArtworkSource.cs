using System;
using System.Collections.Generic;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Adapts Steam's CDN to the plugin's search results.
    //
    // The two halves of that sentence live elsewhere: SteamCdn owns the URLs
    // and asset list, SteamGridDbArtwork owns the result shape. This is only
    // the join - Playnite Game to appid, CDN asset to result tile.
    //
    // Worth having as the FIRST source because of what Steam serves: plain
    // JPEG and PNG, which WPF decodes natively. SteamGridDB's animated results
    // are WebP with WebM thumbnails - 23 of 23 in a sample - and WPF decodes
    // neither, so those need ffmpeg before they can even be shown moving.
    // Steam artwork just works, with no API key and no external tool.
    //
    // The trade is choice: Steam has exactly one of each asset per game, where
    // SteamGridDB has dozens. So this is the reliable default rather than the
    // interesting one.
    //
    // Trailers included, despite Steam's API claiming otherwise. appdetails
    // still lists a game's movies but returns null for their "mp4" and "webm"
    // keys, which reads as though Valve stopped serving them. The files are
    // still on the CDN - verified across several games - and are plain MP4
    // that MediaElement plays with no ffmpeg and no yt-dlp involved.
    public class SteamArtworkSource
    {
        // Playnite's Steam library plugin. A game imported by it carries the
        // appid in GameId, which is the whole reason this can work without a
        // search step.
        private static readonly Guid SteamPluginId =
            Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        // The appid Playnite already knows, or null when this game did not come
        // from Steam. Free and exact - no lookup involved.
        public static string GetKnownAppId(Game game)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.GameId))
            {
                return null;
            }

            if (game.PluginId != SteamPluginId)
            {
                return null;
            }

            // Guard against a non-numeric id: the CDN path is built from this.
            foreach (char c in game.GameId)
            {
                if (!char.IsDigit(c))
                {
                    return null;
                }
            }

            return game.GameId;
        }

        // The appid for a game from ANY library.
        //
        // Steam's artwork is keyed by appid, and an appid exists whether or not
        // this user owns the Steam copy - the GOG, Xbox or manually added
        // install of a game has the same art sitting on the same CDN. So the
        // tab is not restricted to Steam imports; it just has to work out the
        // appid the hard way when Playnite does not already hold it.
        //
        // May hit the network on its first call, so callers should be off the
        // UI thread.
        public static string ResolveAppId(Game game)
        {
            string known = GetKnownAppId(game);

            if (known != null)
            {
                return known;
            }

            return game == null ? null : SteamCdn.FindAppIdByName(game.Name);
        }

        public static bool IsSteamGame(Game game)
        {
            return GetKnownAppId(game) != null;
        }

        // What Steam publishes for one game, as search results.
        public IReadOnlyList<SteamGridDbArtwork> GetArtwork(Game game, ArtworkKind kind)
        {
            var found = new List<SteamGridDbArtwork>();
            string appId = ResolveAppId(game);

            if (appId == null)
            {
                return found;
            }

            if (kind == ArtworkKind.Logo)
            {
                return found;
            }

            IReadOnlyList<SteamCdn.Asset> assets = kind == ArtworkKind.Cover
                ? SteamCdn.CoverAssets
                : SteamCdn.BackgroundAssets;

            int id = 0;

            foreach (SteamCdn.Asset asset in assets)
            {
                string url = SteamCdn.BuildUrl(appId, asset.FileName);

                if (!SteamCdn.Exists(url))
                {
                    continue;
                }

                found.Add(new SteamGridDbArtwork
                {
                    Id = ++id,
                    Url = url,

                    // Steam has no separate thumbnail, and these are small
                    // enough that the full asset serves as one.
                    ThumbnailUrl = url,
                    Width = asset.Width,
                    Height = asset.Height,
                    Style = asset.Label,
                    Mime = asset.MimeType,

                    // Named from a URL hash like a web result: Steam's asset
                    // names are shared across every game, so "header.jpg"
                    // alone would collide.
                    IsFromWeb = true
                });
            }

            return found;
        }

        // Steam's trailers for a game, as video artwork candidates.
        //
        // Only offered for backgrounds. A trailer behind a cover tile would be
        // letterboxed to a portrait frame and mostly black, and covers are
        // small enough that motion there reads as noise.
        public IReadOnlyList<SteamGridDbArtwork> GetVideo(Game game, ArtworkKind kind)
        {
            var found = new List<SteamGridDbArtwork>();

            if (kind == ArtworkKind.Cover || kind == ArtworkKind.Logo)
            {
                return found;
            }

            string appId = ResolveAppId(game);

            if (appId == null)
            {
                return found;
            }

            int id = 1000;

            foreach (SteamCdn.Trailer video in SteamCdn.GetVideos(appId))
            {
                // Checked, like the still assets are. Steam lists movies whose
                // files it no longer serves - Halo Infinite has an entry whose
                // movie480.mp4 is a 404 - and an unplayable tile is worse than
                // a missing one.
                if (!SteamCdn.Exists(video.Url))
                {
                    continue;
                }

                found.Add(new SteamGridDbArtwork
                {
                    Id = ++id,
                    Url = video.Url,
                    ThumbnailUrl = video.ThumbnailUrl,

                    // Store clips are authored at 600x338 and trailers at
                    // 854x480. Neither is guessed from the thumbnail, which is
                    // much smaller than the video behind it.
                    Width = video.IsLoopingClip ? 600 : 854,
                    Height = video.IsLoopingClip ? 338 : 480,

                    // Says which kind it is, because they suit different jobs:
                    // a clip loops seamlessly and is a third the size, a
                    // trailer has cuts and an ending.
                    Style = video.IsLoopingClip ? video.Name + " (loops)" : video.Name,

                    Mime = "video/mp4",
                    IsFromWeb = true
                });
            }

            return found;
        }
    }
}
