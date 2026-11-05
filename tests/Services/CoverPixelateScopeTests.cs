using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public sealed class CoverPixelateScopeTests
    {
        [Test]
        public void PixelateRequiresShowUnfocusedCovers()
        {
            Assert.That(Transition.CoverStyleForSettings(TransitionStyle.Pixelate, false),
                Is.EqualTo(TransitionStyle.Crossfade));
            Assert.That(Transition.CoverStyleForSettings(TransitionStyle.Pixelate, true),
                Is.EqualTo(TransitionStyle.Pixelate));
        }

        [Test]
        public void OtherEffectsAreNotDisabled()
        {
            foreach (var value in new[] { TransitionStyle.Mosaic, TransitionStyle.SideReveal,
                TransitionStyle.DiagonalReveal, TransitionStyle.Cut, TransitionStyle.Focus })
                Assert.That(Transition.CoverStyleForSettings(value, false), Is.EqualTo(value));
        }
    }
}
