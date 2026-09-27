using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // The user-facing side of image storage: add files, open the folder,
    // clear a game's images. Without this the plugin has no way to be
    // populated and is unusable regardless of how well it renders.
    public class ImageMenuHandler
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IPlayniteAPI _api;
        private readonly GameImageStore _store;
        private readonly SessionSelectionCache _sessionCache;
        private readonly ISteamGridDbClient _steamGridDb;
        private readonly ArtworkDownloader _downloader;
        private readonly Func<ImageRotaterSettings> _settings;

        // Called when a game's candidate images have changed. Write mode
        // rotates a game only once per selection, so without this the game
        // currently selected would keep its old artwork until the user
        // navigated away and back. A callback rather than the rotation service
        // itself: this class only needs to announce the change, not drive it.
        private readonly Action<Guid> _onImagesChanged;

        public ImageMenuHandler(
            IPlayniteAPI api,
            GameImageStore store,
            SessionSelectionCache sessionCache,
            ISteamGridDbClient steamGridDb,
            ArtworkDownloader downloader,
            Func<ImageRotaterSettings> settings = null,
            Action<Guid> onImagesChanged = null)
        {
            _api = api;
            _store = store;
            _sessionCache = sessionCache;
            _steamGridDb = steamGridDb;
            _downloader = downloader;

            // A getter rather than the object: settings can change while
            // Playnite runs, and a snapshot taken at startup would leave the
            // search dialog using an ffmpeg path the user has since corrected.
            _settings = settings;
            _onImagesChanged = onImagesChanged;
        }

        // Opens the browse-and-pick dialog for one game. The auto-download path
        // takes the highest-resolution match without asking; this is for when
        // that guess is not the one you wanted.
        public void BrowseSteamGridDb(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            if (_steamGridDb == null || !_steamGridDb.IsConfigured)
            {
                _api.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCImageRotaterSgdbKeyRequired"),
                    "ImageRotater");
                return;
            }

            try
            {
                Window window = _api.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true
                });

                string label = kind == ArtworkKind.Cover
                    ? Loc.Get("LOCImageRotaterCoversLower")
                    : Loc.Get("LOCImageRotaterBackgroundsLower");
                window.Title = Loc.Format("LOCImageRotaterSearchWindowTitle", label, game.Name);
                // Bigger in Fullscreen, where this is read from a sofa and the
                // desktop size leaves most of a TV unused. Clamped to the
                // screen so it cannot open larger than the display.
                bool fullscreen = _api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;

                window.Width = fullscreen
                    ? Math.Min(1600, SystemParameters.PrimaryScreenWidth * 0.92)
                    : 1000;

                window.Height = fullscreen
                    ? Math.Min(950, SystemParameters.PrimaryScreenHeight * 0.9)
                    : 640;

                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Content = new Controls.SteamGridDbSearchView(
                    _api, _steamGridDb, _downloader, game, kind,
                    _settings == null ? null : _settings(),

                    // The web view's data folder goes beside the images rather
                    // than in the user's temp, so it is removed with the plugin.
                    System.IO.Path.GetDirectoryName(_store.ImagesRoot));

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not open the SteamGridDB browser");
                _api.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCImageRotaterSearchOpenFailed"),
                    "ImageRotater");
            }
        }

        // Fetches backgrounds from SteamGridDB for each selected game and saves
        // the highest-resolution match into that game's folder.
        //
        // Runs on Playnite's progress dialog because it is network-bound over
        // potentially many games, and the user needs to be able to cancel.
        public void DownloadFromSteamGridDb(IEnumerable<Game> games, ArtworkKind kind)
        {
            var targets = games?.ToList();
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            if (_steamGridDb == null || !_steamGridDb.IsConfigured)
            {
                _api.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCImageRotaterSgdbKeyRequired"),
                    "ImageRotater");
                return;
            }

            int downloaded = 0;
            var failures = new List<string>();

            _api.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.ProgressMaxValue = targets.Count;

                foreach (Game game in targets)
                {
                    if (progress.CancelToken.IsCancellationRequested)
                    {
                        break;
                    }

                    progress.Text = $"ImageRotater: {game.Name}";

                    try
                    {
                        string saved = DownloadBestForGame(game, kind).GetAwaiter().GetResult();
                        if (saved != null)
                        {
                            downloaded++;
                        }
                        else
                        {
                            failures.Add(game.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, $"ImageRotater: SteamGridDB download failed for {game.Name}");
                        failures.Add(game.Name);
                    }

                    progress.CurrentProgressValue++;
                }
            },
            new GlobalProgressOptions(Loc.Get("LOCImageRotaterDownloadingArtwork"), true)
            {
                IsIndeterminate = false
            });

            ShowDownloadSummary(downloaded, failures);
        }

        private async Task<string> DownloadBestForGame(Game game, ArtworkKind kind)
        {
            SteamGridDbResult<List<SteamGridDbGame>> matches =
                await _steamGridDb.SearchGamesAsync(game.Name).ConfigureAwait(false);

            if (!matches.Success || matches.Data == null || matches.Data.Count == 0)
            {
                return null;
            }

            // Covers come from grids, backgrounds from heroes - different
            // endpoints with different dimension conventions.
            SteamGridDbArtworkType type = kind == ArtworkKind.Cover
                ? SteamGridDbArtworkType.Grid
                : SteamGridDbArtworkType.Hero;

            SteamGridDbResult<List<SteamGridDbArtwork>> artwork =
                await _steamGridDb.GetArtworkAsync(matches.Data[0].Id, type).ConfigureAwait(false);

            if (!artwork.Success || artwork.Data == null)
            {
                return null;
            }

            // Default filter: no adult/humor/epilepsy-tagged art, and nothing
            // animated - ImageRotater cannot render animated formats yet, so
            // downloading one would produce a broken-image placeholder.
            IReadOnlyList<SteamGridDbArtwork> usable =
                ArtworkFilter.Apply(artwork.Data, new ArtworkFilterState());

            SteamGridDbArtwork best;

            if (kind == ArtworkKind.Cover)
            {
                // Covers are drawn into a fixed-aspect card, so shape matters
                // more than pixel count: a correctly-proportioned 600x900 looks
                // right where a sharper 16:9 image would be cropped or letterboxed.
                best = CoverAspect.BestCover(usable, GetTargetCoverAspect());
            }
            else
            {
                // Backgrounds fill the screen, so pixels matter - but not so
                // much that a lossy source should win on count alone.
                //
                // Sorting purely by area picked a heavily compressed JPEG over
                // a clean PNG of nearly the same size. A Fullscreen theme then
                // scales that up to fill a TV and the compression artefacts are
                // exactly what the user sees. PNG is lossless and survives the
                // scaling; the file is bigger, which is the trade being made
                // deliberately.
                //
                // Preferred among COMPARABLE sizes rather than absolutely: a
                // 640x360 PNG beating a 3840x2160 JPEG would be worse than the
                // problem being fixed. A lossless source wins unless the lossy
                // one is meaningfully larger.
                best = PreferLossless(usable, a => (long)a.Width * a.Height);
            }

            if (best == null)
            {
                return null;
            }

            return await _downloader.DownloadAsync(game.Id, best, kind).ConfigureAwait(false);
        }

        // How much bigger a lossy image has to be before it outranks a lossless
        // one.
        //
        // 1.4x on area - roughly 1080p against 1440p. Below that the PNG's
        // lack of compression artefacts is worth more than the extra pixels,
        // especially once a Fullscreen theme scales the result up. Above it,
        // the resolution gap is large enough that the JPEG genuinely carries
        // more detail.
        private const double LossyAreaAdvantageRequired = 1.4;

        // Picks the best artwork, preferring lossless formats among comparable
        // sizes.
        //
        // "score" is what counts as better for this kind - area for a
        // background, aspect fit for a cover.
        private static SteamGridDbArtwork PreferLossless(
            IReadOnlyList<SteamGridDbArtwork> candidates,
            Func<SteamGridDbArtwork, long> score)
        {
            SteamGridDbArtwork bestLossless = null;
            SteamGridDbArtwork bestLossy = null;

            foreach (SteamGridDbArtwork candidate in candidates)
            {
                bool lossless = IsLossless(candidate);

                if (lossless)
                {
                    if (bestLossless == null || score(candidate) > score(bestLossless))
                    {
                        bestLossless = candidate;
                    }
                }
                else if (bestLossy == null || score(candidate) > score(bestLossy))
                {
                    bestLossy = candidate;
                }
            }

            if (bestLossless == null)
            {
                return bestLossy;
            }

            if (bestLossy == null)
            {
                return bestLossless;
            }

            // The lossy one only wins by being substantially bigger.
            return score(bestLossy) > score(bestLossless) * LossyAreaAdvantageRequired
                ? bestLossy
                : bestLossless;
        }

        // One definition of "lossless", shared with the cover ranking.
        private static bool IsLossless(SteamGridDbArtwork artwork)
        {
            return CoverAspect.IsLossless(artwork);
        }

        // Playnite exposes the grid cell ratio the user has actually configured,
        // so the cover target does not have to be a setting we invent and ask
        // them to tune.
        private double GetTargetCoverAspect()
        {
            try
            {
                return CoverAspect.FromGridRatio(
                    _api.ApplicationSettings.GridItemWidthRatio,
                    _api.ApplicationSettings.GridItemHeightRatio);
            }
            catch (Exception)
            {
                return CoverAspect.DefaultAspect;
            }
        }

        private void ShowDownloadSummary(int downloaded, List<string> failures)
        {
            if (downloaded == 0 && failures.Count == 0)
            {
                return;
            }

            string message = Loc.Format("LOCImageRotaterDownloadedGames", downloaded);

            if (failures.Count > 0)
            {
                // Name the games that failed rather than only counting them, so
                // the user can retry or add art manually for those specific
                // titles.
                const int maxNamed = 10;
                message += Loc.Format(
                    "LOCImageRotaterNoArtworkGames",
                    failures.Count,
                    string.Join("\n", failures.Take(maxNamed)));

                if (failures.Count > maxNamed)
                {
                    message += Loc.Format("LOCImageRotaterAndMore", failures.Count - maxNamed);
                }
            }

            _api.Dialogs.ShowMessage(message, "ImageRotater");
        }

        private void NotifyImagesChanged(Guid gameId)
        {
            _sessionCache?.Forget(gameId);
            _onImagesChanged?.Invoke(gameId);
        }

        public void ManageImages(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            try
            {
                Window window = _api.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true
                });

                string label = kind == ArtworkKind.Cover
                    ? Loc.Get("LOCImageRotaterCoversLower")
                    : Loc.Get("LOCImageRotaterBackgroundsLower");
                window.Title = Loc.Format("LOCImageRotaterManagerWindowTitle", label, game.Name);

                bool fullscreen = _api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
                window.Width = fullscreen
                    ? Math.Min(1500, SystemParameters.PrimaryScreenWidth * 0.9)
                    : 1080;
                window.Height = fullscreen
                    ? Math.Min(920, SystemParameters.PrimaryScreenHeight * 0.88)
                    : 700;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                var searchView = new Controls.SteamGridDbSearchView(
                    _api,
                    _steamGridDb,
                    _downloader,
                    game,
                    kind,
                    _settings == null ? null : _settings(),
                    System.IO.Path.GetDirectoryName(_store.ImagesRoot),
                    embedded: true);

                window.Content = new Controls.ArtworkManagerView(
                    _api,
                    _store,
                    _sessionCache,
                    game,
                    kind,
                    NotifyImagesChanged,
                    searchView,
                    () => DownloadFromSteamGridDb(new[] { game }, kind));
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"ImageRotater: could not open the artwork manager for {game.Name}");
                _api.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCImageRotaterManagerOpenFailed"),
                    "ImageRotater");
            }
        }

        // Opens a file picker and copies the chosen images into the game's
        // folder. Returns how many were added.
        public int AddImages(IEnumerable<Game> games, ArtworkKind kind)
        {
            var targets = games?.ToList();
            if (targets == null || targets.Count == 0)
            {
                return 0;
            }

            // One picker for every artwork format, rather than a separate "Add
            // video" command beside "Add images".
            //
            // Nothing downstream distinguishes them: the store copies whatever
            // it is handed, lists mp4 and webm alongside the image formats, and
            // both controls already render video. A second menu item would be
            // two commands doing identical work behind different filters, and a
            // user who picked the wrong one would get a file dialog that
            // silently hides the files they came for.
            //
            // The combined filter leads so the default view shows everything;
            // the narrower groups are there for a folder holding both.
            List<string> selected = _api.Dialogs.SelectFiles(
                Loc.Get("LOCImageRotaterArtworkFilter") + "|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif;*.mp4;*.webm"
                + "|" + Loc.Get("LOCImageRotaterImagesFilter") + "|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif"
                + "|" + Loc.Get("LOCImageRotaterVideoFilter") + "|*.mp4;*.webm");

            if (selected == null || selected.Count == 0)
            {
                return 0;
            }

            int added = 0;
            foreach (Game game in targets)
            {
                foreach (string source in selected)
                {
                    if (_store.AddImage(game.Id, source, kind) != null)
                    {
                        added++;
                    }
                }

                // The candidate list changed, so any remembered choice for this
                // game is stale - drop it so the new image can be picked.
                NotifyImagesChanged(game.Id);
            }

            if (added > 0)
            {
                string message = Loc.Format("LOCImageRotaterAddedImages", added, targets.Count);

                // Rotation needs something to rotate TO. Checked for a single
                // game only; a batch add is not where a one-image setup happens.
                Game only = targets.Count == 1 ? targets[0] : null;
                if (only != null && _store.RotationPoolSize(
                        only.Id, kind,
                        kind == ArtworkKind.Cover ? only.CoverImage : only.BackgroundImage) < 2)
                {
                    message += Loc.Get("LOCImageRotaterAddMoreTip");
                }

                _api.Dialogs.ShowMessage(message, "ImageRotater");
            }

            return added;
        }

        // Opens the game's image folder in Explorer, creating it if needed so
        // the user can drop files straight in.
        public void OpenImageFolder(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            try
            {
                string folder = _store.GetGameFolder(game.Id, kind);
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", folder);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not open image folder for {game.Name}");
            }
        }

        // Remuxes any fragmented video the selected games hold.
        //
        // A YouTube download can arrive as DASH fragments, which Windows
        // renders as SOLID BLACK while reporting them as playing - so the user
        // sees a black tile with no error anywhere. This puts the same video
        // into a container everything can read; a stream copy, no quality
        // loss.
        public void RepairVideos(IEnumerable<Game> games)
        {
            var targets = games?.ToList();
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            if (!GifConverter.IsAvailable)
            {
                _api.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCImageRotaterRepairVideosNeedFfmpeg"),
                    "ImageRotater");
                return;
            }

            int repaired = 0;
            int failed = 0;
            int untouched = 0;

            foreach (Game game in targets)
            {
                BulkConverter.Result result =
                    BulkConverter.RepairVideosForGame(_store, game.Id);

                repaired += result.Converted;
                failed += result.Failed;
                untouched += result.Skipped;

                if (result.Converted > 0)
                {
                    // The repaired file has the same path, but rotation may be
                    // holding a decoded copy of the broken one.
                    NotifyImagesChanged(game.Id);
                }
            }

            string message = repaired > 0
                ? Loc.Format("LOCImageRotaterRepairedVideos", repaired)
                : untouched > 0
                    ? Loc.Get("LOCImageRotaterVideosAlreadyFine")
                    : Loc.Get("LOCImageRotaterNoVideosSelected");

            if (failed > 0)
            {
                message += Loc.Format("LOCImageRotaterRepairFailedCount", failed);
            }

            _api.Dialogs.ShowMessage(message, "ImageRotater");
        }

        public void ClearImages(IEnumerable<Game> games, ArtworkKind kind)
        {
            var targets = games?.ToList();
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            int total = targets.Sum(g => _store.GetImagePaths(g.Id, kind).Count);
            if (total == 0)
            {
                _api.Dialogs.ShowMessage(Loc.Get("LOCImageRotaterNoImagesRemove"), "ImageRotater");
                return;
            }

            // Deleting the user's files is not undoable, so confirm first and
            // say exactly how many are going.
            MessageBoxResult confirm = _api.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterRemoveQuestion", total, targets.Count),
                "ImageRotater",
                MessageBoxButton.YesNo);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            int removed = 0;
            foreach (Game game in targets)
            {
                foreach (string path in _store.GetImagePaths(game.Id, kind))
                {
                    if (_store.RemoveImage(path))
                    {
                        removed++;
                    }
                }

                NotifyImagesChanged(game.Id);
            }

            _api.Dialogs.ShowMessage(Loc.Format("LOCImageRotaterRemovedImages", removed), "ImageRotater");
        }
    }
}
