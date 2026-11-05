using System;
using System.IO;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The debug log is a single file: past its size limit it starts over, and
    // the archives earlier releases kept are removed.
    [TestFixture]
    public class FileLoggerTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterLog_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string LogPath => Path.Combine(_dir, "ImageRotater.log");

        [Test]
        public void AnOversizedLogStartsOverInsteadOfBeingArchived()
        {
            using (var stream = File.Create(LogPath)) stream.SetLength(FileLogger.MaxBytes);

            new FileLogger(_dir, () => true).StartSession("1.6.0", "test");

            Assert.That(new FileInfo(LogPath).Length, Is.LessThan(1024));
            Assert.That(Directory.GetFiles(_dir), Has.Length.EqualTo(1));
        }

        [Test]
        public void OldArchivesAreRemovedAndTheCurrentLogIsKept()
        {
            File.WriteAllText(LogPath, "previous session");
            string archive = Path.Combine(_dir, "ImageRotater.20261101-120000.log");
            File.WriteAllText(archive, "old");

            new FileLogger(_dir, () => true).StartSession("1.6.0", "test");

            Assert.That(File.Exists(archive), Is.False);
            StringAssert.StartsWith("previous session", File.ReadAllText(LogPath));
        }
    }
}
