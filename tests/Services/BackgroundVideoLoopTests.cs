using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using ImageRotater.Controls;
using ImageRotater.Services;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class BackgroundVideoLoopTests
    {
        private sealed class EmptySource : IBackgroundImageSource
        {
            public IReadOnlyList<string> GetImagePaths(Game game) => new string[0];
        }

        private static object GetField(object instance, string name) =>
            instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(instance);

        private static BackgroundImageControl NewControl() =>
            new BackgroundImageControl(new EmptySource(),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), () => new ImageRotaterSettings());

        [TestCase(5700)]
        [TestCase(6100)]
        [TestCase(15000)]
        public void RestartIsScheduledJustBeforeTheEnd(int durationMs)
        {
            var duration = TimeSpan.FromMilliseconds(durationMs);

            TimeSpan? fromStart = BackgroundImageControl.LoopRestartDelay(duration, TimeSpan.Zero);

            Assert.That(fromStart, Is.EqualTo(duration - BackgroundImageControl.LoopLead));
        }

        [Test]
        public void NearTheEndTheVideoRestartsNow()
        {
            var duration = TimeSpan.FromSeconds(5.7);

            Assert.That(BackgroundImageControl.LoopRestartDelay(
                duration, duration - BackgroundImageControl.LoopLead), Is.Null);
            Assert.That(BackgroundImageControl.LoopRestartDelay(duration, duration), Is.Null);
        }

        [Test]
        public void ALateCheckWaitsOnlyForWhatIsLeft()
        {
            var duration = TimeSpan.FromSeconds(15);
            var position = TimeSpan.FromSeconds(10);

            Assert.That(BackgroundImageControl.LoopRestartDelay(duration, position),
                Is.EqualTo(TimeSpan.FromSeconds(5) - BackgroundImageControl.LoopLead));
        }

        [Test]
        public void ClearingAVideoSlotStopsItsLoopTimer()
        {
            var control = NewControl();
            var slot = GetField(control, "_slotA");
            var type = slot.GetType();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Start();
            type.GetField("LoopTimer").SetValue(slot, timer);
            type.GetField("LoopGeneration").SetValue(slot, 5);

            typeof(BackgroundImageControl).GetMethod("ClearSlot",
                BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(control, new[] { slot });

            Assert.That(timer.IsEnabled, Is.False);
            Assert.That(type.GetField("LoopTimer").GetValue(slot), Is.Null);
            Assert.That(type.GetField("LoopGeneration").GetValue(slot), Is.EqualTo(6));
        }

        [Test]
        public void NoLoopPosterLayerRemains()
        {
            var control = NewControl();

            Assert.That(control.FindName("SlotALoopFrame"), Is.Null);
            Assert.That(control.FindName("SlotBLoopFrame"), Is.Null);
        }
    }
}
