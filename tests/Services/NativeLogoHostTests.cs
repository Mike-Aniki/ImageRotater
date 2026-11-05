using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageRotater.Controls;
using ImageRotater.Services;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeLogoHostTests
    {
        private sealed class EmptySource : IBackgroundImageSource
        { public IReadOnlyList<string> GetImagePaths(Game game) => new string[0]; }
        private sealed class Context : DependencyObject
        {
            public static readonly DependencyProperty ArtProperty = DependencyProperty.Register("Art", typeof(ImageSource), typeof(Context));
            public ImageSource Art { get => (ImageSource)GetValue(ArtProperty); set => SetValue(ArtProperty, value); }
        }
        private static BitmapSource Bitmap() => BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        private static void SetDisplayedGame(LogoImageControl renderer, Guid id) =>
            typeof(LogoImageControl).GetProperty("DisplayedGameId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(renderer, id);

        [TestCase(false), TestCase(true)]
        public void LogoDiagnosticsUsePluginLogOnlyWhenDebugEnabled(bool enabled)
        {
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IR-logo-debug-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(folder);
            var logger = new FileLogger(folder, () => enabled);
            var settings = new ImageRotaterSettings { RotateLogos = true };
            var image = new Image { Name = "TestLogo", Source = Bitmap() };
            var parent = new Grid { CacheMode = new BitmapCache() };
            parent.Children.Add(image);
            var renderer = new LogoImageControl(new EmptySource(), new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), () => settings, nativeInjected: true, fileLogger: logger);
            try
            {
                using (var attachment = new NativeLogoHost.Attachment(image, renderer))
                {
                    attachment.Update(new Game { Id = Guid.NewGuid() });
                    ((Image)renderer.FindName("DisplayImage")).Source = Bitmap();
                    image.Source = Bitmap();
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
                    Dispatcher.PushFrame(frame);
                }
                Assert.That(System.IO.File.Exists(logger.Path_), Is.EqualTo(enabled));
                if (enabled)
                {
                    string log = System.IO.File.ReadAllText(logger.Path_);
                    foreach (string action in new[] { "native-attach", "native-context", "refresh", "no-artwork",
                        "native-output", "native-source-changed", "native-hide-before-replace", "native-visibility",
                        "native-replace-dispatched", "native-detach" })
                        Assert.That(log, Does.Contain("LOGO " + action));
                    Assert.That(log, Does.Contain("cache=BitmapCache"));
                    Assert.That(log, Does.Contain("renderer="));
                    Assert.That(log, Does.Contain("image="));
                    // The debug toggle also takes effect without recreating the logger.
                    enabled = false;
                    renderer.TraceLogo("must-not-appear");
                    Assert.That(System.IO.File.ReadAllText(logger.Path_), Does.Not.Contain("must-not-appear"));
                }
                else
                {
                    enabled = true;
                    renderer.TraceLogo("debug-enabled-later");
                    Assert.That(System.IO.File.ReadAllText(logger.Path_), Does.Contain("LOGO debug-enabled-later"));
                }
            }
            finally { System.IO.Directory.Delete(folder, true); }
        }

        [Test]
        public void DetectsOnlyPerGameExtraMetadataLogoPaths()
        {
            Guid id = Guid.NewGuid(), parsed;
            Assert.That(NativeLogoHost.TryLogoGame(@"C:\Playnite\ExtraMetadata\games\" + id + @"\logo.png", out parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(id));
            Assert.That(NativeLogoHost.TryLogoGame("file:///C:/Playnite/ExtraMetadata/games/" + id + "/Logo.png", out parsed), Is.True);
            foreach (string path in new[] { @"C:\other\logo.png", @"C:\ExtraMetadata\games\bad\logo.png", @"C:\ExtraMetadata\games\" + id + @"\cover.png" })
                Assert.That(NativeLogoHost.TryLogoGame(path, out parsed), Is.False);
        }
        [Test]
        public void DirectPathBindingIsDetectedEvenWithoutADecodedLogo()
        {
            var grid = new Grid(); NameScope.SetNameScope(grid, new NameScope());
            var path = new TextBlock { Text = @"C:\Playnite\ExtraMetadata\games\" + Guid.NewGuid() + @"\logo.png" };
            grid.RegisterName("WhateverPath", path);
            var image = new Image(); grid.Children.Add(image);
            BindingOperations.SetBinding(image, Image.SourceProperty, new Binding("Text") { ElementName = "WhateverPath" });
            Assert.That(NativeLogoHost.LogoPath(image), Is.EqualTo(path.Text));
        }
        [Test]
        public void PendingLogoKeepsNativeSourceVisibleAndRestoresItIfLoadingFails()
        {
            var settings = new ImageRotaterSettings { UseExperimentalNativeLogos = true, RotateLogos = true };
            var image = new Image { Source = Bitmap(), Visibility = Visibility.Visible };
            var renderer = new LogoImageControl(new EmptySource(), new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), () => settings, nativeInjected: true);
            var attachment = new NativeLogoHost.Attachment(image, renderer);
            var loading = typeof(LogoImageControl).GetMethod("SetNativeLoading", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                var initial = image.Source;
                loading.Invoke(renderer, new object[] { true });
                // A pending IR bitmap must not clear a perfectly good native
                // logo and expose a black theme background.
                Assert.That(image.Source, Is.SameAs(initial));
                var native = Bitmap(); image.Source = native;
                Assert.That(image.Visibility, Is.EqualTo(Visibility.Hidden));
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
                Dispatcher.PushFrame(frame);
                Assert.That(image.Source, Is.SameAs(native));
                Assert.That(image.Visibility, Is.EqualTo(Visibility.Visible));
                loading.Invoke(renderer, new object[] { false });
                Assert.That(image.Source, Is.SameAs(native));
            }
            finally { attachment.Dispose(); }
        }

        [Test]
        public void ReplacingLogoWithUnrelatedArtworkReleasesAdapterInsteadOfKeepingOldGame()
        {
            var settings = new ImageRotaterSettings { RotateLogos = true };
            var game = new Game { Id = Guid.NewGuid() };
            var image = new Image { DataContext = game, Source = Bitmap() };
            var renderer = new LogoImageControl(new EmptySource(), new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), () => settings, nativeInjected: true);
            var attachment = new NativeLogoHost.Attachment(image, renderer);
            attachment.Update(game);
            SetDisplayedGame(renderer, game.Id);
            ((Image)renderer.FindName("DisplayImage")).Source = Bitmap();
            using (var host = new NativeLogoHost(() => settings, () => renderer, id => game, () => null))
            {
                var attached = (Dictionary<Image, NativeLogoHost.Attachment>)typeof(NativeLogoHost)
                    .GetField("attached", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
                attached.Add(image, attachment);
                var unrelated = new DrawingImage(new GeometryDrawing(Brushes.Blue, null,
                    new RectangleGeometry(new Rect(0, 0, 20, 20))));
                image.Source = unrelated;
                typeof(NativeLogoHost).GetMethod("VisitImage", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(host, new object[] { image });
                Assert.That(attached, Is.Empty);
                Assert.That(image.Source, Is.SameAs(unrelated));
                Assert.That(image.Visibility, Is.EqualTo(Visibility.Visible));
            }
        }

        [Test]
        public void InjectionPreservesBindingAndRestoresLatestNativeImage()
        {
            var settings = new ImageRotaterSettings { UseExperimentalNativeLogos = true, RotateLogos = true };
            var context = new Context { Art = Bitmap() };
            var image = new Image { Width = 240, Stretch = Stretch.Uniform };
            var binding = new Binding("Art") { Source = context };
            BindingOperations.SetBinding(image, Image.SourceProperty, binding);
            var renderer = new LogoImageControl(new EmptySource(), new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), () => settings, nativeInjected: true);
            var attachment = new NativeLogoHost.Attachment(image, renderer);
            var output = (Image)renderer.FindName("DisplayImage");
            var ir = Bitmap();
            var game = new Game { Id = Guid.NewGuid() };
            renderer.GameContext = game;
            SetDisplayedGame(renderer, game.Id);
            output.Source = ir;
            Assert.That(image.Source, Is.SameAs(ir));
            var native = Bitmap(); context.Art = native;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
            Dispatcher.PushFrame(frame);
            Assert.That(image.Source, Is.SameAs(ir));
            attachment.Dispose();
            Assert.That(image.Source, Is.SameAs(native));
            Assert.That(BindingOperations.GetBinding(image, Image.SourceProperty), Is.SameAs(binding));
            Assert.That(image.Width, Is.EqualTo(240));
            GC.KeepAlive(context);
        }
        [Test]
        public void CachedLogoFromPreviousGameIsNotShownWhileNewGameIsLoading()
        {
            var settings = new ImageRotaterSettings { UseExperimentalNativeLogos = true, RotateLogos = true };
            var nativeA = Bitmap();
            var nativeB = Bitmap();
            var logoA = Bitmap();
            var logoB = Bitmap();
            var image = new Image { Source = nativeA, Visibility = Visibility.Visible };
            var renderer = new LogoImageControl(new EmptySource(),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), () => settings, nativeInjected: true);
            var gameA = new Game { Id = Guid.NewGuid() };
            var gameB = new Game { Id = Guid.NewGuid() };
            var output = (Image)renderer.FindName("DisplayImage");
            var loading = typeof(LogoImageControl).GetMethod("SetNativeLoading", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var attachment = new NativeLogoHost.Attachment(image, renderer))
            {
                renderer.GameContext = gameA;
                SetDisplayedGame(renderer, gameA.Id);
                output.Source = logoA;
                Assert.That(image.Source, Is.SameAs(logoA));

                // Playnite reuses its Image for a different game. The old IR
                // bitmap is still held by the async renderer, but must never
                // be painted onto the new game's native logo.
                image.Source = nativeB;
                renderer.GameContext = gameB;
                SetDisplayedGame(renderer, gameA.Id);
                output.Source = logoA;
                loading.Invoke(renderer, new object[] { true });
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
                Dispatcher.PushFrame(frame);
                Assert.That(image.Source, Is.SameAs(nativeB));

                SetDisplayedGame(renderer, gameB.Id);
                output.Source = logoB;
                loading.Invoke(renderer, new object[] { false });
                Assert.That(image.Source, Is.SameAs(logoB));
            }
        }

        [Test]
        public void NativeModeKeepsLogoSettingsAvailableAndSuppressesDuplicateThemeHost()
        {
            var settings = new ImageRotaterSettings { UseExperimentalNativeLogos = true, RotateLogos = true, HasDataLogo = true };
            Assert.That(settings.CanConfigureLogos, Is.True);
            Assert.That(settings.EnableLogoImage, Is.False);
            Assert.That(settings.HasDataLogo, Is.False);
            settings.UseExperimentalNativeLogos = false;
            Assert.That(settings.UseExperimentalNativeLogos, Is.True);
            Assert.That(settings.HasDataLogo, Is.False);
        }
    }
}
