using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ImageMemoryTests
    {
        private string root;
        private string[] paths;
        private BitmapSource bitmap;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "IR_ImageMemory_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            paths = Enumerable.Range(0, 12).Select(i => Path.Combine(root, i + ".png")).ToArray();
            foreach (string path in paths) File.WriteAllBytes(path, new byte[] { 1 });
            bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[256], 32);
            bitmap.Freeze();
        }

        [TearDown]
        public void TearDown() { Directory.Delete(root, true); }

        [Test]
        public async Task NativeBrowsingDoesNotRetainImageHistoryOrEvictPluginArtwork()
        {
            var cache = new ImageCache(1024);
            var loader = new ImageLoader(cache, (path, bucket) => bitmap);
            await loader.LoadAsync(paths[0], 480);
            foreach (string path in paths.Skip(1))
            {
                Assert.That(await loader.LoadAsync(path, 480, cacheResult: false), Is.SameAs(bitmap));
                Assert.That(cache.Count, Is.EqualTo(1));
                Assert.That(cache.CurrentBytes, Is.EqualTo(256));
                Assert.That(cache.Get(path, 480), Is.Null);
            }
            Assert.That(cache.Get(paths[0], 480), Is.SameAs(bitmap));
        }

        [Test]
        public async Task MinimizingClearsCacheAndRestoringAllowsFreshLoads()
        {
            var cache = new ImageCache(1024);
            int calls = 0;
            var loader = new ImageLoader(cache, (path, bucket) => { Interlocked.Increment(ref calls); return bitmap; });
            await loader.LoadAsync(paths[0], 480);
            loader.Suspend();
            Assert.That(loader.IsSuspended, Is.True);
            Assert.That(cache.Count, Is.Zero);
            Assert.That(cache.CurrentBytes, Is.Zero);
            Assert.That(await loader.LoadAsync(paths[1], 480), Is.Null);
            Assert.That(calls, Is.EqualTo(1));
            loader.Resume();
            Assert.That(await loader.LoadAsync(paths[0], 480), Is.SameAs(bitmap));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(cache.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task DecodesFromBeforeMinimizingCannotRefillCacheAfterRestore()
        {
            var cache = new ImageCache(4096);
            using (var started = new CountdownEvent(2))
            using (var release = new ManualResetEventSlim())
            {
                int calls = 0;
                var loader = new ImageLoader(cache, (path, bucket) =>
                {
                    Interlocked.Increment(ref calls);
                    started.Signal();
                    release.Wait(TimeSpan.FromSeconds(10));
                    return bitmap;
                });
                var loads = paths.Select(path => loader.LoadAsync(path, 480)).ToArray();
                bool ready = started.Wait(TimeSpan.FromSeconds(10));
                loader.Suspend();
                loader.Resume();
                release.Set();
                var results = await Task.WhenAll(loads);
                Assert.That(ready, Is.True);
                Assert.That(calls, Is.EqualTo(2), "Queued decodes must be abandoned after minimization.");
                Assert.That(results, Is.All.Null);
                Assert.That(cache.Count, Is.Zero);
                Assert.That(cache.CurrentBytes, Is.Zero);
            }
        }

        [Test]
        public async Task FastBrowsingLimitsConcurrentDecodes()
        {
            var cache = new ImageCache(4096);
            using (var release = new ManualResetEventSlim())
            using (var started = new CountdownEvent(2))
            {
                int active = 0, maximum = 0, calls = 0;
                var gate = new object();
                var loader = new ImageLoader(cache, (path, bucket) =>
                {
                    int count = Interlocked.Increment(ref active);
                    int call = Interlocked.Increment(ref calls);
                    lock (gate) maximum = Math.Max(maximum, count);
                    if (call <= 2) started.Signal();
                    release.Wait(TimeSpan.FromSeconds(10));
                    Interlocked.Decrement(ref active);
                    return bitmap;
                });
                var loads = paths.Select(path => loader.LoadAsync(path, 480, cacheResult: false)).ToArray();
                bool ready = started.Wait(TimeSpan.FromSeconds(10));
                release.Set();
                var results = await Task.WhenAll(loads);
                Assert.That(ready, Is.True);
                Assert.That(maximum, Is.EqualTo(2));
                Assert.That(calls, Is.EqualTo(paths.Length));
                Assert.That(results, Is.All.Not.Null);
            }
        }
    }
}
