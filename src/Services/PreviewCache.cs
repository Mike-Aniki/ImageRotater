using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Makes an animated search result playable, by downloading it and
    // converting it to MP4.
    //
    // Needed because WPF cannot decode what SteamGridDB actually serves. Every
    // animated result sampled was image/webp with a .webm thumbnail, and WPF's
    // codec list is BMP, GIF, Icon, JPEG, PNG, TIFF, WMP - so those results
    // showed no motion, and often no image at all. XamlAnimatedGif does not
    // help either: it plays GIF and nothing else.
    //
    // MP4 is the one format that plays reliably through MediaElement, so
    // converting is the only route to a motion preview for these. That means
    // ffmpeg, which the plugin cannot bundle - without it the dialog still
    // badges animated results, it just cannot move them.
    //
    // Preview files are removed on the next startup rather than during shutdown.
    public static class PreviewCache
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private static readonly object Lock = new object();

        // Source URL -> local MP4. One conversion per result per session, so
        // hovering the same tile repeatedly costs nothing after the first.
        private static readonly Dictionary<string, string> Converted =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string _folder;

        public static bool IsAvailable
        {
            get { return GifConverter.IsAvailable; }
        }

        // Returns a local MP4 for an animated result, or null when it cannot be
        // produced. Blocking: callers run it off the UI thread.
        public static string GetPlayableCopy(string sourceUrl)
        {
            if (string.IsNullOrEmpty(sourceUrl) || !IsAvailable)
            {
                return null;
            }

            lock (Lock)
            {
                string existing;
                if (Converted.TryGetValue(sourceUrl, out existing) && File.Exists(existing))
                {
                    return existing;
                }
            }

            try
            {
                string folder = EnsureFolder();

                if (folder == null)
                {
                    return null;
                }

                string stem = Path.Combine(folder, StableName(sourceUrl));
                string downloaded = stem + Path.GetExtension(new Uri(sourceUrl).AbsolutePath);
                string mp4 = stem + ".mp4";

                if (File.Exists(mp4))
                {
                    Remember(sourceUrl, mp4);
                    return mp4;
                }

                using (var client = new WebClient())
                {
                    client.DownloadFile(sourceUrl, downloaded);
                }

                // Already MP4 - nothing to convert.
                if (downloaded.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    Remember(sourceUrl, downloaded);
                    return downloaded;
                }

                string result = GifConverter.Convert(downloaded, mp4);

                try
                {
                    File.Delete(downloaded);
                }
                catch (Exception)
                {
                    // A leftover source costs one temp file for the session.
                }

                if (string.IsNullOrEmpty(result))
                {
                    return null;
                }

                Remember(sourceUrl, result);
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not prepare a motion preview for {sourceUrl}");
                return null;
            }
        }

        private static void Remember(string url, string path)
        {
            lock (Lock)
            {
                Converted[url] = path;
            }
        }

        // Names by hash rather than by the URL's own file name, which is a
        // content hash on SteamGridDB but arbitrary on the open web.
        private static string StableName(string url)
        {
            unchecked
            {
                int hash = 23;

                foreach (char c in url)
                {
                    hash = hash * 31 + c;
                }

                return "preview_" + ((uint)hash).ToString("x8");
            }
        }

        private static string EnsureFolder()
        {
            lock (Lock)
            {
                if (_folder != null)
                {
                    return _folder;
                }

                try
                {
                    string path = Path.Combine(
                        Path.GetTempPath(), "ImageRotater_previews");

                    Directory.CreateDirectory(path);
                    _folder = path;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ImageRotater: could not create the preview folder");
                }

                return _folder;
            }
        }

        // Deletes preview files left by the previous session.
        public static void Clear()
        {
            lock (Lock)
            {
                Converted.Clear();

                string folder = _folder;
                if (string.IsNullOrEmpty(folder))
                {
                    folder = Path.Combine(Path.GetTempPath(), "ImageRotater_previews");
                }

                if (Directory.Exists(folder))
                {
                    try
                    {
                        Directory.Delete(folder, true);
                    }
                    catch (Exception)
                    {
                        // Cleanup is best-effort; locked files can be retried later.
                    }
                }

                _folder = null;
            }
        }

        // Shutdown releases process-local state; disk cleanup is deferred.
        public static void ReleaseForShutdown()
        {
            lock (Lock)
            {
                Converted.Clear();
                _folder = null;
            }
        }
    }
}
