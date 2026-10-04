using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Compatibility-mode safety copy for the user's native Playnite artwork.
    //
    // Theme integration does NOT need this copy: it renders the current
    // Playnite image directly and therefore always follows metadata changes.
    // Compatibility mode is different because ImageRotater temporarily replaces
    // Game.CoverImage / Game.BackgroundImage with its own imported file. Once
    // that happens Playnite can reclaim the previously referenced native file,
    // so one private copy is kept before the first write.
    //
    // The copy is also refreshed if the user later chooses a different native
    // image in Playnite. Artwork ids written by ImageRotater are recognised by
    // PlayniteBackgroundWriter and never mistaken for a user change.
    public class OriginalArtPreserver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private const string Prefix = "original_";

        private readonly IPlayniteAPI _api;
        private readonly GameImageStore _store;
        private readonly PlayniteBackgroundWriter _writer;

        // Avoid a directory enumeration on every selection/control refresh.
        // Paths are validated with File.Exists, so manual deletion is still
        // noticed immediately.
        private readonly Dictionary<string, string> _preservedPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public OriginalArtPreserver(
            IPlayniteAPI api, GameImageStore store, PlayniteBackgroundWriter writer = null)
        {
            _api = api;
            _store = store;
            _writer = writer;
        }

        // Ensures Compatibility mode has one recoverable copy of the latest
        // artwork the USER selected. Returns that copy, or null when the game
        // has no native artwork to preserve.
        public string Preserve(Game game, ArtworkKind kind)
        {
            if (game == null || _store == null || _api?.Database == null)
            {
                return null;
            }

            string key = Key(game.Id, kind);

            try
            {
                string currentId = CurrentId(game, kind);

                // While Compatibility mode is displaying one of our own writes,
                // the game's current Playnite id is output, not the original.
                // The existing safety copy remains the authoritative source.
                if (_writer != null && _writer.WroteArtworkId(currentId))
                {
                    return FindExisting(game.Id, kind);
                }

                // A current id not written by ImageRotater is a real Playnite
                // choice. Update the writer's logical original when it changed.
                bool userArtworkChanged = _writer != null &&
                    _writer.ObserveUserArtwork(game, kind);

                // The user can also explicitly remove the native image.
                if (string.IsNullOrEmpty(currentId))
                {
                    if (userArtworkChanged)
                    {
                        RemovePreserved(game.Id, kind);
                    }

                    return null;
                }

                string existing = FindExisting(game.Id, kind);

                // Common path: same user artwork as last time and the safety
                // copy still exists. No hashing, decoding or copying required.
                if (existing != null && !userArtworkChanged)
                {
                    return existing;
                }

                string sourcePath = _api.Database.GetFullFilePath(currentId);
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    return existing;
                }

                string folder = _store.GetGameFolder(game.Id, kind);
                Directory.CreateDirectory(folder);

                string extension = DetectExtension(sourcePath);
                string target = Path.Combine(folder, Prefix + "native" + extension);
                // Keep the temporary file on an unsupported extension so a
                // concurrent folder listing can never mistake an in-progress
                // safety copy for a rotation candidate.
                string temp = Path.Combine(
                    folder,
                    ".imagerotater-original-" + Guid.NewGuid().ToString("N") + ".tmp");

                // Copy first, then replace the old safety copy. If the source is
                // unreadable, the previous known-good original survives.
                File.Copy(sourcePath, temp, true);

                foreach (string old in Directory.GetFiles(folder, Prefix + "*"))
                {
                    try
                    {
                        if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(old);
                        }
                    }
                    catch
                    {
                        // Best effort. Multiple old safety copies are harmless;
                        // FindExisting will continue to prefer the canonical one.
                    }
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);
                _preservedPaths[key] = target;
                return target;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not preserve original {kind} for \"{game.Name}\"");
                return FindExisting(game.Id, kind);
            }
        }

        // Returns the logical native/original image without creating a copy.
        // Used by Theme Integration and Artwork Manager. If Playnite currently
        // points at an ImageRotater compatibility write, fall back to the safety
        // copy instead of presenting that rotated image as "Original".
        public string ResolveOriginalPath(Game game, ArtworkKind kind)
        {
            if (game == null || _api?.Database == null)
            {
                return null;
            }

            try
            {
                string currentId = CurrentId(game, kind);
                bool ours = _writer != null && _writer.WroteArtworkId(currentId);

                if (!string.IsNullOrEmpty(currentId) && !ours)
                {
                    string currentPath = _api.Database.GetFullFilePath(currentId);
                    if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath))
                    {
                        return currentPath;
                    }
                }

                string preserved = FindExisting(game.Id, kind);
                if (!string.IsNullOrEmpty(preserved))
                {
                    return preserved;
                }

                // Defensive fallback for an old writer state where the original
                // id still resolves but the safety copy was manually removed.
                string rememberedId = _writer?.GetRememberedOriginalId(game, kind);
                if (!string.IsNullOrEmpty(rememberedId))
                {
                    string rememberedPath = _api.Database.GetFullFilePath(rememberedId);
                    if (!string.IsNullOrEmpty(rememberedPath) && File.Exists(rememberedPath))
                    {
                        return rememberedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not resolve original {kind} for \"{game.Name}\"");
            }

            return null;
        }

        public string GetPreservedPath(Guid gameId, ArtworkKind kind)
        {
            return FindExisting(gameId, kind);
        }

        private string FindExisting(Guid gameId, ArtworkKind kind)
        {
            string key = Key(gameId, kind);

            try
            {
                string cached;
                if (_preservedPaths.TryGetValue(key, out cached) &&
                    !string.IsNullOrEmpty(cached) && File.Exists(cached))
                {
                    return cached;
                }

                string folder = _store.GetGameFolder(gameId, kind);
                if (!Directory.Exists(folder))
                {
                    _preservedPaths.Remove(key);
                    return null;
                }

                // Prefer the new canonical name, then accept old original_* files
                // created by previous ImageRotater versions.
                string[] canonicalMatches = Directory.GetFiles(folder, Prefix + "native.*");
                string found = canonicalMatches.Length > 0 ? canonicalMatches[0] : null;
                if (found == null)
                {
                    string[] matches = Directory.GetFiles(folder, Prefix + "*");
                    found = matches.Length > 0 ? matches[0] : null;
                }

                if (!string.IsNullOrEmpty(found) && File.Exists(found))
                {
                    _preservedPaths[key] = found;
                    return found;
                }
            }
            catch
            {
                // A missing/unreadable backup is handled like no backup.
            }

            _preservedPaths.Remove(key);
            return null;
        }

        private void RemovePreserved(Guid gameId, ArtworkKind kind)
        {
            string folder = _store.GetGameFolder(gameId, kind);
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.GetFiles(folder, Prefix + "*"))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            _preservedPaths.Remove(Key(gameId, kind));
        }

        private static string CurrentId(Game game, ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover ? game.CoverImage : game.BackgroundImage;
        }

        private static string Key(Guid gameId, ArtworkKind kind)
        {
            return gameId.ToString("N") + "|" + (int)kind;
        }

        private static string DetectExtension(string path)
        {
            try
            {
                byte[] header = new byte[12];
                int read;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    read = stream.Read(header, 0, header.Length);
                }

                if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                {
                    return ".jpg";
                }

                if (read >= 8 &&
                    header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                {
                    return ".png";
                }

                if (read >= 6 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                {
                    return ".gif";
                }

                if (read >= 2 && header[0] == 0x42 && header[1] == 0x4D)
                {
                    return ".bmp";
                }

                if (read >= 12 &&
                    header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                    header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                {
                    return ".webp";
                }
            }
            catch
            {
                // Fall through to the filename extension.
            }

            string existing = Path.GetExtension(path);
            return string.IsNullOrEmpty(existing) ? ".png" : existing;
        }


    }

}
