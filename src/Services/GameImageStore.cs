using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Plugin-owned image storage: one folder per game, files inside it.
    //
    //   {pluginUserData}\Images\{gameId}\whatever.jpg
    //
    // Deliberately no metadata JSON. The folder listing IS the data, so there
    // is nothing to keep in sync, nothing to corrupt, and a user can add or
    // remove images with Explorer and have it just work.
    public class GameImageStore
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Extensions we can display.
        //
        // Video earns its place here now that both controls render it with a
        // MediaElement. It stays out of the database write, which decodes to a
        // single bitmap and has no way to show a frame of it - the rotation
        // service skips motion picks for that write and falls back to a still
        // candidate, while a theme gets the real file through current.tile.
        private static readonly HashSet<string> SupportedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif",
                ".mp4", ".webm"
            };

        private static readonly string[] Empty = new string[0];

        // Whether a file would be listed at all, by extension. For callers
        // that copy files in from elsewhere and want to skip what would only
        // sit invisibly on disk.
        public static bool IsSupported(string path)
        {
            return !string.IsNullOrEmpty(path)
                && SupportedExtensions.Contains(Path.GetExtension(path));
        }

        private readonly string _imagesRoot;

        // Themes join this with a game's own id to reach that game's current
        // artwork, so it has to be readable from outside.
        public string ImagesRoot
        {
            get { return _imagesRoot; }
        }

        public GameImageStore(string pluginUserDataPath)
        {
            _imagesRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "Images");
        }

        // Backgrounds and covers live in separate subfolders so a file can never
        // end up in the wrong slot, and so a user managing them by hand can see
        // which is which without a naming convention to remember.
        //
        //   Images\{gameId}\backgrounds\
        //   Images\{gameId}\covers\
        public string GetGameFolder(Guid gameId, ArtworkKind kind)
        {
            return Path.Combine(_imagesRoot, gameId.ToString(), SubfolderFor(kind));
        }

        // Where published copies live: a sibling of the candidate folder, never
        // inside it.
        //
        //   Images\{gameId}\covers\            <- candidates
        //   Images\{gameId}\covers.published\  <- current.tile, current.mp4
        //
        // This is a performance fix, not tidiness. GetImagePaths caches a
        // folder's listing and validates it against the folder's write time.
        // Publishing into that same folder bumps that write time, so every
        // rotation invalidated its own cache and the next selection re-ran the
        // full enumeration and content deduplication. Measured on the shipped
        // assembly: a cache hit costs ~380 ticks, the listing right after a
        // publish 13,000-25,000. Moving the write out keeps the cache valid
        // across rotations.
        //
        // It also retires a subtler problem. A published file sitting among the
        // candidates had to be filtered out by name, or a game's artwork could
        // rotate onto a copy of itself - which worked only because ".tile" was
        // not a listed extension, and had to be patched again the moment video
        // began publishing as ".mp4".
        public string GetPublishedFolder(Guid gameId, ArtworkKind kind)
        {
            return Path.Combine(
                _imagesRoot, gameId.ToString(), SubfolderFor(kind) + PublishedFolderSuffix);
        }

        public const string PublishedFolderSuffix = ".published";

        private static string SubfolderFor(ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover ? "covers" : "backgrounds";
        }

        // Images saved before the split live directly in Images\{gameId}\.
        // They were all backgrounds, so move them into backgrounds\ the first
        // time that game is read. Doing it lazily avoids a startup pass over
        // an entire library.
        private void MigrateLegacyLayout(Guid gameId)
        {
            try
            {
                string gameRoot = Path.Combine(_imagesRoot, gameId.ToString());
                if (!Directory.Exists(gameRoot))
                {
                    return;
                }

                string[] loose = Directory.GetFiles(gameRoot);
                if (loose.Length == 0)
                {
                    return;
                }

                string target = Path.Combine(gameRoot, SubfolderFor(ArtworkKind.Background));
                Directory.CreateDirectory(target);

                foreach (string file in loose)
                {
                    string destination = Path.Combine(target, Path.GetFileName(file));
                    if (!File.Exists(destination))
                    {
                        File.Move(file, destination);
                    }
                }

                Logger.Info($"ImageRotater: moved {loose.Length} image(s) into backgrounds\\ for game {gameId}");
            }
            catch (Exception ex)
            {
                // A failed migration must not stop the game rendering - the
                // files simply stay where they are and are not listed.
                Logger.Warn(ex, $"ImageRotater: could not migrate legacy images for game {gameId}");
            }
        }

        // One selection triggers several listings of the same folder - the
        // candidate list, the opt-in check, the theme's HasDataCover - and a
        // directory enumeration each time is pure waste. Validated by the
        // folder's write time, which every file add, remove or publish bumps,
        // so the cache can never serve a list that a write has outdated.
        private readonly Dictionary<string, Tuple<DateTime, IReadOnlyList<string>>> _listCache =
            new Dictionary<string, Tuple<DateTime, IReadOnlyList<string>>>(StringComparer.Ordinal);

        private readonly object _listCacheLock = new object();

        // Legacy migration scans the game's root folder for loose files. Once
        // per game per session is enough - the layout cannot regress while the
        // process runs.
        private readonly HashSet<Guid> _migrationChecked = new HashSet<Guid>();

        // Every displayable image assigned to this game. Sorted by name so the
        // order is stable across calls - an unstable order would make a
        // "remember which one we picked" cache pick differently each time.
        public IReadOnlyList<string> GetImagePaths(Guid gameId, ArtworkKind kind)
        {
            try
            {
                string folder = CandidateFolderFor(gameId, kind);
                if (folder == null)
                {
                    return Empty;
                }

                string key = gameId.ToString("N") + "|" + (int)kind;
                DateTime stamp = Directory.GetLastWriteTimeUtc(folder);

                lock (_listCacheLock)
                {
                    Tuple<DateTime, IReadOnlyList<string>> hit;
                    if (_listCache.TryGetValue(key, out hit) && hit.Item1 == stamp)
                    {
                        return hit.Item2;
                    }
                }

                IReadOnlyList<string> listed = DeduplicateByContent(ListCandidateFiles(folder));

                listed = PreferChosenOverPreserved(listed);

                lock (_listCacheLock)
                {
                    _listCache[key] = Tuple.Create(stamp, listed);
                }

                return listed;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not list images for game {gameId}");
                return Empty;
            }
        }

        // The same files as GetImagePaths, duplicates included, and without
        // opening any of them.
        //
        // For sweeps over the whole library. GetImagePaths reads every pair
        // of same-length files in full to tell a duplicate from a coincidence,
        // which is right for the one game being rotated and ruinous for all
        // of them at once: a library migrated from BackgroundChanger holds a
        // preserved original beside an identical copy for most games, and the
        // startup seed was hashing the lot on the UI thread at every launch -
        // seconds, for an answer it never used. The seed only asks whether a
        // game has files, which comes first, and whether one is a video.
        public IReadOnlyList<string> GetImagePathsRaw(Guid gameId, ArtworkKind kind)
        {
            try
            {
                string folder = CandidateFolderFor(gameId, kind);
                if (folder == null)
                {
                    return Empty;
                }

                return ListCandidateFiles(folder);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not list images for game {gameId}");
                return Empty;
            }
        }

        public bool HasAnyImage(Guid gameId, ArtworkKind kind)
        {
            try
            {
                string folder = CandidateFolderFor(gameId, kind);
                if (folder == null)
                {
                    return false;
                }

                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(file)) &&
                        !IsPublishedCopy(file))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not check images for game {gameId}");
                return false;
            }
        }

        public void GetSeedCandidates(
            Guid gameId,
            ArtworkKind kind,
            out string firstCandidate,
            out string firstVideo)
        {
            firstCandidate = null;
            firstVideo = null;

            try
            {
                string folder = CandidateFolderFor(gameId, kind);
                if (folder == null)
                {
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (!SupportedExtensions.Contains(Path.GetExtension(file)) ||
                        IsPublishedCopy(file))
                    {
                        continue;
                    }

                    if (firstCandidate == null ||
                        string.Compare(file, firstCandidate, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        firstCandidate = file;
                    }

                    if (PosterFrame.IsVideo(file) &&
                        (firstVideo == null ||
                         string.Compare(file, firstVideo, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        firstVideo = file;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not seed-check images for game {gameId}");
            }
        }

        // The game's candidate folder, after the one-time legacy move, or null
        // when it does not exist. Deliberately uncached: creating the folder
        // later must be seen immediately, and a missing-folder check is cheap.
        private string CandidateFolderFor(Guid gameId, ArtworkKind kind)
        {
            if (kind == ArtworkKind.Background)
            {
                bool firstTime;
                lock (_listCacheLock)
                {
                    firstTime = _migrationChecked.Add(gameId);
                }

                if (firstTime)
                {
                    MigrateLegacyLayout(gameId);
                }
            }

            string folder = GetGameFolder(gameId, kind);
            return Directory.Exists(folder) ? folder : null;
        }

        private static List<string> ListCandidateFiles(string folder)
        {
            return Directory.GetFiles(folder)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                .Where(f => !IsPublishedCopy(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // The fixed name a theme can path to without knowing which file the
        // rotation chose. Themes build this in XAML from the game's own id, so
        // every tile reads its own game's current artwork with no per-tile
        // coordination from the plugin.
        //
        // Kept out of GetImagePaths by the extension check there: this is a
        // copy of a real candidate, and offering it back would let a game's
        // cover rotate onto itself.
        public const string PublishedFileName = "current.tile";

        // Video needs its own published name, for one concrete reason:
        // MediaElement is DirectShow / Media Foundation, which chooses a decoder
        // partly by FILE EXTENSION. WPF's imaging stack sniffs content, which is
        // why stills and GIFs are happy being called ".tile" - video is not.
        // Measured, not assumed: the same mp4 bytes play as "current.mp4" and
        // fail with "Media file download failed" as "current.tile".
        //
        // The extension follows the SOURCE rather than being fixed, because it
        // is what tells the decoder which container to expect - calling a webm
        // ".mp4" would hand Media Foundation a file that is not what its name
        // claims. A theme therefore binds one path per format it supports;
        // "current.mp4" is the one that plays on a stock Windows install.
        public const string PublishedVideoBaseName = "current";

        public static string PublishedVideoNameFor(string sourcePath)
        {
            string ext = Path.GetExtension(sourcePath);

            return string.IsNullOrEmpty(ext)
                ? PublishedVideoBaseName
                : PublishedVideoBaseName + ext.ToLowerInvariant();
        }

        // Artwork the user chose wins over artwork that merely happened to be
        // there.
        //
        // A preserved original is whatever Playnite already had for the game -
        // a Steam grid, a metadata provider's cover - copied in the first time
        // the plugin rotated it. That copy has to exist: once Game.CoverImage
        // points at a plugin file the original is unreferenced, Playnite's
        // library cleanup can reclaim it, and "Restore original backgrounds"
        // then holds an id that resolves to nothing.
        //
        // But existing for safety is not the same as competing for screen time.
        // Rotating a deliberately-added animated cover against the still that
        // came with the game reads as the animation breaking every few seconds,
        // and the still is not even the same picture.
        //
        // So preserved art rotates only when it is ALL the game has. Add
        // anything of your own and it steps aside, still on disk, still
        // restorable.
        private static IReadOnlyList<string> PreferChosenOverPreserved(IReadOnlyList<string> paths)
        {
            if (paths.Count < 2)
            {
                return paths;
            }

            var chosen = new List<string>(paths.Count);

            for (int i = 0; i < paths.Count; i++)
            {
                if (!IsPreservedOriginal(paths[i]))
                {
                    chosen.Add(paths[i]);
                }
            }

            // Nothing but preserved art - it is the whole rotation, as it
            // should be for a game the user has not set up.
            return chosen.Count > 0 ? chosen : paths;
        }

        // Matches the prefix OriginalArtPreserver writes. Kept here rather than
        // referenced from that class so the listing has no dependency on it.
        public const string PreservedPrefix = "original_";

        public static bool IsPreservedOriginal(string path)
        {
            string name = Path.GetFileName(path);

            return !string.IsNullOrEmpty(name)
                && name.StartsWith(PreservedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // How many images a game will rotate through: what its folder holds,
        // plus its own Playnite art if that has not been preserved yet - the
        // first rotation copies it in, and it joins the pool.
        //
        // Rotation needs two. Callers that just added a single image use this
        // to tell the user so, rather than letting them conclude the plugin
        // does nothing. A game with its own cover needs only one download; one
        // with none needs two.
        public int RotationPoolSize(Guid gameId, ArtworkKind kind, string ownArtId)
        {
            IReadOnlyList<string> held = GetImagePaths(gameId, kind);

            bool ownStillToCome = !string.IsNullOrEmpty(ownArtId)
                && !held.Any(IsPreservedOriginal);

            return held.Count + (ownStillToCome ? 1 : 0);
        }

        // The published copy is not a candidate. It is a COPY of one, so
        // offering it back would let a game's artwork rotate onto itself - and
        // for video that also means the file being played is the one the next
        // rotation is about to overwrite.
        //
        // Published files now live in a sibling folder, so nothing this catches
        // should exist any more. It stays for installs that ran an earlier
        // version: their old current.* files are still sitting in the candidate
        // folders, and without this a game whose artwork was published as .mp4
        // would find that copy and rotate onto it. Cheap, and the alternative
        // is a migration pass over the whole library at startup.
        private static bool IsPublishedCopy(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);

            return string.Equals(name, PublishedVideoBaseName, StringComparison.OrdinalIgnoreCase);
        }

        // Drops any published video for this kind. A theme shows its video
        // element when the file exists, so leaving one behind after rotating to
        // a still would keep the tile playing the wrong artwork.
        //
        // Best effort: a MediaElement still holding the file makes the delete
        // fail, and one stale video is a far better outcome than an exception
        // out of a rotation.
        private static void RemovePublishedVideos(string folder, string keep = null)
        {
            foreach (string ext in new[] { ".mp4", ".webm" })
            {
                try
                {
                    string name = PublishedVideoBaseName + ext;

                    if (string.Equals(name, keep, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string stale = Path.Combine(folder, name);

                    if (File.Exists(stale))
                    {
                        File.Delete(stale);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // A 1x1 fully transparent PNG.
        //
        // Themes must load published files with CacheOption=OnLoad so the
        // plugin can republish them, and OnLoad THROWS on a missing file -
        // inside Playnite's layout pass, which is fatal. A tile builds its path
        // from its own game id and cannot ask whether the plugin has artwork,
        // because a WPF trigger compares a binding to a literal and the tile's
        // id is not one. So the file has to exist for every game in the
        // library.
        //
        // It does not have to contain anything: 1x1 transparent renders as
        // nothing and the theme's own artwork shows through. 70 bytes per game
        // instead of a copy of its artwork.
        private static readonly byte[] TransparentPixelPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        // Makes sure the published file exists, without claiming the game has
        // artwork. Does nothing when a real image is already published.
        // Removes published copies for games that no longer have any artwork to
        // publish from.
        //
        // These outlive their source: the placeholder is written once so a
        // theme's binding has a file to resolve, and nothing deleted it when
        // the candidates went away. A theme then stretches a 1x1 transparent
        // PNG across the tile, which renders as a black thumbnail.
        //
        // Returns how many folders were cleared.
        public int RemoveOrphanedPublished()
        {
            int removed = 0;

            if (string.IsNullOrEmpty(_imagesRoot) || !Directory.Exists(_imagesRoot))
            {
                return removed;
            }

            string[] gameFolders;

            try
            {
                gameFolders = Directory.GetDirectories(_imagesRoot);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not list " + _imagesRoot);
                return removed;
            }

            foreach (string gameFolder in gameFolders)
            {
                foreach (ArtworkKind kind in
                    new[] { ArtworkKind.Background, ArtworkKind.Cover })
                {
                    string candidates = Path.Combine(
                        gameFolder, kind == ArtworkKind.Cover ? "covers" : "backgrounds");

                    // Real artwork still here, so the published copy is wanted.
                    if (Directory.Exists(candidates) &&
                        SafeFileCount(candidates) > 0)
                    {
                        continue;
                    }

                    string published = Path.Combine(
                        gameFolder,
                        (kind == ArtworkKind.Cover ? "covers" : "backgrounds")
                            + PublishedFolderSuffix);

                    if (!Directory.Exists(published))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(published, true);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "ImageRotater: could not remove " + published);
                    }
                }
            }

            return removed;
        }

        private static int SafeFileCount(string folder)
        {
            try { return Directory.GetFiles(folder).Length; }
            catch (Exception) { return 0; }
        }

        // Whether a published tile is the placeholder this store writes, by
        // length: no real artwork is 70 bytes.
        private static bool IsPlaceholder(string path)
        {
            try
            {
                return new FileInfo(path).Length == TransparentPixelPng.Length;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool EnsurePublishedPlaceholder(Guid gameId, ArtworkKind kind)
        {
            try
            {
                string folder = GetPublishedFolder(gameId, kind);
                string target = Path.Combine(folder, PublishedFileName);

                if (File.Exists(target))
                {
                    return false;
                }

                Directory.CreateDirectory(folder);
                File.WriteAllBytes(target, TransparentPixelPng);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not create the {kind} placeholder for game {gameId}");
                return false;
            }
        }

        // Copies the chosen file to the fixed per-game name themes bind to.
        //
        // Written temp-then-move so a grid tile never reads a half-written
        // file - tiles can load this at any moment, not just after a rotation.
        // Overwriting is safe: WPF re-reads an overwritten path rather than
        // serving its cached bitmap, and holds no lock on a file it has
        // finished decoding (both verified against this exact flow).
        public bool PublishCurrent(Guid gameId, string sourcePath, ArtworkKind kind)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return false;
            }

            try
            {
                // The published folder, NOT the candidate folder. Writing here
                // used to bump the candidate folder's write time, which is what
                // the listing cache validates against - so every rotation
                // invalidated its own cache and the next selection paid for a
                // full re-enumeration and re-deduplication.
                string folder = GetPublishedFolder(gameId, kind);
                Directory.CreateDirectory(folder);

                // Video publishes under its own extension so MediaElement can
                // pick a decoder; everything else keeps the single .tile name a
                // theme binds for stills.
                bool video = PosterFrame.IsVideo(sourcePath);

                string target = Path.Combine(
                    folder,
                    video ? PublishedVideoNameFor(sourcePath) : PublishedFileName);

                // A still pick REMOVES any published video, and a video pick
                // replaces it.
                //
                // This used to keep the video across still picks, reasoning
                // that a theme's MediaElement would otherwise go dark whenever
                // rotation landed on a still. That was wrong in practice: the
                // video is drawn ON TOP of the still tile, so keeping it meant
                // the video simply never went away. A game with a video among
                // its covers appeared frozen on that one clip no matter how
                // many times rotation picked something else.
                //
                // Rotation that cannot be seen is not rotation. Clearing the
                // video lets the still show, and the next pick that lands on
                // the video brings it back.
                if (!video)
                {
                    RemovePublishedVideos(folder);
                }
                else
                {
                    RemovePublishedVideos(folder, keep: Path.GetFileName(target));

                    // A poster frame for the .tile a theme binds - but only
                    // when that tile is still the 1x1 placeholder.
                    //
                    // Publishing only the video left that file on the
                    // placeholder, which a theme stretches across the tile and
                    // Playnite renders as SOLID BLACK - so a downloaded video
                    // looked like broken artwork on every theme whose tile is
                    // a plain Image rather than a MediaElement.
                    //
                    // A real still already there is left alone. Taking a
                    // poster spawns ffmpeg and waits for it, half a second on
                    // the UI thread - and the startup seed republishes every
                    // video a still pick has displaced, so a library with a
                    // handful of videos paid that on every launch, for a
                    // poster no one could see under the video anyway.
                    //
                    // Needs ffmpeg. Without it the placeholder stays, which is
                    // the pre-existing behaviour rather than a new failure.
                    string tile = Path.Combine(folder, PublishedFileName);

                    if (!File.Exists(tile) || IsPlaceholder(tile))
                    {
                        GifConverter.ExtractPoster(sourcePath, tile);
                    }
                }

                // Already current - skip the copy. Session mode republishes the
                // same pick on every selection, and a full file copy each time
                // is waste that also bumps the folder's write time, needlessly
                // invalidating the listing cache.
                //
                // Length plus source-not-newer is a heuristic: two DIFFERENT
                // picks with identical byte counts would be wrongly skipped.
                // Real artwork files virtually never collide on exact length,
                // and the cost of a miss is one stale publish until the next
                // rotation.
                if (File.Exists(target))
                {
                    var sourceInfo = new FileInfo(sourcePath);
                    var targetInfo = new FileInfo(target);

                    if (targetInfo.Length == sourceInfo.Length &&
                        targetInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
                    {
                        return true;
                    }
                }

                string temp = target + ".tmp";

                File.Copy(sourcePath, temp, true);

                // Temp-then-swap so a tile never reads a half-written file.
                //
                // This only works because themes bind with CacheOption=OnLoad,
                // which closes WPF's file handle at load. Under the default
                // binding WPF holds that handle for the bitmap's lifetime and
                // every write here fails with IOException - the file becomes
                // permanently unwritable the first time a tile shows it. That
                // is exactly what stopped covers from publishing while
                // backgrounds, which no tile binds, kept working.
                if (File.Exists(target))
                {
                    File.Replace(temp, target, null);
                }
                else
                {
                    File.Move(temp, target);
                }

                return true;
            }
            catch (Exception ex)
            {
                // The database write still happened, so every other view is
                // correct - only theme-side tiles keep their previous image.
                Logger.Warn(ex, $"ImageRotater: could not publish the current {kind} for game {gameId}");
                return false;
            }
        }

        // Drops files whose CONTENT duplicates an earlier candidate.
        //
        // Duplicates happen naturally: the preserver copies whatever artwork
        // Playnite had stored, and that is often an earlier download of a file
        // already in the pool under its own name. Rotation then legitimately
        // "changes" between the two - a visible fade to the identical picture,
        // which is the single most confusing thing a slideshow can do.
        //
        // Cheap first, exact second: only files with a length collision get
        // hashed, and hashes are computed at most once per listing rebuild -
        // the result is cached with the listing itself.
        private static IReadOnlyList<string> DeduplicateByContent(List<string> paths)
        {
            if (paths.Count < 2)
            {
                return paths;
            }

            try
            {
                var byLength = new Dictionary<long, List<string>>();

                foreach (string path in paths)
                {
                    if (PosterFrame.IsVideo(path))
                    {
                        continue;
                    }

                    long length = new FileInfo(path).Length;

                    List<string> bucket;
                    if (!byLength.TryGetValue(length, out bucket))
                    {
                        byLength[length] = bucket = new List<string>();
                    }

                    bucket.Add(path);
                }

                // Within a bucket, artwork the user chose outranks a preserved
                // original of the SAME image.
                //
                // The survivor used to be whichever sorted first, and
                // "original_" sorts before "sgdb_" and "web_" - so a preserved
                // copy displaced the real file it was copied from. Harmless for
                // stills, since the bytes are identical either way, but it also
                // meant the surviving name looked like the user's own art when
                // it was a duplicate of ours.
                foreach (List<string> bucket in byLength.Values)
                {
                    bucket.Sort((a, b) =>
                    {
                        bool pa = IsPreservedOriginal(a);
                        bool pb = IsPreservedOriginal(b);

                        return pa == pb
                            ? string.Compare(a, b, StringComparison.OrdinalIgnoreCase)
                            : (pa ? 1 : -1);
                    });
                }

                var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (List<string> bucket in byLength.Values)
                {
                    if (bucket.Count < 2)
                    {
                        continue;
                    }

                    var seenHashes = new Dictionary<string, string>(StringComparer.Ordinal);

                    foreach (string path in bucket)
                    {
                        string hash = HashFile(path);
                        if (hash == null)
                        {
                            continue;
                        }

                        if (seenHashes.ContainsKey(hash))
                        {
                            // The first in bucket order survives, and the sort
                            // above puts chosen artwork ahead of preserved
                            // copies, alphabetical within each group - so this
                            // stays deterministic across calls.
                            drop.Add(path);
                        }
                        else
                        {
                            seenHashes[hash] = path;
                        }
                    }
                }

                if (drop.Count == 0)
                {
                    return paths;
                }

                return paths.Where(p => !drop.Contains(p)).ToList();
            }
            catch (Exception)
            {
                // Deduplication is an improvement, not a requirement.
                return paths;
            }
        }

        private static string HashFile(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA1.Create())
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    return BitConverter.ToString(sha.ComputeHash(stream));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Copies a file into the game's folder. Returns the new path, or null
        // on failure. Name collisions get a numeric suffix rather than
        // overwriting - the user picked both files deliberately.
        public string AddImage(Guid gameId, string sourcePath, ArtworkKind kind)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            try
            {
                string folder = GetGameFolder(gameId, kind);
                Directory.CreateDirectory(folder);

                string name = Path.GetFileNameWithoutExtension(sourcePath);
                string ext = Path.GetExtension(sourcePath);
                string target = Path.Combine(folder, name + ext);

                int suffix = 1;
                while (File.Exists(target))
                {
                    target = Path.Combine(folder, name + "_" + suffix + ext);
                    suffix++;
                }

                File.Copy(sourcePath, target);
                return target;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not add image for game {gameId}");
                return null;
            }
        }

        public bool RemoveImage(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not remove image {path}");
            }

            return false;
        }
    }
}
