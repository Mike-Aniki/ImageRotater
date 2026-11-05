using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public sealed class CoverRevealMaskTests
    {
        // Simulates WPF's Pad gradient interpolation at an arbitrary point.
        // This covers portrait, landscape and tiny thumbnails without a render host.
        private static double Alpha(LinearGradientBrush mask, Point at)
        {
            var a = mask.StartPoint;
            var b = mask.EndPoint;
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double value = ((at.X - a.X) * dx + (at.Y - a.Y) * dy) / (dx * dx + dy * dy);
            value = Math.Max(0.0, Math.Min(1.0, value));
            return mask.GradientStops[0].Color.A / 255.0 * (1 - value) +
                   mask.GradientStops[1].Color.A / 255.0 * value;
        }

        [TestCase(80, 120)]
        [TestCase(240, 135)]
        [TestCase(900, 900)]
        [TestCase(20, 30)]
        public void BothRevealsAreFullyHiddenAtStartAndFullyVisibleAtEnd(
            double width, double height)
        {
            var bounds = new Rect(17, 29, width, height);
            foreach (var style in new[] { TransitionStyle.SideReveal, TransitionStyle.DiagonalReveal })
            {
                foreach (bool incoming in new[] { true, false })
                {
                    var beginning = CoverRevealMask.Create(style, bounds, 0.0, incoming);
                    var ending = CoverRevealMask.Create(style, bounds, 1.0, incoming);
                    foreach (var pt in new[] {
                        bounds.TopLeft, bounds.TopRight, bounds.BottomLeft,
                        bounds.BottomRight, new Point(bounds.Left + width / 2, bounds.Top + height / 2) })
                    {
                        Assert.That(Alpha(beginning, pt), Is.EqualTo(incoming ? 0 : 1).Within(1e-9),
                            $"{style} {width}x{height} start incoming={incoming} at {pt}");
                        Assert.That(Alpha(ending, pt), Is.EqualTo(incoming ? 1 : 0).Within(1e-9),
                            $"{style} {width}x{height} end incoming={incoming} at {pt}");
                    }
                }
            }
        }

        [Test]
        public void MidwayShowsRealFeatherAndNeverReversesDirection()
        {
            var bounds = new Rect(0, 0, 100, 160);
            foreach (var style in new[] { TransitionStyle.SideReveal, TransitionStyle.DiagonalReveal })
            {
                double previous = double.PositiveInfinity;
                for (int i = 0; i <= 20; i++)
                {
                var edge = CoverRevealMask.At(style, bounds, i / 20.0);
                // Side moves towards the left; diagonal starts near the
                // top-right and progresses towards the bottom-left.
                    Assert.That(edge.Start.X, Is.LessThanOrEqualTo(previous + 1e-9));
                    previous = edge.Start.X;
                }
                var mid = CoverRevealMask.Create(style, bounds, .5, true);
                var edgeMid = CoverRevealMask.At(style, bounds, .5);
                var halfway = new Point((edgeMid.Start.X + edgeMid.End.X) / 2,
                                        (edgeMid.Start.Y + edgeMid.End.Y) / 2);
                Assert.That(Alpha(mid, halfway), Is.EqualTo(.5).Within(1e-9),
                    style + " must blend within the advancing edge, not cut");
            }
        }

        [Test]
        public void DiagonalRevealRunsFromTopRightToBottomLeft()
        {
            var bounds = new Rect(0, 0, 120, 180);

            var start = CoverRevealMask.Create(TransitionStyle.DiagonalReveal, bounds, 0.0, true);
            var mid = CoverRevealMask.Create(TransitionStyle.DiagonalReveal, bounds, 0.5, true);
            var end = CoverRevealMask.Create(TransitionStyle.DiagonalReveal, bounds, 1.0, true);

            Assert.That(Alpha(start, bounds.TopRight), Is.EqualTo(0.0).Within(1e-9));
            Assert.That(Alpha(mid, bounds.TopRight), Is.GreaterThan(0.5));
            Assert.That(Alpha(mid, bounds.BottomLeft), Is.LessThan(0.5));
            Assert.That(Alpha(end, bounds.BottomLeft), Is.EqualTo(1.0).Within(1e-9));
        }
    }
}
