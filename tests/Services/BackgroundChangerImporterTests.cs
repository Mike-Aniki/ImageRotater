using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Against a fake BackgroundChanger data folder laid out the way the real
    // plugin writes it: BackgroundChanger\{gameId}.json listing items, files
    // under Images\{gameId}\.
    [TestFixture]
    public class BackgroundChangerImporterTests
    {
        private string _root;
        private string _bcRoot;
        private GameImageStore _store;
        private Guid _known;
        private Guid _unknown;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterBc_" + Guid.NewGuid().ToString("N"));
            _bcRoot = Path.Combine(_root, "bc");
            Directory.CreateDirectory(Path.Combine(_bcRoot, "BackgroundChanger"));
            _store = new GameImageStore(Path.Combine(_root, "ir"));
            _known = Guid.NewGuid();
            _unknown = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void WriteBcFile(Guid gameId, string name)
        {
            string folder = Path.Combine(_bcRoot, "Images", gameId.ToString());
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), System.Text.Encoding.UTF8.GetBytes("img:" + name));
        }

        private void WriteBcJson(Guid gameId, string itemsJson)
        {
            File.WriteAllText(
                Path.Combine(_bcRoot, "BackgroundChanger", gameId + ".json"),
                "{\"Items\":[" + itemsJson + "],\"Id\":\"" + gameId + "\",\"Name\":\"Game\",\"GameExist\":true}");
        }

        private static string Item(string name, Guid? folder, bool cover)
        {
            string folderJson = folder.HasValue ? "\"" + folder.Value + "\"" : "null";
            return "{\"Name\":\"" + name.Replace("\\", "\\\\") + "\",\"FolderName\":" + folderJson
                + ",\"IsDefault\":false,\"IsCover\":" + (cover ? "true" : "false") + ",\"IsFavorite\":false}";
        }

        private BackgroundChangerImporter.Result Import()
        {
            return BackgroundChangerImporter.Import(_bcRoot, new HashSet<Guid> { _known }, _store);
        }

        [Test]
        public void Routes_by_IsCover_and_skips_what_it_cannot_use()
        {
            WriteBcFile(_known, "a.png");
            WriteBcFile(_known, "b.jpg");
            WriteBcFile(_known, "c.tiff");
            WriteBcFile(_known, "d.webp");
            WriteBcJson(_known, string.Join(",",
                Item("a.png", _known, cover: true),
                Item("b.jpg", _known, cover: false),
                Item("c.tiff", _known, cover: false),
                Item("d.webp", _known, cover: false),
                Item("gone.png", _known, cover: false),
                Item(@"C:\Playnite\library\files\x\default.jpg", null, cover: true)));

            WriteBcFile(_unknown, "z.png");
            WriteBcJson(_unknown, Item("z.png", _unknown, cover: false));

            BackgroundChangerImporter.Result result = Import();

            Assert.AreEqual(3, result.Copied, result.Summary);
            Assert.AreEqual(1, result.Games);
            Assert.AreEqual(1, result.Missing);
            Assert.AreEqual(1, result.WebP);
            Assert.AreEqual(0, result.Failed);

            CollectionAssert.AreEquivalent(
                new[] { "a.png" },
                Directory.GetFiles(_store.GetGameFolder(_known, ArtworkKind.Cover)).Select(Path.GetFileName));
            CollectionAssert.AreEquivalent(
                new[] { "b.jpg", "d.webp" },
                Directory.GetFiles(_store.GetGameFolder(_known, ArtworkKind.Background)).Select(Path.GetFileName));

            Assert.IsFalse(Directory.Exists(Path.Combine(_store.ImagesRoot, _unknown.ToString())));
            StringAssert.Contains(Loc.Format("LOCImageRotaterWebPCopied", 1), result.Summary);
        }

        [Test]
        public void Second_run_copies_nothing_and_matches_by_stem()
        {
            WriteBcFile(_known, "a.png");
            WriteBcFile(_known, "b.webp");
            WriteBcJson(_known, string.Join(",",
                Item("a.png", _known, cover: true),
                Item("b.webp", _known, cover: false)));

            Assert.AreEqual(2, Import().Copied);

            // As if the user had converted the WebP to MP4 in between.
            string bg = _store.GetGameFolder(_known, ArtworkKind.Background);
            File.Move(Path.Combine(bg, "b.webp"), Path.Combine(bg, "b.mp4"));

            BackgroundChangerImporter.Result again = Import();

            Assert.AreEqual(0, again.Copied);
            Assert.AreEqual(2, again.Skipped);
            Assert.AreEqual(1, Directory.GetFiles(bg).Length);
        }

        [Test]
        public void Nothing_found_names_the_path()
        {
            Directory.Delete(Path.Combine(_bcRoot, "BackgroundChanger"), true);

            BackgroundChangerImporter.Result result = Import();

            Assert.AreEqual(0, result.Copied);
            Assert.AreEqual(Loc.Format("LOCImageRotaterNoBcAt", result.SourcePath), result.Summary);
            StringAssert.StartsWith(_bcRoot, result.SourcePath);
        }
    }
}
