using System.Windows.Media;
using System.Windows.Media.Imaging;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ImageCacheTests
    {
        // Builds a frozen bitmap of a known byte size: width*height*4 bytes.
        private static BitmapSource MakeBitmap(int width, int height)
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            var bmp = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        [Test]
        public void Clear_ReleasesEntriesAndByteAccounting_AndAllowsReuse()
        {
            var cache = new ImageCache(1024 * 1024);
            var bitmap = MakeBitmap(10, 10);
            cache.Put("a.jpg", 480, bitmap);
            cache.Put("b.jpg", 960, bitmap);
            cache.Clear();
            Assert.That(cache.Count, Is.Zero);
            Assert.That(cache.CurrentBytes, Is.Zero);
            Assert.That(cache.Get("a.jpg", 480), Is.Null);
            cache.Put("c.jpg", 480, bitmap);
            Assert.That(cache.CurrentBytes, Is.EqualTo(400));
            Assert.That(cache.Get("c.jpg", 480), Is.SameAs(bitmap));
        }

        [Test]
        public void Get_Miss_ReturnsNull()
        {
            var cache = new ImageCache(1024 * 1024);
            Assert.IsNull(cache.Get("nothing.jpg", 960));
        }

        [Test]
        public void PutThenGet_ReturnsSameInstance()
        {
            var cache = new ImageCache(1024 * 1024);
            var bmp = MakeBitmap(10, 10);

            cache.Put("a.jpg", 960, bmp);

            Assert.AreSame(bmp, cache.Get("a.jpg", 960));
        }

        // Put reads Format/PixelWidth outside the lock, which is only safe on an
        // immutable bitmap. An unfrozen one is refused rather than trusted.
        [Test]
        public void Put_UnfrozenBitmap_IsRefused()
        {
            var cache = new ImageCache(1024 * 1024);
            var bmp = new WriteableBitmap(10, 10, 96, 96, PixelFormats.Bgra32, null);
            Assert.IsFalse(bmp.IsFrozen);

            cache.Put("a.jpg", 960, bmp);

            Assert.IsNull(cache.Get("a.jpg", 960));
            Assert.AreEqual(0, cache.Count);
            Assert.AreEqual(0, cache.CurrentBytes);
        }

        // The whole point of the bucket in the key: the same file at two
        // render sizes is two different bitmaps.
        [Test]
        public void SamePathDifferentBucket_AreDistinctEntries()
        {
            var cache = new ImageCache(1024 * 1024);
            var small = MakeBitmap(10, 10);
            var large = MakeBitmap(20, 20);

            cache.Put("a.jpg", 480, small);
            cache.Put("a.jpg", 1920, large);

            Assert.AreSame(small, cache.Get("a.jpg", 480));
            Assert.AreSame(large, cache.Get("a.jpg", 1920));
            Assert.AreEqual(2, cache.Count);
        }

        [Test]
        public void Put_TracksBytes()
        {
            var cache = new ImageCache(1024 * 1024);
            cache.Put("a.jpg", 960, MakeBitmap(10, 10)); // 10*10*4 = 400 bytes

            Assert.AreEqual(400, cache.CurrentBytes);
        }

        [Test]
        public void Put_EvictsWhenOverBudget()
        {
            // Budget fits exactly two 400-byte bitmaps.
            var cache = new ImageCache(900);

            cache.Put("a.jpg", 960, MakeBitmap(10, 10));
            cache.Put("b.jpg", 960, MakeBitmap(10, 10));
            cache.Put("c.jpg", 960, MakeBitmap(10, 10));

            Assert.LessOrEqual(cache.CurrentBytes, 900);
            Assert.AreEqual(2, cache.Count);
            Assert.IsNull(cache.Get("a.jpg", 960), "oldest entry should have been evicted");
        }

        [Test]
        public void Eviction_IsLeastRecentlyUsed()
        {
            var cache = new ImageCache(900);

            cache.Put("a.jpg", 960, MakeBitmap(10, 10));
            cache.Put("b.jpg", 960, MakeBitmap(10, 10));

            // Touch "a" so "b" becomes least-recently-used.
            cache.Get("a.jpg", 960);

            cache.Put("c.jpg", 960, MakeBitmap(10, 10));

            Assert.IsNotNull(cache.Get("a.jpg", 960), "recently used entry was evicted");
            Assert.IsNull(cache.Get("b.jpg", 960), "least recently used entry survived");
        }

        // A single image larger than the whole budget must not wedge the cache
        // into an infinite eviction loop.
        [Test]
        public void Put_ImageLargerThanBudget_IsNotCachedButDoesNotThrow()
        {
            var cache = new ImageCache(100);

            Assert.DoesNotThrow(() => cache.Put("huge.jpg", 3840, MakeBitmap(50, 50)));
            Assert.AreEqual(0, cache.Count);
            Assert.AreEqual(0, cache.CurrentBytes);
        }

        [Test]
        public void Put_SameKeyTwice_DoesNotDoubleCountBytes()
        {
            var cache = new ImageCache(1024 * 1024);

            cache.Put("a.jpg", 960, MakeBitmap(10, 10));
            cache.Put("a.jpg", 960, MakeBitmap(10, 10));

            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(400, cache.CurrentBytes);
        }

        [Test]
        public void Put_NullImage_IsIgnored()
        {
            var cache = new ImageCache(1024 * 1024);

            Assert.DoesNotThrow(() => cache.Put("a.jpg", 960, null));
            Assert.AreEqual(0, cache.Count);
        }

        [Test]
        public void Key_IsCaseInsensitiveOnPath()
        {
            // Windows paths are case-insensitive; two casings must not occupy
            // two cache slots for the same file.
            var cache = new ImageCache(1024 * 1024);
            var bmp = MakeBitmap(10, 10);

            cache.Put(@"C:\Games\A.jpg", 960, bmp);

            Assert.AreSame(bmp, cache.Get(@"c:\games\a.jpg", 960));
            Assert.AreEqual(1, cache.Count);
        }
    }
}
