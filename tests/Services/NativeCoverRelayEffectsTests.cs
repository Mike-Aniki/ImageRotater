using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public sealed class NativeCoverRelayEffectsTests
    {
        private static ImageSource Source(Brush brush) =>
            new DrawingImage(new GeometryDrawing(brush, null,
                new RectangleGeometry(new Rect(0, 0, 80, 120))));

        private static Image Frame(Brush brush)
        {
            return new Image { Source = Source(brush), Stretch = Stretch.UniformToFill };
        }

        [Test]
        public void EachEffectIsHandledForNativeEntryReturnAndNativeNative()
        {
            foreach (TransitionStyle style in Enum.GetValues(typeof(TransitionStyle)))
            {
                Assert.That(NativeCoverEntryTransition.EffectiveStyle(style), Is.EqualTo(style));
                Assert.That(NativeCoverReturnTransition.EffectiveStyle(style), Is.EqualTo(style));
                Assert.That(NativeCoverNativeTransition.EffectiveStyle(style), Is.EqualTo(style));

                var both = new Grid();
                var oldImage = Frame(Brushes.Red);
                var newImage = Frame(Brushes.Blue);
                both.Children.Add(oldImage);
                both.Children.Add(newImage);
                Assert.DoesNotThrow(() => NativeCoverRelayEffects.Apply(both, style,
                    oldImage, newImage, 120, 180, Transition.DurationFor(style)));

                var entry = new Grid();
                var stillToVideo = Frame(Brushes.Red);
                entry.Children.Add(stillToVideo);
                Assert.DoesNotThrow(() => NativeCoverRelayEffects.Apply(entry, style,
                    stillToVideo, null, 120, 180, Transition.DurationFor(style)));

                var returning = new Grid();
                var videoToStill = Frame(Brushes.Blue);
                returning.Children.Add(videoToStill);
                Assert.DoesNotThrow(() => NativeCoverRelayEffects.Apply(returning, style,
                    null, videoToStill, 120, 180, Transition.DurationFor(style)));
            }
        }

        [Test]
        public void CrossfadeHasFullOpaqueIncomingBaseAndOutgoingTop()
        {
            var layer = new Grid();
            var outgoing = Frame(Brushes.Red);
            var incoming = Frame(Brushes.Blue);
            layer.Children.Add(incoming);
            layer.Children.Add(outgoing);
            NativeCoverRelayEffects.Apply(layer, TransitionStyle.Crossfade, outgoing, incoming,
                120, 180, TimeSpan.FromMilliseconds(400));
            Assert.That(Panel.GetZIndex(outgoing), Is.GreaterThan(Panel.GetZIndex(incoming)));
            Assert.That(incoming.Opacity, Is.EqualTo(1));
        }

        [Test]
        public void RevealKeepsOutgoingOpaqueUnderFeatheredIncoming()
        {
            foreach (var effect in new[] { TransitionStyle.SideReveal, TransitionStyle.DiagonalReveal })
            {
                var layer = new Grid();
                var outgoing = Frame(Brushes.Red);
                var incoming = Frame(Brushes.Blue);
                layer.Children.Add(outgoing);
                layer.Children.Add(incoming);
                NativeCoverRelayEffects.Apply(layer, effect, outgoing, incoming,
                    120, 180, TimeSpan.FromMilliseconds(650));
                var mask = incoming.OpacityMask as LinearGradientBrush;
                Assert.That(mask, Is.Not.Null, effect.ToString());
                Assert.That(mask.MappingMode, Is.EqualTo(BrushMappingMode.Absolute));
                Assert.That(mask.GradientStops[0].Color, Is.EqualTo(Colors.Transparent));
                Assert.That(mask.GradientStops[1].Color, Is.EqualTo(Colors.White));
                Assert.That(incoming.Clip, Is.Null, "No hard-edged clipping on stills");
                Assert.That(Panel.GetZIndex(incoming), Is.GreaterThan(Panel.GetZIndex(outgoing)));
                Assert.That(outgoing.Opacity, Is.EqualTo(1));
            }
        }

        [Test]
        public void RevealOfLiveVideoUsesInverseFeatherMaskOnOutgoingStill()
        {
            foreach (var effect in new[] { TransitionStyle.SideReveal, TransitionStyle.DiagonalReveal })
            {
                var layer = new Grid();
                var outgoing = Frame(Brushes.Red);
                layer.Children.Add(outgoing);
                NativeCoverRelayEffects.Apply(layer, effect, outgoing, null,
                    120, 180, TimeSpan.FromMilliseconds(650));
                var mask = outgoing.OpacityMask as LinearGradientBrush;
                Assert.That(mask, Is.Not.Null, effect.ToString());
                Assert.That(mask.GradientStops[0].Color, Is.EqualTo(Colors.White));
                Assert.That(mask.GradientStops[1].Color, Is.EqualTo(Colors.Transparent));
            }
        }

        [Test]
        public void MosaicHidesBothFullImagesUnderSingleCompositedVisual()
        {
            var layer = new Grid();
            var outgoing = Frame(Brushes.Red);
            var incoming = Frame(Brushes.Blue);
            layer.Children.Add(outgoing);
            layer.Children.Add(incoming);
            NativeCoverRelayEffects.Apply(layer, TransitionStyle.Mosaic, outgoing, incoming,
                120, 180, TimeSpan.FromMilliseconds(700));
            Assert.That(outgoing.Opacity, Is.EqualTo(0));
            Assert.That(incoming.Opacity, Is.EqualTo(0));
            Assert.That(layer.Children.OfType<NativeCoverMosaicVisual>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void SlideAddsAnOpaqueBaseBeforeTranslatingIncoming()
        {
            var layer = new Grid();
            var outgoing = Frame(Brushes.Red);
            var incoming = Frame(Brushes.Blue);
            layer.Children.Add(outgoing);
            layer.Children.Add(incoming);
            NativeCoverRelayEffects.Apply(layer, TransitionStyle.SlideFromRight, outgoing, incoming,
                120, 180, TimeSpan.FromMilliseconds(800));
            Assert.That(layer.Children.OfType<Image>().Count(), Is.EqualTo(3));
            Assert.That(Panel.GetZIndex(outgoing), Is.GreaterThan(Panel.GetZIndex(incoming)));
        }
    }
}
