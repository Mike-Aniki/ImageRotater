using System;
using System.Threading;
using Playnite.SDK.Models;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeCoverDetailsTransitionTests
    {
        private sealed class IdContext { public Guid Id { get; set; } }
        private sealed class GameContext { public object Game { get; set; } }

        [Test]
        public void ResolvesDetailsViewModelIdWithoutDatabaseLookup()
        {
            Guid id = Guid.NewGuid();
            Assert.That(NativeCoverHost.ResolveContextGameId(new IdContext { Id = id }), Is.EqualTo(id));
            Assert.That(NativeCoverHost.ResolveContextGameId(new GameContext
            {
                Game = new GameContext { Game = new Game { Id = id } }
            }), Is.EqualTo(id));
            Assert.That(NativeCoverHost.ResolveContextGameId(null), Is.EqualTo(Guid.Empty));
        }
        [TestCase(true,  true,  true,  false, true)]
        [TestCase(true,  false, true,  false, true)]
        [TestCase(false, true,  true,  false, false)]
        [TestCase(false, false, true,  false, true)]
        [TestCase(true,  false, false, false, false)]
        [TestCase(true,  false, true,  true,  false)]
        public void DetailsUsesNativeEntryRegardlessOfAllVisibleCoverOptionButNotAtStartup(
            bool details, bool showUnfocused, bool navigated, bool firstPaint, bool expected)
        {
            Assert.That(NativeCoverHost.ShouldAnimateEntry(
                details, showUnfocused, navigated, firstPaint), Is.EqualTo(expected));
        }

        [TestCase(true, true, false, true, true, false, true)]
        [TestCase(true, true, true,  true, true, false, false)]
        [TestCase(true, false,false, true, true, false, false)]
        [TestCase(true, true, false, false,true, false, false)]
        [TestCase(false,true, false, true, true, false, false)]
        [TestCase(true, true, false, true, false,false, false)]
        [TestCase(true, true, false, true, true, true,  false)]
        public void DetailsReturnOnlyWhenChangingFromVisibleIrToNativeAfterFirstPaint(
            bool details, bool gameChanged, bool newHasIrArtwork, bool oldIrVisible,
            bool navigated, bool firstPaint, bool expected)
        {
            Assert.That(NativeCoverHost.ShouldReturnDetailsToNative(
                details, gameChanged, newHasIrArtwork, oldIrVisible,
                navigated, firstPaint), Is.EqualTo(expected));
        }

        [Test]
        public void BothDirectionsPreservePixelateAndDepthShift()
        {
            Assert.That(NativeCoverEntryTransition.EffectiveStyle(TransitionStyle.Pixelate),
                Is.EqualTo(TransitionStyle.Pixelate));
            Assert.That(NativeCoverReturnTransition.EffectiveStyle(TransitionStyle.Pixelate),
                Is.EqualTo(TransitionStyle.Pixelate));
            Assert.That(NativeCoverEntryTransition.EffectiveStyle(TransitionStyle.DepthShift),
                Is.EqualTo(TransitionStyle.DepthShift));
            Assert.That(NativeCoverReturnTransition.EffectiveStyle(TransitionStyle.DepthShift),
                Is.EqualTo(TransitionStyle.DepthShift));
        }
    }
}
