using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using ImageRotater.Models;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    public class ArtworkBackupTests
    {
        private string workspace;
        private string source;
        private string destination;
        private string archive;
        private static readonly Guid LibraryPlugin = Guid.NewGuid();

        [SetUp]
        public void SetUp()
        {
            workspace = Path.Combine(Path.GetTempPath(), "ir-backup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            source = Path.Combine(workspace, "source");
            destination = Path.Combine(workspace, "destination");
            archive = Path.Combine(workspace, "backup.zip");
        }

        [TearDown]
        public void TearDown() => Directory.Delete(workspace, true);

        private static ArtworkBackup.GameEntry Game(string externalId, Guid? id = null)
        {
            return new ArtworkBackup.GameEntry
            {
                Id = id ?? Guid.NewGuid(),
                PluginId = LibraryPlugin,
                ExternalId = externalId,
                Name = externalId
            };
        }

        private static string Cover(string root, ArtworkBackup.GameEntry game, string file) =>
            Path.Combine(root, game.Id.ToString("D"), "covers", file);

        private void AddMedia(ArtworkBackup.GameEntry game)
        {
            string file = Cover(source, game, "video.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            File.WriteAllText(Cover(source, game, ".imagerotater-order"), "video.mp4");
        }

        private void Export(params ArtworkBackup.GameEntry[] games)
        {
            foreach (var game in games) AddMedia(game);
            Assert.That(ArtworkBackup.Export(archive, source, games, (done, total, name) => { },
                CancellationToken.None), Is.EqualTo(games.Length));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RestoreKeepsMediaMarkersAndPacksWithOriginalOrRecreatedLibrary(bool recreated)
        {
            var original = Game("123");
            original.Pack = new ArtworkPackConfiguration { Packs = { new ArtworkPack { Cover = "video.mp4" } } };
            var target = Game("123", recreated ? (Guid?)null : original.Id);
            Export(original);
            using (var stream = File.OpenRead(archive))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var item = ArtworkBackup.Plan(zip, new[] { target }, destination).Single();
                Assert.That(item.Status, Is.Null);
                Assert.That(item.Target.Id, Is.EqualTo(target.Id));
                Assert.That(item.Game.Pack.Packs.Single().Cover, Is.EqualTo("video.mp4"));
                ArtworkBackup.Restore(item, destination, _ => { }, CancellationToken.None);
                Assert.That(File.ReadAllBytes(Cover(destination, target, "video.mp4")), Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(File.ReadAllText(Cover(destination, target, ".imagerotater-order")), Is.EqualTo("video.mp4"));
                Assert.That(ArtworkBackup.Plan(zip, new[] { target }, destination).Single().Status, Is.EqualTo("Existing"));
            }
        }

        [Test]
        public void UnmatchedAmbiguousAndCollidingRemapsAreReported()
        {
            var missing = Game("missing");
            var ambiguous = Game("ambiguous");
            var exact = Game("duplicate");
            var duplicate = Game("duplicate");
            Export(missing, ambiguous, exact, duplicate);
            using (var stream = File.OpenRead(archive))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var library = new[] { Game("ambiguous"), Game("ambiguous"), Game("duplicate", exact.Id) };
                var plan = ArtworkBackup.Plan(zip, library, destination);
                Assert.That(plan.Select(item => item.Status), Is.EqualTo(new[] { "Unmatched", "Ambiguous", null, "Ambiguous" }));
                Assert.That(Directory.Exists(destination), Is.False);
            }
        }

        [Test]
        public void TraversalEntryRejectsTheArchiveBeforeWriting()
        {
            var game = Game("123");
            Export(game);
            using (var stream = File.Open(archive, FileMode.Open))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
                zip.CreateEntry($"Images/{game.Id:D}/covers/../../escape.mp4");

            using (var stream = File.OpenRead(archive))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                Assert.Throws<InvalidDataException>(() => ArtworkBackup.Plan(zip, new[] { game }, destination));
            Assert.That(Directory.Exists(destination), Is.False);
        }

        [Test]
        public void FixedArtworkCannotReferenceAFileOutsideTheGame()
        {
            var game = Game("123");
            Export(game);
            using (var stream = File.Open(archive, FileMode.Open))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(zip.CreateEntry($"Images/{game.Id:D}/covers/.imagerotater-fixed").Open()))
                writer.Write("../outside.png");

            using (var stream = File.OpenRead(archive))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var item = ArtworkBackup.Plan(zip, new[] { game }, destination).Single();
                Assert.Throws<InvalidDataException>(() =>
                    ArtworkBackup.Restore(item, destination, _ => { }, CancellationToken.None));
            }
            Assert.That(Directory.EnumerateFileSystemEntries(destination), Is.Empty);
        }

        [Test]
        public void CancelledGameIsNotPublishedAndCanBeRetried()
        {
            var game = Game("123");
            Export(game);
            using (var stream = File.OpenRead(archive))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            using (var cancel = new CancellationTokenSource())
            {
                var item = ArtworkBackup.Plan(zip, new[] { game }, destination).Single();
                Assert.Throws<OperationCanceledException>(() =>
                    ArtworkBackup.Restore(item, destination, _ => cancel.Cancel(), cancel.Token));
                Assert.That(Directory.EnumerateFileSystemEntries(destination), Is.Empty);
                ArtworkBackup.Restore(item, destination, _ => { }, CancellationToken.None);
                Assert.That(File.Exists(Cover(destination, game, "video.mp4")), Is.True);
            }
        }
    }
}
