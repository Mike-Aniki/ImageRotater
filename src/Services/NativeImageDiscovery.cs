using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageRotater.Controls;

namespace ImageRotater.Services
{
    // Loaded uses direct routing. Observe Image at class level instead of
    // expecting child Loaded events to bubble to Window. Weak registrations
    // prevent this process-wide handler from retaining a host or old window.
    internal static class NativeImageDiscovery
    {
        private static readonly List<WeakReference> listeners = new List<WeakReference>();
        static NativeImageDiscovery()
        {
            EventManager.RegisterClassHandler(typeof(Image), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded), true);
        }
        internal static IDisposable Subscribe(Window window, Action<Image> handler)
        {
            var subscription = new Subscription(window, handler);
            listeners.Add(new WeakReference(subscription));
            return subscription;
        }
        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null) return;
            for (DependencyObject parent = VisualTreeHelper.GetParent(image); parent != null;
                parent = VisualTreeHelper.GetParent(parent))
                if (parent is BackgroundImageControl || parent is CoverImageControl || parent is LogoImageControl) return;
            Window window = Window.GetWindow(image);
            if (window == null) return;
            foreach (WeakReference reference in listeners.ToArray())
            {
                var subscription = reference.Target as Subscription;
                if (subscription == null || subscription.Disposed) { listeners.Remove(reference); continue; }
                if (window == subscription.Window.Target) subscription.Handler(image);
            }
        }
        private sealed class Subscription : IDisposable
        {
            internal readonly WeakReference Window;
            internal Action<Image> Handler;
            internal bool Disposed;
            internal Subscription(Window window, Action<Image> handler)
            { Window = new WeakReference(window); Handler = handler; }
            public void Dispose()
            {
                Disposed = true; Handler = null;
                listeners.RemoveAll(reference => !reference.IsAlive || ReferenceEquals(reference.Target, this));
            }
        }
    }
}
