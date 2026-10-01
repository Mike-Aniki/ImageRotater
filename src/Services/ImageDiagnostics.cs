using System;
using System.IO;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Reports what was actually applied to a game, so an image that looks wrong
    // on screen can be traced to a real file with real dimensions instead of
    // being guessed at.
    //
    // Gated behind the debug-logging setting: reading a bitmap's header on
    // every rotation is cheap but not free, and the log should stay quiet in
    // normal use.
    public static class ImageDiagnostics
    {
        // decodedAtBucket / decodedWidth are only meaningful in the control
        // display mode, where ImageRotater does its own decoding. In write mode
        // Playnite renders the file itself and they are left at 0.
        public static void LogApplied(
            string gameName,
            string path,
            Func<ImageRotaterSettings> settings,
            FileLogger fileLogger,
            int decodedAtBucket = 0,
            int decodedWidth = 0,
            ArtworkKind kind = ArtworkKind.Background)
        {
            ImageRotaterSettings current = settings != null ? settings() : null;
            if (current == null || !current.EnableDebugLogging || fileLogger == null || !fileLogger.IsEnabled)
            {
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    fileLogger.Log($"applied MISSING file to \"{gameName}\": {path}");
                    return;
                }

                var info = new FileInfo(path);
                string ext = Path.GetExtension(path);

                // Videos are intentionally handled by MediaElement, not by the
                // bitmap decoder below. Trying to inspect an MP4/WebM as an
                // image produced scary-looking "UNREADABLE" debug entries
                // even when video playback opened successfully.
                if (PosterFrame.IsVideo(path))
                {
                    fileLogger.Log(
                        $"applied video to \"{gameName}\": " +
                        $"{Path.GetFileName(path)} {info.Length / 1024} KB, {ext}");
                    return;
                }

                // Reads only the header, not the pixels - enough for dimensions
                // without decoding the whole image.
                int width = 0;
                int height = 0;
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        BitmapFrame frame = BitmapFrame.Create(
                            stream,
                            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                            BitmapCacheOption.None);

                        width = frame.PixelWidth;
                        height = frame.PixelHeight;
                    }
                }
                catch (Exception ex)
                {
                    // A file we cannot even read the header of is itself the
                    // finding - say so rather than staying silent.
                    fileLogger.Log($"applied UNREADABLE file to \"{gameName}\": {path} ({ex.Message})");
                    return;
                }

                // Only meaningful for backgrounds, which get stretched across
                // the screen. Cover art is legitimately small - 600x900 is a
                // correct box-art size - so flagging it produced false alarms
                // that looked like real findings.
                string note = kind == ArtworkKind.Background && width > 0 && width < 1920
                    ? "  <-- LOW RESOLUTION, will look soft when stretched"
                    : string.Empty;

                // WPF has no native WebP decoder and shows only a GIF's first
                // frame, so either can render unlike the original file.
                if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    note += "  <-- FORMAT WPF RENDERS POORLY";
                }

                string decode = decodedAtBucket > 0
                    ? $", decoded at {decodedWidth} (bucket {decodedAtBucket})"
                    : string.Empty;

                // The kind is named because backgrounds and covers are applied
                // moments apart for the same game, and without it a 600x900
                // cover reads as a suspiciously small background.
                fileLogger.Log(
                    $"applied {kind.ToString().ToLowerInvariant()} to \"{gameName}\": " +
                    $"{Path.GetFileName(path)} {width}x{height}, {info.Length / 1024} KB, " +
                    $"{ext}{decode}{note}");
            }
            catch (Exception ex)
            {
                fileLogger.Log($"could not report applied image for \"{gameName}\": {ex.Message}");
            }
        }
    }
}
