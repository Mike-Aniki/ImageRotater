using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ExtraMetadataLogoSourceTests
    {
        private string _root;
        private GameImageStore _store;
        private ExtraMetadataLogoSource _extra;
        private Game _game;
        private string _original;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "IRExtraLogo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new GameImageStore(Path.Combine(_root, "ir"));
            _extra = new ExtraMetadataLogoSource(Path.Combine(_root, "playnite"));
            _game = new Game { Id = Guid.NewGuid(), Name = "Test" };
            _original = Path.Combine(_root, "playnite", "ExtraMetadata", "games",
                _game.Id.ToString(), "Logo.png");
            Directory.CreateDirectory(Path.GetDirectoryName(_original));
            File.WriteAllBytes(_original, new byte[] { 1, 2, 3, 4 });
        }

        [TearDown]
        public void Teardown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string AddIrLogo(string name, byte[] content)
        {
            string source = Path.Combine(_root, name);
            File.WriteAllBytes(source, content);
            return _store.AddImage(_game.Id, source, ArtworkKind.Logo);
        }

        private FolderImageSource MakeSource()
        {
            return new FolderImageSource(_store, ArtworkKind.Logo, null, null, _extra);
        }

        [Test]
        public void ExtraMetadataOriginalAlone_DoesNotActivateRotation()
        {
            Assert.AreEqual(_original, _extra.GetLogoPath(_game.Id));
            Assert.AreEqual(0, MakeSource().GetImagePaths(_game).Count);
        }

        [Test]
        public void OriginalIncludedAlongsideIrLogo_WithoutCopying()
        {
            string added = AddIrLogo("extra.png", new byte[] { 9, 8, 7, 6 });
            var paths = MakeSource().GetImagePaths(_game);
            CollectionAssert.AreEqual(new[] { _original, added }, paths);
            Assert.IsTrue(File.Exists(_original));
        }

        [Test]
        public void OriginalExclusionIsIndependentAndPersistsThroughStoreScans()
        {
            string added = AddIrLogo("mine.png", new byte[] { 9, 8, 7, 6 });
            Assert.IsTrue(_extra.SetExcluded(_store, _game.Id, true));
            Assert.IsTrue(_extra.IsExcluded(_store, _game.Id));
            CollectionAssert.AreEqual(new[] { added }, MakeSource().GetImagePaths(_game));

            // GameImageStore cleans its own stale filenames on each scan; the
            // external exclusion must remain intact after that cleanup.
            _store.GetImagePathsRaw(_game.Id, ArtworkKind.Logo);
            Assert.IsTrue(_extra.IsExcluded(_store, _game.Id));
            Assert.IsTrue(_extra.SetExcluded(_store, _game.Id, false));
            Assert.IsFalse(_extra.IsExcluded(_store, _game.Id));
            Assert.AreEqual(2, MakeSource().GetImagePaths(_game).Count);
            Assert.IsTrue(File.Exists(_original));
        }

        [Test]
        public void ExclusionCanBeSetBeforeAnyIrLogoExists()
        {
            Assert.IsTrue(_extra.SetExcluded(_store, _game.Id, true));
            Assert.IsTrue(_extra.IsExcluded(_store, _game.Id));
            Assert.AreEqual(0, MakeSource().GetImagePaths(_game).Count);
        }

        [Test]
        public void UnsupportedIrLogo_DoesNotActivateExtraMetadataRotation()
        {
            AddIrLogo("animation.gif", new byte[] { 9, 8, 7, 6 });
            Assert.AreEqual(0, MakeSource().GetImagePaths(_game).Count);
        }

        [Test]
        public void OriginalCopiedIntoIr_IsNotDoubleWeighted()
        {
            AddIrLogo("same.png", new byte[] { 1, 2, 3, 4 });
            Assert.AreEqual(1, MakeSource().GetImagePaths(_game).Count);
        }

        [Test]
        public void MissingExtraMetadataFile_KeepsOwnArtwork()
        {
            string added = AddIrLogo("mine.png", new byte[] { 9, 8, 7, 6 });
            File.Delete(_original);
            CollectionAssert.AreEqual(new[] { added }, MakeSource().GetImagePaths(_game));
        }
    }
}
