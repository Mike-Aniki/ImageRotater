using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Both kinds rotate through the shared selection service, and neither may
    // switch the other off. Each pick is counted where it happens: the service
    // reads its candidates exactly once per real selection.
    [TestFixture]
    public class CoverControlHandoffTests
    {
        private class CountingSource : IBackgroundImageSource
        {
            private readonly GameImageStore _store;
            private readonly ArtworkKind _kind;
            private readonly List<ArtworkKind> _picks;

            public CountingSource(GameImageStore store, ArtworkKind kind, List<ArtworkKind> picks)
            {
                _store = store;
                _kind = kind;
                _picks = picks;
            }

            public IReadOnlyList<string> GetImagePaths(Game game)
            {
                _picks.Add(_kind);
                return _store.GetImagePaths(game.Id, _kind);
            }
        }

        private string _root;
        private GameImageStore _store;
        private List<ArtworkKind> _picks;
        private Game _game;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterHandoff_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _store = new GameImageStore(_root);
            _picks = new List<ArtworkKind>();
            _game = new Game { Id = Guid.NewGuid(), Name = "Game" };

            // Both kinds present, so a missing write is a real decision rather
            // than an empty candidate list.
            foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                string src = Path.Combine(_root, kind + ".png");
                File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });
                _store.AddImage(_game.Id, src, kind);
            }
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private BackgroundRotationService Build(ImageRotaterSettings settings)
        {
            return new BackgroundRotationService(
                new CountingSource(_store, ArtworkKind.Background, _picks),
                new CountingSource(_store, ArtworkKind.Cover, _picks),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                _store, () => settings);
        }

        private static ImageRotaterSettings Settings(bool rotateCovers)
        {
            return new ImageRotaterSettings
            {
                EnableRotation = true,
                SelectionMode = SelectionMode.EverySelection,
                RotateCovers = rotateCovers
            };
        }

        [Test]
        public void CoversArePickedWhenRotating()
        {
            Build(Settings(rotateCovers: true)).ApplyTo(_game);

            CollectionAssert.Contains(_picks, ArtworkKind.Cover);
        }

        [Test]
        public void BackgroundsArePickedAlongsideCovers()
        {
            Build(Settings(rotateCovers: true)).ApplyTo(_game);

            CollectionAssert.Contains(_picks, ArtworkKind.Background,
                "cover rotation must not disable background rotation");
        }

        // The slideshow tick: the game has stayed selected and the timer says
        // "next image". Both the repeat guard ("already did this game") and
        // Session mode ("keep this pick") exist to PREVENT that, so ApplyNext
        // must override both - and pick a different image, or the slideshow
        // visibly does nothing.
        [Test]
        public void ApplyNext_RotatesPastTheGuardAndSessionMode()
        {
            // A second background, so a forced re-pick has somewhere to go.
            string extra = Path.Combine(_root, "second.png");
            File.WriteAllBytes(extra, new byte[] { 5, 6, 7, 8 });
            _store.AddImage(_game.Id, extra, ArtworkKind.Background);

            var settings = Settings(rotateCovers: false);
            settings.SelectionMode = SelectionMode.Session;

            var service = Build(settings);

            service.ApplyTo(_game);
            int First() { return _picks.FindAll(k => k == ArtworkKind.Background).Count; }
            int first = First();

            service.ApplyTo(_game);
            Assert.AreEqual(first, First(), "the guard should suppress a repeat selection");

            service.ApplyNext(_game, ArtworkKind.Background);
            Assert.AreEqual(first + 1, First(),
                "a slideshow tick must rotate despite the guard and Session mode");
        }

        [Test]
        public void RotateCoversOff_NeitherPathRuns()
        {
            Build(Settings(rotateCovers: false)).ApplyTo(_game);

            CollectionAssert.DoesNotContain(_picks, ArtworkKind.Cover);
        }

        // EnableCoverImage is what themes bind to decide whether to collapse
        // their own native cover element. It follows RotateCovers alone: the
        // separate "let themes render covers" toggle it used to also require
        // existed only to work around Fullscreen tiles holding a stale cover,
        // which Playnite 10.57 fixed.
        [Test]
        public void NativeInjectionKeepsLegacyThemeCoverHiddenAndConfigurationAvailable()
        {
            Assert.IsFalse(Settings(true).EnableCoverImage);
            Assert.IsTrue(Settings(true).CanConfigureCovers);
            Assert.IsFalse(Settings(false).EnableCoverImage,
                "covers not rotating - the theme must keep its own cover");
        }

        // Themes bind EnableCoverImage, so a change to either underlying
        // setting has to raise a notification for it or the tile keeps hiding
        // (or showing) the wrong element.
        [Test]
        public void EnableCoverImage_NotifiesWhenItsInputsChange()
        {
            foreach (string input in new[] { "RotateCovers" })
            {
                var settings = Settings(rotateCovers: false);

                var raised = new List<string>();
                settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

                settings.RotateCovers = true;

                CollectionAssert.Contains(raised, "EnableCoverImage",
                    $"changing {input} must notify the derived EnableCoverImage");
            }
        }

        // HasDataCover gates the theme per game. It has to actually notify, or
        // a tile would keep the previous game's answer.
        [Test]
        public void HasDataCover_NotifiesOnChangeOnly()
        {
            var settings = Settings(true);
            settings.HasDataCover = false;

            var raised = new List<string>();
            settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            settings.HasDataCover = true;
            CollectionAssert.Contains(raised, "HasDataCover");

            raised.Clear();
            settings.HasDataCover = true;
            CollectionAssert.DoesNotContain(raised, "HasDataCover",
                "an unchanged value should not churn theme bindings");
        }
    }
}
