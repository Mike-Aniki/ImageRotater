using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Converts everything the plugin holds, in one pass.
    //
    // Two jobs, both the same shape - walk every game's artwork, convert what
    // qualifies, delete the source only once the replacement exists:
    //
    //   GIF  -> MP4   smaller, and plays through the media pipeline rather than
    //                 decoding every frame on the UI thread. Measured on a real
    //                 library GIF: 4751 KB down to 1026 KB.
    //   JPEG -> PNG   lossless, at the cost of size. The user asked for this
    //                 explicitly and said they would take the size hit.
    //
    // Nothing is deleted unless its replacement is on disk, so an interrupted
    // run leaves the library exactly as usable as it was.
    public static class BulkConverter
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        public class Result
        {
            public int Converted { get; set; }
            public int Failed { get; set; }
            public int Skipped { get; set; }
            public long BytesBefore { get; set; }
            public long BytesAfter { get; set; }

            public string Summary
            {
                get
                {
                    if (Converted == 0)
                    {
                        return Failed > 0
                            ? Loc.Format("LOCImageRotaterNothingConvertedFailed", Failed)
                            : Loc.Get("LOCImageRotaterNothingConverting");
                    }

                    string text = Loc.Format("LOCImageRotaterConvertedFiles", Converted);

                    if (BytesBefore > 0)
                    {
                        double before = BytesBefore / 1048576.0;
                        double after = BytesAfter / 1048576.0;

                        text += Loc.Format("LOCImageRotaterMegabytesBecame", before, after);
                    }

                    if (Failed > 0)
                    {
                        text += Loc.Format("LOCImageRotaterFilesFailedUntouched", Failed);
                    }

                    return text;
                }
            }
        }

        // Every GIF in the library, converted to MP4.
        public static Result GifsToMp4(GameImageStore store)
        {
            var result = new Result();

            if (!GifConverter.IsAvailable)
            {
                result.Failed = 1;
                return result;
            }

            foreach (string file in EveryArtworkFile(store))
            {
                if (!file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A single-frame GIF is a still, and turning it into a video
                // would make it unplayable as a cover on a still-only theme.
                if (!GifConverter.IsConvertible(file))
                {
                    result.Skipped++;
                    continue;
                }

                Convert(file, result, () => GifConverter.Convert(file));
            }

            return result;
        }

        // Rewrites every fragmented MP4 as a normal progressive one.
        //
        // A YouTube download arrives as DASH fragments, which ffmpeg reads
        // happily and Windows does not - MediaElement reports such a file as
        // playing while rendering solid black, and desktop players refuse it
        // outright. A stream copy fixes the container without touching the
        // video.
        public static Result RepairVideos(GameImageStore store)
        {
            var result = new Result();

            if (!GifConverter.IsAvailable)
            {
                result.Failed = 1;
                return result;
            }

            // Candidates AND published copies. Everywhere else the published
            // folders are skipped because they are regenerated from the
            // candidates - but that regeneration only happens on the next
            // rotation, and a fragmented current.mp4 is what the theme is
            // showing as black RIGHT NOW. Repairing it in place fixes the tile
            // without waiting.
            foreach (string file in EveryArtworkFile(store)
                .Concat(EveryPublishedVideo(store)))
            {
                if (!file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RemuxInto(file, result);
            }

            return result;
        }

        // The same repair for ONE game - the right-click menu's version.
        //
        // One sweep of the game's whole folder, published copies included,
        // because the user runs this pointing at a specific black tile and
        // expects that tile fixed now, not after the next rotation.
        public static Result RepairVideosForGame(GameImageStore store, Guid gameId)
        {
            var result = new Result();

            if (!GifConverter.IsAvailable)
            {
                result.Failed = 1;
                return result;
            }

            string folder = Path.Combine(store?.ImagesRoot ?? string.Empty, gameId.ToString());

            if (!Directory.Exists(folder))
            {
                return result;
            }

            string[] videos;

            try
            {
                videos = Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not list videos for " + gameId);
                result.Failed++;
                return result;
            }

            foreach (string file in videos)
            {
                RemuxInto(file, result);
            }

            return result;
        }

        private static void RemuxInto(string file, Result result)
        {
            if (!GifConverter.IsFragmented(file))
            {
                result.Skipped++;
                return;
            }

            long before = SizeOf(file);

            if (GifConverter.Remux(file))
            {
                result.Converted++;
                result.BytesBefore += before;
                result.BytesAfter += SizeOf(file);
            }
            else
            {
                result.Failed++;
            }
        }

        // The published video copies - current.mp4 in each *.published folder.
        private static IEnumerable<string> EveryPublishedVideo(GameImageStore store)
        {
            string root = store?.ImagesRoot;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                yield break;
            }

            string[] published;

            try
            {
                published = Directory.GetDirectories(
                    root, "*" + GameImageStore.PublishedFolderSuffix,
                    SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not list published folders");
                yield break;
            }

            foreach (string folder in published)
            {
                string[] videos;

                try
                {
                    videos = Directory.GetFiles(folder, "*.mp4");
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string video in videos)
                {
                    yield return video;
                }
            }
        }

        // Every JPEG in the library, converted to PNG.
        public static Result JpegsToPng(GameImageStore store)
        {
            var result = new Result();

            foreach (string file in EveryArtworkFile(store))
            {
                if (!file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Convert(file, result, () => ToPng(file));
            }

            return result;
        }

        // Re-encodes one JPEG as PNG beside itself.
        //
        // The quality already lost to JPEG does not come back - this stops
        // further loss on every subsequent operation, which is the point.
        private static string ToPng(string jpegPath)
        {
            string target = Path.ChangeExtension(jpegPath, ".png");
            string temp = target + ".tmp";

            try
            {
                // Loaded into memory before the file handle closes, so the
                // source can be deleted afterwards. Image.FromFile keeps the
                // file locked for the lifetime of the object.
                using (var source = new Bitmap(jpegPath))
                using (var copy = new Bitmap(source))
                {
                    copy.Save(temp, ImageFormat.Png);
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);
                return target;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not convert " + Path.GetFileName(jpegPath));

                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }

                return null;
            }
        }

        // Runs one conversion and removes the source only once the replacement
        // exists - a failed conversion leaves the original untouched.
        private static void Convert(string source, Result result, Func<string> convert)
        {
            long before = SizeOf(source);

            string produced;

            try
            {
                produced = convert();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not convert " + Path.GetFileName(source));
                result.Failed++;
                return;
            }

            if (string.IsNullOrEmpty(produced) || !File.Exists(produced))
            {
                result.Failed++;
                return;
            }

            // Same path in and out means nothing actually changed.
            if (string.Equals(produced, source, StringComparison.OrdinalIgnoreCase))
            {
                result.Skipped++;
                return;
            }

            result.BytesBefore += before;
            result.BytesAfter += SizeOf(produced);
            result.Converted++;

            try
            {
                File.Delete(source);
            }
            catch (Exception ex)
            {
                // The replacement is already in place, so this leaves a
                // duplicate rather than a loss.
                Logger.Warn(ex, "ImageRotater: converted but could not remove " + source);
            }
        }

        private static long SizeOf(string path)
        {
            try { return new FileInfo(path).Length; } catch (Exception) { return 0; }
        }

        // Every candidate file the plugin owns.
        //
        // The published folders are deliberately skipped: those are copies the
        // plugin regenerates from the candidates, so converting them would do
        // the work twice and leave the copy disagreeing with its source.
        private static IEnumerable<string> EveryArtworkFile(GameImageStore store)
        {
            string root = store?.ImagesRoot;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                yield break;
            }

            string[] gameFolders;

            try
            {
                gameFolders = Directory.GetDirectories(root);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not list " + root);
                yield break;
            }

            foreach (string gameFolder in gameFolders)
            {
                foreach (string kind in new[] { "backgrounds", "covers" })
                {
                    string folder = Path.Combine(gameFolder, kind);

                    if (!Directory.Exists(folder))
                    {
                        continue;
                    }

                    string[] files;

                    try
                    {
                        files = Directory.GetFiles(folder);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (string file in files)
                    {
                        yield return file;
                    }
                }
            }
        }
    }
}
