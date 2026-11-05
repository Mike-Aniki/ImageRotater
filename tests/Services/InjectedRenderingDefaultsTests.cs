using System;
using Newtonsoft.Json;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class InjectedRenderingDefaultsTests
    {
        [Test]
        public void OldModeChoicesMigrateToAutomaticInjectionWithoutChangingContentChoices()
        {
            var settings = JsonConvert.DeserializeObject<ImageRotaterSettings>(
                "{\"UseThemeIntegration\":false,\"UseExperimentalFadeImage\":false,\"UseExperimentalNativeCovers\":false,\"UseExperimentalNativeLogos\":false,\"RotateBackgrounds\":false,\"RotateCovers\":true,\"RotateLogos\":true,\"EnableRotation\":false,\"EnableTransitions\":true}");
            Assert.That(settings.UseThemeIntegration && settings.UseExperimentalFadeImage &&
                settings.UseExperimentalNativeCovers && settings.UseExperimentalNativeLogos, Is.True);
            Assert.That(settings.RotateBackgrounds, Is.False);
            Assert.That(settings.RotateCovers && settings.RotateLogos, Is.True);
            Assert.That(settings.EnableRotation, Is.False);
            Assert.That(settings.EnableTransitions, Is.True);
            string saved = JsonConvert.SerializeObject(settings);
            Assert.That(saved, Does.Not.Contain("UseThemeIntegration").And.Not.Contain("UseExperimental"));
        }
        [Test]
        public void TransitionsOnlyRemainAvailableWithoutArtworkRotation()
        {
            var settings = new ImageRotaterSettings { EnableRotation = false, EnableTransitions = true };
            Assert.That(settings.ShouldRenderBackground(Guid.NewGuid()), Is.True);
            Assert.That(settings.CanConfigureCovers, Is.False);
            settings.EnableTransitions = false;
            Assert.That(settings.ShouldRenderBackground(Guid.NewGuid()), Is.False);
        }
    }
}
