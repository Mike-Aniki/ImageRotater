using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageRotater.Services
{
    // Decodes an image at a bucketed width, off the UI thread, and caches the
    // frozen result.
    //
    // All five decode settings below are load-bearing:
    //   DecodePixelWidth  - WIC's JPEG decoder does true DCT-domain scaled
    //                       decoding (1/2, 1/4, 1/8); PNG streams without
    //                       materialising a full-size intermediate. Omitting
    //                       this is the defect this project exists to fix.
    //   OnLoad            - releases the file handle immediately, so users can
    //                       still delete/replace their own images.
    //   IgnoreColorProfile- skips colour-profile work.
    //   Freeze()          - makes the bitmap safe to hand to the UI thread.
    //   Task.Run          - keeps decode off the UI thread.
    public class ImageLoader
    {
        private readonly ImageCache _cache;
        private readonly object _inFlightLock = new object();
        private readonly Dictionary<string, Task<BitmapSource>> _inFlight =
            new Dictionary<string, Task<BitmapSource>>(StringComparer.OrdinalIgnoreCase);

        public ImageLoader(ImageCache cache)
        {
            _cache = cache;
        }

        public async Task<BitmapSource> LoadAsync(string path, int bucket)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            // A cache hit must still be backed by a real file. Write mode
            // replaces a game's artwork and deletes the file it replaced, so a
            // decoded bitmap can outlive its source. Returning it would show
            // artwork the user has effectively removed, and the caller would
            // report success for a path that no longer resolves.
            //
            // Decode() checks existence too, but a cache hit never reaches it.
            if (!File.Exists(path))
            {
                _cache?.Forget(path);
                return null;
            }

            // An already-decoded larger bucket can safely satisfy a smaller
            // request. Reusing it avoids another WIC decode after a resize or
            // layout change without ever lowering image quality.
            BitmapSource cached = _cache != null ? _cache.GetAtLeast(path, bucket) : null;
            if (cached != null)
            {
                return cached;
            }

            string key = bucket.ToString() + "|" + path;
            Task<BitmapSource> decodeTask;
            bool ownsTask = false;

            // Multiple refreshes can reach the same still before the first
            // decode has populated ImageCache (theme controls, SizeChanged and
            // selection events can overlap). Share that in-flight decode
            // instead of making WIC decode the same file twice concurrently.
            lock (_inFlightLock)
            {
                if (!_inFlight.TryGetValue(key, out decodeTask))
                {
                    decodeTask = Task.Run(() => Decode(path, bucket));
                    _inFlight[key] = decodeTask;
                    ownsTask = true;
                }
            }

            try
            {
                BitmapSource decoded = await decodeTask.ConfigureAwait(false);

                if (decoded != null && _cache != null)
                {
                    _cache.Put(path, bucket, decoded);
                }

                return decoded;
            }
            finally
            {
                if (ownsTask)
                {
                    lock (_inFlightLock)
                    {
                        Task<BitmapSource> current;
                        if (_inFlight.TryGetValue(key, out current) &&
                            object.ReferenceEquals(current, decodeTask))
                        {
                            _inFlight.Remove(key);
                        }
                    }
                }
            }
        }

        private static BitmapSource Decode(string path, int bucket)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                // Artwork is read once from start to finish because OnLoad
                // materialises the bitmap before the stream is closed. Tell
                // Windows this is sequential I/O and use a larger buffer so
                // cold large images need fewer small reads.
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.DecodePixelWidth = bucket;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception)
            {
                // A missing or corrupt image is a data problem, not a crash.
                // The caller decides what to show; the control keeps its
                // previous image rather than flashing to blank.
                return null;
            }
        }
    }
}
