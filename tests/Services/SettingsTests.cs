using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    // Pins the settings defaults. These are the values a fresh install gets, so a
    // change here is a user-visible behavior change and should be deliberate.
    [TestFixture]
    public class SettingsTests
    {
        [Test]
        public void EnableRotation_DefaultsToTrue()
        {
            var s = new ImageRotaterSettings();
            Assert.IsTrue(s.EnableRotation);
        }

        [Test]
        public void EnableDebugLogging_DefaultsToFalse()
        {
            var s = new ImageRotaterSettings();
            Assert.IsFalse(s.EnableDebugLogging);
        }

        [Test]
        public void OptimiseDownloadedImages_DefaultsToFalse()
        {
            var s = new ImageRotaterSettings();
            Assert.IsFalse(s.OptimiseDownloadedImages);
        }

        [Test]
        public void Settings_RoundTripThroughProperties()
        {
            var s = new ImageRotaterSettings
            {
                EnableRotation = false,
                EnableDebugLogging = true
            };

            Assert.IsFalse(s.EnableRotation);
            Assert.IsTrue(s.EnableDebugLogging);
        }
    }
}
