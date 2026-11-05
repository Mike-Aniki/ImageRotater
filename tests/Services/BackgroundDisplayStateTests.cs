using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageRotater.Controls;
using ImageRotater.Services;
using Newtonsoft.Json;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class BackgroundDisplayStateTests
    {
        private sealed class EmptySource : IBackgroundImageSource
        {
            public IReadOnlyList<string> GetImagePaths(Game game) => new string[0];
        }

        private static object Field(object target, string name) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void Invoke(object target, string name, params object[] args) => target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);

        private static BackgroundImageControl Create(Func<ImageRotaterSettings> settings)
        {
            var control = new BackgroundImageControl(new EmptySource(),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), settings);
            control.GameContext = new Game { Id = Guid.NewGuid(), Name = "Test" };
            return control;
        }

        private static void Display(BackgroundImageControl control, string slotName = "_slotA")
        {
            object slot = Field(control, slotName);
            Type type = slot.GetType();
            type.GetField("GameId").SetValue(slot, control.GameContext.Id);
            type.GetField("RequestToken").SetValue(slot, Field(control, "_requestToken"));
            type.GetField("Kind").SetValue(slot, Enum.Parse(type.GetField("Kind").FieldType, "Still"));
            var image = (Image)type.GetProperty("Image").GetValue(slot);
            image.Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            Invoke(control, "BeginTransition", slot);
        }

        [Test]
        public void InterruptedTransitionKeepsVisibleIncomingGameInsteadOfResurrectingOutgoingGame()
        {
            var control = Create(() => new ImageRotaterSettings());
            try
            {
                Display(control);
                object outgoing = Field(control, "_slotA");
                object incoming = Field(control, "_slotB");
                Type slotType = incoming.GetType();
                slotType.GetField("Kind").SetValue(incoming,
                    Enum.Parse(slotType.GetField("Kind").FieldType, "Still"));
                slotType.GetField("Ready").SetValue(incoming, true);
                var container = (FrameworkElement)slotType.GetProperty("Container").GetValue(incoming);
                container.Visibility = Visibility.Visible;
                container.Opacity = 0.7;
                control.GetType().GetField("_pendingSlot", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(control, incoming);

                // Selecting C interrupts the A -> B transition before C is loaded.
                control.GameContext = new Game { Id = Guid.NewGuid(), Name = "C" };

                Assert.That(Field(control, "_activeSlot"), Is.SameAs(incoming));
                Assert.That(Field(control, "_pendingSlot"), Is.Null);
                Assert.That(container.Opacity, Is.EqualTo(1));
                Assert.That(slotType.GetField("Kind").GetValue(outgoing).ToString(), Is.EqualTo("None"));
            }
            finally { control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        }

        [Test]
        public void ExistingThemeBackgroundCanRenderWhenNativePartIsUnavailable()
        {
            var settings = new ImageRotaterSettings
            {
                UseExperimentalFadeImage = true, UseThemeBackgroundFallback = true
            };
            var control = Create(() => settings);
            try
            {
                Display(control);
                Invoke(control, "Refresh");
                Assert.That(((Image)control.FindName("SlotAImage")).Source, Is.Not.Null);
                settings.UseThemeBackgroundFallback = false;
                Invoke(control, "Refresh");
                Assert.That(((Image)control.FindName("SlotAImage")).Source, Is.Null);
            }
            finally { control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        }

        [Test]
        public void StateTracksDisplayedMediaAndUnload()
        {
            var settings = new ImageRotaterSettings();
            var control = Create(() => settings);
            Assert.That(control.IsBackgroundDisplayed, Is.False);
            Display(control);
            Assert.That(control.IsBackgroundDisplayed, Is.True);
            Assert.That(settings.IsBackgroundDisplayed, Is.True);
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.That(control.IsBackgroundDisplayed, Is.False);
            Assert.That(settings.IsBackgroundDisplayed, Is.False);
        }

        [TestCase("EnableRotation")]
        [TestCase("RotateBackgrounds")]
        public void DisablingRenderingClearsBothSlotsImmediately(string option)
        {
            var settings = new ImageRotaterSettings();
            var control = Create(() => settings);
            Display(control);
            Display(control, "_slotB");
            typeof(ImageRotaterSettings).GetProperty(option).SetValue(settings, false);
            Invoke(control, "Refresh");
            Assert.That(control.IsBackgroundDisplayed, Is.False);
            Assert.That(settings.IsBackgroundDisplayed, Is.False);
            Assert.That(((Image)control.FindName("SlotAImage")).Source, Is.Null);
            Assert.That(((Image)control.FindName("SlotBImage")).Source, Is.Null);
            Assert.That(Field(control, "_activeSlot"), Is.Null);
            Assert.That(Field(control, "_pendingSlot"), Is.Null);
        }

        [Test]
        public void DisabledControlRejectsAnOldPendingTransition()
        {
            var settings = new ImageRotaterSettings();
            var control = Create(() => settings);
            Display(control);
            object oldSlot = Field(control, "_slotA");
            settings.EnableRotation = false;
            Invoke(control, "Refresh");
            Invoke(control, "BeginTransition", oldSlot);
            Assert.That(control.IsBackgroundDisplayed, Is.False);
            Assert.That(Field(control, "_activeSlot"), Is.Null);
        }

        [Test]
        public void ClearingOneTransitionLayerKeepsTheOtherDisplayed()
        {
            var settings = new ImageRotaterSettings();
            var control = Create(() => settings);
            Display(control);
            Display(control, "_slotB");
            Invoke(control, "ClearSlot", Field(control, "_slotA"));
            Assert.That(control.IsBackgroundDisplayed, Is.True);
            Invoke(control, "ClearSlot", Field(control, "_slotB"));
            Assert.That(settings.IsBackgroundDisplayed, Is.False);
        }

        [Test]
        public void UnloadingOneViewDoesNotResetAnotherAndStateIsNotSerialized()
        {
            var settings = new ImageRotaterSettings();
            var a = Create(() => settings);
            var b = Create(() => settings);
            Display(a);
            Display(b);
            a.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.That(settings.IsBackgroundDisplayed, Is.True);
            Assert.That(JsonConvert.SerializeObject(settings), Does.Not.Contain("IsBackgroundDisplayed"));
            b.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.That(settings.IsBackgroundDisplayed, Is.False);
        }

        [Test]
        public void ReplacingSettingsTransfersRuntimeState()
        {
            var oldSettings = new ImageRotaterSettings();
            var settings = oldSettings;
            var control = Create(() => settings);
            Display(control);
            settings = new ImageRotaterSettings();
            Invoke(control, "PublishBackgroundState");
            Assert.That(oldSettings.IsBackgroundDisplayed, Is.False);
            Assert.That(settings.IsBackgroundDisplayed, Is.True);
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }
}
