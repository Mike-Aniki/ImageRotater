using System;
using System.IO;
using System.Threading.Tasks;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    public enum ArtworkDownloadStage
    {
        Resizing,
        ResizeFinished,
        Optimising,
        OptimisationFinished
    }

    public sealed class ArtworkDownloadProgress
    {
        public ArtworkDownloadStage Stage { get; set; }
        public bool Resized { get; set; }
        public bool Optimised { get; set; }
    }

    // Downloads chosen artwork into a game's ImageRotater folder, so it becomes
    // a normal candidate like any manually added file.
    public class ArtworkDownloader
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly ISteamGridDbClient _client;
        private readonly GameImageStore _store;
        private readonly SessionSelectionCache _sessionCache;

        // Convert downloaded GIFs to MP4 when ffmpeg is present.
        //
        // On by default: it is smaller (78% on a real library GIF) and it moves
        // playback from frame-by-frame decoding on the UI thread to
        // hardware-assisted H.264. A user who wants the GIF kept as a GIF turns
        // it off in the search dialog.
        //
        // Does nothing at all without ffmpeg, which the plugin cannot bundle -
        // it is GPL and this project is MIT.
        public bool ConvertGifsToMp4 { get; set; } = true;

        public ArtworkDownloader(
            ISteamGridDbClient client,
            GameImageStore store,
            SessionSelectionCache sessionCache)
        {
            _client = client;
            _store = store;
            _sessionCache = sessionCache;
        }

        // Supplies the external tool paths for YouTube downloads.
        //
        // A getter rather than the settings object, so a path the user corrects
        // mid-session takes effect without rebuilding the downloader. Static
        // because the downloader is constructed in several places and only this
        // one path needs it.
        public static Func<ImageRotaterSettings> SettingsSource { get; set; }

        // Fetches a YouTube video as the game's artwork.
        //
        // Saved as MP4 next to the images, which is what the rest of the plugin
        // already plays - a video pick and a still pick are the same thing to
        // everything downstream of here.
        private async Task<string> DownloadVideoAsync(
            Guid gameId, SteamGridDbArtwork artwork, ArtworkKind kind)
        {
            ImageRotaterSettings settings =
                SettingsSource == null ? null : SettingsSource();

            var downloader = new YouTubeDownloader(settings);

            if (!downloader.IsAvailable)
            {
                Logger.Warn("ImageRotater: YouTube download needs both yt-dlp and ffmpeg");
                return null;
            }

            try
            {
                string folder = _store.GetGameFolder(gameId, kind);
                Directory.CreateDirectory(folder);

                // Named from a hash of the watch URL, like a web result: a
                // YouTube id is not a SteamGridDB id, and re-picking the same
                // video should replace the file rather than add another.
                string target = Path.Combine(
                    folder, "yt_" + StableHash(artwork.Url) + ".mp4");

                bool ok = await downloader
                    .DownloadAsync(artwork.Url, target, System.Threading.CancellationToken.None)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    return null;
                }

                _sessionCache?.Forget(gameId);
                return target;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not save the YouTube download");
                return null;
            }
        }

        // Saves one artwork into the game's folder. Returns the resulting path,
        // or null on failure.
        public async Task<string> DownloadAsync(
            Guid gameId,
            SteamGridDbArtwork artwork,
            ArtworkKind kind = ArtworkKind.Background,
            Action<ArtworkDownloadProgress> onProgress = null)
        {
            if (artwork == null || string.IsNullOrEmpty(artwork.Url))
            {
                return null;
            }

            // A YouTube result's Url is a watch PAGE, not a file - fetching it
            // here would save the HTML. yt-dlp has to do the download, and
            // ffmpeg the container work, so that path forks off entirely.
            if (artwork.IsYouTube)
            {
                return await DownloadVideoAsync(gameId, artwork, kind).ConfigureAwait(false);
            }

            SteamGridDbResult<byte[]> download = await _client.DownloadAsync(artwork.Url).ConfigureAwait(false);
            if (!download.Success || download.Data == null || download.Data.Length == 0)
            {
                Logger.Warn($"ImageRotater: artwork {artwork.Id} download failed: {download.Error}");
                return null;
            }

            try
            {
                string folder = _store.GetGameFolder(gameId, kind);
                Directory.CreateDirectory(folder);

                // Named by SteamGridDB id so re-downloading the same artwork
                // replaces it rather than accumulating near-duplicates.
                //
                // Web results have no such id - theirs is just a position in
                // one search's results, so two different searches would both
                // yield "1" and silently overwrite each other. Those are named
                // from a hash of the URL instead, which is stable for the same
                // image and distinct for different ones.
                string fileName = artwork.IsFromWeb
                    ? "web_" + StableHash(artwork.Url) + GetExtension(artwork)
                    : "sgdb_" + artwork.Id + GetExtension(artwork);
                string target = Path.Combine(folder, fileName);

                // Write to a temp file then move, so an interrupted download
                // cannot leave a half-written image that renders as broken.
                string temp = target + ".tmp";
                File.WriteAllBytes(temp, download.Data);

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);

                // Any mp4 fetched as plain bytes gets its container checked.
                //
                // Steam's serve normal progressive files today (verified:
                // ftyp isom), but a fragmented one from any source renders as
                // solid black with no error anywhere, so the one cheap read
                // that rules it out is worth doing on every video download.
                if (target.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    && GifConverter.IsFragmented(target))
                {
                    GifConverter.Remux(target);
                }

                // A downloaded GIF becomes an MP4 when the user has ffmpeg and
                // has left the option on.
                //
                // Worth it on both counts, measured against a real 4.7 MB
                // library GIF: 1.0 MB out, and it then plays through
                // MediaElement rather than decoding frames on the UI thread.
                //
                // The GIF is only deleted once the MP4 exists. A failed
                // conversion leaves the download exactly as it was rather than
                // losing the artwork the user just chose.
                if (ConvertGifsToMp4 && GifConverter.IsConvertible(target))
                {
                    string converted = GifConverter.Convert(target);

                    if (!string.IsNullOrEmpty(converted))
                    {
                        try
                        {
                            File.Delete(target);
                        }
                        catch (Exception)
                        {
                            // Keeping both is untidy but harmless - dedup by
                            // content will not collapse them, since they are
                            // genuinely different files.
                        }

                        target = converted;
                    }
                }

                ImageRotaterSettings settings =
                    SettingsSource == null ? null : SettingsSource();

                // Resize downloaded BACKGROUNDS before optimisation. The preset
                // is a maximum bounding box, not a forced aspect ratio: artwork
                // is never stretched, cropped or upscaled. Covers are deliberately
                // excluded because their portrait ratios need separate semantics.
                if (settings?.ResizeDownloadedBackgrounds == true
                    && kind == ArtworkKind.Background
                    && !artwork.IsAnimated
                    && IsResizableStill(target))
                {
                    onProgress?.Invoke(new ArtworkDownloadProgress
                    {
                        Stage = ArtworkDownloadStage.Resizing
                    });

                    bool resized = false;
                    try
                    {
                        resized = DownloadedBackgroundResizer.ResizeToPreset(
                            target,
                            settings.DownloadedBackgroundResizePreset);
                    }
                    catch (Exception ex)
                    {
                        // Resizing is optional. A failure must never invalidate a
                        // download that already completed successfully.
                        Logger.Warn(ex, $"ImageRotater: automatic background resize failed for {target}");
                    }

                    onProgress?.Invoke(new ArtworkDownloadProgress
                    {
                        Stage = ArtworkDownloadStage.ResizeFinished,
                        Resized = resized
                    });
                }

                // Optional automatic optimisation for newly downloaded stills.
                // This intentionally runs AFTER resizing, so the optimiser works
                // on the final dimensions rather than re-encoding a huge source
                // that is about to be downscaled. The optimiser may change the
                // extension (PNG -> JPG), so keep the returned path in sync.
                if (settings?.OptimiseDownloadedImages == true
                    && !artwork.IsAnimated
                    && IsOptimisableStill(target))
                {
                    onProgress?.Invoke(new ArtworkDownloadProgress
                    {
                        Stage = ArtworkDownloadStage.Optimising
                    });

                    bool optimised = false;
                    try
                    {
                        var optimizer = new ImageOptimizer(_store);
                        string optimisedPath;
                        optimised = optimizer.Optimise(target, null, out optimisedPath);
                        if (optimised && !string.IsNullOrEmpty(optimisedPath))
                        {
                            target = optimisedPath;
                        }
                    }
                    catch (Exception ex)
                    {
                        // A failed optimisation must never turn a successful
                        // download into a failed download. Keep the original.
                        Logger.Warn(ex, $"ImageRotater: automatic download optimisation failed for {target}");
                    }

                    onProgress?.Invoke(new ArtworkDownloadProgress
                    {
                        Stage = ArtworkDownloadStage.OptimisationFinished,
                        Optimised = optimised
                    });
                }

                // The candidate list changed, so a remembered choice for this
                // game is stale.
                _sessionCache?.Forget(gameId);

                return target;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not save artwork {artwork.Id}");
                return null;
            }
        }

        private static bool IsResizableStill(string path)
        {
            string ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png";
        }

        private static bool IsOptimisableStill(string path)
        {
            string ext = Path.GetExtension(path) ?? string.Empty;

            return !ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".avi", StringComparison.OrdinalIgnoreCase);
        }

        // Extension from the MIME type, defaulting to .jpg. SteamGridDB serves
        // jpg/png/webp; the URL may carry query parameters, so the MIME is the
        // more reliable source.
        // Short, filename-safe, and the same every time for the same URL.
        //
        // Deliberately not string.GetHashCode(): that is not guaranteed stable
        // across runs, so the same image would land under a different name
        // after a restart and accumulate duplicates.
        private static string StableHash(string value)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));

                var text = new System.Text.StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    text.Append(hash[i].ToString("x2"));
                }

                return text.ToString();
            }
        }

        private static string GetExtension(SteamGridDbArtwork artwork)
        {
            if (!string.IsNullOrEmpty(artwork.Mime))
            {
                // gif before png: "image/gif" must never fall through, or the
                // saved file loses the extension the poster/playback routing
                // keys on. The same reasoning covers video: a .webm saved as
                // .jpg would neither play nor be recognised as motion.
                if (artwork.Mime.IndexOf("gif", StringComparison.OrdinalIgnoreCase) >= 0) return ".gif";
                if (artwork.Mime.IndexOf("webm", StringComparison.OrdinalIgnoreCase) >= 0) return ".webm";
                if (artwork.Mime.IndexOf("mp4", StringComparison.OrdinalIgnoreCase) >= 0) return ".mp4";
                if (artwork.Mime.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0) return ".png";
                if (artwork.Mime.IndexOf("webp", StringComparison.OrdinalIgnoreCase) >= 0) return ".webp";
                if (artwork.Mime.IndexOf("jpeg", StringComparison.OrdinalIgnoreCase) >= 0) return ".jpg";
                if (artwork.Mime.IndexOf("jpg", StringComparison.OrdinalIgnoreCase) >= 0) return ".jpg";
            }

            try
            {
                string fromUrl = Path.GetExtension(new Uri(artwork.Url).AbsolutePath);
                if (!string.IsNullOrEmpty(fromUrl))
                {
                    return fromUrl;
                }
            }
            catch (Exception)
            {
                // Malformed URL - fall through to the default.
            }

            return ".jpg";
        }
    }
}
