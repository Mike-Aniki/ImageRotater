using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // A static JPEG of an animated file's first frame, for every consumer that
    // cannot animate.
    //
    // The write path is static by nature: Playnite decodes Game.CoverImage /
    // BackgroundImage to a bitmap, so writing a raw GIF both shows only its
    // first frame AND imports the full multi-frame file - easily tens of MB -
    // into Playnite's store on every rotation. The poster is that same first
    // frame as a lightweight JPEG. The ORIGINAL file stays in the pool
    // untouched, and renderers that can animate (the plugin's own cover
    // control) use it directly.
    //
    // Same cache pattern as the letterboxer: a dot-folder the candidate
    // listing cannot see, invalidated by source mtime.
    public static class PosterFrame
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        public const string CacheFolderName = ".poster";

        private const long JpegQuality = 90L;

        // True for formats whose stored file should not be written raw.
        public static bool IsAnimated(string path)
        {
            return !string.IsNullOrEmpty(path)
                && string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
        }

        // Video the controls play with a MediaElement.
        //
        // Deliberately NOT folded into IsAnimated, which means "animated, and a
        // still frame can be pulled out of it". Extract() is GDI+, and GDI+
        // cannot decode video at all - so a video has no poster, and every
        // consumer that needs a still has to skip it rather than try and fail.
        // That is exactly why these extensions stayed out of the store's
        // supported list until something could render them.
        public static bool IsVideo(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string ext = Path.GetExtension(path);

            return string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".webm", StringComparison.OrdinalIgnoreCase);
        }

        // Anything a control can put in motion, by either route. The write path
        // asks this to decide whether the raw file may be written; the controls
        // ask the two specific predicates to decide HOW to render it.
        public static bool IsMotion(string path)
        {
            return IsAnimated(path) || IsVideo(path);
        }

        // Optional ffmpeg-backed stand-in used by packs on standard themes and
        // their composition preview. Kept separate from the GDI+ GIF path.
        public static string ForVideo(string path)
        {
            if (!IsVideo(path) || !File.Exists(path)) return null;
            try
            {
                string folder = Path.Combine(Path.GetDirectoryName(path), CacheFolderName);
                string poster = Path.Combine(folder, Path.GetFileName(path) + ".png");
                if (File.Exists(poster) && File.GetLastWriteTimeUtc(poster) >= File.GetLastWriteTimeUtc(path))
                    return poster;
                Directory.CreateDirectory(folder);
                return GifConverter.ExtractPoster(path, poster);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not prepare a pack video poster");
                return null;
            }
        }

        // The static stand-in for an animated pick - or the original path when
        // it is not animated, or null when a poster cannot be produced (the
        // caller treats that pick as unusable and falls back to another
        // candidate).
        public static string For(string sourcePath)
        {
            try
            {
                // No poster exists for video: Extract is GDI+, which cannot
                // decode a video container at all. Null means "unusable as a
                // still", and callers already treat that as "pick something
                // else" rather than writing the raw file.
                if (IsVideo(sourcePath))
                {
                    return null;
                }

                if (!IsAnimated(sourcePath))
                {
                    return sourcePath;
                }

                if (!File.Exists(sourcePath))
                {
                    return null;
                }

                string cacheDir = Path.Combine(Path.GetDirectoryName(sourcePath), CacheFolderName);
                string cached = Path.Combine(
                    cacheDir, Path.GetFileNameWithoutExtension(sourcePath) + ".jpg");

                if (File.Exists(cached) &&
                    File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(sourcePath))
                {
                    return cached;
                }

                Directory.CreateDirectory(cacheDir);

                return Extract(sourcePath, cached) ? cached : null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not build a poster for {sourcePath}");
                return null;
            }
        }

        // GDI+ hands over frame 0 of a GIF by default; drawing it onto a fresh
        // bitmap flattens palette and transparency into something JPEG can
        // carry.
        private static bool Extract(string sourcePath, string target)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(sourcePath);

                using (var input = new MemoryStream(bytes))
                using (Image source = Image.FromStream(input))
                using (var flat = new Bitmap(source.Width, source.Height))
                using (Graphics g = Graphics.FromImage(flat))
                {
                    // GIF transparency becomes black on a JPEG regardless;
                    // filling first makes that deliberate rather than
                    // whatever the encoder felt like.
                    g.Clear(Color.Black);
                    g.DrawImage(source, 0, 0, source.Width, source.Height);

                    ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders()
                        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

                    if (jpeg == null)
                    {
                        return false;
                    }

                    string temp = target + ".tmp";

                    using (var parameters = new EncoderParameters(1))
                    using (var quality = new EncoderParameter(Encoder.Quality, JpegQuality))
                    {
                        parameters.Param[0] = quality;
                        flat.Save(temp, jpeg, parameters);
                    }

                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    File.Move(temp, target);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: poster extraction failed for {sourcePath}");
                return false;
            }
        }
    }
}
