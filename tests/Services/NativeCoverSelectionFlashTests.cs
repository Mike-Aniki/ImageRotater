using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using ImageRotater.Controls;
using ImageRotater.Services;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class NativeCoverSelectionFlashTests
    {
        private sealed class Source : IBackgroundImageSource
        {
            internal string Path;
            public IReadOnlyList<string> GetImagePaths(Game game) => Path == null ? new string[0] : new[] { Path };
        }
        private sealed class NativeSource
        {
            internal int Reads;
            internal ImageSource Frame;
            public ImageSource Image { get { Reads++; return Frame; } }
        }
        private static BitmapSource Bitmap() => BitmapSource.Create(32, 48, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 48 * 4], 32 * 4);
        private static CoverImageControl Renderer(Source source) => new CoverImageControl(source,
            new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
            () => new ImageRotaterSettings { RotateCovers = true, ShowUnfocusedCovers = false, AnimateUnfocusedCovers = false }, null, true);

        private static void Pump(Func<bool> predicate)
        {
            var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (s, e) => { if (predicate() || watch.ElapsedMilliseconds > 3000) frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            Assert.That(predicate(), Is.True);
        }

        [TestCase(false), TestCase(true)]
        public void MovingInheritedNativeCoverMustNotRestartItsAsyncPriorityBinding(bool borderParent)
        {
            var source = new NativeSource { Frame = Bitmap() };
            var placeholder = Bitmap();
            var native = new Image { Width = 160, Height = 240 };
            var priority = new PriorityBinding();
            priority.Bindings.Add(new Binding("Image") { IsAsync = true });
            priority.Bindings.Add(new Binding { Source = placeholder });
            BindingOperations.SetBinding(native, Image.SourceProperty, priority);
            FrameworkElement root;
            if (borderParent) root = new Border { DataContext = source, Child = native };
            else
            {
                var stack = new StackPanel { DataContext = source }; stack.Children.Add(native); root = stack;
            }
            using (var presentation = new HwndSource(new HwndSourceParameters("IR inherited cover context regression")
            { Width = 200, Height = 300, PositionX = -32000, PositionY = -32000, WindowStyle = 0x10000000, ExtendedWindowStyle = 0x08000080 }))
            {
                presentation.RootVisual = root;
                root.Measure(new Size(200, 300)); root.Arrange(new Rect(0, 0, 200, 300)); root.UpdateLayout();
                Pump(() => ReferenceEquals(native.Source, source.Frame));
                int gaps = 0;
                native.DataContextChanged += (s, e) => { if (e.NewValue == null) gaps++; };
                var expression = BindingOperations.GetBindingExpressionBase(native, Image.SourceProperty);
                int initialReads = source.Reads;
                var attachment = NativeCoverHost.Attachment.TryCreate(native, Renderer(new Source()), new Game(), () => { });
                try
                {
                    root.UpdateLayout();
                    Pump(() => attachment.Renderer.IsLoaded);
                    TestContext.WriteLine($"context-gaps={gaps} showing-placeholder={ReferenceEquals(native.Source, placeholder)}");
                    Assert.Multiple(() =>
                    {
                        Assert.That(gaps, Is.Zero, "Wrapping must not temporarily clear inherited context");
                        Assert.That(native.Source, Is.SameAs(source.Frame), "Do not fall back to the platform cover while the async binding restarts");
                    });
                }
                finally { attachment.Dispose(); }
                root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Assert.That(gaps, Is.Zero, "Unwrapping must preserve context too");
                Assert.That(native.Source, Is.SameAs(source.Frame));
                Assert.That(source.Reads, Is.EqualTo(initialReads), "No reload may be introduced by the real WPF load/unload cycle");
                Assert.That(BindingOperations.GetBindingExpressionBase(native, Image.SourceProperty), Is.SameAs(expression));
                Assert.That(native.ReadLocalValue(FrameworkElement.DataContextProperty), Is.SameAs(DependencyProperty.UnsetValue));
                var next = new NativeSource { Frame = Bitmap() };
                root.DataContext = next;
                Assert.That(native.DataContext, Is.SameAs(next), "Context must still follow the theme after removing IR");
            }
            GC.KeepAlive(source);
        }

        [Test]
        public void SelectedCoverKeepsNativeWhileLoadingAndDoesNotReloadItWhenSelectionIsLost()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IR_coverflash_" + Guid.NewGuid() + ".png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap()));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var nativeSource = new NativeSource { Frame = Bitmap() };
            var native = new Image();
            native.SetBinding(Image.SourceProperty, new Binding("Image") { Source = nativeSource });
            var expression = BindingOperations.GetBindingExpression(native, Image.SourceProperty);
            int reads = nativeSource.Reads;
            var root = new Grid(); root.Children.Add(native);
            var game = new Game { Id = Guid.NewGuid() };
            CoverImageControl.NotifySelectionChanged(game.Id);
            var renderer = Renderer(new Source { Path = path });
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, game, () => { });
            var savedStyle = Transition.CoverStyle;
            try
            {
                Assert.That(renderer.IsCoverDisplayed, Is.False);
                Assert.That(native.Source, Is.SameAs(nativeSource.Frame));
                Assert.That(native.Visibility, Is.EqualTo(Visibility.Visible));
                var media = (Image)renderer.FindName("DisplayImage");
                media.Source = Bitmap(); media.Visibility = Visibility.Visible;
                renderer.NotifyNativeMediaReady(media);
                Assert.That(native.Visibility, Is.EqualTo(Visibility.Hidden));
                Assert.That(native.Source, Is.SameAs(nativeSource.Frame));
                Transition.CoverStyle = TransitionStyle.Cut;
                bool returned = false; attachment.ReturnToNative(() => returned = true);
                Assert.That(returned, Is.True);
                Assert.That(native.Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(BindingOperations.GetBindingExpression(native, Image.SourceProperty), Is.SameAs(expression));
                Assert.That(nativeSource.Reads, Is.EqualTo(reads), "Restoring selection must not restart native loading and its platform placeholder");
            }
            finally
            {
                attachment.Dispose(); Transition.CoverStyle = savedStyle;
                CoverImageControl.NotifySelectionChanged(Guid.Empty);
                try { File.Delete(path); } catch (IOException) { }
                GC.KeepAlive(nativeSource);
            }
        }

        [Test]
        public void VideoOpeningDoesNotHideNativeUntilRevealHasFinished()
        {
            var original = Bitmap(); var native = new Image { Source = original };
            var root = new Grid(); root.Children.Add(native);
            var renderer = Renderer(new Source());
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, new Game(), () => { });
            try
            {
                ((Image)renderer.FindName("DisplayImage")).Visibility = Visibility.Collapsed;
                var video = (MediaElement)renderer.FindName("DisplayVideo");
                video.Source = new Uri("file:///C:/IR_test_not_played.mp4");
                video.Visibility = Visibility.Visible; video.Opacity = 0;
                renderer.NotifyNativeMediaReady(video);
                Assert.That(native.Source, Is.SameAs(original));
                Assert.That(native.Visibility, Is.EqualTo(Visibility.Visible));
                video.Opacity = 1;
                typeof(CoverImageControl).GetMethod("CompleteVideoReveal", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(renderer, null);
                Assert.That(native.Visibility, Is.EqualTo(Visibility.Hidden));
                Assert.That(native.Source, Is.SameAs(original));
            }
            finally { attachment.Dispose(); }
        }
    }
}
