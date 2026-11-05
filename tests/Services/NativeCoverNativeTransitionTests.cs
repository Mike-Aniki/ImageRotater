using System;
using System.Threading;
using System.Windows.Controls;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeCoverNativeTransitionTests
    {
        // The names intentionally match Playnite's Desktop overview control
        // types. Fullscreen GameDetails and ordinary grid tiles must not match.
        private sealed class GridViewGameOverview : Grid { }
        private sealed class DetailsViewGameOverview : Grid { }
        private sealed class GameDetails : Grid { }

        [Test]
        public void ObservesOnlyDesktopOverviewControlsNotGridTilesOrFullscreenDetails()
        {
            var gridOverview = new GridViewGameOverview();
            var gridOverviewCover = new Image();
            gridOverview.Children.Add(gridOverviewCover);
            Assert.That(NativeCoverHost.IsDesktopOverviewRegion(gridOverviewCover), Is.True);

            var listOverview = new DetailsViewGameOverview();
            var listOverviewCover = new Image();
            listOverview.Children.Add(listOverviewCover);
            Assert.That(NativeCoverHost.IsDesktopOverviewRegion(listOverviewCover), Is.True);

            var libraryTile = new Grid();
            var libraryTileCover = new Image();
            libraryTile.Children.Add(libraryTileCover);
            Assert.That(NativeCoverHost.IsDesktopOverviewRegion(libraryTileCover), Is.False);

            var fullscreen = new GameDetails();
            var fullscreenCover = new Image();
            fullscreen.Children.Add(fullscreenCover);
            Assert.That(NativeCoverHost.IsDesktopOverviewRegion(fullscreenCover), Is.False);
        }

        [TestCase(true, true, TransitionStyle.Crossfade, true)]
        [TestCase(true, true, TransitionStyle.DepthShift, true)]
        [TestCase(true, true, TransitionStyle.Pixelate, true)]
        [TestCase(true, false, TransitionStyle.Crossfade, false)]
        [TestCase(false, true, TransitionStyle.Crossfade, false)]
        [TestCase(true, true, TransitionStyle.Cut, false)]
        public void NativeTransitionOnlyRunsForEnabledDesktopOverview(bool desktop, bool enabled,
            TransitionStyle style, bool expected)
        {
            Assert.That(NativeCoverHost.ShouldTrackNativeOverview(desktop, enabled, style), Is.EqualTo(expected));
        }

        [Test]
        public void NativePixelateIsSupportedWithoutChangingOtherStyles()
        {
            Assert.That(NativeCoverNativeTransition.EffectiveStyle(TransitionStyle.Pixelate),
                Is.EqualTo(TransitionStyle.Pixelate));
            Assert.That(NativeCoverNativeTransition.EffectiveStyle(TransitionStyle.DepthShift),
                Is.EqualTo(TransitionStyle.DepthShift));
        }
    }
}
