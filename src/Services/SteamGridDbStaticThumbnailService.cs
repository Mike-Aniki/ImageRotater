using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Creates small, STILL JPG posters for SteamGridDB's animated .webm thumbnails.
    //
    // The artwork returned by the API is an animated WebP, but its thumb is a
    // WebM video. A WPF Image cannot display that WebM at all. We read the
    // small thumbnail, never the potentially huge original WebP, and ask the
    // FFmpeg already configured for animated artwork to decode ONE frame.
    // Cached posters are plain JPGs, so browsing does not start video players.
    internal sealed class SteamGridDbStaticThumbnailService
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(18)
        };
        private const long MaximumThumbnailBytes = 12 * 1024 * 1024;
        private readonly SemaphoreSlim _workSlots = new SemaphoreSlim(2, 2);
        private readonly string _cacheDirectory;
        private readonly string _ffmpeg;

        public bool IsAvailable => !string.IsNullOrEmpty(_ffmpeg);

        public SteamGridDbStaticThumbnailService(string pluginUserDataPath)
        {
            _ffmpeg = GifConverter.FindFfmpeg();
            string root = string.IsNullOrEmpty(pluginUserDataPath)
                ? Path.Combine(Path.GetTempPath(), "ImageRotater")
                : pluginUserDataPath;
            _cacheDirectory = Path.Combine(root, "Cache", "SteamGridDbStaticThumbnails");

            // The posters are tiny, but searches over months should not
            // accumulate an unlimited number on the user's disk.
            Task.Run(() => TrimCache());
        }

        public async Task<string> CreateAsync(string previewUrl, CancellationToken cancellation)
        {
            if (!IsWebmUrl(previewUrl) || cancellation.IsCancellationRequested)
            {
                return null;
            }

            string key = Sha256(previewUrl);
            string posterPath = Path.Combine(_cacheDirectory, key + ".jpg");
            if (ValidPoster(posterPath))
            {
                return new Uri(posterPath).AbsoluteUri;
            }
            if (!IsAvailable)
            {
                // Already cached posters still work when FFmpeg is removed.
                return null;
            }

            await _workSlots.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (ValidPoster(posterPath))
                {
                    return new Uri(posterPath).AbsoluteUri;
                }

                Directory.CreateDirectory(_cacheDirectory);
                string sourcePath = Path.Combine(_cacheDirectory, key + "." + Guid.NewGuid().ToString("N") + ".webm");
                string tempPoster = Path.Combine(_cacheDirectory, key + "." + Guid.NewGuid().ToString("N") + ".jpg");
                try
                {
                    if (!await DownloadSmallVideoAsync(previewUrl, sourcePath, cancellation).ConfigureAwait(false))
                    {
                        return null;
                    }

                    cancellation.ThrowIfCancellationRequested();
                    bool madePoster = await Task.Run(() => ExtractFrame(sourcePath, tempPoster, cancellation), cancellation)
                        .ConfigureAwait(false);
                    if (!madePoster || cancellation.IsCancellationRequested)
                    {
                        return null;
                    }

                    if (!ValidPoster(posterPath))
                    {
                        // Different searches can be in flight for the same URL.
                        // Another completed job wins, rather than deleting it.
                        try { File.Move(tempPoster, posterPath); }
                        catch (IOException) when (ValidPoster(posterPath)) { }
                    }

                    return ValidPoster(posterPath) ? new Uri(posterPath).AbsoluteUri : null;
                }
                finally
                {
                    DeleteQuietly(sourcePath);
                    DeleteQuietly(tempPoster);
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not create a static SteamGridDB thumbnail");
                return null;
            }
            finally
            {
                _workSlots.Release();
            }
        }

        private static async Task<bool> DownloadSmallVideoAsync(string url, string output, CancellationToken cancellation)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return false;
            }

            using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentLength > MaximumThumbnailBytes)
                {
                    return false;
                }

                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    long total = 0;
                    int length;
                    while ((length = await input.ReadAsync(buffer, 0, buffer.Length, cancellation)
                        .ConfigureAwait(false)) > 0)
                    {
                        total += length;
                        if (total > MaximumThumbnailBytes)
                        {
                            return false;
                        }
                        await file.WriteAsync(buffer, 0, length, cancellation).ConfigureAwait(false);
                    }
                    return total > 0;
                }
            }
        }

        private bool ExtractFrame(string input, string output, CancellationToken cancellation)
        {
            try
            {
                // Take a frame shortly after the start (rather than a black
                // fade-in at 0), then make a compact ~480px-wide JPEG. The
                // original WebP and the preview video are never modified.
                var start = new ProcessStartInfo
                {
                    FileName = _ffmpeg,
                    Arguments = "-nostdin -hide_banner -loglevel error -threads 1 -y "
                        + "-ss 0.5 -i \"" + input + "\" "
                        + "-frames:v 1 -an -sn -dn "
                        + "-vf \"scale=480:-2:force_original_aspect_ratio=decrease\" "
                        + "-q:v 4 -update 1 \"" + output + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null) return false;
                    using (cancellation.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
                    }))
                    {
                        if (!process.WaitForExit(16000))
                        {
                            try { process.Kill(); } catch (Exception) { }
                            process.WaitForExit(3000);
                            return false;
                        }
                        return !cancellation.IsCancellationRequested && process.ExitCode == 0 && ValidPoster(output);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: FFmpeg could not extract the SteamGridDB thumbnail frame");
                return false;
            }
        }

        private static bool IsWebmUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri)
                && uri.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", "").ToLowerInvariant();
            }
        }

        private static bool ValidPoster(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > 100; }
            catch (Exception) { return false; }
        }

        private static void DeleteQuietly(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        private void TrimCache()
        {
            try
            {
                if (!Directory.Exists(_cacheDirectory)) return;
                var posters = new DirectoryInfo(_cacheDirectory).GetFiles("*.jpg")
                    .OrderByDescending(file => file.LastWriteTimeUtc).ToList();
                foreach (var old in posters.Skip(300)) DeleteQuietly(old.FullName);
            }
            catch (Exception) { /* Cache cleaning is optional. */ }
        }
    }
}
