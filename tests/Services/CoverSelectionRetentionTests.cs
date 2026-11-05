using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageRotater.Controls;
using ImageRotater.Services;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class CoverSelectionRetentionTests
    {
        private sealed class Source : IBackgroundImageSource
        {
            internal string[] Paths;
            public IReadOnlyList<string> GetImagePaths(Game game) => Paths;
        }

        private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private Guid previousSelection;
        private string folder;

        [SetUp]
        public void SetUp()
        {
            previousSelection = (Guid)typeof(CoverImageControl).GetField("_selectedGame", Static).GetValue(null);
            folder = Path.Combine(Path.GetTempPath(), "IR-retain-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void TearDown()
        {
            CoverImageControl.NotifySelectionChanged(previousSelection);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        private string Still(string filename)
        {
            string path = Path.Combine(folder, filename);
            var bitmap = BitmapSource.Create(16, 24, 96, 96, PixelFormats.Bgra32,
                null, new byte[16 * 24 * 4], 16 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
            return path;
        }

        private static void Refresh(CoverImageControl renderer) =>
            typeof(CoverImageControl).GetMethod("Refresh", Instance).Invoke(renderer, null);

        [Test]
        public void EverySelectionDoesNotRollAgainOnDeselectionOrOrdinaryRefresh()
        {
            string first = Still("first.png"), second = Still("second.png");
            var settings = new ImageRotaterSettings
            {
                RotateCovers = true, CoverSelectionMode = SelectionMode.EverySelection,
                CoverSelectionOrder = SelectionOrder.Sequential,
                ShowUnfocusedCovers = true, AnimateUnfocusedCovers = false
            };
            var game = new Game { Id = Guid.NewGuid() };
            var renderer = new CoverImageControl(new Source { Paths = new[] { first, second } },
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()), () => settings, null, true);
            try
            {
                CoverImageControl.NotifySelectionChanged(game.Id);
                renderer.GameContext = game;
                Assert.That(renderer.CoverData.ImagePath, Is.EqualTo(first));
                Refresh(renderer);
                Assert.That(renderer.CoverData.ImagePath, Is.EqualTo(first), "A normal Refresh must not count as another selection");

                CoverImageControl.NotifySelectionChanged(Guid.NewGuid());
                Refresh(renderer);
                Assert.That(renderer.CoverData.ImagePath, Is.EqualTo(first), "Losing selection must retain the current artwork");

                CoverImageControl.NotifySelectionChanged(game.Id);
                Refresh(renderer);
                Assert.That(renderer.CoverData.ImagePath, Is.EqualTo(second), "Re-selecting the game must advance EverySelection once");
                Refresh(renderer);
                Assert.That(renderer.CoverData.ImagePath, Is.EqualTo(second), "The new selection must remain stable");
            }
            finally { renderer.ReleaseNativeRendering(); }
        }

        [Test]
        public void DeselectedVideoKeepsItsFrameWhilePosterIsBeingPrepared()
        {
            string path = Path.Combine(folder, "video.mp4");
            File.WriteAllBytes(path, new byte[0]);
            var gate = (SemaphoreSlim)typeof(CoverImageControl).GetField("StaticPosterGate", Static).GetValue(null);
            var renderer = new CoverImageControl(new Source { Paths = new[] { path } },
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                () => new ImageRotaterSettings { RotateCovers = true, ShowUnfocusedCovers = true, AnimateUnfocusedCovers = false },
                null, true);
            gate.Wait();
            try
            {
                var game = new Game { Id = Guid.NewGuid() };
                CoverImageControl.NotifySelectionChanged(game.Id);
                renderer.GameContext = game;
                var video = (MediaElement)renderer.FindName("DisplayVideo");
                var source = new Uri(path);
                video.Source = source;
                video.Visibility = Visibility.Visible;
                typeof(CoverImageControl).GetField("_videoPath", Instance).SetValue(renderer, path);
                renderer.NotifyNativeMediaReady(video);
                Assert.That(renderer.IsCoverDisplayed, Is.True);
                CoverImageControl.NotifySelectionChanged(Guid.NewGuid());
                typeof(CoverImageControl).GetMethod("ShowStaticVideoPoster", Instance)
                    .Invoke(renderer, new object[] { path, null });
                Assert.Multiple(() =>
                {
                    Assert.That(renderer.IsCoverDisplayed, Is.True,
                        "Poster preparation must not expose Playnite's native image");
                    Assert.That(video.Source, Is.EqualTo(source));
                    Assert.That(video.Visibility, Is.EqualTo(Visibility.Visible));
                });
            }
            finally
            {
                renderer.ReleaseNativeRendering();
                gate.Release();
            }
        }
    }
}
