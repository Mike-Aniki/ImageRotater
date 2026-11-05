using System;
using System.IO;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The invariant that six earlier fixes failed to establish:
    //
    // Playnite's library store is OUTPUT ONLY. Rotation candidates come solely
    // from the plugin's own folder.
    //
    // When the store was also a source, each rotation imported a new copy,
    // deleted the previous one, and offered the current one back as a
    // candidate - so a rotation could select the exact file the next write was
    // about to delete. It was valid when chosen and gone when used, which no
    // amount of File.Exists checking can fix, because the race is between our
    // own read and our own delete. That produced the intermittent blank and
    // stretched artwork.
    [TestFixture]
    public class CandidateSourceIsolationTests
    {
        private string _root;
        private GameImageStore _store;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterIso_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(_root);
            _gameId = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string MakeSourceFile(string name)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            return path;
        }

        private Game GameWithPlayniteArtwork()
        {
            // Ids that would resolve to real files if the store were consulted.
            return new Game
            {
                Id = _gameId,
                Name = "Test",
                BackgroundImage = "playnite-background-id",
                CoverImage = "playnite-cover-id"
            };
        }

        [Test]
        public void FolderSource_IgnoresPlayniteBackgroundImage()
        {
            var source = new FolderImageSource(_store, ArtworkKind.Background);

            // The game has a Playnite background but nothing in our folder.
            Assert.AreEqual(0, source.GetImagePaths(GameWithPlayniteArtwork()).Count,
                "Playnite's own artwork must not appear as a rotation candidate");
        }

        [Test]
        public void FolderSource_IgnoresPlayniteCoverImage()
        {
            var source = new FolderImageSource(_store, ArtworkKind.Cover);

            Assert.AreEqual(0, source.GetImagePaths(GameWithPlayniteArtwork()).Count,
                "Playnite's own cover must not appear as a rotation candidate");
        }

        [Test]
        public void FolderSource_ReturnsOnlyOurOwnFiles()
        {
            _store.AddImage(_gameId, MakeSourceFile("ours.png"), ArtworkKind.Background);

            var paths = new FolderImageSource(_store, ArtworkKind.Background)
                .GetImagePaths(GameWithPlayniteArtwork());

            Assert.AreEqual(1, paths.Count);
            Assert.IsTrue(paths[0].EndsWith("ours.png", StringComparison.OrdinalIgnoreCase));

            // Nothing resolving into Playnite's library store may be offered:
            // that is what the writer deletes from.
            Assert.IsFalse(
                paths[0].IndexOf(Path.Combine("Playnite", "library", "files"), StringComparison.OrdinalIgnoreCase) >= 0,
                "a candidate resolved into Playnite's library store");
        }

        // A preserved original_* copy is a safety copy of the game's own
        // artwork, not artwork the user set up: on its own it must not opt the
        // game into rotation.
        [Test]
        public void PreservedOriginalAloneDoesNotOptTheGameIn()
        {
            _store.AddImage(_gameId, MakeSourceFile("original_theirs.jpg"), ArtworkKind.Background);

            var paths = new FolderImageSource(_store, ArtworkKind.Background)
                .GetImagePaths(GameWithPlayniteArtwork());

            Assert.AreEqual(0, paths.Count);
        }

        // The per-tile check must agree with the full candidate list it replaces.
        [TestCase("")]
        [TestCase("original_theirs.jpg")]
        [TestCase("ours.png")]
        [TestCase("original_theirs.jpg,ours.png")]
        public void HasArtworkAgreesWithTheCandidateList(string files)
        {
            byte seed = 0;
            foreach (string name in files.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string path = Path.Combine(_root, name);
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, ++seed });
                _store.AddImage(_gameId, path, ArtworkKind.Cover);
            }

            var source = new FolderImageSource(_store, ArtworkKind.Cover);
            Game game = GameWithPlayniteArtwork();

            Assert.AreEqual(source.GetImagePaths(game).Count > 0, source.HasArtwork(game));
        }

        [Test]
        public void GamesWithNoPluginArtwork_HaveNoCandidatesAtAll()
        {
            foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                Assert.AreEqual(0,
                    new FolderImageSource(_store, kind).GetImagePaths(GameWithPlayniteArtwork()).Count,
                    kind + " offered candidates for a game the user never set up");
            }
        }
    }
}
