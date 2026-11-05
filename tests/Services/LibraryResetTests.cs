using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Clearing the plugin out of a library.
    //
    // Worth testing carefully because it is the one irreversible thing the
    // plugin does. The ordering guarantee is the part that matters: the
    // preserved originals live INSIDE the folders being deleted, so restoring
    // has to happen first. Getting that backwards would delete a user's own
    // artwork and leave their games blank - the exact failure this feature is
    // meant to undo.
    [TestFixture]
    public class LibraryResetTests
    {
        private string _root;
        private GameImageStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ir-reset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _store = new GameImageStore(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string MakeGameFolder(string name)
        {
            string folder = Path.Combine(_store.ImagesRoot, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "art.jpg"), "x");
            return folder;
        }

        [Test]
        public void EveryGameFolderIsDeleted()
        {
            Directory.CreateDirectory(_store.ImagesRoot);

            string a = MakeGameFolder(Guid.NewGuid().ToString());
            string b = MakeGameFolder(Guid.NewGuid().ToString());

            var result = new LibraryReset(null, _store).Run();

            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.FoldersDeleted);
            Assert.IsFalse(Directory.Exists(a));
            Assert.IsFalse(Directory.Exists(b));
        }

        [Test]
        public void AnEmptyLibraryIsNotAnError()
        {
            // Running this on a fresh install must not report a failure.
            var result = new LibraryReset(null, _store).Run();

            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, result.FoldersDeleted);
        }

        [Test]
        public void ALockedFileDoesNotStopTheRest()
        {
            // Folders are deleted one at a time on purpose. A single open file -
            // a video the UI still has a handle on - would otherwise abort a
            // recursive delete of the whole root partway through, clearing an
            // arbitrary half of the library.
            Directory.CreateDirectory(_store.ImagesRoot);

            string locked = MakeGameFolder("locked");
            string other = MakeGameFolder("other");

            using (File.Open(
                Path.Combine(locked, "art.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = new LibraryReset(null, _store).Run();

                Assert.IsTrue(result.Success, "a locked file is reported, not thrown");
                Assert.AreEqual(1, result.FoldersFailed);
                Assert.AreEqual(1, result.FoldersDeleted);
                Assert.IsFalse(Directory.Exists(other), "the other folder still went");
            }
        }

        [Test]
        public void NothingIsDeletedWhenTheRestoreFails()
        {
            // THE critical case. The preserved originals live inside the
            // folders about to be deleted, so a restore that failed followed by
            // a delete that succeeded would destroy a user's own artwork with
            // no way back.
            Directory.CreateDirectory(_store.ImagesRoot);

            string folder = MakeGameFolder(Guid.NewGuid().ToString());

            var result = new LibraryReset(new ThrowingWriter(), _store).Run();

            Assert.IsFalse(result.Success);
            Assert.AreEqual(0, result.FoldersDeleted);
            Assert.IsTrue(
                Directory.Exists(folder),
                "a failed restore must leave every file exactly where it was");
        }

        [Test]
        public void TheFailureSaysNothingWasDeleted()
        {
            // The user has to know the library is untouched, or their next move
            // is to go looking for artwork that is actually still there.
            Directory.CreateDirectory(_store.ImagesRoot);

            var result = new LibraryReset(new ThrowingWriter(), _store).Run();

            Assert.AreEqual(Loc.Format("LOCImageRotaterResetRestoreFailed", "restore failed"), result.Error);
            StringAssert.Contains("nothing was deleted", EnglishText("LOCImageRotaterResetRestoreFailed"));
        }

        // The test host has no Playnite resources, so the user-facing wording is
        // read from the shipped English dictionary.
        private static string EnglishText(string key)
        {
            string dir = Path.GetDirectoryName(typeof(LibraryReset).Assembly.Location);
            string xaml = File.ReadAllText(Path.Combine(dir, "Localization", "en_US.xaml"));
            var match = System.Text.RegularExpressions.Regex.Match(
                xaml, "x:Key=\"" + key + "\"[^>]*>([^<]*)<");
            Assert.IsTrue(match.Success, key + " is missing from en_US.xaml");
            return match.Groups[1].Value;
        }

        // A writer whose restore always fails.
        private class ThrowingWriter : PlayniteBackgroundWriter
        {
            public ThrowingWriter() : base(null, Path.GetTempPath())
            {
            }

            public override int RestoreAll()
            {
                throw new InvalidOperationException("restore failed");
            }

            public override int RestoreAll(Action<int, int> onProgress)
            {
                throw new InvalidOperationException("restore failed");
            }
        }
    }
}
