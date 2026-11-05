using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeImageDiscoveryTests
    {
        [Test]
        public void ObservesDirectImageLoadedOnlyForItsOwnWindowAndUnsubscribes()
        {
            var first = new Window();
            var second = new Window();
            var image = new Image(); first.Content = image;
            var other = new Image(); second.Content = other;
            int count = 0;
            var subscription = NativeImageDiscovery.Subscribe(first, value => { Assert.That(value, Is.SameAs(image)); count++; });
            image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            other.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.That(count, Is.EqualTo(1));
            subscription.Dispose();
            image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.That(count, Is.EqualTo(1));
        }
    }
}
