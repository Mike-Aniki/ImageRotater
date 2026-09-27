using System;
using System.IO;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Undoes everything the plugin has done to a library.
    //
    // Two halves, in this order and not the other:
    //
    //   1. Put every game's own artwork back, from the preserved originals.
    //   2. Delete the plugin's image folders.
    //
    // The order is the whole point. Deleting first would destroy the preserved
    // originals, and the restore would then have nothing to put back - leaving
    // games with no artwork at all. Restore first, verify, then delete.
    //
    // The caller confirms with the user before any of this runs. Nothing here
    // asks; by the time it is called the decision is made.
    public class LibraryReset
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        public class Result
        {
            public int GamesRestored { get; set; }
            public int FoldersDeleted { get; set; }
            public int FoldersFailed { get; set; }
            public string Error { get; set; }

            public bool Success
            {
                get { return Error == null; }
            }
        }

        private readonly PlayniteBackgroundWriter _writer;
        private readonly GameImageStore _store;

        public LibraryReset(PlayniteBackgroundWriter writer, GameImageStore store)
        {
            _writer = writer;
            _store = store;
        }

        public Result Run()
        {
            var result = new Result();

            try
            {
                // Restore FIRST, while the preserved originals still exist.
                if (_writer != null)
                {
                    result.GamesRestored = _writer.RestoreAll();
                }
            }
            catch (Exception ex)
            {
                // A failed restore stops everything. Deleting after this would
                // take the originals with it and leave games blank.
                Logger.Error(ex, "ImageRotater: restore failed, so nothing was deleted");

                result.Error = Loc.Format("LOCImageRotaterResetRestoreFailed", ex.Message);

                return result;
            }

            // A restore that put NOTHING back, when there is artwork to delete,
            // means the record of what to restore was lost - and deleting then
            // strands every game on artwork that is about to stop existing.
            //
            // This is not hypothetical: it happened. The originals map was
            // empty while 494 written references were live, the restore
            // returned 0, the delete went ahead, and 304 games were left
            // pointing at deleted files - which Playnite renders as solid
            // black, and which left rotation with no candidates at all.
            //
            // Zero restored is only acceptable when there was nothing to
            // restore in the first place.
            if (result.GamesRestored == 0 && _writer != null && _writer.HasWrittenArtwork)
            {
                result.Error = Loc.Get("LOCImageRotaterResetMissingRecord");

                return result;
            }

            string root = _store?.ImagesRoot;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return result;
            }

            // Per-game folders deleted individually rather than the root in one
            // call. One locked file - a video the UI still has open - would
            // otherwise abort the whole delete partway through, leaving an
            // arbitrary half of the library cleared.
            foreach (string folder in SafeGetDirectories(root))
            {
                try
                {
                    Directory.Delete(folder, true);
                    result.FoldersDeleted++;
                }
                catch (Exception ex)
                {
                    result.FoldersFailed++;
                    Logger.Warn(ex, "ImageRotater: could not delete " + folder);
                }
            }

            return result;
        }

        private static string[] SafeGetDirectories(string root)
        {
            try
            {
                return Directory.GetDirectories(root);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not list " + root);
                return new string[0];
            }
        }
    }
}
