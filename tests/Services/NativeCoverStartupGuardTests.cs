using System.Threading;
using System.Windows.Threading;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeCoverStartupGuardTests
    {
        [Test]
        public void FallbackDiscoveryCannotPreemptInitialRender()
        {
            Assert.That(NativeCoverHost.VisualScanPriority, Is.LessThan(DispatcherPriority.Render));
        }

        [TestCase(false, true, false)]
        [TestCase(false, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        public void EntryAnimationOnlyAfterStartupAndAUserNavigation(
            bool navigated, bool initialScan, bool expected)
        {
            Assert.That(NativeCoverHost.CanAnimateNativeEntry(navigated, initialScan), Is.EqualTo(expected));
        }
    }
}
