using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace ImageRotater.Services
{
    // Bounded by total decoded bytes, not item count. Twenty thumbnails and
    // twenty 4K frames differ ~30x in memory; only a byte budget is a real
    // ceiling. Playnite is a 32-bit process, so this also limits large
    // contiguous allocations that fragment the address space.
    public class ImageCache
    {
        private class Entry
        {
            public BitmapSource Image;
            public long Bytes;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        // Most-recently-used at the end.
        private readonly List<string> _lru = new List<string>();

        private readonly long _maxBytes;
        private long _currentBytes;

        public ImageCache(long maxBytes)
        {
            _maxBytes = maxBytes;
        }

        public long CurrentBytes
        {
            get { lock (_lock) { return _currentBytes; } }
        }

        public int Count
        {
            get { lock (_lock) { return _entries.Count; } }
        }

        private static string MakeKey(string path, int bucket)
        {
            return bucket.ToString() + "|" + path;
        }

        public BitmapSource Get(string path, int bucket)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string key = MakeKey(path, bucket);

            lock (_lock)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    return null;
                }

                Touch(key);
                return entry.Image;
            }
        }

        // Returns an exact cached decode when available. If not, a larger
        // decoded bucket for the same file is also safe to reuse: WPF only has
        // to scale it down, so quality is never reduced. This mainly avoids a
        // second decode after layout/resolution changes.
        public BitmapSource GetAtLeast(string path, int bucket)
        {
            BitmapSource exact = Get(path, bucket);
            if (exact != null)
            {
                return exact;
            }

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (_lock)
            {
                for (int i = 0; i < Models.WidthBucket.Buckets.Length; i++)
                {
                    int candidateBucket = Models.WidthBucket.Buckets[i];
                    if (candidateBucket <= bucket)
                    {
                        continue;
                    }

                    string key = MakeKey(path, candidateBucket);
                    Entry entry;
                    if (_entries.TryGetValue(key, out entry))
                    {
                        Touch(key);
                        return entry.Image;
                    }
                }
            }

            return null;
        }

        public void Put(string path, int bucket, BitmapSource image)
        {
            // Frozen is required, not merely expected: Format/PixelWidth are
            // read outside the lock below, which is only safe on an immutable
            // bitmap. Enforce it here rather than trusting ImageLoader.
            if (string.IsNullOrEmpty(path) || image == null || !image.IsFrozen)
            {
                return;
            }

            long bytes = EstimateBytes(image);
            string key = MakeKey(path, bucket);

            lock (_lock)
            {
                // A single image bigger than the whole budget is never cached.
                // Admitting it would evict everything and still not fit.
                if (bytes > _maxBytes)
                {
                    return;
                }

                Entry existing;
                if (_entries.TryGetValue(key, out existing))
                {
                    _currentBytes -= existing.Bytes;
                    _entries.Remove(key);
                    _lru.Remove(key);
                }

                while (_currentBytes + bytes > _maxBytes && _lru.Count > 0)
                {
                    EvictOldest();
                }

                _entries[key] = new Entry { Image = image, Bytes = bytes };
                _lru.Add(key);
                _currentBytes += bytes;
            }
        }

        // Drops every bucket's entry for a path. Used when the file behind it
        // has gone, so a decoded bitmap cannot outlive its source and keep
        // being served.
        public void Forget(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            lock (_lock)
            {
                // The key carries the bucket, so one path can hold several
                // entries. All of them are now stale.
                foreach (int bucket in Models.WidthBucket.Buckets)
                {
                    string key = MakeKey(path, bucket);

                    Entry entry;
                    if (_entries.TryGetValue(key, out entry))
                    {
                        _currentBytes -= entry.Bytes;
                        _entries.Remove(key);
                        _lru.Remove(key);
                    }
                }
            }
        }

        private void Touch(string key)
        {
            _lru.Remove(key);
            _lru.Add(key);
        }

        private void EvictOldest()
        {
            string oldest = _lru[0];
            _lru.RemoveAt(0);

            Entry entry;
            if (_entries.TryGetValue(oldest, out entry))
            {
                _currentBytes -= entry.Bytes;
                _entries.Remove(oldest);
            }
        }

        // Decoded footprint, not file size: width * height * bytes-per-pixel.
        private static long EstimateBytes(BitmapSource image)
        {
            int bytesPerPixel = (image.Format.BitsPerPixel + 7) / 8;
            if (bytesPerPixel <= 0)
            {
                bytesPerPixel = 4;
            }

            return (long)image.PixelWidth * image.PixelHeight * bytesPerPixel;
        }
    }
}
