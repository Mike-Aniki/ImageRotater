using System.Threading;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeCoverEntryTransitionTests
    {
        [TestCase(TransitionStyle.Pixelate, TransitionStyle.Pixelate)]
        [TestCase(TransitionStyle.Crossfade, TransitionStyle.Crossfade)]
        [TestCase(TransitionStyle.DepthShift, TransitionStyle.DepthShift)]
        [TestCase(TransitionStyle.Mosaic, TransitionStyle.Mosaic)]
        [TestCase(TransitionStyle.SlideFromRight, TransitionStyle.SlideFromRight)]
        [TestCase(TransitionStyle.Zoom, TransitionStyle.Zoom)]
        [TestCase(TransitionStyle.Focus, TransitionStyle.Focus)]
        [TestCase(TransitionStyle.SideReveal, TransitionStyle.SideReveal)]
        [TestCase(TransitionStyle.DiagonalReveal, TransitionStyle.DiagonalReveal)]
        [TestCase(TransitionStyle.FadeThroughBlack, TransitionStyle.FadeThroughBlack)]
        [TestCase(TransitionStyle.FadeThroughWhite, TransitionStyle.FadeThroughWhite)]
        [TestCase(TransitionStyle.Cut, TransitionStyle.Cut)]
        public void NativeEntryPreservesEveryConfiguredStyle(
            TransitionStyle configured, TransitionStyle expected)
        {
            Assert.That(NativeCoverEntryTransition.EffectiveStyle(configured), Is.EqualTo(expected));
        }
    }
}
