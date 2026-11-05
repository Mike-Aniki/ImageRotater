using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Artwork the user chose beats artwork that merely came with the game.
    //
    // A preserved original is whatever Playnite already had - a Steam grid, a
    // metadata provider's cover - copied into the plugin's folder the first
    // time it rotated that game. The copy has to exist: once Game.CoverImage
    // points at a plugin file the original is unreferenced, Playnite's library
    // cleanup can reclaim it, and "Restore original backgrounds" then holds an
    // id resolving to nothing.
    //
    // Existing for safety is not the same as competing for screen time. A game
    // given a deliberate animated cover rotated it against the still that came
    // with it, every six seconds, which read as the animation breaking - and
    // the still was not even the same picture.
    [TestFixture]
    public class PreservedArtworkPriorityTests
    {
        private string _root;
        private GameImageStore _store;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterPreserved_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(_root);
            _gameId = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // Distinct content per file, or the store's content deduplication
        // collapses them and the test measures the wrong thing.
        private int _seed;

        private string Add(string name)
        {
            string path = Path.Combine(_root, name);

            using (var bmp = new Bitmap(16 + (++_seed), 16))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(10 + _seed * 20, 40, 60));
                bmp.Save(path, ImageFormat.Jpeg);
            }

            return _store.AddImage(_gameId, path, ArtworkKind.Cover);
        }

        private IReadOnlyList<string> Candidates()
        {
            return _store.GetImagePaths(_gameId, ArtworkKind.Cover);
        }

        // Since 1.3.0 the game's own artwork is part of the rotation, so the
        // store keeps listing it beside what the user added. The Artwork
        // Manager shows it there, and the rotation source decides its place.
        [Test]
        public void PreservedArtStaysListedBesideTheUsersArtwork()
        {
            string preserved = Add("original_steamgrid.jpg");
            string chosen = Add("web_chosen.jpg");

            CollectionAssert.AreEquivalent(new[] { preserved, chosen }, Candidates());
        }

        // The safety guarantee: the file is still there, still restorable. Only
        // its place in the ROTATION changed.
        [Test]
        public void PreservedArtIsStillOnDisk()
        {
            string preserved = Add("original_steamgrid.jpg");
            Add("web_chosen.jpg");

            Assert.IsTrue(File.Exists(preserved),
                "excluding it from rotation must not delete it - Playnite's cleanup can "
                + "reclaim the original, and Restore needs this copy to resolve");
        }

        // A game the user has not set up has nothing else, so the preserved
        // original IS the rotation - otherwise the plugin would show nothing
        // for a game whose only artwork it had just copied in.
        [Test]
        public void PreservedArtRotatesWhenItIsAllTheGameHas()
        {
            Add("original_steamgrid.jpg");

            var listed = Candidates();

            Assert.AreEqual(1, listed.Count);
            Assert.IsTrue(GameImageStore.IsPreservedOriginal(listed[0]),
                "with nothing else, the preserved original must still be shown");
        }

        [Test]
        public void SeveralPreservedOriginalsAllRotateWhenThereIsNothingElse()
        {
            Add("original_a.jpg");
            Add("original_b.jpg");

            Assert.AreEqual(2, Candidates().Count);
        }

        // When a preserved copy and the file it was copied FROM are both
        // present, the real file is the one that survives deduplication.
        //
        // The survivor used to be whichever sorted first, and "original_" sorts
        // before "sgdb_" and "web_" - so the preserved duplicate displaced the
        // artwork it had been copied from. The bytes are the same either way,
        // but the surviving name then looked like the user's own art when it
        // was a copy of ours, and the exclusion rule above could no longer tell
        // them apart.
        [Test]
        public void APreservedDuplicateLosesToTheArtworkItCopied()
        {
            // Same bytes under both names - which is exactly what preservation
            // of an already-plugin-owned cover produces.
            string chosen = Path.Combine(_root, "sgdb_135535.jpg");

            using (var bmp = new Bitmap(40, 40))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.SlateBlue);
                bmp.Save(chosen, ImageFormat.Jpeg);
            }

            string preserved = Path.Combine(_root, "original_copy.jpg");
            File.Copy(chosen, preserved);

            _store.AddImage(_gameId, chosen, ArtworkKind.Cover);
            _store.AddImage(_gameId, preserved, ArtworkKind.Cover);

            var listed = Candidates();

            Assert.AreEqual(1, listed.Count, "identical content must collapse to one candidate");

            Assert.IsFalse(GameImageStore.IsPreservedOriginal(listed[0]),
                "the real artwork must survive, not the preserved copy of it");

            StringAssert.Contains("sgdb_135535", listed[0]);
        }

        [Test]
        public void ThePrefixIsMatchedCaseInsensitively()
        {
            Assert.IsTrue(GameImageStore.IsPreservedOriginal(@"C:\a\original_x.jpg"));
            Assert.IsTrue(GameImageStore.IsPreservedOriginal(@"C:\a\ORIGINAL_x.jpg"));
            Assert.IsFalse(GameImageStore.IsPreservedOriginal(@"C:\a\web_original.jpg"),
                "the prefix anchors at the start - a file merely containing the word is not preserved art");
            Assert.IsFalse(GameImageStore.IsPreservedOriginal(@"C:\a\sgdb_1.jpg"));
        }
    }
}
