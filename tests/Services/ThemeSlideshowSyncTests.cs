using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ThemeSlideshowSyncTests
    {
        private sealed class StubSource : IBackgroundImageSource
        {
            private readonly IReadOnlyList<string> _paths;

            public StubSource(IReadOnlyList<string> paths)
            {
                _paths = paths;
            }

            public IReadOnlyList<string> GetImagePaths(Game game)
            {
                return _paths;
            }
        }

        private string _root;
        private Game _game;
        private GameImageStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "IR_ThemeSync_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(_root);
            _game = new Game { Id = Guid.NewGuid(), Name = "Theme slideshow test" };

            foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                for (int i = 1; i <= 4; i++)
                {
                    string source = Path.Combine(_root, kind + "_" + i + ".png");
                    File.WriteAllBytes(source, new byte[] { 1, 2, 3, (byte)i });
                    _store.AddImage(_game.Id, source, kind);
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private static Guid CoverSelectionKey(Guid gameId)
        {
            byte[] bytes = gameId.ToByteArray();
            bytes[0] ^= 0xC0;
            return new Guid(bytes);
        }

        [Test]
        public void NativeCoverInjectionNeverWritesCoverMetadata()
        {
            var settings = new ImageRotaterSettings
            {
                UseThemeIntegration = false, UseExperimentalNativeCovers = true, RotateCovers = true,
                CoverSelectionMode = SelectionMode.Slideshow, CoverSelectionOrder = SelectionOrder.Sequential
            };
            _game.CoverImage = "native-cover.png";
            var covers = _store.GetImagePaths(_game.Id, ArtworkKind.Cover);
            var service = new BackgroundRotationService(new StubSource(new string[0]), new StubSource(covers),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()), _store, () => settings);
            service.ApplyTo(_game, ArtworkKind.Cover);
            for (int i = 0; i < 3; i++) service.ApplyNext(_game, ArtworkKind.Cover);
            Assert.That(_game.CoverImage, Is.EqualTo("native-cover.png"));
        }

        [Test]
        public void NativeInjectionWithoutThemeIntegrationNeverWritesBackgroundMetadata()
        {
            var settings = new ImageRotaterSettings
            {
                UseThemeIntegration = false, UseExperimentalFadeImage = true,
                SelectionMode = SelectionMode.Slideshow,
                BackgroundSelectionOrder = SelectionOrder.Sequential
            };
            _game.BackgroundImage = "native-original.png";
            var backgrounds = _store.GetImagePaths(_game.Id, ArtworkKind.Background);
            var selector = new ImageSelector(new ImagePicker(), new SessionSelectionCache());
            var service = new BackgroundRotationService(new StubSource(backgrounds),
                new StubSource(new string[0]), selector, _store, () => settings);
            service.ApplyTo(_game, ArtworkKind.Background);
            for (int i = 0; i < 3; i++)
            {
                service.PrepareNext(_game, ArtworkKind.Background);
                service.ApplyNext(_game, ArtworkKind.Background);
            }
            Assert.That(_game.BackgroundImage, Is.EqualTo("native-original.png"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ThemeIntegration_FirstAndLaterSlideshowTicks_KeepCoverAndBackgroundAligned(bool nativeInjection)
        {
            var settings = new ImageRotaterSettings
            {
                EnableRotation = true,
                RotateCovers = true,
                RotateBackgrounds = true,
                UseThemeIntegration = true,
                UseExperimentalFadeImage = nativeInjection,
                SelectionMode = SelectionMode.Slideshow,
                CoverSelectionMode = SelectionMode.Slideshow,
                BackgroundSelectionOrder = SelectionOrder.Sequential,
                CoverSelectionOrder = SelectionOrder.Sequential,
                BackgroundSlideshowSeconds = 5,
                CoverSlideshowSeconds = 5
            };

            IReadOnlyList<string> backgrounds = _store.GetImagePaths(_game.Id, ArtworkKind.Background);
            IReadOnlyList<string> covers = _store.GetImagePaths(_game.Id, ArtworkKind.Cover);
            Assert.AreEqual(4, backgrounds.Count);
            Assert.AreEqual(4, covers.Count);

            var selector = new ImageSelector(new ImagePicker(), new SessionSelectionCache());
            var service = new BackgroundRotationService(
                new StubSource(backgrounds), new StubSource(covers), selector, _store, () => settings);
            Guid coverKey = CoverSelectionKey(_game.Id);

            // Both theme controls render their first frame directly from the
            // selector. In integration mode ApplyTo(Cover) has NOT run here.
            Assert.AreEqual(backgrounds[0], selector.Select(
                _game.Id, backgrounds, null, SelectionMode.Slideshow, SelectionOrder.Sequential));
            Assert.AreEqual(covers[0], selector.Select(
                coverKey, covers, null, SelectionMode.Slideshow, SelectionOrder.Sequential));

            // The production timer preloads backgrounds, not covers. Verify
            // that covers still advance on tick #1, then stay aligned on wrap.
            for (int tick = 1; tick <= 5; tick++)
            {
                service.PrepareNext(_game, ArtworkKind.Background);
                service.ApplyNext(_game, ArtworkKind.Background);
                service.ApplyNext(_game, ArtworkKind.Cover);

                int expectedIndex = tick % 4;
                Assert.AreEqual(backgrounds[expectedIndex], selector.Select(
                    _game.Id, backgrounds, null, SelectionMode.Slideshow, SelectionOrder.Sequential),
                    "Background must advance on tick " + tick);
                Assert.AreEqual(covers[expectedIndex], selector.Select(
                    coverKey, covers, null, SelectionMode.Slideshow, SelectionOrder.Sequential),
                    "Cover must advance on tick " + tick);
            }
        }
    }
}
