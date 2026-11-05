using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeCoverMosaicVisualTests
    {
        private static ImageSource Source() =>
            new DrawingImage(new GeometryDrawing(Brushes.CadetBlue, null,
                new RectangleGeometry(new Rect(0, 0, 100, 100))));

        [Test]
        public void NativeMosaicKeepsMosaicForAllThreeRelays()
        {
            Assert.That(NativeCoverEntryTransition.EffectiveStyle(TransitionStyle.Mosaic),
                Is.EqualTo(TransitionStyle.Mosaic));
            Assert.That(NativeCoverReturnTransition.EffectiveStyle(TransitionStyle.Mosaic),
                Is.EqualTo(TransitionStyle.Mosaic));
            Assert.That(NativeCoverNativeTransition.EffectiveStyle(TransitionStyle.Mosaic),
                Is.EqualTo(TransitionStyle.Mosaic));
        }

        [Test]
        public void PixelateNowSupportedByAllNativeRelays()
        {
            Assert.That(NativeCoverEntryTransition.EffectiveStyle(TransitionStyle.Pixelate),
                Is.EqualTo(TransitionStyle.Pixelate));
            Assert.That(NativeCoverReturnTransition.EffectiveStyle(TransitionStyle.Pixelate),
                Is.EqualTo(TransitionStyle.Pixelate));
            Assert.That(NativeCoverNativeTransition.EffectiveStyle(TransitionStyle.Pixelate),
                Is.EqualTo(TransitionStyle.Pixelate));
        }

        [Test]
        public void MosaicRendersAsOneVisualInsteadOfSixtyIndividualImageControls()
        {
            var template = new Image { Stretch = Stretch.UniformToFill };
            var layer = new Grid();
            var mosaic = NativeCoverMosaicVisual.Begin(layer, template, Source(), Source(),
                TimeSpan.FromMilliseconds(700));
            Assert.That(mosaic, Is.Not.Null);
            Assert.That(layer.Children.Count, Is.EqualTo(1));
            Assert.That(layer.Children[0], Is.SameAs(mosaic));
            mosaic.BeginAnimation(NativeCoverMosaicVisual.ProgressProperty, null);
        }

        [Test]
        public void MosaicSupportsLiveVideoByRenderingOnlyTheAvailableStill()
        {
            var template = new Image { Stretch = Stretch.Uniform };
            var layer = new Grid();
            var videoToNative = NativeCoverMosaicVisual.Begin(layer, template, null, Source(),
                TimeSpan.FromMilliseconds(700));
            Assert.That(videoToNative, Is.Not.Null);

            var nativeToVideo = NativeCoverMosaicVisual.Begin(new Grid(), template, Source(), null,
                TimeSpan.FromMilliseconds(700));
            Assert.That(nativeToVideo, Is.Not.Null);
            videoToNative.BeginAnimation(NativeCoverMosaicVisual.ProgressProperty, null);
            nativeToVideo.BeginAnimation(NativeCoverMosaicVisual.ProgressProperty, null);
        }
    }
}
