> Historical design document. Its rendering/integration instructions are superseded by the [current Theme Developer Guide](../../THEME_INTEGRATION.md).

# ImageRotater v0.1 — Background Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render per-game background images in Playnite, decoded at display size rather than source size, changing when the selected game changes.

**Architecture:** A theme-placeable WPF control observes Playnite's game-context change, asks an `IBackgroundImageSource` for that game's candidate image paths, picks one via a pure `ImagePicker`, and displays a frozen `BitmapSource` decoded off-thread at a bucketed width by `ImageLoader`, which is fronted by a byte-bounded LRU `ImageCache`.

**Tech Stack:** C# 7.3, .NET Framework 4.6.2, WPF, Playnite SDK 6.16.0, NUnit 3.13.3, Moq 4.18.4. No imaging dependency — WPF's built-in `BitmapImage` is the decoder.

## Global Constraints

- Target framework .NET Framework 4.6.2, C# 7.3. No C# 8+ syntax (no switch expressions, no nullable reference types, no target-typed `new`, no range/index operators).
- **Add no NuGet packages.** The imaging evaluation in the spec concluded the built-in WPF path is correct. Adding SkiaSharp/Magick.NET/ImageSharp/System.Drawing is a plan violation.
- Comment style: single-line `//`. No `/// <summary>` except on public APIs needing `<param>`/`<returns>`.
- Naming: PascalCase public, `_camelCase` private fields, `I`-prefix interfaces.
- Width buckets are exactly `480, 960, 1920, 3840`. Measure, then round **up**. Anything wider than 3840 clamps to 3840.
- Decode must set all five: `DecodePixelWidth`, `CacheOption = OnLoad`, `CreateOptions = IgnoreColorProfile`, `Freeze()`, and run off the UI thread.
- Cache is bounded by **total decoded bytes**, never by item count. Default budget 150 MB.
- Every event subscription made in `Loaded` has a matching unsubscribe in `Unloaded`. Nothing subscribes in a constructor.
- Playnite is a 32-bit process. Avoid unnecessary large contiguous allocations.
- `dotnet build -c Release` must report `0 Error(s)` before any commit.

**Reference spec:** `docs/superpowers/specs/2026-07-30-background-rendering-v0.1-design.md`

**Run tests:** `dotnet test tests/ImageRotater.Tests.csproj -c Release`
**Run one:** `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~TestName"`

## Verified SDK facts (do not re-derive)

These were checked against `Playnite.SDK.dll` 6.16.0 before this plan was written:

- `Game.BackgroundImage` is a **`string`**, and it is a database **ID, not a file path**.
- Resolve it with `IPlayniteAPI.Database.GetFullFilePath(string id)` → absolute path string. (Precedent in a sibling project: `MusicInfoCardHandler.cs:159`.)
- A theme-placeable control derives from `Playnite.SDK.Controls.PluginUserControl`.
- That base class exposes `Game GameContext { get; set; }` and a virtual
  `void GameContextChanged(Game oldContext, Game newContext)` which the SDK calls on selection change. **This is the rotation trigger** — do not poll, do not subscribe to database events for it.
- Controls are exposed to themes by calling `AddCustomElementSupport(new AddCustomElementSupportArgs { ... })` in the plugin constructor and returning instances from `public override Control GetGameViewControl(GetGameViewControlArgs args)`.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/Models/WidthBucket.cs` | Bucket constants + round-up rule. Pure. | 1 |
| `tests/Services/WidthBucketTests.cs` | Bucket tests | 1 |
| `src/Services/ImagePicker.cs` | Choose one path from a list. Pure. | 2 |
| `tests/Services/ImagePickerTests.cs` | Picker tests | 2 |
| `src/Services/ImageCache.cs` | `(path,bucket)` → frozen bitmap, byte-bounded LRU | 3 |
| `tests/Services/ImageCacheTests.cs` | Cache + eviction tests | 3 |
| `src/Services/ImageLoader.cs` | Off-thread decode at bucket width, cache-fronted | 4 |
| `tests/Services/ImageLoaderTests.cs` | Decode width / frozen / corrupt-file tests | 4 |
| `src/Services/IBackgroundImageSource.cs` | The extension seam | 5 |
| `src/Services/PlayniteImageSource.cs` | Reads `game.BackgroundImage` | 5 |
| `tests/Services/PlayniteImageSourceTests.cs` | Source tests | 5 |
| `src/Controls/BackgroundImageControl.xaml(.cs)` | WPF control: context change → display | 6 |
| `src/ImageRotater.cs` | Register + serve the control | 7 |

---

### Task 1: Width buckets

Pure rounding rule, no dependencies. First because everything keys off it.

**Files:**
- Create: `src/Models/WidthBucket.cs`
- Test: `tests/Services/WidthBucketTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class WidthBucket` with `public static int ForWidth(double measuredWidth)` returning one of `480, 960, 1920, 3840`.

- [ ] **Step 1: Write the failing test**

Create `tests/Services/WidthBucketTests.cs`:

```csharp
using NUnit.Framework;
using ImageRotater.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class WidthBucketTests
    {
        [TestCase(1.0, 480)]
        [TestCase(480.0, 480)]
        [TestCase(481.0, 960)]
        [TestCase(960.0, 960)]
        [TestCase(1600.0, 1920)]
        [TestCase(1920.0, 1920)]
        [TestCase(1921.0, 3840)]
        [TestCase(3840.0, 3840)]
        public void ForWidth_RoundsUpToBucket(double measured, int expected)
        {
            Assert.AreEqual(expected, WidthBucket.ForWidth(measured));
        }

        // A 5K/8K display or a bad measurement must not produce a bucket
        // larger than the largest supported decode width.
        [TestCase(5120.0)]
        [TestCase(7680.0)]
        [TestCase(100000.0)]
        public void ForWidth_ClampsAboveLargestBucket(double measured)
        {
            Assert.AreEqual(3840, WidthBucket.ForWidth(measured));
        }

        // Layout can report 0 or NaN before the first measure pass. Never
        // return 0 — a zero DecodePixelWidth means "full size" to WPF, which
        // is the exact bug this whole design exists to prevent.
        [TestCase(0.0)]
        [TestCase(-5.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void ForWidth_InvalidMeasurementFallsBackToSmallestBucket(double measured)
        {
            Assert.AreEqual(480, WidthBucket.ForWidth(measured));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~WidthBucketTests"`
Expected: FAIL — compile error, `WidthBucket` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Models/WidthBucket.cs`:

```csharp
namespace ImageRotater.Models
{
    // Decode widths are bucketed so ordinary resizes stay within a bucket and
    // hit the cache instead of forcing a re-decode. DecodePixelWidth is applied
    // before EndInit() and a frozen bitmap cannot be resized, so every width
    // change costs a full decode.
    public static class WidthBucket
    {
        public static readonly int[] Buckets = { 480, 960, 1920, 3840 };

        public static int ForWidth(double measuredWidth)
        {
            // Layout reports 0/NaN before the first measure pass. Falling back
            // to the smallest bucket is safe; falling back to 0 would tell WPF
            // "decode at full size".
            if (double.IsNaN(measuredWidth) || double.IsInfinity(measuredWidth) || measuredWidth <= 0)
            {
                return Buckets[0];
            }

            for (int i = 0; i < Buckets.Length; i++)
            {
                if (measuredWidth <= Buckets[i])
                {
                    return Buckets[i];
                }
            }

            return Buckets[Buckets.Length - 1];
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~WidthBucketTests"`
Expected: PASS, 15 tests.

- [ ] **Step 5: Build**

Run: `dotnet build -c Release`
Expected: `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/Models/WidthBucket.cs tests/Services/WidthBucketTests.cs
git commit -m "feat: add width bucketing for decode sizing"
```

---

### Task 2: Image picker

The entire user-visible behavior of v0.1, isolated as a pure function so it is testable.

**Files:**
- Create: `src/Services/ImagePicker.cs`
- Test: `tests/Services/ImagePickerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public class ImagePicker` with
  `public string Pick(IReadOnlyList<string> candidates, string previousPick)` — returns a chosen path, or `null` when `candidates` is null/empty.
  Constructor `public ImagePicker(Random random = null)` — injectable RNG so tests are deterministic.

- [ ] **Step 1: Write the failing test**

Create `tests/Services/ImagePickerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ImagePickerTests
    {
        [Test]
        public void Pick_NullList_ReturnsNull()
        {
            var picker = new ImagePicker(new Random(1));
            Assert.IsNull(picker.Pick(null, null));
        }

        [Test]
        public void Pick_EmptyList_ReturnsNull()
        {
            var picker = new ImagePicker(new Random(1));
            Assert.IsNull(picker.Pick(new List<string>(), null));
        }

        [Test]
        public void Pick_SingleImage_ReturnsIt()
        {
            var picker = new ImagePicker(new Random(1));
            var list = new List<string> { "a.jpg" };
            Assert.AreEqual("a.jpg", picker.Pick(list, null));
        }

        // A one-image game must keep showing its only image on revisit rather
        // than returning null because "it was the previous pick".
        [Test]
        public void Pick_SingleImage_ReturnsItEvenWhenItWasPreviousPick()
        {
            var picker = new ImagePicker(new Random(1));
            var list = new List<string> { "a.jpg" };
            Assert.AreEqual("a.jpg", picker.Pick(list, "a.jpg"));
        }

        [Test]
        public void Pick_TwoImages_AlwaysReturnsTheOtherOne()
        {
            var picker = new ImagePicker(new Random(1));
            var list = new List<string> { "a.jpg", "b.jpg" };

            // With two candidates, avoiding the previous pick is deterministic.
            Assert.AreEqual("b.jpg", picker.Pick(list, "a.jpg"));
            Assert.AreEqual("a.jpg", picker.Pick(list, "b.jpg"));
        }

        [Test]
        public void Pick_NeverReturnsPreviousPick_WhenAlternativesExist()
        {
            var picker = new ImagePicker(new Random(12345));
            var list = new List<string> { "a.jpg", "b.jpg", "c.jpg", "d.jpg" };

            string previous = "c.jpg";
            for (int i = 0; i < 200; i++)
            {
                string pick = picker.Pick(list, previous);
                Assert.AreNotEqual(previous, pick, "picker returned the previous pick");
                previous = pick;
            }
        }

        [Test]
        public void Pick_ReturnsOnlyCandidatesFromTheList()
        {
            var picker = new ImagePicker(new Random(7));
            var list = new List<string> { "a.jpg", "b.jpg", "c.jpg" };

            for (int i = 0; i < 100; i++)
            {
                Assert.Contains(picker.Pick(list, null), list);
            }
        }

        // Previous pick may name a file that has since been removed from the
        // list. That must not prevent a normal pick.
        [Test]
        public void Pick_PreviousNotInList_StillPicks()
        {
            var picker = new ImagePicker(new Random(3));
            var list = new List<string> { "a.jpg", "b.jpg" };
            Assert.Contains(picker.Pick(list, "gone.jpg"), list);
        }

        [Test]
        public void Pick_EventuallyReturnsEveryCandidate()
        {
            var picker = new ImagePicker(new Random(99));
            var list = new List<string> { "a.jpg", "b.jpg", "c.jpg" };
            var seen = new HashSet<string>();

            string previous = null;
            for (int i = 0; i < 300; i++)
            {
                previous = picker.Pick(list, previous);
                seen.Add(previous);
            }

            Assert.AreEqual(3, seen.Count, "some candidate was never picked");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~ImagePickerTests"`
Expected: FAIL — compile error, `ImagePicker` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Services/ImagePicker.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ImageRotater.Services
{
    // Chooses which of a game's images to show. With no timer in v0.1, this is
    // the whole user-visible rotation behaviour, so it is kept pure and
    // injectable rather than embedded in the control.
    public class ImagePicker
    {
        private readonly Random _random;

        public ImagePicker(Random random = null)
        {
            // One instance reused for the life of the picker. Constructing
            // Random per call seeds from the clock and produces correlated
            // sequences when called in quick succession.
            _random = random ?? new Random();
        }

        // Returns a path from candidates, avoiding previousPick when an
        // alternative exists. Returns null when there is nothing to show.
        public string Pick(IReadOnlyList<string> candidates, string previousPick)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            // Build the set of allowed choices once, then pick uniformly from
            // it. A retry loop would be unbounded when every candidate equals
            // previousPick (a list containing duplicates).
            var allowed = new List<string>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!string.Equals(candidates[i], previousPick, StringComparison.OrdinalIgnoreCase))
                {
                    allowed.Add(candidates[i]);
                }
            }

            if (allowed.Count == 0)
            {
                // Every candidate is the previous pick. Showing it again beats
                // showing nothing.
                return candidates[0];
            }

            return allowed[_random.Next(allowed.Count)];
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~ImagePickerTests"`
Expected: PASS, 9 tests.

- [ ] **Step 5: Build**

Run: `dotnet build -c Release`
Expected: `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/Services/ImagePicker.cs tests/Services/ImagePickerTests.cs
git commit -m "feat: add pure image picker with previous-pick avoidance"
```

---

### Task 3: Byte-bounded image cache

**Files:**
- Create: `src/Services/ImageCache.cs`
- Test: `tests/Services/ImageCacheTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public class ImageCache` with
  - `public ImageCache(long maxBytes)`
  - `public BitmapSource Get(string path, int bucket)` — returns null on miss
  - `public void Put(string path, int bucket, BitmapSource image)`
  - `public long CurrentBytes { get; }`
  - `public int Count { get; }`

- [ ] **Step 1: Write the failing test**

Create `tests/Services/ImageCacheTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~ImageCacheTests"`
Expected: FAIL — compile error, `ImageCache` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Services/ImageCache.cs`:

```csharp
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

        public void Put(string path, int bucket, BitmapSource image)
        {
            if (string.IsNullOrEmpty(path) || image == null)
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~ImageCacheTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: Build**

Run: `dotnet build -c Release`
Expected: `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/Services/ImageCache.cs tests/Services/ImageCacheTests.cs
git commit -m "feat: add byte-bounded LRU image cache"
```

---

### Task 4: Image loader

The decode itself — the component that fixes the actual performance problem.

**Files:**
- Create: `src/Services/ImageLoader.cs`
- Test: `tests/Services/ImageLoaderTests.cs`

**Interfaces:**
- Consumes: `ImageCache` (Task 3), `WidthBucket` (Task 1).
- Produces: `public class ImageLoader` with
  - `public ImageLoader(ImageCache cache)`
  - `public Task<BitmapSource> LoadAsync(string path, int bucket)` — returns null if the file is missing or fails to decode. Never throws for bad input.

- [ ] **Step 1: Write the failing test**

Create `tests/Services/ImageLoaderTests.cs`:

```csharp
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ImageLoaderTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // System.Drawing is used ONLY here, to author test fixtures. It must not
        // appear in src/ — see the plan's Global Constraints.
        private string WriteJpeg(string name, int width, int height)
        {
            string path = Path.Combine(_dir, name);
            using (var bmp = new Bitmap(width, height))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.CornflowerBlue);
                bmp.Save(path, ImageFormat.Jpeg);
            }
            return path;
        }

        [Test]
        public async Task LoadAsync_DecodesToRequestedBucketWidth()
        {
            string path = WriteJpeg("big.jpg", 3840, 2160);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var bmp = await loader.LoadAsync(path, 960);

            Assert.IsNotNull(bmp);
            // This is the whole point of the design: a 3840px source must not
            // occupy 3840px of memory when displayed at 960.
            Assert.AreEqual(960, bmp.PixelWidth);
        }

        [Test]
        public async Task LoadAsync_ResultIsFrozen()
        {
            string path = WriteJpeg("a.jpg", 200, 100);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var bmp = await loader.LoadAsync(path, 480);

            Assert.IsNotNull(bmp);
            Assert.IsTrue(bmp.IsFrozen, "bitmap must be frozen to cross threads safely");
        }

        [Test]
        public async Task LoadAsync_MissingFile_ReturnsNullWithoutThrowing()
        {
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var bmp = await loader.LoadAsync(Path.Combine(_dir, "does-not-exist.jpg"), 480);

            Assert.IsNull(bmp);
        }

        [Test]
        public async Task LoadAsync_CorruptFile_ReturnsNullWithoutThrowing()
        {
            string path = Path.Combine(_dir, "corrupt.jpg");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var bmp = await loader.LoadAsync(path, 480);

            Assert.IsNull(bmp);
        }

        [Test]
        public async Task LoadAsync_NullOrEmptyPath_ReturnsNull()
        {
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            Assert.IsNull(await loader.LoadAsync(null, 480));
            Assert.IsNull(await loader.LoadAsync(string.Empty, 480));
        }

        [Test]
        public async Task LoadAsync_SecondCallForSameKey_ReturnsCachedInstance()
        {
            string path = WriteJpeg("a.jpg", 800, 600);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var first = await loader.LoadAsync(path, 960);
            var second = await loader.LoadAsync(path, 960);

            Assert.AreSame(first, second, "second load should come from cache");
        }

        [Test]
        public async Task LoadAsync_DifferentBuckets_ProduceDifferentBitmaps()
        {
            string path = WriteJpeg("a.jpg", 2000, 1000);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            var small = await loader.LoadAsync(path, 480);
            var large = await loader.LoadAsync(path, 1920);

            Assert.AreNotSame(small, large);
            Assert.AreEqual(480, small.PixelWidth);
            Assert.AreEqual(1920, large.PixelWidth);
        }

        // Decoding must not hold the file open, or the user cannot delete or
        // replace their own images while Playnite runs.
        [Test]
        public async Task LoadAsync_DoesNotHoldFileHandle()
        {
            string path = WriteJpeg("a.jpg", 400, 300);
            var loader = new ImageLoader(new ImageCache(64 * 1024 * 1024));

            await loader.LoadAsync(path, 480);

            Assert.DoesNotThrow(() => File.Delete(path), "file was still locked after decode");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~ImageLoaderTests"`
Expected: FAIL — compile error, `ImageLoader` does not exist.

Note: `System.Drawing` must be referenced by the **test** project for the fixture helper. If the compile fails on `System.Drawing`, add to `tests/ImageRotater.Tests.csproj`:

```xml
<ItemGroup>
    <Reference Include="System.Drawing" />
</ItemGroup>
```

- [ ] **Step 3: Write the implementation**

Create `src/Services/ImageLoader.cs`:

```csharp
using System;
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

            BitmapSource cached = _cache != null ? _cache.Get(path, bucket) : null;
            if (cached != null)
            {
                return cached;
            }

            BitmapSource decoded = await Task.Run(() => Decode(path, bucket)).ConfigureAwait(false);

            if (decoded != null && _cache != null)
            {
                _cache.Put(path, bucket, decoded);
            }

            return decoded;
        }

        private static BitmapSource Decode(string path, int bucket)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~ImageLoaderTests"`
Expected: PASS, 8 tests.

If `LoadAsync_DecodesToRequestedBucketWidth` fails with a width other than 960, the `DecodePixelWidth` assignment is misplaced — it must be set **between** `BeginInit()` and `EndInit()`.

- [ ] **Step 5: Build**

Run: `dotnet build -c Release`
Expected: `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/Services/ImageLoader.cs tests/Services/ImageLoaderTests.cs tests/ImageRotater.Tests.csproj
git commit -m "feat: add off-thread image loader with decode-time downscaling"
```

---

### Task 5: Image source seam

The extension point that makes later versions cheap.

**Files:**
- Create: `src/Services/IBackgroundImageSource.cs`
- Create: `src/Services/PlayniteImageSource.cs`
- Test: `tests/Services/PlayniteImageSourceTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `public interface IBackgroundImageSource` with `IReadOnlyList<string> GetImagePaths(Game game)`
  - `public class PlayniteImageSource : IBackgroundImageSource` with constructor `public PlayniteImageSource(Func<string, string> resolveFullPath)`.
    The delegate exists so tests need no live Playnite API; production passes `api.Database.GetFullFilePath`.

- [ ] **Step 1: Write the failing test**

Create `tests/Services/PlayniteImageSourceTests.cs`:

```csharp
using System;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class PlayniteImageSourceTests
    {
        [Test]
        public void GetImagePaths_NullGame_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => @"C:\resolved\" + id);
            Assert.AreEqual(0, source.GetImagePaths(null).Count);
        }

        // The common case: most libraries have games with no background set.
        // This must be empty, not an error.
        [Test]
        public void GetImagePaths_NoBackgroundImage_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => @"C:\resolved\" + id);
            var game = new Game { BackgroundImage = null };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        [Test]
        public void GetImagePaths_EmptyBackgroundImage_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => @"C:\resolved\" + id);
            var game = new Game { BackgroundImage = string.Empty };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        // BackgroundImage is a database ID, not a path. It must be resolved.
        [Test]
        public void GetImagePaths_ResolvesIdToFullPath()
        {
            var source = new PlayniteImageSource(id => @"C:\resolved\" + id);
            var game = new Game { BackgroundImage = "abc123" };

            var paths = source.GetImagePaths(game);

            Assert.AreEqual(1, paths.Count);
            Assert.AreEqual(@"C:\resolved\abc123", paths[0]);
        }

        [Test]
        public void GetImagePaths_ResolverReturnsNull_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => null);
            var game = new Game { BackgroundImage = "abc123" };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        // A throwing resolver must not take the control down.
        [Test]
        public void GetImagePaths_ResolverThrows_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(id => { throw new InvalidOperationException("boom"); });
            var game = new Game { BackgroundImage = "abc123" };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }

        [Test]
        public void GetImagePaths_NullResolver_ReturnsEmpty()
        {
            var source = new PlayniteImageSource(null);
            var game = new Game { BackgroundImage = "abc123" };

            Assert.AreEqual(0, source.GetImagePaths(game).Count);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~PlayniteImageSourceTests"`
Expected: FAIL — compile error, `PlayniteImageSource` does not exist.

- [ ] **Step 3: Write the interface**

Create `src/Services/IBackgroundImageSource.cs`:

```csharp
using System.Collections.Generic;
using Playnite.SDK.Models;

namespace ImageRotater.Services
{
    // The extension seam. v0.1 ships one implementation reading Playnite's own
    // BackgroundImage. Plugin-owned folders and downloaded art become further
    // implementations behind this interface, composed by a merging source,
    // without touching rendering.
    public interface IBackgroundImageSource
    {
        // All candidate image paths for this game. Never null; may be empty.
        IReadOnlyList<string> GetImagePaths(Game game);
    }
}
```

- [ ] **Step 4: Write the implementation**

Create `src/Services/PlayniteImageSource.cs`:

```csharp
using System;
using System.Collections.Generic;
using Playnite.SDK.Models;

namespace ImageRotater.Services
{
    // Reads the background image Playnite already holds for a game.
    //
    // Game.BackgroundImage is a database ID, not a file path, so it must be
    // resolved through IGameDatabaseAPI.GetFullFilePath. That resolver is
    // injected rather than taking IPlayniteAPI directly, so this class is
    // testable without a running Playnite.
    public class PlayniteImageSource : IBackgroundImageSource
    {
        private static readonly IReadOnlyList<string> Empty = new string[0];

        private readonly Func<string, string> _resolveFullPath;

        public PlayniteImageSource(Func<string, string> resolveFullPath)
        {
            _resolveFullPath = resolveFullPath;
        }

        public IReadOnlyList<string> GetImagePaths(Game game)
        {
            if (game == null || string.IsNullOrEmpty(game.BackgroundImage) || _resolveFullPath == null)
            {
                return Empty;
            }

            try
            {
                string full = _resolveFullPath(game.BackgroundImage);
                if (string.IsNullOrEmpty(full))
                {
                    return Empty;
                }

                return new[] { full };
            }
            catch (Exception)
            {
                // A resolver failure is a library-data problem. Show nothing
                // rather than taking the control down.
                return Empty;
            }
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release --filter "FullyQualifiedName~PlayniteImageSourceTests"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Build**

Run: `dotnet build -c Release`
Expected: `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add src/Services/IBackgroundImageSource.cs src/Services/PlayniteImageSource.cs tests/Services/PlayniteImageSourceTests.cs
git commit -m "feat: add background image source seam with Playnite implementation"
```

---

### Task 6: The WPF control

Wires everything together. Not unit-tested — needs a live visual tree and a running Playnite. Verified manually in Task 8.

**Files:**
- Create: `src/Controls/BackgroundImageControl.xaml`
- Create: `src/Controls/BackgroundImageControl.xaml.cs`

**Interfaces:**
- Consumes: `IBackgroundImageSource` (Task 5), `ImagePicker` (Task 2), `ImageLoader` (Task 4), `WidthBucket` (Task 1).
- Produces: `public class BackgroundImageControl : PluginUserControl` with constructor
  `public BackgroundImageControl(IBackgroundImageSource source, ImagePicker picker, ImageLoader loader)`.

- [ ] **Step 1: Write the XAML**

Create `src/Controls/BackgroundImageControl.xaml`:

```xml
<c:PluginUserControl x:Class="ImageRotater.Controls.BackgroundImageControl"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:c="clr-namespace:Playnite.SDK.Controls;assembly=Playnite.SDK">
    <Grid x:Name="RootGrid" ClipToBounds="True">

        <Image x:Name="DisplayImage"
               Stretch="UniformToFill"
               RenderOptions.BitmapScalingMode="HighQuality"/>

        <!-- Shown only when a path exists but the file is missing or corrupt.
             Never shown for "this game has no image" - that renders nothing so
             the theme's own background shows through. Vector-drawn so it stays
             crisp from 480px to 3840px without shipping bitmap assets. -->
        <Viewbox x:Name="MissingImagePlaceholder"
                 Visibility="Collapsed"
                 Width="96" Height="96"
                 HorizontalAlignment="Center"
                 VerticalAlignment="Center"
                 Opacity="0.28">
            <Canvas Width="24" Height="24">
                <Path Data="M3,3 H21 V21 H3 Z"
                      Stroke="{DynamicResource TextBrush}"
                      StrokeThickness="1.2"
                      Fill="Transparent"/>
                <Path Data="M9.2,9.1 C9.2,7.7 10.4,6.8 12,6.8 C13.6,6.8 14.8,7.7 14.8,9.1 C14.8,10.6 13.4,10.9 12.6,11.8 C12.2,12.3 12.1,12.8 12.1,13.6"
                      Stroke="{DynamicResource TextBrush}"
                      StrokeThickness="1.4"
                      Fill="Transparent"
                      StrokeStartLineCap="Round"
                      StrokeEndLineCap="Round"/>
                <Ellipse Canvas.Left="11.35" Canvas.Top="15.2"
                         Width="1.4" Height="1.4"
                         Fill="{DynamicResource TextBrush}"/>
            </Canvas>
        </Viewbox>

    </Grid>
</c:PluginUserControl>
```

- [ ] **Step 2: Write the code-behind**

Create `src/Controls/BackgroundImageControl.xaml.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    public partial class BackgroundImageControl : PluginUserControl
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IBackgroundImageSource _source;
        private readonly ImagePicker _picker;
        private readonly ImageLoader _loader;

        // Last path shown for the CURRENT game, so a revisit can avoid
        // repeating it.
        private string _previousPick;

        // Guards against a slow decode landing after the user has already
        // moved on to another game.
        private int _requestToken;

        private int _currentBucket = 0;

        public BackgroundImageControl(IBackgroundImageSource source, ImagePicker picker, ImageLoader loader)
        {
            InitializeComponent();

            _source = source;
            _picker = picker;
            _loader = loader;

            // Subscribe in Loaded, unsubscribe in Unloaded. Never in the
            // constructor - a control whose visual tree is rebuilt would
            // otherwise accumulate handlers and pin every dead instance.
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SizeChanged += OnSizeChanged;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SizeChanged -= OnSizeChanged;

            // Drop the bitmap reference. The cache owns the lifetime; the
            // control only borrows.
            DisplayImage.Source = null;

            // Invalidate any decode still in flight.
            _requestToken++;
        }

        // The SDK calls this when the selected game changes. This is the
        // rotation trigger - v0.1 has no timer.
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            // A different game means the previous pick no longer applies.
            _previousPick = null;
            Refresh();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Only re-decode when the width crosses into a different bucket.
            // Ordinary resizes stay in-bucket and cost nothing.
            int bucket = WidthBucket.ForWidth(ActualWidth);
            if (bucket != _currentBucket)
            {
                Refresh();
            }
        }

        private async void Refresh()
        {
            int token = ++_requestToken;

            Game game = GameContext;
            if (game == null || _source == null || _picker == null || _loader == null)
            {
                ShowNothing();
                return;
            }

            IReadOnlyList<string> candidates = _source.GetImagePaths(game);
            if (candidates == null || candidates.Count == 0)
            {
                // The common case: this game simply has no background. Render
                // nothing so the theme's own background shows through, and do
                // not log - it is not an error.
                ShowNothing();
                return;
            }

            string path = _picker.Pick(candidates, _previousPick);
            if (string.IsNullOrEmpty(path))
            {
                ShowNothing();
                return;
            }

            int bucket = WidthBucket.ForWidth(ActualWidth);

            BitmapSource image = await _loader.LoadAsync(path, bucket);

            // The user moved on while this was decoding.
            if (token != _requestToken)
            {
                return;
            }

            if (image == null)
            {
                // A path existed but did not load: the file is missing or
                // corrupt. That is a real problem worth surfacing.
                Logger.Warn($"ImageRotater: could not load background image: {path}");
                ShowPlaceholder();
                return;
            }

            _previousPick = path;
            _currentBucket = bucket;
            DisplayImage.Source = image;
            DisplayImage.Visibility = Visibility.Visible;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
        }

        private void ShowNothing()
        {
            DisplayImage.Source = null;
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
        }

        private void ShowPlaceholder()
        {
            DisplayImage.Source = null;
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Visible;
        }
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build -c Release`
Expected: `0 Error(s)`.

If the XAML fails to resolve `PluginUserControl`, confirm the `xmlns:c` assembly name is exactly `Playnite.SDK`.

- [ ] **Step 4: Run the full suite (no regressions)**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release`
Expected: all prior tests still pass. This task adds none.

- [ ] **Step 5: Commit**

```bash
git add src/Controls/BackgroundImageControl.xaml src/Controls/BackgroundImageControl.xaml.cs
git commit -m "feat: add background image control with symmetric lifecycle"
```

---

### Task 7: Register the control with Playnite

**Files:**
- Modify: `src/ImageRotater.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-6.
- Produces: theme element `ImageRotater_Background`.

- [ ] **Step 1: Wire up the plugin**

Replace the body of `src/ImageRotater.cs` with:

```csharp
using System;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using ImageRotater.Controls;
using ImageRotater.Services;

namespace ImageRotater
{
    public class ImageRotater : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // 150 MB of decoded bitmaps. Playnite is a 32-bit process sharing
        // ~2 GB of address space with Chromium, and decoded frames are large
        // contiguous allocations, so this budget is a fragmentation control as
        // much as a memory cap.
        private const long CacheBudgetBytes = 150L * 1024 * 1024;

        private readonly ImageRotaterSettingsViewModel _settingsViewModel;
        private readonly ImageCache _cache;
        private readonly ImageLoader _loader;
        private readonly ImagePicker _picker;
        private readonly IBackgroundImageSource _imageSource;

        public override Guid Id { get; } = Guid.Parse("72b7d457-0621-429b-8368-665bc53ff896");

        private ImageRotaterSettings Settings => _settingsViewModel?.Settings;

        public ImageRotater(IPlayniteAPI api) : base(api)
        {
            _settingsViewModel = new ImageRotaterSettingsViewModel(this);

            _cache = new ImageCache(CacheBudgetBytes);
            _loader = new ImageLoader(_cache);
            _picker = new ImagePicker();

            // Game.BackgroundImage is a database ID; GetFullFilePath turns it
            // into a real path.
            _imageSource = new PlayniteImageSource(id => api.Database.GetFullFilePath(id));

            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            // Themes place this as <ContentControl x:Name="ImageRotater_Background" />
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "ImageRotater",
                ElementList = new System.Collections.Generic.List<string> { "Background" }
            });
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args.Name == "Background")
            {
                return new BackgroundImageControl(_imageSource, _picker, _loader);
            }

            return null;
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new ImageRotaterSettingsView();
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            Logger.Info($"ImageRotater loaded (mode: {PlayniteApi.ApplicationInfo.Mode})");
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build -c Release`
Expected: `0 Error(s)`.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/ImageRotater.cs
git commit -m "feat: register ImageRotater_Background theme element"
```

---

### Task 8: Package and verify

**Files:** none modified.

- [ ] **Step 1: Full clean build and package**

```bash
dotnet clean -c Release
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File scripts/package_extension.ps1
```

Expected: `0 Error(s)`; a `.pext` is written under `pext/`.

- [ ] **Step 2: Full test suite**

Run: `dotnet test tests/ImageRotater.Tests.csproj -c Release`
Expected: all tests pass. Report the count.

- [ ] **Step 3: Manual verification in Playnite**

Install the `.pext`, then add this to a Fullscreen theme view:

```xml
<ContentControl x:Name="ImageRotater_Background" />
```

Verify each:

- [ ] A game with a background image displays it.
- [ ] Selecting a different game changes the background.
- [ ] A game with **no** background image shows nothing — the theme's own background is visible, and no warning is logged.
- [ ] Deleting a game's background file on disk, then selecting that game, shows the placeholder and logs one warning.
- [ ] Browsing quickly through many games does not visibly stutter.
- [ ] **The core assertion:** with a 4K background on a 1080p display, the decoded bitmap is 1920 wide, not 3840. Confirm by enabling debug logging and checking the decoded width, or by observing that memory does not climb ~32 MB per distinct image.
- [ ] Memory does not grow without bound while browsing a large library.
- [ ] Switching views repeatedly (which unloads and reloads the control) does not degrade performance over time.

- [ ] **Step 4: Report results**

Report build output, test count, and which manual checks passed. Do not claim completion until the manual checklist has been run.

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| Scope / v0.1 boundary | Whole plan; no timer, no folders, no acquisition, no crossfade |
| Trigger = selection only | 6 (`GameContextChanged`) |
| Random pick avoiding previous | 2 |
| The one seam (`IBackgroundImageSource`) | 5 |
| Decode path (all five settings) | 4 |
| Width buckets | 1, used in 4 and 6 |
| Byte-bounded cache | 3, wired in 7 |
| Lifecycle symmetry | 6 |
| Error handling: no image vs broken file | 5 (empty list), 6 (placeholder branch) |
| Vector placeholder | 6 (XAML) |
| Testing approach | 1-5 unit, 6 manual per spec |
| Success criteria | 8 |

All covered.

**Placeholder scan:** none — every step carries literal code or a literal command.

**Type consistency:** `WidthBucket.ForWidth(double) -> int` (T1) is called in T4 and T6.
`ImagePicker.Pick(IReadOnlyList<string>, string) -> string` (T2) is called in T6.
`ImageCache.Get/Put(string, int, BitmapSource)` (T3) is called in T4.
`ImageLoader.LoadAsync(string, int) -> Task<BitmapSource>` (T4) is called in T6.
`IBackgroundImageSource.GetImagePaths(Game) -> IReadOnlyList<string>` (T5) is called in T6.
All constructor signatures in T7 match the classes as defined in T2-T5.

**Known deviation from the spec, deliberate:** the spec's testing table lists
`PlayniteImageSource` as "tested via a stubbed game object". The plan injects a
`Func<string, string>` resolver instead of `IPlayniteAPI`, because `GetFullFilePath` is an
instance method on an interface the SDK does not make trivially mockable across the
version range. Behaviour is identical; testability is better.
