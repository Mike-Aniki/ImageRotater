using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Video artwork: playable by the controls, invisible to the database write.
    //
    // Video differs from a GIF in one way that matters everywhere: no still
    // frame can be pulled out of it. PosterFrame.Extract is GDI+, which cannot
    // decode a container at all, so "animated" and "video" cannot be the same
    // predicate. Game.BackgroundImage / Game.CoverImage decode to a single
    // bitmap, so a video must never be written there - while a theme, which
    // renders the published file itself, must still receive it.
    [TestFixture]
    public class VideoArtworkTests
    {
        private string _root;
        private GameImageStore _store;
        private Game _game;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterVideo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _store = new GameImageStore(_root);
            _game = new Game { Id = Guid.NewGuid(), Name = "Game" };
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // Content is irrelevant to the routing under test - that is by
        // extension - but each file must have DISTINCT bytes, or the store's
        // content deduplication correctly collapses them into one candidate.
        private int _videoSeed;

        private string AddVideo(string name, ArtworkKind kind = ArtworkKind.Background)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[] { 0, 0, 0, 24, 102, 116, 121, 112, (byte)(++_videoSeed) });
            _store.AddImage(_game.Id, path, kind);
            return path;
        }

        [Test]
        public void VideoIsMotionButNotAnimated()
        {
            foreach (string v in new[] { @"C:\a\clip.mp4", @"C:\a\clip.webm", @"C:\a\CLIP.MP4" })
            {
                Assert.IsTrue(PosterFrame.IsVideo(v), $"{v} should be video");
                Assert.IsTrue(PosterFrame.IsMotion(v), $"{v} should count as motion");

                Assert.IsFalse(PosterFrame.IsAnimated(v),
                    $"{v} must not be 'animated' - that predicate promises an extractable "
                    + "still frame, and GDI+ cannot decode a video container");
            }

            Assert.IsTrue(PosterFrame.IsMotion(@"C:\a\clip.gif"), "a GIF is still motion");
            Assert.IsFalse(PosterFrame.IsVideo(@"C:\a\clip.gif"));
            Assert.IsFalse(PosterFrame.IsMotion(@"C:\a\clip.jpg"));
        }

        // Asking for a poster must fail cleanly rather than throwing out of
        // GDI+, because every caller treats null as "pick something else".
        [Test]
        public void VideoHasNoPoster()
        {
            string video = AddVideo("clip.mp4");

            Assert.IsNull(PosterFrame.For(video),
                "a video has no extractable still, and callers rely on null to fall back");
        }

        // Video has to reach the store's listing, or nothing can ever pick it.
        [Test]
        public void VideoIsListedAsACandidate()
        {
            AddVideo("clip.mp4");
            AddVideo("clip2.webm");

            IReadOnlyList<string> listed = _store.GetImagePaths(_game.Id, ArtworkKind.Background);

            Assert.AreEqual(2, listed.Count, "video must be offered to rotation like any other artwork");
        }
    }
}
