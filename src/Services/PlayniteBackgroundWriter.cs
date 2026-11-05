using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Undoes the artwork writes made by releases before 1.6, which stored the
    // rotation in Playnite's own Game.BackgroundImage / CoverImage fields.
    //
    // Rendering is now injected and never writes the library. This class keeps
    // the record of every original value those older releases replaced, so
    // startup, shutdown, Restore and Reset can still put each game back.
    public class PlayniteBackgroundWriter
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // What Load/Save persist. Kept as one object so both maps are written
        // together and cannot drift apart across a crash.
        private class WriterState
        {
            public Dictionary<string, string> Originals { get; set; }
            public Dictionary<string, string> Imported { get; set; }

            // Artwork ids this plugin wrote, so a restart can still tell them
            // from the user's own art. See _written.
            public List<string> Written { get; set; }

            // Library files queued for deletion at the next startup.
            public List<string> DeferredDeletePaths { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly string _backupPath;
        private readonly FileLogger _fileLogger;

        // gameId -> the BackgroundImage value the game had before ImageRotater
        // first touched it. An empty string means "the game genuinely had none",
        // which must be preserved as distinct from "never recorded".
        private Dictionary<string, string> _originals = new Dictionary<string, string>();

        // "gameId|sourcePath" -> the id Playnite gave that file when we imported
        // it. Persisted so a restart does not re-import everything: AddFile
        // copies unconditionally, so without this the store would grow by one
        // copy per rotation, forever.
        private Dictionary<string, string> _imported = new Dictionary<string, string>();

        // Every artwork id this plugin has written, so "is this the user's own
        // artwork or something we put there?" can be answered exactly.
        //
        // A path test cannot answer it. AddFile IMPORTS a copy into Playnite's
        // store, so after one rotation the game's artwork id resolves inside
        // Playnite's folder rather than ours - and the preserver, which skips
        // artwork it recognises as ours, recognised none of it. It therefore
        // copied our own rotation back in as if it were the user's original,
        // adding a candidate per restart and, for an animated pick, quietly
        // giving rotation a still to land on instead.
        //
        // PERSISTED, and an earlier version of this comment argued the opposite
        // on reasoning that was simply wrong. It claimed the ids were transient
        // because "the current value is rewritten before any preserve can run".
        //
        // It is not. On restart the set starts empty while Game.CoverImage
        // still holds an id THIS PLUGIN wrote last session, and Preserve runs
        // on the first selection - before any rotation rewrites anything. So it
        // saw our own artwork, failed to recognise it, and copied it in as a
        // fresh "original_" candidate. One per restart, and for a game whose
        // pick was animated that quietly handed rotation a still frame of its
        // own GIF to alternate with.
        //
        // Bounded by pruning on save rather than by forgetting: only ids still
        // referenced by a game survive, so this cannot grow without limit.
        private HashSet<string> _written =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Obsolete library files can be deleted on the next startup.
        private List<string> _deferredDeletePaths = new List<string>();

        private void NoteWritten(string artworkId)
        {
            if (string.IsNullOrEmpty(artworkId))
            {
                return;
            }

            if (_written.Add(artworkId))
            {
                Save();
            }
        }

        // True when this artwork id is one we wrote rather than the user's own.
        public bool WroteArtworkId(string artworkId)
        {
            return !string.IsNullOrEmpty(artworkId) && _written.Contains(artworkId);
        }

        // Compatibility mode temporarily replaces Game.CoverImage / BackgroundImage
        // with artwork imported by ImageRotater. If the user later chooses a new
        // image in Playnite, that new id is NOT one of ours. Treat it as the new
        // logical original so both Restore and the preserved safety copy follow
        // the user's latest choice instead of the image that happened to be set
        // when ImageRotater first touched the game.
        //
        // Returns true only when the remembered native value actually changed.
        // This makes it safe to call on every candidate rebuild: the common path
        // is just dictionary/set comparisons and performs no disk write.
        public bool ObserveUserArtwork(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return false;
            }

            string current = GetCurrent(game, kind) ?? string.Empty;

            // A value written by ImageRotater is the compatibility-mode output,
            // not a new user choice. Never let it replace the remembered native
            // artwork.
            if (!string.IsNullOrEmpty(current) && WroteArtworkId(current))
            {
                return false;
            }

            string key = MakeKey(game.Id, kind);
            string remembered;
            if (_originals.TryGetValue(key, out remembered) &&
                string.Equals(remembered ?? string.Empty, current, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _originals[key] = current;
            Save();

            return true;
        }

        public string GetRememberedOriginalId(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return null;
            }

            string value;
            return _originals.TryGetValue(MakeKey(game.Id, kind), out value)
                ? value
                : null;
        }

        // Kept so restore can find the preserved copy of a game's original
        // artwork when the recorded id no longer resolves.
        private readonly string _imagesRoot;

        public PlayniteBackgroundWriter(IPlayniteAPI api, string pluginUserDataPath, FileLogger fileLogger = null)
        {
            _api = api;
            _fileLogger = fileLogger;
            _backupPath = Path.Combine(pluginUserDataPath ?? string.Empty, "original-backgrounds.json");
            _imagesRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "Images");
            Load();
        }

        // True when an artwork id still points at a real file.
        //
        // Playnite reclaims unreferenced library files, so an id recorded
        // before the plugin replaced the artwork can be dead by the time anyone
        // restores it.
        private bool ArtworkIdResolves(string artworkId)
        {
            try
            {
                string full = _api?.Database?.GetFullFilePath(artworkId);
                return !string.IsNullOrEmpty(full) && File.Exists(full);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Puts the preserved copy of a game's original artwork back into
        // Playnite's store and returns its new id, or null when there is no
        // copy to import.
        //
        // OriginalArtPreserver writes these as "original_*" in the game's own
        // folder the first time the plugin touches it, precisely so the user's
        // artwork survives Playnite reclaiming the file it replaced.
        private string ReimportPreservedOriginal(Game game, ArtworkKind kind)
        {
            try
            {
                string folder = Path.Combine(
                    _imagesRoot,
                    game.Id.ToString(),
                    kind == ArtworkKind.Cover ? "covers" : "backgrounds");

                if (!Directory.Exists(folder))
                {
                    return null;
                }

                foreach (string file in Directory.GetFiles(folder, "original_*"))
                {
                    string id = ImportFile(file, game.Id);

                    if (!string.IsNullOrEmpty(id))
                    {
                        if (_fileLogger != null && _fileLogger.IsEnabled)
                        {
                            _fileLogger.Log(
                                $"restored \"{game.Name}\" {kind} from preserved copy; "
                                + "original Playnite file had been reclaimed");
                        }

                        return id;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not re-import the preserved {kind} for \"{game.Name}\"");
                return null;
            }
        }

        public int BackedUpCount
        {
            get { return _originals.Count; }
        }

        // Writes either Playnite field the way releases before 1.6 did.
        // Rendering no longer writes the library; this remains so the restore
        // path, which still repairs those older writes, is tested against the
        // state they actually left behind.
        public virtual bool SetArtwork(Game game, string imagePath, ArtworkKind kind)
        {
            if (game == null || string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                return false;
            }

            try
            {
                RememberOriginal(game, kind);
                string previousId = GetCurrent(game, kind);
                string newId = ImportFile(imagePath, game.Id);
                if (string.IsNullOrEmpty(newId))
                {
                    return false;
                }

                NoteWritten(newId);
                InvokeOnUi(() =>
                {
                    SetCurrent(game, kind, newId);
                    CommitGame(game);
                });
                DeleteReplacedCopy(game, kind, previousId);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not set {kind} for {game.Name}");
                return false;
            }
        }

        // Whether this plugin has written artwork into the library at all.
        //
        // Read before a reset deletes anything: a restore that put nothing back
        // is fine on a library the plugin never touched, and a disaster on one
        // where it did.
        public bool HasWrittenArtwork
        {
            get { return _written.Count > 0; }
        }

        // Clears any game still pointing at artwork this plugin wrote that no
        // longer exists on disk.
        //
        // Playnite renders a dead artwork id as SOLID BLACK, not as a blank
        // tile, so a grid of them looks like the theme broke rather than like
        // artwork went missing. The game's own art cannot be recovered here -
        // that is what the preserved originals are for - but nulling the field
        // makes Playnite fall back to its own placeholder, which is honest.
        //
        // Returns how many games were mended.
        public int ClearDeadReferences()
        {
            int cleared = 0;

            // Scans every GAME, not the written list.
            //
            // The written list is pruned on save - only ids still referenced by
            // a game survive - so after Playnite reclaimed the files it no
            // longer holds the very ids that went dead. Walking it therefore
            // found nothing, while hundreds of games still pointed at deleted
            // artwork. The game records are the only complete answer.
            //
            // Safe for artwork this plugin never touched: an id that resolves
            // is left alone, so a game with its own working artwork is never
            // altered.
            foreach (Game game in _api.Database.Games.ToList())
            {
                bool changed = false;

                foreach (ArtworkKind kind in
                    new[] { ArtworkKind.Background, ArtworkKind.Cover })
                {
                    string current = GetCurrent(game, kind);

                    if (string.IsNullOrEmpty(current) || ArtworkIdResolves(current))
                    {
                        continue;
                    }

                    // Prefer the game's own preserved original over a blank.
                    string replacement = ReimportPreservedOriginal(game, kind);

                    SetCurrent(game, kind, replacement);

                    _written.Remove(current);
                    changed = true;
                    cleared++;
                }

                if (!changed)
                {
                    continue;
                }

                Game target = game;

                InvokeOnUi(() => CommitGame(target));
            }

            if (cleared > 0)
            {
                Save();
            }

            return cleared;
        }

        public virtual int RestoreAll()
        {
            return RestoreAllCore(false, null);
        }

        // Virtual as a test seam: LibraryReset calls this overload, and its
        // "nothing is deleted when the restore fails" rule needs a failing one.
        public virtual int RestoreAll(Action<int, int> onProgress)
        {
            return RestoreAllCore(false, onProgress);
        }

        // Restore artwork references now and defer obsolete file deletion to startup.
        public int RestoreAllForShutdown()
        {
            return RestoreAllCore(true, null);
        }

        private int RestoreAllCore(bool deferFileCleanup, Action<int, int> onProgress)
        {
            int restored = 0;

            // Gathered first, committed once. This runs when Playnite closes,
            // for every game the plugin rotated in every session so far. One
            // Games.Update per game was one database write and one change
            // notification apiece - and the theme is still on screen to
            // service every notification - so a well-used library made
            // closing Fullscreen noticeably slow. A bulk update is one write.
            var touched = new List<Game>();
            var toDelete = new List<string>();
            List<KeyValuePair<string, string>> originals = _originals.ToList();
            int processed = 0;

            onProgress?.Invoke(0, originals.Count);

            foreach (KeyValuePair<string, string> entry in originals)
            {
                Guid gameId;
                if (!TryParseKey(entry.Key, out gameId))
                {
                    onProgress?.Invoke(++processed, originals.Count);
                    continue;
                }

                try
                {
                    Game game = LookupGame(gameId);
                    if (game == null)
                    {
                        // Game was removed from the library; nothing to restore.
                        onProgress?.Invoke(++processed, originals.Count);
                        continue;
                    }

                    ArtworkKind kind = KindFromKey(entry.Key);
                    string current = GetCurrent(game, kind);

                    // Empty means the game originally had no artwork of this
                    // kind, so null is the correct value to put back.
                    string restoreTo = string.IsNullOrEmpty(entry.Value) ? null : entry.Value;

                    // The recorded id may no longer resolve. Re-import the
                    // preserved original instead of handing Playnite a dead id.
                    if (restoreTo != null && !ArtworkIdResolves(restoreTo))
                    {
                        restoreTo = ReimportPreservedOriginal(game, kind) ?? restoreTo;
                    }

                    string finalValue = restoreTo;
                    InvokeOnUi(() => SetCurrent(game, kind, finalValue));

                    if (!touched.Contains(game))
                    {
                        touched.Add(game);
                    }

                    // Delete obsolete plugin files immediately or queue them for startup cleanup.
                    if (!string.IsNullOrEmpty(current) &&
                        !string.Equals(current, entry.Value, StringComparison.OrdinalIgnoreCase) &&
                        IsSafeToDelete(game, kind, current))
                    {
                        toDelete.Add(current);
                    }

                    restored++;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"ImageRotater: could not restore artwork for {entry.Key}");
                }

                onProgress?.Invoke(++processed, originals.Count);
            }

            if (touched.Count > 0)
            {
                try
                {
                    InvokeOnUi(() => CommitGames(touched));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ImageRotater: could not commit restored artwork");
                }
            }

            foreach (string id in toDelete.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (deferFileCleanup)
                {
                    try
                    {
                        string path = _api.Database.GetFullFilePath(id);
                        if (!string.IsNullOrEmpty(path) &&
                            !_deferredDeletePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            _deferredDeletePaths.Add(path);
                        }
                    }
                    catch (Exception)
                    {
                        // Deferred cleanup is best-effort.
                    }
                }
                else
                {
                    TryDeleteLibraryFileOnce(id);
                }

                // This plugin-written id is no longer referenced by the game.
                _written.Remove(id);
            }

            _originals.Clear();
            _imported.Clear();
            Save();

            return restored;
        }

        // Deletes files deferred by the previous clean shutdown.
        public int CleanupDeferredFiles()
        {
            if (_deferredDeletePaths == null || _deferredDeletePaths.Count == 0)
            {
                return 0;
            }

            int removed = 0;
            var remaining = new List<string>();

            foreach (string path in _deferredDeletePaths
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch (Exception)
                {
                    remaining.Add(path);
                }
            }

            _deferredDeletePaths = remaining;
            Save();
            return removed;
        }

        public virtual int RestoreKind(ArtworkKind targetKind)
        {
            return RestoreKind(targetKind, null);
        }

        public int RestoreArtwork(Guid gameId, ArtworkKind kind)
        {
            return RestoreKind(kind, gameId);
        }

        private int RestoreKind(ArtworkKind targetKind, Guid? onlyGame)
        {
            int restored = 0;
            var touched = new List<Game>();
            var toDelete = new List<string>();
            var restoredKeys = new List<string>();

            foreach (KeyValuePair<string, string> entry in _originals.ToList())
            {
                Guid gameId;
                if (!TryParseKey(entry.Key, out gameId) || KindFromKey(entry.Key) != targetKind ||
                    (onlyGame.HasValue && gameId != onlyGame.Value))
                {
                    continue;
                }

                try
                {
                    Game game = LookupGame(gameId);
                    if (game == null)
                    {
                        restoredKeys.Add(entry.Key);
                        continue;
                    }

                    string current = GetCurrent(game, targetKind);
                    string restoreTo = string.IsNullOrEmpty(entry.Value) ? null : entry.Value;

                    if (restoreTo != null && !ArtworkIdResolves(restoreTo))
                    {
                        restoreTo = ReimportPreservedOriginal(game, targetKind) ?? restoreTo;
                    }

                    string finalValue = restoreTo;
                    InvokeOnUi(() => SetCurrent(game, targetKind, finalValue));

                    if (!touched.Contains(game))
                    {
                        touched.Add(game);
                    }

                    if (!string.IsNullOrEmpty(current) &&
                        !string.Equals(current, entry.Value, StringComparison.OrdinalIgnoreCase) &&
                        IsSafeToDelete(game, targetKind, current))
                    {
                        toDelete.Add(current);
                    }

                    restoredKeys.Add(entry.Key);
                    restored++;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"ImageRotater: could not restore {targetKind} for {entry.Key}");
                }
            }

            if (touched.Count > 0)
            {
                try
                {
                    InvokeOnUi(() => CommitGames(touched));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"ImageRotater: could not commit restored {targetKind}");
                }
            }

            foreach (string id in toDelete)
            {
                TryDeleteLibraryFileOnce(id);
            }

            foreach (string key in restoredKeys)
            {
                _originals.Remove(key);
            }

            if (restoredKeys.Count > 0)
            {
                Save();
            }

            return restored;
        }

        // The database calls restore makes, as seams so the flow above can be
        // tested without Playnite. Nothing in the plugin overrides them.
        protected virtual Game LookupGame(Guid id)
        {
            return _api.Database.Games.Get(id);
        }

        protected virtual void CommitGame(Game game)
        {
            _api.Database.Games.Update(game);
        }

        protected virtual void CommitGames(IEnumerable<Game> games)
        {
            _api.Database.Games.Update(games);
        }

        protected virtual bool TryDeleteLibraryFileOnce(string id)
        {
            try
            {
                string path = _api.Database.GetFullFilePath(id);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Removes the copy a rotation just replaced, keeping the store bounded
        // now that every rotation imports afresh.
        //
        // Safe because Playnite's store is output only - candidates come solely
        // from the plugin's own folder, so nothing being deleted here can be a
        // rotation candidate. When the store was also a source, this delete
        // removed files a concurrent rotation had already chosen, which is what
        // produced the intermittent blank and stretched artwork.
        //
        // Still called only after Games.Update has committed the new id, so the
        // file being removed is no longer referenced.
        //
        // The removal itself runs on a worker thread. The theme's tile still
        // has the previous file open - a WPF bitmap not loaded with OnLoad
        // keeps its handle - so the delete finds it locked, and Playnite's
        // RemoveFile retries five times half a second apart before giving up.
        // On the UI thread that was a 2.6 s freeze on every cover rotation,
        // measured in Fullscreen, for a delete that then failed anyway. Off
        // the UI thread the retries cost nothing, and by the time they run
        // the tile has usually let go.
        private void DeleteReplacedCopy(Game game, ArtworkKind kind, string previousId)
        {
            if (string.IsNullOrEmpty(previousId))
            {
                return;
            }

            DeleteReplacedCopyNow(game, kind, previousId);
        }

        private void DeleteReplacedCopyNow(Game game, ArtworkKind kind, string previousId)
        {
            if (string.IsNullOrEmpty(previousId))
            {
                return;
            }

            // Decided here, on the caller's thread, against the game as it is
            // now. Never the user's own artwork: IsSafeToDelete refuses
            // anything recorded as an original for either kind, or still used
            // by the game's other artwork slots.
            if (!IsSafeToDelete(game, kind, previousId))
            {
                return;
            }

            string name = game.Name;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    RemoveLibraryFile(previousId);
                }
                catch (Exception ex)
                {
                    // A failed delete costs one leftover file, which RestoreAll
                    // clears. Not worth failing the rotation over.
                    Logger.Warn(ex, $"ImageRotater: could not remove the replaced {kind} copy for \"{name}\"");
                }
            });
        }

        // Virtual as a test seam: the real call is Playnite's, retries and all.
        protected virtual void RemoveLibraryFile(string id)
        {
            _api.Database.RemoveFile(id);
        }

        // Runs the action on Playnite's UI thread, synchronously.
        //
        // Synchronous on purpose: SetArtwork deletes the replaced file straight
        // after this returns, and that delete must not overtake the commit.
        //
        // Falls back to running inline when there is no dispatcher - unit tests
        // and any non-UI host - so the writer stays usable without one.
        //
        // virtual so a test can stand in for the dispatcher and simulate the
        // user moving on WHILE a write is queued. That gap is the entire bug
        // this guards against, and it cannot be reproduced by calling
        // SetArtwork normally.
        protected virtual void InvokeOnUi(Action action)
        {
            System.Windows.Threading.Dispatcher dispatcher = null;

            try
            {
                dispatcher = _api?.MainView?.UIDispatcher;
            }
            catch (Exception)
            {
                // Some hosts throw rather than return null when there is no
                // main view. Either way the inline path below is correct.
            }

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        private void RememberOriginal(Game game, ArtworkKind kind)
        {
            string key = MakeKey(game.Id, kind);

            // Only the FIRST value is kept. Recording again on a later rotation
            // would overwrite the user's real original with plugin artwork,
            // making restore a no-op.
            if (_originals.ContainsKey(key))
            {
                return;
            }

            _originals[key] = GetCurrent(game, kind) ?? string.Empty;
            Save();
        }

        // Backgrounds and covers are backed up independently, so the key
        // carries the kind. Backgrounds keep the bare-guid form used before
        // covers existed, so an existing backup file still restores.
        private static string MakeKey(Guid gameId, ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover
                ? "cover:" + gameId
                : gameId.ToString();
        }

        private static bool TryParseKey(string key, out Guid gameId)
        {
            gameId = Guid.Empty;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            string raw = key.StartsWith("cover:", StringComparison.OrdinalIgnoreCase)
                ? key.Substring("cover:".Length)
                : key;

            return Guid.TryParse(raw, out gameId);
        }

        private static ArtworkKind KindFromKey(string key)
        {
            return !string.IsNullOrEmpty(key) &&
                   key.StartsWith("cover:", StringComparison.OrdinalIgnoreCase)
                ? ArtworkKind.Cover
                : ArtworkKind.Background;
        }

        private static string GetCurrent(Game game, ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover ? game.CoverImage : game.BackgroundImage;
        }

        // Imports a file into Playnite's own store and returns its id.
        //
        // virtual for the same reason as the two below: without it, reaching
        // any of the commit logic in a test needs a live Playnite database.
        protected virtual string ImportFile(string imagePath, Guid gameId)
        {
            return _api.Database.AddFile(imagePath, gameId);
        }

        // virtual so a test can observe whether a write actually reached the
        // game - the difference between "suppressed as stale" and "committed"
        // is otherwise invisible without a live Playnite database.
        protected virtual void SetCurrent(Game game, ArtworkKind kind, string value)
        {
            if (kind == ArtworkKind.Cover)
            {
                game.CoverImage = value;
            }
            else
            {
                game.BackgroundImage = value;
            }
        }

        // Never delete a file that is a recorded original, or that Playnite is
        // still using elsewhere on the game. Both kinds' originals are checked,
        // not just this one's: a game whose cover and background were the same
        // file would otherwise lose the other kind's way back.
        private bool IsSafeToDelete(Game game, ArtworkKind kind, string fileId)
        {
            foreach (ArtworkKind other in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                string original;
                if (_originals.TryGetValue(MakeKey(game.Id, other), out original) &&
                    string.Equals(original, fileId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Still referenced by the game's other artwork slots.
            string otherSlot = kind == ArtworkKind.Cover ? game.BackgroundImage : game.CoverImage;

            return !string.Equals(fileId, otherSlot, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fileId, game.Icon, StringComparison.OrdinalIgnoreCase);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_backupPath))
                {
                    return;
                }

                string json = File.ReadAllText(_backupPath);

                // Files written before the imported-id map existed are a bare
                // dictionary of originals. Read either shape so an existing
                // backup keeps working and restore is not silently lost.
                var state = JsonConvert.DeserializeObject<WriterState>(json);
                if (state != null && state.Originals != null)
                {
                    _originals = state.Originals;
                    _imported = state.Imported ?? new Dictionary<string, string>();
                    _written = state.Written != null
                        ? new HashSet<string>(state.Written, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _deferredDeletePaths = state.DeferredDeletePaths ?? new List<string>();
                    return;
                }

                _originals = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
                _imported = new Dictionary<string, string>();
                _written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _deferredDeletePaths = new List<string>();
            }
            catch (Exception ex)
            {
                // A corrupt backup must not stop the plugin loading, but it does
                // mean restore is no longer possible - say so loudly.
                Logger.Error(ex, "ImageRotater: could not read the original-background backup. Restore will not be available.");
                _originals = new Dictionary<string, string>();
                _imported = new Dictionary<string, string>();
                _written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _deferredDeletePaths = new List<string>();
            }
        }

        private void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_backupPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Temp-then-move: an interrupted write must not leave a corrupt
                // backup, because that is the user's only way back.
                var state = new WriterState
                {
                    Originals = _originals,
                    Imported = _imported,
                    Written = new List<string>(_written),
                    DeferredDeletePaths = new List<string>(_deferredDeletePaths)
                };

                string temp = _backupPath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));

                if (File.Exists(_backupPath))
                {
                    File.Delete(_backupPath);
                }

                File.Move(temp, _backupPath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not save the original-background backup");
            }
        }
    }
}
