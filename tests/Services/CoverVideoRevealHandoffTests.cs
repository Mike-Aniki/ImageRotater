using ImageRotater.Controls;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public sealed class CoverVideoRevealHandoffTests
    {
        [Test]
        public void InitialVideoFromNativeIsAnimatedOnlyByNativeEntry()
        {
            Assert.That(CoverImageControl.NativeOwnsInitialVideoEntry(
                nativeInjected: true, replacingImage: false, replacingVideo: false), Is.True);
        }

        [Test]
        public void LocalImageToVideoRevealDoesNotStartANativeEntry()
        {
            Assert.That(CoverImageControl.NativeOwnsInitialVideoEntry(
                nativeInjected: true, replacingImage: true, replacingVideo: false), Is.False);
        }

        [Test]
        public void VideoToVideoAndThemeHostedVideoKeepTheirLocalHandoff()
        {
            Assert.That(CoverImageControl.NativeOwnsInitialVideoEntry(
                nativeInjected: true, replacingImage: false, replacingVideo: true), Is.False);
            Assert.That(CoverImageControl.NativeOwnsInitialVideoEntry(
                nativeInjected: false, replacingImage: false, replacingVideo: false), Is.False);
        }
    }
}