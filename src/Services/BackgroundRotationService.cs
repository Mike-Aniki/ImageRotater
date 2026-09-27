using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Drives write mode: picks an image for a game and pushes it into
    // Playnite's own BackgroundImage field.
    //
    // The theme-element mode does its own picking inside the control. This
    // service is the equivalent for themes that never place that element -
    // which is most of them.
    public class BackgroundRotationService
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IBackgroundImageSource _source;
        private readonly IBackgroundImageSource _coverSource;
        private readonly ImageSelector _selector;
        private readonly PlayniteBackgroundWriter _writer;
        private readonly OriginalArtPreserver _preserver;
        private readonly GameImageStore _store;
        private readonly Func<ImageRotaterSettings> _settings;

        // Optional: absent in tests, present at runtime when debug logging is on.
        private readonly FileLogger _fileLogger;

        // Makes a pick reachable from theme XAML. Separate because rotation
        // decides WHICH image a game shows, while publishing decides how a
        // theme gets at it - they change for unrelated reasons.
        private readonly ArtworkPublisher _publisher;

        // Last path written per game and kind, so an unchanged pick does not
        // cause a pointless database write on every selection. May hold a
        // letterboxed composite rather than a source image.
        private readonly Dictionary<string, string> _lastWritten = new Dictionary<string, string>();

        // Last SOURCE image picked per game and kind - what the selector's
        // avoid-previous compares against. Kept separately from _lastWritten
        // because letterboxing swaps the written path for a composite, which
        // never matches any candidate and would quietly disable
        // repeat-avoidance for exactly the games that letterbox.
        private readonly Dictionary<string, string> _lastPicked = new Dictionary<string, string>();

        // Last media published for theme-side background rendering, separate from the database fallback.
        private readonly Dictionary<string, PublishedFingerprint> _lastPublished =
            new Dictionary<string, PublishedFingerprint>();

        // Theme-side background publication is queued off the UI thread; stale per-game requests are discarded.
        private readonly object _backgroundPublishStateLock = new object();
        private readonly object _backgroundPublishIoLock = new object();
        private readonly Dictionary<Guid, long> _backgroundPublishGeneration =
            new Dictionary<Guid, long>();

        // Global sequence keeps invalidated publish requests from becoming current again.
        private long _backgroundPublishSequence;

        private sealed class PublishedFingerprint
        {
            public string Path { get; set; }
            public long Length { get; set; }
            public DateTime LastWriteTimeUtc { get; set; }
        }

        // The game each kind last rotated. Fullscreen re-raises the selection
        // event for the game already selected - on view changes and focus
        // shifts, not just navigation - so without this, EverySelection
        // re-picked several times while the user sat on one game.
        //
        // Per kind, because the two kinds now rotate at different moments:
        // backgrounds when a game is LEFT (a mid-transition background write
        // is the visible flash), covers when a game is ARRIVED AT (the user
        // wants to see the cover change, and the grid refresher makes the
        // tile re-read on demand). One shared guard would let either kind
        // suppress the other.
        private readonly Dictionary<ArtworkKind, Guid> _lastApplied =
            new Dictionary<ArtworkKind, Guid>();

        // When that rotation happened, so the guard can tell Playnite's
        // duplicate selection events (same instant) from the user genuinely
        // navigating back to a game (seconds later).
        private readonly Dictionary<ArtworkKind, DateTime> _lastAppliedAt =
            new Dictionary<ArtworkKind, DateTime>();

        private static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(750);

        public BackgroundRotationService(
            IBackgroundImageSource source,
            IBackgroundImageSource coverSource,
            ImageSelector selector,
            PlayniteBackgroundWriter writer,
            OriginalArtPreserver preserver,
            GameImageStore store,
            Func<ImageRotaterSettings> settings,
            FileLogger fileLogger = null,
            ArtworkPublisher publisher = null)
        {
            _fileLogger = fileLogger;

            // Never null: publishing is how a pick reaches a theme, so a
            // missing publisher would silently disable the feature rather than
            // fail. The parameter exists so callers can supply one wired to a
            // logger, not so they can opt out.
            _publisher = publisher ?? new ArtworkPublisher(store, fileLogger);

            _source = source;
            _coverSource = coverSource;
            _selector = selector;
            _writer = writer;
            _preserver = preserver;
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
        // Ceiling: in the theme-element display mode with Session selection, a
        // theme-hosted control keeps its own remembered pick and will not
        // follow slideshow swaps. The write path - which is what nearly every
        // setup renders - follows them everywhere.
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

            if (kind == ArtworkKind.Background && !settings.RotateBackgrounds)
            {
                return;
            }

            if (kind == ArtworkKind.Cover && !settings.RotateCovers)
            {
                return;
            }

            _lastApplied[kind] = game.Id;

            Apply(game, kind, kind == ArtworkKind.Cover ? _coverSource : _source, settings,
                SelectionMode.EverySelection);
        }

        private void ApplyTo(Game game, ArtworkKind kind, ImageRotaterSettings settings)
        {
            if (kind == ArtworkKind.Background && !settings.RotateBackgrounds)
            {
                return;
            }

            if (kind == ArtworkKind.Cover && !settings.RotateCovers)
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

            // Covers are ALWAYS written, even when a theme hosts the cover
            // control. The two are not alternatives: Game.CoverImage feeds
            // every view, while the control renders only where a theme places
            // it. The control picking independently for its own tile is a
            // second pick, not a conflicting one - nothing else draws from it.
            Apply(game, kind, kind == ArtworkKind.Cover ? _coverSource : _source, settings);
        }

        private void Apply(
            Game game,
            ArtworkKind kind,
            IBackgroundImageSource source,
            ImageRotaterSettings settings,
            SelectionMode? modeOverride = null)
        {
            if (source == null)
            {
                return;
            }

            // Any actual background rotation supersedes an older deferred
            // publish for this game, even if this new rotation later discovers
            // that nothing needs copying. Without this invalidation an older
            // queued copy could wake up after a newer no-op and publish stale
            // media back into backgrounds.published.
            if (kind == ArtworkKind.Background)
            {
                InvalidateBackgroundPublish(game.Id);
            }

            bool trace = kind == ArtworkKind.Background && _fileLogger != null && _fileLogger.IsEnabled;
            Stopwatch total = trace ? Stopwatch.StartNew() : null;
            Stopwatch step = trace ? Stopwatch.StartNew() : null;
            long artworkMs = 0;
            long preserveMs = 0;
            long candidatesMs = 0;
            long selectMs = 0;
            long motionMs = 0;
            long letterboxMs = 0;
            long publishMs = 0;
            long writeMs = 0;

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

            _preserver?.Preserve(game, kind);
            if (trace)
            {
                preserveMs = step.ElapsedMilliseconds;
                step.Restart();
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
                        $"has={artworkMs}ms preserve={preserveMs}ms list={candidatesMs}ms");
                }
                return;
            }

            Guid selectionKey = SelectionKey(game.Id, kind);
            SelectionMode mode = modeOverride
                ?? (kind == ArtworkKind.Cover ? settings.CoverSelectionMode : settings.SelectionMode);

            string path = _selector.Select(
                selectionKey, candidates, PreviousFor(game.Id, kind), mode);

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

            _lastPicked[WrittenKey(game.Id, kind)] = path;
            string picked = path;

            if (PosterFrame.IsMotion(path))
            {
                string still = PosterFrame.For(path);

                if (still == null)
                {
                    still = FirstStillCandidate(candidates, path);

                    if (string.IsNullOrEmpty(still))
                    {
                        if (trace)
                        {
                            motionMs = step.ElapsedMilliseconds;
                            step.Restart();
                        }

                        bool motionOnlySamePublished = kind == ArtworkKind.Background &&
                            IsSamePublishedBackground(game, picked);

                        if (!motionOnlySamePublished)
                        {
                            _publisher?.Publish(game, picked, kind, settings);

                            if (kind == ArtworkKind.Background)
                            {
                                RememberPublishedBackground(game, picked);
                            }
                        }

                        if (trace)
                        {
                            publishMs = step.ElapsedMilliseconds;
                            _fileLogger.Log(
                                $"BG PERF apply \"{game.Name}\" motion-only total={total.ElapsedMilliseconds}ms " +
                                $"has={artworkMs}ms preserve={preserveMs}ms list={candidatesMs}ms select={selectMs}ms " +
                                $"poster={motionMs}ms publish={publishMs}ms samePublished={motionOnlySamePublished} " +
                                $"candidates={candidates.Count} source={picked}");
                        }
                        return;
                    }

                    if (!PosterFrame.IsVideo(path))
                    {
                        picked = still;
                    }
                }

                path = still;
            }

            if (trace)
            {
                motionMs = step.ElapsedMilliseconds;
                step.Restart();
            }

            if (kind == ArtworkKind.Background && settings.LetterboxBackgrounds)
            {
                path = Letterboxer.For(path, ShapeBias.ScreenAspect);
            }

            if (trace)
            {
                letterboxMs = step.ElapsedMilliseconds;
                step.Restart();
            }

            string key = WrittenKey(game.Id, kind);
            string publishPath = PosterFrame.IsMotion(picked) ? picked : path;

            string previous;
            bool sameWritten = _lastWritten.TryGetValue(key, out previous) &&
                string.Equals(previous, path, StringComparison.OrdinalIgnoreCase);

            // A database fallback being unchanged does NOT mean the theme-side
            // media is unchanged. A video can use the same still fallback as a
            // previous pick, so only skip before publishing when BOTH channels
            // are already current. This preserves still<->video rotation while
            // avoiding truly redundant publication work.
            bool samePublished = kind == ArtworkKind.Background &&
                IsSamePublishedBackground(game, publishPath);

            if (sameWritten && samePublished)
            {
                if (trace)
                {
                    _fileLogger.Log(
                        $"BG PERF apply \"{game.Name}\" skip=same-all total={total.ElapsedMilliseconds}ms " +
                        $"has={artworkMs}ms preserve={preserveMs}ms list={candidatesMs}ms select={selectMs}ms " +
                        $"poster={motionMs}ms letterbox={letterboxMs}ms publish=0ms " +
                        $"candidates={candidates.Count} mode={mode} source={picked} write={path}");
                }
                return;
            }

            if (sameWritten && kind == ArtworkKind.Background)
            {
                if (!samePublished)
                {
                    QueueBackgroundPublish(game, publishPath);
                }

                if (trace)
                {
                    _fileLogger.Log(
                        $"BG PERF apply \"{game.Name}\" skip=db-same total={total.ElapsedMilliseconds}ms " +
                        $"has={artworkMs}ms preserve={preserveMs}ms list={candidatesMs}ms select={selectMs}ms " +
                        $"poster={motionMs}ms letterbox={letterboxMs}ms publish=queued samePublished={samePublished} " +
                        $"candidates={candidates.Count} mode={mode} source={picked} write={path}");
                }
                return;
            }

            if (!samePublished)
            {
                _publisher?.Publish(game, publishPath, kind, settings);

                if (kind == ArtworkKind.Background)
                {
                    RememberPublishedBackground(game, publishPath);
                }
            }

            if (trace)
            {
                publishMs = step.ElapsedMilliseconds;
                step.Restart();
            }

            if (sameWritten)
            {
                if (trace)
                {
                    _fileLogger.Log(
                        $"BG PERF apply \"{game.Name}\" skip=db-same total={total.ElapsedMilliseconds}ms " +
                        $"has={artworkMs}ms preserve={preserveMs}ms list={candidatesMs}ms select={selectMs}ms " +
                        $"poster={motionMs}ms letterbox={letterboxMs}ms publish={publishMs}ms samePublished={samePublished} " +
                        $"candidates={candidates.Count} mode={mode} source={picked} write={path}");
                }
                return;
            }

            bool written = _writer.SetArtwork(game, path, kind);
            if (trace)
            {
                writeMs = step.ElapsedMilliseconds;
            }

            if (written)
            {
                _lastWritten[WrittenKey(game.Id, kind)] = path;
                ImageDiagnostics.LogApplied(game.Name, path, _settings, 0, 0, kind);
            }

            if (trace)
            {
                _fileLogger.Log(
                    $"BG PERF apply \"{game.Name}\" total={total.ElapsedMilliseconds}ms written={written} " +
                    $"has={artworkMs}ms preserve={preserveMs}ms list={candidatesMs}ms select={selectMs}ms " +
                    $"poster={motionMs}ms letterbox={letterboxMs}ms publish={publishMs}ms writer={writeMs}ms " +
                    $"candidates={candidates.Count} mode={mode} source={picked} write={path}");
            }
        }

        // The session cache is keyed by Guid, so covers need a key distinct from
        // the game's own id or the two kinds would overwrite each other's
        // remembered choice. Deriving it from the id keeps that mapping stable
        // across restarts without a second cache.
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

        private static string WrittenKey(Guid gameId, ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover ? "cover:" + gameId : gameId.ToString();
        }

        // True only when the user has put artwork of this kind in the plugin's
        // own folder for this game - by adding files, downloading, or having
        // had a previous rotation preserve their original.
        //
        // Deliberately reads the store directly rather than the merged source,
        // because the merged source also offers Playnite's existing image and
        // would therefore report "yes" for every game in the library.
        private bool HasPluginArtwork(Game game, ArtworkKind kind)
        {
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

        // A candidate the database write can actually store: on disk, and not
        // itself moving. Falling back from an unstillable pick to another GIF
        // or a video would just repeat the same failure one candidate later.
        private static string FirstStillCandidate(IReadOnlyList<string> candidates, string skip)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];

                if (string.Equals(candidate, skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!PosterFrame.IsMotion(candidate) && IsUsable(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private void QueueBackgroundPublish(Game game, string sourcePath)
        {
            if (game == null || string.IsNullOrEmpty(sourcePath) || _publisher == null)
            {
                return;
            }

            long generation;
            lock (_backgroundPublishStateLock)
            {
                generation = ++_backgroundPublishSequence;
                _backgroundPublishGeneration[game.Id] = generation;
            }

            Task.Run(() =>
            {
                try
                {
                    // Serialise file swaps to avoid competing large media copies.
                    lock (_backgroundPublishIoLock)
                    {
                        if (!IsBackgroundPublishCurrent(game.Id, generation))
                        {
                            LogPublishCancelled(game, sourcePath, "before-copy");
                            return;
                        }

                        Stopwatch watch = _fileLogger != null && _fileLogger.IsEnabled
                            ? Stopwatch.StartNew()
                            : null;

                        _publisher.Publish(game, sourcePath, ArtworkKind.Background, null);

                        // Re-check after the non-cancellable copy before recording the publish as current.
                        if (!IsBackgroundPublishCurrent(game.Id, generation))
                        {
                            LogPublishCancelled(game, sourcePath, "after-copy");
                            return;
                        }

                        RememberPublishedBackground(game, sourcePath);

                        if (watch != null)
                        {
                            _fileLogger.Log(
                                $"BG PERF publish-async \"{game.Name}\" {watch.ElapsedMilliseconds}ms source={sourcePath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ImageRotater: deferred background publish failed");
                }
                finally
                {
                    CompleteBackgroundPublish(game.Id, generation);
                }
            });
        }

        private void InvalidateBackgroundPublish(Guid gameId)
        {
            lock (_backgroundPublishStateLock)
            {
                _backgroundPublishGeneration.Remove(gameId);
            }
        }

        private bool IsBackgroundPublishCurrent(Guid gameId, long generation)
        {
            lock (_backgroundPublishStateLock)
            {
                long current;
                return _backgroundPublishGeneration.TryGetValue(gameId, out current)
                    && current == generation;
            }
        }

        private void CompleteBackgroundPublish(Guid gameId, long generation)
        {
            lock (_backgroundPublishStateLock)
            {
                long current;
                if (_backgroundPublishGeneration.TryGetValue(gameId, out current)
                    && current == generation)
                {
                    _backgroundPublishGeneration.Remove(gameId);
                }
            }
        }

        private void LogPublishCancelled(Game game, string sourcePath, string stage)
        {
            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log(
                    $"BG PERF publish-cancelled \"{game?.Name}\" stage={stage} source={sourcePath}");
            }
        }

        private bool IsSamePublishedBackground(Game game, string sourcePath)
        {
            if (game == null || string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return false;
            }

            PublishedFingerprint previous;
            lock (_backgroundPublishStateLock)
            {
                if (!_lastPublished.TryGetValue(WrittenKey(game.Id, ArtworkKind.Background), out previous) ||
                    previous == null ||
                    !string.Equals(previous.Path, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            try
            {
                var sourceInfo = new FileInfo(sourcePath);
                if (sourceInfo.Length != previous.Length ||
                    sourceInfo.LastWriteTimeUtc != previous.LastWriteTimeUtc)
                {
                    return false;
                }

                string published = _publisher?.PublishedPathFor(
                    game.Id, ArtworkKind.Background, sourcePath);

                if (string.IsNullOrEmpty(published) || !File.Exists(published))
                {
                    return false;
                }

                return new FileInfo(published).Length == sourceInfo.Length;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void RememberPublishedBackground(Game game, string sourcePath)
        {
            if (game == null || string.IsNullOrEmpty(sourcePath))
            {
                return;
            }

            try
            {
                string published = _publisher?.PublishedPathFor(
                    game.Id, ArtworkKind.Background, sourcePath);

                if (string.IsNullOrEmpty(published) || !File.Exists(published) || !File.Exists(sourcePath))
                {
                    return;
                }

                var sourceInfo = new FileInfo(sourcePath);
                var publishedInfo = new FileInfo(published);

                if (publishedInfo.Length != sourceInfo.Length)
                {
                    return;
                }

                lock (_backgroundPublishStateLock)
                {
                    _lastPublished[WrittenKey(game.Id, ArtworkKind.Background)] = new PublishedFingerprint
                    {
                        Path = sourcePath,
                        Length = sourceInfo.Length,
                        LastWriteTimeUtc = sourceInfo.LastWriteTimeUtc
                    };
                }
            }
            catch (Exception)
            {
                // A failed fingerprint must not block a later publish.
            }
        }

        // Reads the SOURCE pick, not the written path: letterboxing writes a
        // composite whose path matches no candidate, and avoid-previous only
        // works when it compares like with like.
        private string PreviousFor(Guid gameId, ArtworkKind kind)
        {
            string previous;
            return _lastPicked.TryGetValue(WrittenKey(gameId, kind), out previous) ? previous : null;
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
            // Editing artwork invalidates any queued publish based on the old candidate set.
            InvalidateBackgroundPublish(gameId);

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
            _lastWritten.Clear();
            _lastPicked.Clear();
            lock (_backgroundPublishStateLock)
            {
                _lastPublished.Clear();
                _backgroundPublishGeneration.Clear();
            }

            // Also clear the guards, or re-selecting the game that was showing
            // when the restore ran would be treated as a repeat and skipped -
            // leaving it on its restored artwork until the user selected
            // something else and came back.
            _lastApplied.Clear();
            _lastAppliedAt.Clear();
        }
    }
}
