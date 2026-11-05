using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageRotater.Controls;
using ImageRotater.Services;
using Newtonsoft.Json;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class CoverVisibilityPolicyTests
    {
        private sealed class Source : IBackgroundImageSource
        {
            internal string Path;
            public IReadOnlyList<string> GetImagePaths(Game game) => new[] { Path };
        }

        [TestCase(false), TestCase(true)]
        public void NewSettingsDefaultToAllVisibleStaticCovers(bool emptyJson)
        {
            var settings = emptyJson ? JsonConvert.DeserializeObject<ImageRotaterSettings>("{}") : new ImageRotaterSettings();
            Assert.That(settings.ShowUnfocusedCovers, Is.True);
            Assert.That(settings.AnimateUnfocusedCovers, Is.False);
            Assert.That(settings.ShouldAnimateCover(false), Is.False);
            Assert.That(settings.ShouldAnimateCover(true), Is.True);
        }

        [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true)]
        public void DisplayScopeAndPlaybackAreIndependent(bool show, bool animate)
        {
            var settings = new ImageRotaterSettings { RotateCovers = true, ShowUnfocusedCovers = show, AnimateUnfocusedCovers = animate };
            var game = new Game { Id = Guid.NewGuid() };
            Assert.That(NativeCoverHost.ShouldRender(settings, game, Guid.NewGuid()), Is.EqualTo(show));
            Assert.That(NativeCoverHost.ShouldRender(settings, game, game.Id), Is.True);
            Assert.That(settings.ShouldAnimateCover(false), Is.EqualTo(show && animate));
            Assert.That(settings.ShouldAnimateCover(true), Is.True);
            var copy = JsonConvert.DeserializeObject<ImageRotaterSettings>(JsonConvert.SerializeObject(settings));
            Assert.That(copy.ShowUnfocusedCovers, Is.EqualTo(show));
            Assert.That(copy.AnimateUnfocusedCovers, Is.EqualTo(animate));
        }

        [TestCase(false), TestCase(true)]
        public void OldConfigurationMigratesOnceAndOptionsStayIndependent(bool oldValue)
        {
            var settings = JsonConvert.DeserializeObject<ImageRotaterSettings>(
                "{\"AnimateUnfocusedCovers\":" + oldValue.ToString().ToLowerInvariant() + "}");
            Assert.That(settings.ShowUnfocusedCovers, Is.EqualTo(oldValue));
            settings.AnimateUnfocusedCovers = !oldValue;
            Assert.That(settings.ShowUnfocusedCovers, Is.EqualTo(oldValue), "Playback must no longer change scope");
        }

        [TestCase("{\"ShowUnfocusedCovers\":true,\"AnimateUnfocusedCovers\":false}")]
        [TestCase("{\"AnimateUnfocusedCovers\":false,\"ShowUnfocusedCovers\":true}")]
        public void MigrationRespectsExplicitScopeRegardlessOfPropertyOrder(string json)
        {
            var settings = JsonConvert.DeserializeObject<ImageRotaterSettings>(json);
            Assert.That(settings.ShowUnfocusedCovers, Is.True);
            Assert.That(settings.AnimateUnfocusedCovers, Is.False);
        }

        private string folder;
        private Guid oldSelected;
        private readonly List<CoverImageControl> renderers = new List<CoverImageControl>();
        private static readonly BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;

        [SetUp]
        public void SetUp()
        {
            oldSelected = (Guid)typeof(CoverImageControl).GetField("_selectedGame", Private).GetValue(null);
            typeof(CoverImageControl).GetField("_selectedGame", Private).SetValue(null, Guid.Empty);
            folder = Path.Combine(Path.GetTempPath(), "IR-cover-scope-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var renderer in renderers) renderer.ReleaseNativeRendering();
            renderers.Clear();
            typeof(CoverImageControl).GetField("_selectedGame", Private).SetValue(null, oldSelected);
            // WPF's asynchronous URI bitmap cache can retain a temporary file
            // after the control is released; cleanup must not race that decoder.
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        private string Still(string name)
        {
            string path = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
            var bitmap = BitmapSource.Create(16, 24, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 24 * 4], 16 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            return path;
        }

        private CoverImageControl Create(Source source, ImageRotaterSettings settings, bool details = false)
        {
            settings.RotateCovers = true;
            var renderer = new CoverImageControl(source, new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                () => settings, null, true);
            typeof(CoverImageControl).GetField("_nativeDetailsRegion", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(renderer, details);
            renderers.Add(renderer);
            renderer.GameContext = new Game { Id = Guid.NewGuid() };
            return renderer;
        }

        private static void Pump(Func<bool> done)
        {
            var frame = new DispatcherFrame();
            var watch = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (s, e) => { if (done() || watch.ElapsedMilliseconds > 3000) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            Assert.That(done(), Is.True);
        }

        [TestCase(false, false), TestCase(true, false), TestCase(false, true)]
        public void NonSelectedStillUsesScopeWhileDetailsRemainEligible(bool show, bool details)
        {
            var source = new Source { Path = Still("cover.png") };
            var renderer = Create(source, new ImageRotaterSettings
                { ShowUnfocusedCovers = show, AnimateUnfocusedCovers = false }, details);
            Assert.That(renderer.CoverData.ImagePath, Is.EqualTo(show || details ? source.Path : string.Empty));
            Assert.That(((MediaElement)renderer.FindName("DisplayVideo")).Source, Is.Null);
        }

        [Test]
        public void NonSelectedGifUsesFlattenedPosterInsteadOfAnimation()
        {
            string gif = Path.Combine(folder, "cover.gif");
            File.WriteAllBytes(gif, Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));
            var renderer = Create(new Source { Path = gif }, new ImageRotaterSettings
                { ShowUnfocusedCovers = true, AnimateUnfocusedCovers = false });
            Assert.That(renderer.CoverData.ImagePath, Is.EqualTo(PosterFrame.For(gif)));
            Assert.That(renderer.CoverData.ImagePath, Is.Not.EqualTo(gif));
            Assert.That((bool)typeof(CoverImageControl).GetField("_animating", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(renderer), Is.False);
        }

        [Test]
        public void NonSelectedVideoUsesCachedPosterWithoutOpeningVideoPlayer()
        {
            string video = Path.Combine(folder, "cover.mp4");
            File.WriteAllBytes(video, new byte[0]);
            Directory.CreateDirectory(Path.Combine(folder, PosterFrame.CacheFolderName));
            string poster = Still(PosterFrame.CacheFolderName + "/cover.mp4.png");
            File.SetLastWriteTimeUtc(poster, DateTime.UtcNow.AddMinutes(1));
            var renderer = Create(new Source { Path = video }, new ImageRotaterSettings
                { ShowUnfocusedCovers = true, AnimateUnfocusedCovers = false });
            Pump(() => renderer.CoverData.ImagePath == poster);
            Assert.That(((MediaElement)renderer.FindName("DisplayVideo")).Source, Is.Null);
            Assert.That((bool)typeof(CoverImageControl).GetField("_animating", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(renderer), Is.False);
        }

        [TestCase(false), TestCase(true)]
        public void QueuedPosterCannotOverwriteRecycledOrReleasedControl(bool release)
        {
            var gate = (SemaphoreSlim)typeof(CoverImageControl).GetField("StaticPosterGate", Private).GetValue(null);
            gate.Wait();
            CoverImageControl renderer;
            string replacement = Still("replacement.png");
            try
            {
                string video = Path.Combine(folder, "queued.mp4");
                File.WriteAllBytes(video, new byte[0]);
                var source = new Source { Path = video };
                renderer = Create(source, new ImageRotaterSettings
                    { ShowUnfocusedCovers = true, AnimateUnfocusedCovers = false });
                Assert.That(typeof(CoverImageControl).GetField("_staticPosterRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(renderer), Is.EqualTo(video));
                if (release) renderer.ReleaseNativeRendering();
                else
                {
                    source.Path = replacement;
                    renderer.GameContext = new Game { Id = Guid.NewGuid() };
                }
            }
            finally { gate.Release(); }
            // Flush the canceled asynchronous request and WPF binding work.
            bool drained = false;
            renderer.Dispatcher.BeginInvoke(new Action(() => drained = true), DispatcherPriority.ApplicationIdle);
            Pump(() => drained);
            Assert.That(renderer.CoverData.ImagePath, Is.EqualTo(release ? string.Empty : replacement));
            Assert.That(((MediaElement)renderer.FindName("DisplayVideo")).Source, Is.Null);
        }
    }
}
