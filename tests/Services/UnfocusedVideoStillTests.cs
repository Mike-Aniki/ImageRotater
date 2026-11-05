using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ImageRotater.Controls;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    // Covers the passive video -> still selection policy without relying on
    // FFmpeg, the MediaElement decoder or a visible Playnite window.
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class UnfocusedVideoStillTests
    {
        private string folder;
        private static readonly MethodInfo Choose = typeof(CoverImageControl).GetMethod(
            "ChooseUnfocusedStill", BindingFlags.Static | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "ir-unfocused-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        private string FileNamed(string name)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, new byte[] { 1 });
            return path;
        }

        private static string Pick(string[] candidates, string lastStill)
        {
            return (string)Choose.Invoke(null, new object[] { candidates, lastStill });
        }

        [Test]
        public void PrefersLastFixedArtWhenItStillBelongsToThisGame()
        {
            string native = FileNamed("original.png");
            string extra = FileNamed("alternate.jpg");
            string video = FileNamed("moving.mp4");
            Assert.That(Pick(new[] { native, video, extra }, extra), Is.EqualTo(extra));
        }

        [Test]
        public void FallsBackToNativeOrAnotherExistingFixedCover()
        {
            string native = FileNamed("original.png");
            string video = FileNamed("moving.mp4");
            Assert.That(Pick(new[] { video, native }, null), Is.EqualTo(native));
        }

        [Test]
        public void NeverReusesAStillFromAnotherGameOrAnExcludedCandidate()
        {
            string native = FileNamed("original.png");
            string stale = FileNamed("from-another-game.png");
            Assert.That(Pick(new[] { native }, stale), Is.EqualTo(native));
        }

        [Test]
        public void MotionCandidatesDoNotBecomeFixedCovers()
        {
            string video = FileNamed("moving.mp4");
            string animatedGif = FileNamed("animation.gif");
            Assert.That(Pick(new[] { video, animatedGif }, null), Is.Null);
        }
    }
}
