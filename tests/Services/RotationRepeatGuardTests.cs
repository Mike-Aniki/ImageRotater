using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Selection events repeat for the game that is ALREADY selected -
    // Fullscreen raises them on view changes and focus shifts, not only on
    // navigation. Rotation must happen once per game, not once per event.
    //
    // Without the guard, EverySelection re-picked on every repeat, and because
    // the picker deliberately avoids the previous choice, each repeat showed a
    // different image. Playnite caches decoded bitmaps per grid entry, so
    // entries rendered at different moments kept different covers - five
    // distinct covers for one game inside 43 seconds in the reported log.
    [TestFixture]
    public class RotationRepeatGuardTests
    {
        // The service reads candidates once per real pick and never when the
        // guard suppresses a repeat, so counting reads counts rotations.
        private class CountingSource : IBackgroundImageSource
        {
            private readonly IReadOnlyList<string> _paths;
            public int Picks;
            public CountingSource(IReadOnlyList<string> paths) { _paths = paths; }
            public IReadOnlyList<string> GetImagePaths(Game game) { Picks++; return _paths; }
        }

        private string _root;
        private GameImageStore _store;
        private CountingSource _source;
        private BackgroundRotationService _service;
        private Game _game;
        private Game _other;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterGuard_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _store = new GameImageStore(_root);

            _game = new Game { Id = Guid.NewGuid(), Name = "Game" };
            _other = new Game { Id = Guid.NewGuid(), Name = "Other" };

            // Several candidates, so an unguarded re-pick would visibly choose
            // a different one rather than coincidentally repeating itself.
            foreach (Game g in new[] { _game, _other })
            {
                for (int i = 0; i < 4; i++)
                {
                    string src = Path.Combine(_root, g.Id.ToString("N") + "_" + i + ".png");

                    // Distinct content per file: the store deduplicates
                    // byte-identical candidates, and identical bytes here
                    // would collapse the pool this fixture exists to provide.
                    File.WriteAllBytes(src, System.Text.Encoding.UTF8.GetBytes(g.Id.ToString("N") + i));
                    _store.AddImage(g.Id, src, ArtworkKind.Background);
                }
            }

            _source = new CountingSource(_store.GetImagePaths(_game.Id, ArtworkKind.Background));

            _service = new BackgroundRotationService(
                _source, _source, new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                _store, () => EverySelection());
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // EverySelection is the mode that exposed this: Session mode would have
        // masked it by returning the same cached pick every time.
        private static ImageRotaterSettings EverySelection()
        {
            return new ImageRotaterSettings
            {
                EnableRotation = true,
                SelectionMode = SelectionMode.EverySelection,
                RotateCovers = false
            };
        }

        [Test]
        public void RepeatedSelectionOfTheSameGame_PicksOnce()
        {
            for (int i = 0; i < 10; i++)
            {
                _service.ApplyTo(_game);
            }

            Assert.AreEqual(1, _source.Picks,
                "a repeated selection event re-rotated the game the user was already on");
        }

        [Test]
        public void SelectingADifferentGame_StillRotates()
        {
            _service.ApplyTo(_game);
            _service.ApplyTo(_other);

            Assert.AreEqual(2, _source.Picks,
                "the guard must not block a genuine change of game");
        }

        // EverySelection means "re-pick when a different game is selected", so
        // coming back must roll again. This is what separates the guard from
        // Session mode.
        [Test]
        public void ReturningToAGame_RotatesAgain()
        {
            _service.ApplyTo(_game);
            _service.ApplyTo(_other);
            _service.ApplyTo(_game);

            Assert.AreEqual(3, _source.Picks,
                "returning to a game should re-pick in EverySelection mode");
        }

        [Test]
        public void Forget_LetsTheSameGameRotateAgain()
        {
            _service.ApplyTo(_game);
            _service.Forget(_game.Id);
            _service.ApplyTo(_game);

            Assert.AreEqual(2, _source.Picks,
                "adding or removing images must let the current game rotate again");
        }

        [Test]
        public void ForgetAll_ClearsTheGuard()
        {
            _service.ApplyTo(_game);
            _service.ForgetAll();
            _service.ApplyTo(_game);

            Assert.AreEqual(2, _source.Picks,
                "after a restore the current game must be able to rotate again");
        }

        // Forgetting some other game must not release the guard for this one.
        [Test]
        public void ForgettingAnotherGame_LeavesTheGuardInPlace()
        {
            _service.ApplyTo(_game);
            _service.Forget(_other.Id);
            _service.ApplyTo(_game);

            Assert.AreEqual(1, _source.Picks,
                "forgetting an unrelated game released the wrong guard");
        }
    }
}
