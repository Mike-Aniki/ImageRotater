using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageRotater.Controls;
using ImageRotater.Services;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class MinimizedArtworkTests
    {
        private sealed class EmptySource : IBackgroundImageSource
        {
            public IReadOnlyList<string> GetImagePaths(Game game) => new string[0];
        }

        private static BitmapSource Image()
        {
            var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[256], 32);
            bitmap.Freeze();
            return bitmap;
        }

        [Test]
        public void MinimizingReleasesBothBackgroundLayersEvenBeforeInitialLayout()
        {
            var loader = new ImageLoader(new ImageCache(4096));
            var control = new BackgroundImageControl(new EmptySource(),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()), loader,
                () => new ImageRotaterSettings());
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            try
            {
                var a = (Image)control.FindName("SlotAImage");
                var b = (Image)control.FindName("SlotBImage");
                a.Source = b.Source = Image();
                loader.Suspend();
                BackgroundImageControl.NotifyPlaybackPolicyChanged();
                Assert.That(a.Source, Is.Null);
                Assert.That(b.Source, Is.Null);
            }
            finally { control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        }

        [Test]
        public void MinimizingReleasesTheDisplayedLogo()
        {
            var loader = new ImageLoader(new ImageCache(4096));
            var control = new LogoImageControl(new EmptySource(),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()), loader,
                () => new ImageRotaterSettings());
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            try
            {
                var image = (Image)control.FindName("DisplayImage");
                image.Source = Image();
                loader.Suspend();
                LogoImageControl.NotifySettingsChanged();
                Assert.That(image.Source, Is.Null);
            }
            finally { control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        }
    }
}
