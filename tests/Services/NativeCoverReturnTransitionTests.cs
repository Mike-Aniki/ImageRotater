using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageRotater.Controls;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeCoverReturnTransitionTests
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
        public void NativeReturnPreservesEveryConfiguredStyle(TransitionStyle configured, TransitionStyle expected)
        {
            Assert.That(NativeCoverReturnTransition.EffectiveStyle(configured), Is.EqualTo(expected));
        }

        [Test]
        public void NullRendererHasNoStillAndCannotBeAnimatedAsStill()
        {
            Assert.That(NativeCoverReturnTransition.VisibleStill(null), Is.Null);
        }
    }
}
