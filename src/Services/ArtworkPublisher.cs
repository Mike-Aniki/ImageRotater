using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Everything a theme consumes: the per-game file it binds, and the settings
    // values that describe the current selection.
    //
    // Split out of BackgroundRotationService because the two answer different
    // questions. Rotation decides WHICH image a game should show; publishing
    // decides how a theme gets at it. They changed for unrelated reasons all
    // evening - every theme-binding experiment churned this half while the
    // picking logic stayed still.
    public class ArtworkPublisher
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly GameImageStore _store;
        private readonly FileLogger _fileLogger;

        public ArtworkPublisher(GameImageStore store, FileLogger fileLogger = null)
        {
            _store = store;
            _fileLogger = fileLogger;
        }

        // Makes a rotation's pick reachable from theme XAML.
        //
        // Two separate channels, because neither is sufficient alone:
        //   - the per-game file, which a tile can path to from its own game id
        //   - the settings values, which describe only the selected game
        public void Publish(Game game, string path, ArtworkKind kind, ImageRotaterSettings settings)
        {
            if (game == null || string.IsNullOrEmpty(path))
            {
                return;
            }

            bool published = _store != null && _store.PublishCurrent(game.Id, path, kind);

            if (kind == ArtworkKind.Cover && settings != null)
            {
                settings.CurrentCoverPath = path;

                // Set with the path for the same reason the id is: a theme
                // decides whether to show its MediaElement from this, and the
                // two going out of step would leave a video element covering a
                // still pick, or a still covering a video.
                settings.CurrentCoverIsVideo = PosterFrame.IsVideo(path);

                // Set with the path, never separately: a theme compares this
                // against its own tile's game id to decide whether the path
                // applies to it, so the pair going out of step would show one
                // game's cover on another game's tile.
                settings.CurrentCoverGameId = game.Id.ToString();
            }

            LogPublish(game, path, kind, published);
        }

        // Makes sure the published file exists for every game before any tile
        // renders.
        //
        // Themes must load these with CacheOption=OnLoad, because the default
        // binding never releases its file handle and the plugin could then
        // never republish. But OnLoad reads the bytes at EndInit, so a MISSING
        // file throws FileNotFoundException instead of yielding null - and that
        // throw lands inside FullscreenTilePanel.MeasureOverride, which takes
        // Playnite down.
        //
        // A tile builds its path from its own game id and cannot ask whether
        // the plugin has artwork: a WPF trigger compares a binding to a
        // literal, and the tile's id is not one. So the file has to exist for
        // every game in the library, not just the ones the user set up.
        public int SeedEveryGame(IEnumerable<Game> games)
        {
            if (games == null || _store == null)
            {
                return 0;
            }

            int seeded = 0;

            foreach (Game game in games)
            {
                foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
                {
                    try
                    {
                        // Only the first candidate and first video are needed here.
                        string firstCandidate;
                        string firstVideo;
                        _store.GetSeedCandidates(game.Id, kind, out firstCandidate, out firstVideo);

                        // Seed a video separately from the still, because they
                        // publish to different files and a theme's MediaElement
                        // has nothing to show until the video one exists.
                        //
                        // Without this, a freshly started Playnite shows no
                        // animation on any tile until rotation happens to pick
                        // that game's video - which for a game with several
                        // covers can take a while and looks like the feature is
                        // broken.
                        SeedVideo(game.Id, kind, firstVideo);

                        if (File.Exists(PublishedPathFor(game.Id, kind)))
                        {
                            continue;
                        }

                        bool wrote = firstCandidate != null
                            ? _store.PublishCurrent(game.Id, firstCandidate, kind)
                            // A 70-byte transparent placeholder, NOT a copy of
                            // the game's artwork. The file only has to EXIST:
                            // 1x1 transparent renders as nothing and the
                            // theme's own artwork shows through. Copying real
                            // images would duplicate the whole library (~600 MB
                            // on a 300-game library) for no visual difference.
                            : _store.EnsurePublishedPlaceholder(game.Id, kind);

                        if (wrote)
                        {
                            seeded++;
                        }
                    }
                    catch (Exception)
                    {
                        // One unreadable game must not stop the rest seeding.
                    }
                }
            }

            if (seeded > 0)
            {
                _fileLogger?.Log($"seeded {seeded} published file(s) so no theme tile references a missing path");
            }

            return seeded;
        }

        // Publishes a game's first video candidate if it has one and nothing is
        // published yet. Separate from the still seeding above: the two live in
        // different files and a theme picks between them by existence, so a
        // game with both needs both present.
        private void SeedVideo(Guid gameId, ArtworkKind kind, string candidate)
        {
            try
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    return;
                }

                if (File.Exists(PublishedPathFor(gameId, kind, candidate)))
                {
                    return;
                }

                _store.PublishCurrent(gameId, candidate, kind);
            }
            catch (Exception)
            {
                // One unreadable game must not stop the rest seeding.
            }
        }

        public string PublishedPathFor(Guid gameId, ArtworkKind kind, string sourcePath = null)
        {
            // Video publishes under a real video extension rather than the
            // .tile name stills use, because MediaElement picks its decoder by
            // extension. Reporting the .tile path for a video pick made the log
            // say "exists=False" for a publish that had actually succeeded -
            // which read as a failure twice while chasing an unrelated bug.
            string name = PosterFrame.IsVideo(sourcePath)
                ? GameImageStore.PublishedVideoNameFor(sourcePath)
                : GameImageStore.PublishedFileName;

            // Asks the store rather than rebuilding the path, so the two cannot
            // disagree about where published files live - they did briefly, and
            // the log then reported a path nothing was ever written to.
            return Path.Combine(_store.GetPublishedFolder(gameId, kind), name);
        }

        // The path a theme has to bind and whether the file is really there -
        // the two facts that decide whether a tile can render this, and neither
        // is visible from Playnite's shared log.
        private void LogPublish(Game game, string path, ArtworkKind kind, bool published)
        {
            if (_fileLogger == null || !_fileLogger.IsEnabled)
            {
                return;
            }

            string expected = _store == null ? "(no store)" : PublishedPathFor(game.Id, kind, path);

            _fileLogger.Log($"{kind}: \"{game.Name}\" -> {Path.GetFileName(path)}");
            _fileLogger.Log($"    published={published} exists={File.Exists(expected)} at {expected}");
        }
    }
}
