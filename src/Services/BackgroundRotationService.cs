using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Advances shared artwork selection for injected renderers without database writes.
    public class BackgroundRotationService
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IBackgroundImageSource _source;
        private readonly IBackgroundImageSource _coverSource;
        private readonly ImageSelector _selector;
        private readonly GameImageStore _store;
        private readonly Func<ImageRotaterSettings> _settings;

        // Optional: absent in tests, present at runtime when debug logging is on.
        private readonly FileLogger _fileLogger;

        // Last source choice per game/kind, used to avoid repeating a selection.
        private readonly Dictionary<string, string> _lastPicked = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _preparedNext = new Dictionary<string, string>();

        // Duplicate-selection guards are independent for each artwork kind.
        private readonly Dictionary<ArtworkKind, Guid> _lastApplied =
            new Dictionary<ArtworkKind, Guid>();

        // When that rotation happened, so the guard can tell Playnite's
        // duplicate selection events (same instant) from the user genuinely
        // navigating back to a game (seconds later).
        private readonly Dictionary<ArtworkKind, DateTime> _lastAppliedAt =
            new Dictionary<ArtworkKind, DateTime>();

        private static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(750);

        public BackgroundRotationService(IBackgroundImageSource source, IBackgroundImageSource coverSource,
            ImageSelector selector, GameImageStore store, Func<ImageRotaterSettings> settings, FileLogger fileLogger = null)
        {
            _fileLogger = fileLogger;
            _source = source;
            _coverSource = coverSource;
            _selector = selector;
            _store = store;
            _settings = settings;
        }

        // Called when the selected game changes, and once per game at startup.
        public void ApplyTo(Game game)
        {
            if (game == null)
            {
                return;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings == null || !settings.EnableRotation)
            {
                return;
            }

            // DisplayMode deliberately does not gate this: backgrounds always
            // write Game.BackgroundImage, and a theme element simply draws
            // over the top.
            ApplyTo(game, ArtworkKind.Background, settings);
            ApplyTo(game, ArtworkKind.Cover, settings);
        }

        // Rotates one kind for one game. The two kinds are driven at different
        // moments - backgrounds when a game is LEFT (a mid-transition
        // background write is the visible flash on switches), covers when a
        // game is ARRIVED AT (the user wants to watch the cover change, and
        // the grid refresher makes the tile re-read on demand) - so each must
        // be callable on its own.
        public void ApplyTo(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings == null || !settings.EnableRotation)
            {
                return;
            }

            ApplyTo(game, kind, settings);
        }

        // Rotates one kind for one game RIGHT NOW, ignoring the repeat guard
        // and the configured selection mode.
        //
        // This is the slideshow tick: the game has stayed selected, the timer
        // elapsed, and the whole point is a new image - so Session mode's
        // "keep this pick" and the guard's "already did this game" must both
        // be overridden. EverySelection semantics give the avoid-previous
        // behaviour a slideshow wants.
        //
        // Slideshow is now a dedicated selection mode. The exact pick chosen
        // here is also written into the remembered-current slot so native
        // Playnite artwork and theme-hosted controls advance together.
        public void ApplyNext(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings == null || !settings.EnableRotation)
            {
                return;
            }

            if (kind == ArtworkKind.Background && !settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Background))
            {
                return;
            }

            if (kind == ArtworkKind.Cover && !settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Cover))
            {
                return;
            }

            _lastApplied[kind] = game.Id;

            string prepared = TakePreparedNext(game.Id, kind);
            Apply(game, kind, kind == ArtworkKind.Cover ? _coverSource : _source, settings,
                SelectionMode.EverySelection, prepared);
        }

        // Reserve the exact next slideshow pick without applying it yet. The
        // renderer can decode this path during the timer wait, and ApplyNext
        // consumes the same reservation later so preloading never guesses the
        // wrong file. This is deliberately separate from normal
        // EverySelection focus changes.
        public string PrepareNext(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return null;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings == null || !settings.EnableRotation ||
                (kind == ArtworkKind.Background && !settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Background)) ||
                (kind == ArtworkKind.Cover && !settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Cover)))
            {
                return null;
            }

            IBackgroundImageSource source = kind == ArtworkKind.Cover ? _coverSource : _source;
            if (source == null || !HasPluginArtwork(game, kind))
            {
                return null;
            }

            IReadOnlyList<string> candidates = source.GetImagePaths(game);
            if (candidates == null || candidates.Count < 2)
            {
                return null;
            }

            Guid selectionKey = SelectionKey(game.Id, kind);
            string previous = PreviousFor(game.Id, kind);
            SelectionOrder selectionOrder = settings.GetSelectionOrder(
                game.Id, kind, kind == ArtworkKind.Cover ? settings.CoverSelectionOrder : settings.BackgroundSelectionOrder);

            // On the first slideshow wait the writer may not have rotated this
            // game yet (the theme control can already be showing its remembered
            // initial pick). Use that remembered Slideshow pick as the previous
            // item so the first timer tick cannot reserve the image that is
            // already on screen.
            if (string.IsNullOrEmpty(previous))
            {
                SelectionMode configuredMode = settings.GetSelectionMode(
                    game.Id, kind, kind == ArtworkKind.Cover ? settings.CoverSelectionMode : settings.SelectionMode);

                if (configuredMode == SelectionMode.Slideshow)
                {
                    previous = _selector.Select(
                        selectionKey, candidates, null, SelectionMode.Slideshow, selectionOrder);
                }
            }

            string path = _selector.Select(
                selectionKey, candidates, previous, SelectionMode.EverySelection, selectionOrder);

            if (!IsUsable(path))
            {
                path = FirstUsable(candidates, path);
            }

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            _preparedNext[ArtworkKey(game.Id, kind)] = path;
            return path;
        }

        public void ClearPreparedNext(Guid gameId, ArtworkKind kind)
        {
            _preparedNext.Remove(ArtworkKey(gameId, kind));
        }

        private string TakePreparedNext(Guid gameId, ArtworkKind kind)
        {
            string key = ArtworkKey(gameId, kind);
            string path;
            if (_preparedNext.TryGetValue(key, out path))
            {
                _preparedNext.Remove(key);
                return path;
            }

            return null;
        }

        private void ApplyTo(Game game, ArtworkKind kind, ImageRotaterSettings settings)
        {
            if (kind == ArtworkKind.Background && !settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Background))
            {
                return;
            }

            if (kind == ArtworkKind.Cover && !settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Cover))
            {
                return;
            }

            // Suppress the REPEAT selection events Playnite raises for the game
            // already selected - Fullscreen re-raises them on view changes and
            // focus shifts, and re-picking on those produced several different
            // images per game per second.
            //
            // Time-based, not identity-based, and that distinction is the whole
            // bug it used to cause. Keyed on "last game rotated for this kind",
            // navigating A -> B -> A found the key still holding A and returned
            // early, so coming BACK to a game never re-picked its cover: the
            // tile showed the same artwork forever in EverySelection mode.
            // Backgrounds hid this because they rotate for the game being LEFT,
            // which alternates the key naturally.
            //
            // A real navigation back to a game is seconds apart; the duplicate
            // events Playnite fires arrive in the same instant. A short window
            // separates them without ever blocking a genuine revisit.
            Guid last;
            DateTime when;

            if (_lastApplied.TryGetValue(kind, out last) && last == game.Id &&
                _lastAppliedAt.TryGetValue(kind, out when) &&
                (DateTime.UtcNow - when) < RepeatWindow)
            {
                return;
            }

            _lastApplied[kind] = game.Id;
            _lastAppliedAt[kind] = DateTime.UtcNow;

            Apply(game, kind, kind == ArtworkKind.Cover ? _coverSource : _source, settings);
        }

        private void Apply(
            Game game,
            ArtworkKind kind,
            IBackgroundImageSource source,
            ImageRotaterSettings settings,
            SelectionMode? modeOverride = null,
            string preparedPath = null)
        {
            if (source == null)
            {
                return;
            }

            bool trace = kind == ArtworkKind.Background && _fileLogger != null && _fileLogger.IsEnabled;
            Stopwatch total = trace ? Stopwatch.StartNew() : null;
            Stopwatch step = trace ? Stopwatch.StartNew() : null;
            long artworkMs = 0;
            long candidatesMs = 0;
            long selectMs = 0;

            bool hasArtwork = HasPluginArtwork(game, kind);
            if (trace)
            {
                artworkMs = step.ElapsedMilliseconds;
                step.Restart();
            }

            if (!hasArtwork)
            {
                if (trace)
                {
                    _fileLogger.Log($"BG PERF apply \"{game.Name}\" skip=no-art total={total.ElapsedMilliseconds}ms has={artworkMs}ms");
                }
                return;
            }

            IReadOnlyList<string> candidates = source.GetImagePaths(game);
            if (trace)
            {
                candidatesMs = step.ElapsedMilliseconds;
                step.Restart();
            }

            if (candidates == null || candidates.Count == 0)
            {
                if (trace)
                {
                    _fileLogger.Log(
                        $"BG PERF apply \"{game.Name}\" skip=no-candidates total={total.ElapsedMilliseconds}ms " +
                        $"has={artworkMs}ms list={candidatesMs}ms");
                }
                return;
            }

            Guid selectionKey = SelectionKey(game.Id, kind);
            SelectionMode configuredModeForGame = settings.GetSelectionMode(
                game.Id, kind, kind == ArtworkKind.Cover ? settings.CoverSelectionMode : settings.SelectionMode);
            SelectionMode mode = modeOverride ?? configuredModeForGame;
            SelectionOrder selectionOrder = settings.GetSelectionOrder(
                game.Id, kind, kind == ArtworkKind.Cover ? settings.CoverSelectionOrder : settings.BackgroundSelectionOrder);

            // In Theme Integration the controls choose the initial Slideshow
            // frame without calling ApplyTo, so _lastPicked can still be empty
            // at the first timer tick. Start from the frame remembered by the
            // control, not from null (which would select candidate 0 AGAIN and
            // leave that kind one tick behind the other kind).
            string previousSelection = PreviousFor(game.Id, kind);
            if (string.IsNullOrEmpty(previousSelection) && modeOverride.HasValue &&
                configuredModeForGame == SelectionMode.Slideshow &&
                !CandidateContains(candidates, preparedPath))
            {
                previousSelection = _selector.Select(
                    selectionKey, candidates, null, SelectionMode.Slideshow, selectionOrder);
            }

            string path = CandidateContains(candidates, preparedPath)
                ? preparedPath
                : _selector.Select(
                    selectionKey, candidates, previousSelection, mode, selectionOrder);

            if (trace)
            {
                selectMs = step.ElapsedMilliseconds;
                step.Restart();
            }

            if (!IsUsable(path))
            {
                path = FirstUsable(candidates, path);
            }

            if (string.IsNullOrEmpty(path))
            {
                if (trace)
                {
                    _fileLogger.Log(
                        $"BG PERF apply \"{game.Name}\" skip=no-usable total={total.ElapsedMilliseconds}ms " +
                        $"list={candidatesMs}ms select={selectMs}ms");
                }
                return;
            }

            _lastPicked[ArtworkKey(game.Id, kind)] = path;

            // A Slideshow renderer reads its current frame from the same
            // remembered-selection slot as Session. ApplyNext deliberately
            // picks with EverySelection semantics to avoid repeats, then moves
            // that exact pick into the remembered slot so the theme control
            // follows the shared choice instead of re-selecting an unrelated image.
            SelectionMode configuredMode = configuredModeForGame;
            if (modeOverride.HasValue &&
                configuredMode == SelectionMode.Slideshow)
            {
                _selector.Remember(selectionKey, path);
            }

            // Injection renders the shared selection directly. Rotation never
            // publishes copies, creates poster fallbacks or rewrites game metadata.
            if (trace)
            {
                _fileLogger.Log($"rotation selection game={game.Id} kind={kind} elapsed={total.ElapsedMilliseconds}ms candidates={candidates.Count} mode={mode}");
            }
        }
        // The session cache is keyed by Guid, so covers need a key distinct from
        // the game's own id or the two kinds would overwrite each other's
        // remembered choice. Deriving it from the id keeps that mapping stable
        // across restarts without a second cache.
        private static bool CandidateContains(IReadOnlyList<string> candidates, string path)
        {
            if (candidates == null || string.IsNullOrEmpty(path))
            {
                return false;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static Guid SelectionKey(Guid gameId, ArtworkKind kind)
        {
            if (kind != ArtworkKind.Cover)
            {
                return gameId;
            }

            byte[] bytes = gameId.ToByteArray();
            bytes[0] ^= 0xC0;
            return new Guid(bytes);
        }

        private static string ArtworkKey(Guid gameId, ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover ? "cover:" + gameId : gameId.ToString();
        }

        // True only when the user has put artwork of this kind in the plugin's
        // own folder for this game - by adding files, downloading, or having
        // The Compatibility safety copy (original_*) does not count: it may
        // remain after the user removes all actual ImageRotater artwork.
        //
        // Deliberately reads the store directly rather than the merged source,
        // because Theme Integration also offers Playnite's live Original and
        // would otherwise report "yes" for every game in the library.
        private bool HasPluginArtwork(Game game, ArtworkKind kind)
        {
            if (_settings()?.IsPackEnabled(game.Id) == true) return true;
            if (_store == null)
            {
                // No store to consult - treat as not opted in rather than
                // touching games we cannot verify.
                return false;
            }

            try
            {
                return _store.HasAnyImage(game.Id, kind);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsUsable(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path);
            }
            catch (Exception)
            {
                // A malformed path is unusable, not a reason to stop rendering.
                return false;
            }
        }

        // First candidate that still exists, skipping the one already rejected.
        private static string FirstUsable(IReadOnlyList<string> candidates, string skip)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];

                if (string.Equals(candidate, skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUsable(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private string PreviousFor(Guid gameId, ArtworkKind kind)
        {
            string previous;
            return _lastPicked.TryGetValue(ArtworkKey(gameId, kind), out previous) ? previous : null;
        }

        // Releases the repeat guard for one game, so the next selection event
        // rotates it again.
        //
        // Needed because adding or removing images changes the candidate list.
        // Without this, doing that to the game currently selected would leave
        // the guard set, and the new artwork would not appear until the user
        // selected another game and came back.
        public void Forget(Guid gameId)
        {
            // Editing artwork invalidates a prepared choice from the old candidate set.
            _preparedNext.Remove(ArtworkKey(gameId, ArtworkKind.Background));
            _preparedNext.Remove(ArtworkKey(gameId, ArtworkKind.Cover));

            foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                Guid last;
                if (_lastApplied.TryGetValue(kind, out last) && last == gameId)
                {
                    _lastApplied.Remove(kind);
                }
            }
        }

        // After a restore, the plugin no longer owns any game's background.
        public void ForgetAll()
        {
            _lastPicked.Clear();
            _preparedNext.Clear();

            // Also clear the guards, or re-selecting the game that was showing
            // when the restore ran would be treated as a repeat and skipped -
            // leaving it on its restored artwork until the user selected
            // something else and came back.
            _lastApplied.Clear();
            _lastAppliedAt.Clear();
        }
    }
}
