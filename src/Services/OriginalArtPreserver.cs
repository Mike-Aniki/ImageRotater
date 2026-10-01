using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Copies a game's pre-existing artwork into ImageRotater's own folder the
    // first time that game is rotated.
    //
    // Two problems this solves, both reported by users:
    //
    // "My original art is not in the rotation." It was the value being
    // replaced, not a candidate. Copying it in makes it an ordinary candidate
    // like any other, so the art the user chose keeps appearing.
    //
    // "The plugin deleted my original art." Rotation never deleted anything -
    // but once game.CoverImage points at our file, the original is unreferenced
    // and Playnite's own library cleanup can reclaim it. The backup then holds
    // an id that no longer resolves and restore silently fails. Owning a copy
    // means the user's art survives regardless of what Playnite reclaims.
    public class OriginalArtPreserver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Prefix so a preserved file is recognisable in the folder and cannot
        // collide with downloaded artwork, which is named by SteamGridDB id.
        private const string Prefix = "original_";

        private readonly IPlayniteAPI _api;
        private readonly GameImageStore _store;

        // Consulted to tell the user's artwork from our own rotation. Optional
        // so the preserver stays constructible without the whole write stack.
        private readonly PlayniteBackgroundWriter _writer;

        // Games already handled this session. The on-disk check below is the
        // real guard; this just avoids repeating it on every selection.
        private readonly HashSet<string> _checked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public OriginalArtPreserver(
            IPlayniteAPI api, GameImageStore store, PlayniteBackgroundWriter writer = null)
        {
            _api = api;
            _store = store;
            _writer = writer;
        }

        // Copies the game's current artwork of this kind into our folder if we
        // have not already. Returns the copied path, or null when there was
        // nothing to preserve or it was already done.
        public string Preserve(Game game, ArtworkKind kind)
        {
            if (game == null || _store == null)
            {
                return null;
            }

            string key = game.Id + "|" + kind;
            if (_checked.Contains(key))
            {
                return null;
            }

            _checked.Add(key);

            try
            {
                string currentId = kind == ArtworkKind.Cover ? game.CoverImage : game.BackgroundImage;
                if (string.IsNullOrEmpty(currentId))
                {
                    // Nothing to preserve - the game had no artwork of this kind.
                    return null;
                }

                // Our own rotation, not the user's artwork.
                //
                // Asked by ID rather than by path, because the path test below
                // cannot see this: AddFile imports a COPY into Playnite's store,
                // so anything we write resolves inside Playnite's folder. Every
                // restart therefore preserved the previous rotation as a new
                // "original", adding a candidate each time - and for a game
                // whose pick was animated, that meant rotation acquired a still
                // to land on and the animation appeared to stop working.
                if (_writer != null && _writer.WroteArtworkId(currentId))
                {
                    return null;
                }

                // Already preserved on a previous run. Checked on disk rather
                // than from state, so a deleted state file cannot cause a
                // second copy.
                string existing = FindExisting(game.Id, kind);
                if (existing != null)
                {
                    return null;
                }

                string sourcePath = _api.Database.GetFullFilePath(currentId);
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    return null;
                }

                // Skip artwork we put there ourselves. Without this, enabling
                // the plugin, rotating, then restarting would preserve our own
                // rotation as if it were the user's original.
                if (IsOurs(sourcePath, game.Id))
                {
                    return null;
                }

                string folder = _store.GetGameFolder(game.Id, kind);
                Directory.CreateDirectory(folder);

                // Extension comes from the file's own bytes, not its name.
                //
                // Playnite stores library files under a GUID with whatever
                // extension the original download happened to carry - often
                // none, sometimes something meaningless like ".php". Copying
                // that name through left the preserved original invisible to
                // GetImagePaths, which filters on supported extensions: the
                // user's own artwork never became a rotation candidate, and
                // the game got no published file, which then crashed theme
                // tiles that load with CacheOption=OnLoad.
                string target = Path.Combine(
                    folder,
                    Prefix + Path.GetFileNameWithoutExtension(sourcePath) + DetectExtension(sourcePath));

                if (File.Exists(target))
                {
                    return null;
                }

                File.Copy(sourcePath, target);

                return target;
            }
            catch (Exception ex)
            {
                // Failing to preserve must not stop the game rendering. The
                // user keeps whatever Playnite already had.
                Logger.Warn(ex, $"ImageRotater: could not preserve original {kind} for \"{game.Name}\"");
                return null;

            }
        }

        // Real image type from the file's leading bytes.
        //
        // The name cannot be trusted here: these come from Playnite's library
        // store, which keeps whatever extension the original download carried -
        // frequently none at all, sometimes something meaningless like ".php".
        // An unsupported extension makes the file invisible to GetImagePaths,
        // so the user's own artwork silently drops out of the rotation.
        //
        // Falls back to the existing extension when the header is unrecognised,
        // and to .png when there is none, so a file is always readable by name.
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

                // "RIFF" .... "WEBP"
                if (read >= 12 &&
                    header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                    header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                {
                    return ".webp";
                }
            }
            catch (Exception)
            {
                // Unreadable header - fall through to the name.
            }

            string existing = Path.GetExtension(path);
            return string.IsNullOrEmpty(existing) ? ".png" : existing;
        }

        private string FindExisting(Guid gameId, ArtworkKind kind)
        {
            string folder = _store.GetGameFolder(gameId, kind);
            if (!Directory.Exists(folder))
            {
                return null;
            }

            string[] matches = Directory.GetFiles(folder, Prefix + "*");
            return matches.Length > 0 ? matches[0] : null;
        }

        // True when the path is inside this game's own ImageRotater folder,
        // which means we wrote it rather than the user bringing it with them.
        private bool IsOurs(string path, Guid gameId)
        {
            try
            {
                string ours = Path.GetFullPath(Path.Combine(_store.GetGameFolder(gameId, ArtworkKind.Background), ".."));
                string candidate = Path.GetFullPath(path);

                return candidate.StartsWith(ours, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
