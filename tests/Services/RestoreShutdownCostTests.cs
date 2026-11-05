using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // RestoreAll runs when Playnite closes, for every game the plugin has
    // rotated. It used to commit each game on its own - one database write
    // and one change notification apiece - and remove each replaced file
    // through Playnite's RemoveFile, which retries a locked file for 2.5 s.
    // Covers still on screen ARE locked at shutdown, so closing Fullscreen
    // took several seconds longer with the plugin installed.
    //
    // Now: one bulk commit, and one delete attempt per file with no retry -
    // a locked file was being left behind after the retries anyway.
    [TestFixture]
    public class RestoreShutdownCostTests
    {
        private string _dir;

        private class RecordingWriter : PlayniteBackgroundWriter
        {
            public readonly Dictionary<Guid, Game> Games = new Dictionary<Guid, Game>();
            public readonly List<int> CommitBatchSizes = new List<int>();
            public readonly List<string> DeleteAttempts = new List<string>();
            public bool DeletesFail;

            private int _imports;

            public RecordingWriter(string dir) : base(null, dir) { }

            protected override string ImportFile(string imagePath, Guid gameId)
            {
                return "import-" + (++_imports);
            }

            protected override void InvokeOnUi(Action action)
            {
                action();
            }

            protected override Game LookupGame(Guid id)
            {
                Game game;
                return Games.TryGetValue(id, out game) ? game : null;
            }

            protected override void CommitGames(IEnumerable<Game> games)
            {
                CommitBatchSizes.Add(games.Count());
            }

            protected override bool TryDeleteLibraryFileOnce(string id)
            {
                DeleteAttempts.Add(id);
                return !DeletesFail;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterRestoreCost_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private Game Rotated(RecordingWriter writer, string art, string ownCover)
        {
            var game = new Game { Id = Guid.NewGuid(), Name = "Game", CoverImage = ownCover };
            writer.Games[game.Id] = game;

            // SetCurrent is the real one here, so the game now points at the
            // imported copy and RestoreAll has something to undo.
            writer.SetArtwork(game, art, ArtworkKind.Cover);
            return game;
        }

        [Test]
        public void RestoreArtwork_RestoresOnlyTheRequestedGameAndKind()
        {
            string art = Path.Combine(_dir, "pack.png");
            File.WriteAllBytes(art, new byte[] { 1, 2, 3, 4 });
            var writer = new RecordingWriter(_dir);
            Game a = Rotated(writer, art, "own-a");
            Game b = Rotated(writer, art, "own-b");

            Assert.That(writer.RestoreArtwork(a.Id, ArtworkKind.Cover), Is.EqualTo(1));
            Assert.That(a.CoverImage, Is.EqualTo("own-a"));
            Assert.That(b.CoverImage, Is.EqualTo("import-2"));
            Assert.That(writer.RestoreArtwork(a.Id, ArtworkKind.Cover), Is.Zero);
            Assert.That(writer.RestoreArtwork(b.Id, ArtworkKind.Cover), Is.EqualTo(1));
            Assert.That(b.CoverImage, Is.EqualTo("own-b"));
        }

        [Test]
        public void RestoreAll_CommitsEveryGameInOneBatch_AndAttemptsEachDeleteOnce()
        {
            string art = Path.Combine(_dir, "art.png");
            File.WriteAllBytes(art, new byte[] { 1, 2, 3, 4 });

            var writer = new RecordingWriter(_dir) { DeletesFail = true };
            Game a = Rotated(writer, art, "own-a");
            Game b = Rotated(writer, art, "own-b");
            Assert.AreEqual("import-1", a.CoverImage);
            Assert.AreEqual("import-2", b.CoverImage);

            writer.CommitBatchSizes.Clear();
            writer.DeleteAttempts.Clear();

            int restored = writer.RestoreAll();

            Assert.AreEqual(2, restored);
            Assert.AreEqual("own-a", a.CoverImage);
            Assert.AreEqual("own-b", b.CoverImage);
            CollectionAssert.AreEqual(new[] { 2 }, writer.CommitBatchSizes, "one commit for all games");
            CollectionAssert.AreEquivalent(new[] { "import-1", "import-2" }, writer.DeleteAttempts,
                "each replaced copy is tried exactly once, even when the delete fails");
        }
    }
}
