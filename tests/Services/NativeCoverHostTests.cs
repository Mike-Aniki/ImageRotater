using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageRotater.Controls;
using ImageRotater.Models;
using ImageRotater.Services;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeCoverHostTests
    {
        private sealed class EmptySource : IBackgroundImageSource
        {
            public IReadOnlyList<string> GetImagePaths(Game game) => new string[0];
        }
        private sealed class GameWrapper { public object Game { get; set; } }
        private sealed class IdWrapper { public Guid Id { get; set; } }
        private sealed class MutableSource : IBackgroundImageSource
        {
            public IReadOnlyList<string> Paths = new string[0];
            public IReadOnlyList<string> GetImagePaths(Game game) => Paths;
        }
        private sealed class ChangingContext : INotifyPropertyChanged
        {
            private Game game;
            public Game Game
            {
                get => game;
                set { game = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Game))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<string> temporaryImages = new List<string>();
        private MutableSource PendingSource()
        {
            string path = Path.Combine(Path.GetTempPath(), "IR_native_handoff_" + Guid.NewGuid() + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Bitmap(32, 48)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            temporaryImages.Add(path);
            return new MutableSource { Paths = new[] { path } };
        }
        [TearDown]
        public void CleanupImages()
        {
            foreach (string path in temporaryImages) { try { File.Delete(path); } catch (IOException) { } }
            temporaryImages.Clear();
        }

        private static BitmapSource Bitmap(int width = 16, int height = 24)
        {
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                new byte[width * height * 4], width * 4);
            result.Freeze();
            return result;
        }
        private static CoverImageControl Renderer(ImageRotaterSettings settings) =>
            new CoverImageControl(new EmptySource(), new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                () => settings, null, nativeInjected: true);

        private static void Ready(CoverImageControl renderer)
        {
            var displayed = (Image)renderer.FindName("DisplayImage");
            displayed.Source = Bitmap(32, 48);
            displayed.Visibility = Visibility.Visible;
            var key = (DependencyPropertyKey)typeof(CoverImageControl)
                .GetField("IsCoverDisplayedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            renderer.SetValue(key, true);
        }

        [TestCase(BitmapScalingMode.Fant)]
        [TestCase(BitmapScalingMode.Linear)]
        [TestCase(BitmapScalingMode.NearestNeighbor)]
        [TestCase(BitmapScalingMode.Unspecified)]
        public void CoverRenderingFollowsNativeThemeScalingAcrossTransitionResets(BitmapScalingMode mode)
        {
            var native = new Image { Source = Bitmap(32, 48) };
            var root = new Grid();
            root.Children.Add(native);
            var initialStyle = new Style(typeof(Image));
            initialStyle.Setters.Add(new Setter(RenderOptions.BitmapScalingModeProperty, mode));
            native.Style = initialStyle;
            var renderer = Renderer(new ImageRotaterSettings { RotateCovers = true });
            using (var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, new Game(), () => { }))
            {
                foreach (string name in new[] { "DisplayImage", "PreviousImage", "DisplayVideo" })
                    Assert.That(RenderOptions.GetBitmapScalingMode((DependencyObject)renderer.FindName(name)), Is.EqualTo(mode));

                var displayed = (Image)renderer.FindName("DisplayImage");
                var previous = (Image)renderer.FindName("PreviousImage");
                var binding = BindingOperations.GetBindingBase(displayed, RenderOptions.BitmapScalingModeProperty);
                // Simulate the temporary pixelation filter and its cancellation.
                displayed.SetCurrentValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.NearestNeighbor);
                previous.SetCurrentValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.NearestNeighbor);
                typeof(CoverImageControl).GetMethod("ClearPreviousCover", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(renderer, null);
                Assert.That(RenderOptions.GetBitmapScalingMode(displayed), Is.EqualTo(mode));
                Assert.That(RenderOptions.GetBitmapScalingMode(previous), Is.EqualTo(mode));
                Assert.That(BindingOperations.GetBindingBase(displayed, RenderOptions.BitmapScalingModeProperty), Is.SameAs(binding));

                var style = new Style(typeof(Image));
                style.Setters.Add(new Setter(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.Fant));
                native.Style = style;
                Assert.That(RenderOptions.GetBitmapScalingMode(displayed), Is.EqualTo(BitmapScalingMode.Fant));
                RenderOptions.SetBitmapScalingMode(native, BitmapScalingMode.Linear);
                typeof(CoverImageControl).GetMethod("StagePreviousCover", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(renderer, new object[] { string.Empty });
                Assert.That(RenderOptions.GetBitmapScalingMode(displayed), Is.EqualTo(BitmapScalingMode.Linear));
                Assert.That(RenderOptions.GetBitmapScalingMode(previous), Is.EqualTo(BitmapScalingMode.Linear));
            }
            Assert.That(RenderOptions.GetBitmapScalingMode(native), Is.EqualTo(BitmapScalingMode.Linear));
            Assert.That(native.Style, Is.Not.Null);
        }

        [TestCase(false, true, true)]
        [TestCase(false, false, false)]
        [TestCase(true, true, true)]
        [TestCase(true, false, true)]
        public void ScopeUsesDisplayOptionIndependentlyOfAnimation(bool allCovers, bool selected, bool expected)
        {
            var game = new Game { Id = Guid.NewGuid() };
            var settings = new ImageRotaterSettings
            {
                UseExperimentalNativeCovers = true, RotateCovers = true, ShowUnfocusedCovers = allCovers, AnimateUnfocusedCovers = false
            };
            Assert.That(NativeCoverHost.ShouldRender(settings, game, selected ? game.Id : Guid.NewGuid()), Is.EqualTo(expected));
            settings.RotateCovers = false;
            Assert.That(NativeCoverHost.ShouldRender(settings, game, game.Id), Is.False);
            settings.RotateCovers = true;
            settings.EnableRotation = false;
            Assert.That(NativeCoverHost.ShouldRender(settings, game, game.Id), Is.False);
        }

        [Test]
        public void CoverObjectBindingDetectsCustomNamesAndStyleBindings()
        {
            var image = new Image { Name = "CoverImg" };
            BindingOperations.SetBinding(image, Image.SourceProperty, new Binding("CoverImageObject"));
            Assert.That(NativeCoverHost.IsCoverImage(image), Is.True);
            var style = new Style(typeof(Image));
            style.Setters.Add(new Setter(Image.SourceProperty, new Binding("CoverImageObject")));
            var styled = new Image { Style = style };
            Assert.That(NativeCoverHost.IsCoverImage(styled), Is.True);
            Assert.That(NativeCoverHost.IsCoverImage(new Image { Name = "CoverImg" }), Is.False);
            Assert.That(NativeCoverHost.IsCoverImage(new Image { Name = "PART_ImageCover" }), Is.True);
        }

        [Test]
        public void CoverObjectBindingRejectsDifferentOrExternalSources()
        {
            foreach (var binding in new[] {
                new Binding("BackgroundImageObject"), new Binding("Game.CoverImageObject"),
                new Binding("CoverImageObject") { Source = new object() },
                new Binding("CoverImageObject") { ElementName = "OtherGame" },
                new Binding("CoverImageObject") { RelativeSource = RelativeSource.Self },
                new Binding("CoverImageObject") { Mode = BindingMode.OneWayToSource } })
                Assert.That(NativeCoverHost.IsCoverObjectBinding(binding), Is.False);
        }

        [Test]
        public void ResolvesEachTilesOwnGameWithoutFallingBackToSelectedGame()
        {
            var a = new Game { Id = Guid.NewGuid() };
            var b = new Game { Id = Guid.NewGuid() };
            Func<Guid, Game> lookup = id => id == a.Id ? a : id == b.Id ? b : null;
            Assert.That(NativeCoverHost.ResolveGame(a, lookup), Is.SameAs(a));
            Assert.That(NativeCoverHost.ResolveGame(new GameWrapper { Game = new GameWrapper { Game = b } }, lookup), Is.SameAs(b));
            Assert.That(NativeCoverHost.ResolveGame(new IdWrapper { Id = a.Id }, lookup), Is.SameAs(a));
            Assert.That(NativeCoverHost.ResolveGame(new object(), lookup), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeSourceIsRetainedUntilReadyAndParentAndBindingAreRestored(bool borderParent)
        {
            var image = new Image { Name = "PART_ImageCover", Width = 160, Height = 240,
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(7) };
            Grid.SetRow(image, 2);
            Grid.SetColumnSpan(image, 3);
            var grid = new Grid();
            var border = new Border();
            if (borderParent) border.Child = image; else grid.Children.Add(image);
            var original = Bitmap();
            var context = new TextBlock { Tag = original };
            var binding = new Binding("Tag") { Source = context };
            BindingOperations.SetBinding(image, Image.SourceProperty, binding);
            var renderer = Renderer(new ImageRotaterSettings { UseExperimentalNativeCovers = true, RotateCovers = true });
            var attachment = NativeCoverHost.Attachment.TryCreate(image, renderer, new Game(), () => { });
            Assert.That(attachment, Is.Not.Null);
            Assert.That(image.Source, Is.SameAs(original), "Keep native cover until media is ready");
            Assert.That(renderer.Width, Is.EqualTo(160));
            Assert.That(renderer.Margin, Is.EqualTo(new Thickness(7)));
            Assert.That(((Image)renderer.FindName("DisplayImage")).Stretch, Is.EqualTo(Stretch.Uniform));
            Assert.That(((Image)renderer.FindName("DisplayImage")).StretchDirection, Is.EqualTo(StretchDirection.DownOnly));
            Assert.That(Grid.GetRow(attachment.Wrapper), Is.EqualTo(2));
            Assert.That(Grid.GetColumnSpan(attachment.Wrapper), Is.EqualTo(3));
            Ready(renderer);
            Assert.That(image.Source, Is.SameAs(original));
            Assert.That(image.Visibility, Is.EqualTo(Visibility.Hidden));
            attachment.Dispose();
            image.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Assert.That(image.Source, Is.SameAs(original));
            Assert.That(BindingOperations.GetBindingBase(image, Image.SourceProperty), Is.SameAs(binding));
            Assert.That(borderParent ? border.Child : grid.Children[0], Is.SameAs(image));
            Assert.That(renderer.IsCoverDisplayed, Is.False);
            Assert.That(((Image)renderer.FindName("DisplayImage")).Source, Is.Null);
            Assert.That(((MediaElement)renderer.FindName("DisplayVideo")).Source, Is.Null);
            attachment.Dispose();
            GC.KeepAlive(context); // WPF keeps DependencyObject binding sources weakly.
        }

        [Test]
        public void RecycledTileReleasesOldGameImmediatelyAndUsesNativeFallback()
        {
            var a = new Game { Id = Guid.NewGuid() };
            var b = new Game { Id = Guid.NewGuid() };
            var image = new Image { Name = "PART_ImageCover", DataContext = a, Source = Bitmap() };
            var grid = new Grid(); grid.Children.Add(image);
            var renderer = Renderer(new ImageRotaterSettings { UseExperimentalNativeCovers = true });
            int refreshes = 0;
            var attachment = NativeCoverHost.Attachment.TryCreate(image, renderer, a, () => refreshes++);
            Ready(renderer);
            image.DataContext = b;
            image.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Assert.That(refreshes, Is.EqualTo(1));
            Assert.That(renderer.GameContext, Is.Null);
            Assert.That(renderer.IsCoverDisplayed, Is.False);
            Assert.That(image.Source, Is.InstanceOf<BitmapSource>());
            attachment.Dispose();
        }

        [Test]
        public void HandoffSuppressesNativeDuringLoadingKeepsOutgoingIRAndRestoresFallback()
        {
            var source = PendingSource();
            var settings = new ImageRotaterSettings { UseExperimentalNativeCovers = true, RotateCovers = true };
            var renderer = new CoverImageControl(source, new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                () => settings, null, nativeInjected: true);
            var original = Bitmap();
            var native = new Image { Name = "PART_ImageCover", Source = original };
            var parent = new Grid(); parent.Children.Add(native);
            var a = new Game { Id = Guid.NewGuid() };
            var b = new Game { Id = Guid.NewGuid() };
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, a, () => { }, details: true);
            Assert.That(native.Source, Is.SameAs(original), "Keep the real native image during IR loading");
            Ready(renderer);
            var outgoing = ((Image)renderer.FindName("DisplayImage")).Source;
            attachment.UpdateGame(b, preserveOutgoing: true);
            Assert.That(renderer.GameContext, Is.SameAs(b));
            Assert.That(((Image)renderer.FindName("DisplayImage")).Source, Is.SameAs(outgoing));
            Assert.That(native.Source, Is.SameAs(original));
            source.Paths = new string[0];
            attachment.UpdateGame(new Game { Id = Guid.NewGuid() }, preserveOutgoing: true);
            Assert.That(native.Source, Is.SameAs(original), "A game without IR art must recover native rendering");
            attachment.Dispose();
        }

        [Test]
        public void InPlaceViewModelGameChangeRetargetsAfterBindingPropagationBeforeRender()
        {
            var source = PendingSource();
            var settings = new ImageRotaterSettings { UseExperimentalNativeCovers = true, RotateCovers = true };
            var renderer = new CoverImageControl(source, new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                () => settings, null, nativeInjected: true);
            var a = new Game { Id = Guid.NewGuid() };
            var b = new Game { Id = Guid.NewGuid() };
            var context = new ChangingContext { Game = a };
            var native = new Image { Name = "PART_ImageCover", Source = Bitmap(), DataContext = context };
            var parent = new Grid(); parent.Children.Add(native);
            int retargets = 0;
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, a, () => { },
                changed =>
                {
                    retargets++;
                    changed.UpdateGame(NativeCoverHost.ResolveGame(native.DataContext, id => null), true);
                }, details: true);
            Ready(renderer);
            context.Game = b;
            Assert.That(retargets, Is.Zero, "Do not change the visual tree inside the view-model notification");
            bool correctBeforeRender = false;
            native.Dispatcher.BeginInvoke(new Action(() => correctBeforeRender = renderer.GameContext == b), DispatcherPriority.Render);
            native.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Assert.That(retargets, Is.EqualTo(1));
            Assert.That(correctBeforeRender, Is.True);
            Assert.That(renderer.GameContext, Is.SameAs(b));
            Assert.That(native.Source, Is.InstanceOf<BitmapSource>());
            attachment.Dispose();
            context.Game = a;
            native.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Assert.That(retargets, Is.EqualTo(1), "Disposal removes view-model observers");
        }

        [Test]
        public void RapidInheritedContextChangesKeepMetadataAndCoverOnLatestGame()
        {
            var source = PendingSource();
            var settings = new ImageRotaterSettings { UseExperimentalNativeCovers = true, RotateCovers = true };
            var renderer = new CoverImageControl(source, new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                () => settings, null, nativeInjected: true);
            var a = new Game { Id = Guid.NewGuid(), Name = "UnMetal", Playtime = 1000 };
            var b = new Game { Id = Guid.NewGuid(), Name = "Intermediate", Playtime = 2000 };
            var c = new Game { Id = Guid.NewGuid(), Name = "Dawnwalker", Playtime = 3000 };
            var root = new Grid { DataContext = new ChangingContext { Game = a } };
            var name = new TextBlock();
            name.SetBinding(TextBlock.TextProperty, new Binding("Game.Name"));
            var playtime = new TextBlock();
            playtime.SetBinding(TextBlock.TextProperty, new Binding("Game.Playtime"));
            var native = new Image { Name = "PART_ImageCover", Source = Bitmap() };
            root.Children.Add(name); root.Children.Add(native); root.Children.Add(playtime);
            int retargets = 0;
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, a, () => { }, changed =>
            {
                retargets++;
                changed.UpdateGame(NativeCoverHost.ResolveGame(native.DataContext, id => null), true);
            }, details: true);
            Assert.That(VisualTreeHelper.GetParent(native), Is.SameAs(root), "Do not reparent native grid template parts");
            Ready(renderer);
            root.DataContext = new ChangingContext { Game = b };
            root.DataContext = new ChangingContext { Game = c };
            Assert.That(retargets, Is.Zero);
            bool frameMatches = false;
            root.Dispatcher.BeginInvoke(new Action(() =>
            {
                frameMatches = name.Text == c.Name && playtime.Text == "3000" && renderer.GameContext == c;
            }), DispatcherPriority.Render);
            root.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Assert.That(retargets, Is.EqualTo(1));
            Assert.That(frameMatches, Is.True, "The next frame must not combine old metadata and new selection");
            attachment.Dispose();
            Assert.That(VisualTreeHelper.GetParent(native), Is.SameAs(root));
        }

        [Test]
        [TestCase(Stretch.Uniform)]
        [TestCase(Stretch.UniformToFill)]
        [TestCase(Stretch.Fill)]
        public void HeliumStackPanelKeepsNativeCoverBounds(Stretch stretch)
        {
            var native = new Image { Name = "PART_ImageCover", Height = 240, Stretch = stretch,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Source = Bitmap(64, 96) };
            var root = new StackPanel { Width = 160 };
            root.Children.Add(new Border { Height = 24 }); root.Children.Add(native);
            root.Measure(new Size(160, 500)); root.Arrange(new Rect(0, 0, 160, 500)); root.UpdateLayout();
            Size expected = native.RenderSize;
            var renderer = Renderer(new ImageRotaterSettings());
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, new Game(), () => { });
            try
            {
                var current = (Image)renderer.FindName("DisplayImage");
                current.Visibility = Visibility.Visible; current.Source = Bitmap(64, 96);
                renderer.NotifyNativeMediaReady(current);
                root.Measure(new Size(160, 500)); root.Arrange(new Rect(0, 0, 160, 500)); root.UpdateLayout();
                Assert.That(renderer.RenderSize.Width, Is.EqualTo(expected.Width).Within(0.01));
                Assert.That(renderer.RenderSize.Height, Is.EqualTo(expected.Height).Within(0.01));
                Assert.That(current.Stretch, Is.EqualTo(stretch));
            }
            finally { attachment.Dispose(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReturningToNativeKeepsOutgoingMediaAndCanBeCancelledOnReselection(bool linearParent)
        {
            var renderer = Renderer(new ImageRotaterSettings());
            var original = Bitmap();
            var native = new Image { Source = original };
            Panel parent = linearParent ? (Panel)new StackPanel() : new Grid(); parent.Children.Add(native);
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, new Game(), () => { });
            bool completed = false;
            var previousStyle = Transition.CoverStyle;
            Transition.CoverStyle = TransitionStyle.Crossfade;
            try
            {
                Ready(renderer);
                attachment.ReturnToNative(() => completed = true);
                Assert.That(native.Source, Is.SameAs(original));
                Assert.That(renderer.IsCoverDisplayed, Is.True);
                Assert.That(completed, Is.False);
                Assert.That(attachment.Wrapper.HasAnimatedProperties, Is.False,
                    "The wrapper may also contain the native cover, which must remain visible during the fade.");
                Assert.That(renderer.HasAnimatedProperties, Is.True);
                attachment.CancelNativeReturn();
                Assert.That(attachment.ReturningToNative, Is.False);
                Assert.That(native.Source, Is.SameAs(original));
                Assert.That(attachment.Wrapper.Opacity, Is.EqualTo(1));
                Assert.That(renderer.HasAnimatedProperties, Is.False);
            }
            finally { attachment.Dispose(); Transition.CoverStyle = previousStyle; }
        }

        [Test]
        public void VideoLoopTimerIsReleasedWithVideoResources()
        {
            var renderer = Renderer(new ImageRotaterSettings());
            var timerField = typeof(CoverImageControl).GetField("_videoLoopTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Start();
            timerField.SetValue(renderer, timer);
            renderer.ReleaseNativeRendering();
            Assert.That(timer.IsEnabled, Is.False);
            Assert.That(timerField.GetValue(renderer), Is.Null);
        }

        private static void Pick(CoverImageControl renderer, string path)
        {
            object data = typeof(CoverImageControl).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(renderer);
            data.GetType().GetProperty("ImagePath").SetValue(data, path);
        }

        [TestCase(@"C:\Covers\original_native.jpg", true)]
        [TestCase(@"C:\Covers\sgdb_123.png", false)]
        public void APickOfTheGamesOwnCoverIsRecognised(string path, bool expected)
        {
            var renderer = Renderer(new ImageRotaterSettings());
            Pick(renderer, path);
            Assert.That(renderer.ShowsNativeOriginal, Is.EqualTo(expected));
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void LeavingAGameShowingItsOwnCoverReturnsWithoutAnimation(bool sameGame, bool immediate)
        {
            var renderer = Renderer(new ImageRotaterSettings());
            var native = new Image { Source = Bitmap() };
            var parent = new Grid(); parent.Children.Add(native);
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, new Game(), () => { });
            var previousStyle = Transition.CoverStyle;
            Transition.CoverStyle = TransitionStyle.Crossfade;
            try
            {
                Ready(renderer);
                Pick(renderer, @"C:\Covers\original_native.jpg");
                bool completed = false;
                attachment.ReturnToNative(() => completed = true, sameGame);
                Assert.That(completed, Is.EqualTo(immediate));
                Assert.That(renderer.HasAnimatedProperties, Is.EqualTo(!immediate));
            }
            finally { attachment.Dispose(); Transition.CoverStyle = previousStyle; }
        }

        [Test]
        public void DepartingNativeCoverIsHeldUntilIrCanTakeOver()
        {
            var native = new Image { Source = Bitmap(32, 48) };
            var root = new Grid();
            root.Children.Add(native);
            var renderer = Renderer(new ImageRotaterSettings { RotateCovers = true });
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, new Game(), () => { });
            try
            {
                var departing = Bitmap(32, 48);
                attachment.HoldDepartingNative(departing);
                Assert.That(attachment.DepartingNativeSource, Is.SameAs(departing));
                Assert.That(attachment.Wrapper.Children.Count, Is.EqualTo(2));

                // No IR media for this game: Playnite's own cover must show.
                typeof(NativeCoverHost.Attachment).GetMethod("OnNativeFallback", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(attachment, null);
                Assert.That(attachment.DepartingNativeSource, Is.Null);
                Assert.That(attachment.Wrapper.Children.Count, Is.EqualTo(1));

                // The cover already on screen needs no hold.
                attachment.HoldDepartingNative(native.Source);
                Assert.That(attachment.DepartingNativeSource, Is.Null);
            }
            finally { attachment.Dispose(); }
        }

        [Test]
        public void DisposingReleasesTheDepartingNativeHold()
        {
            var native = new Image { Source = Bitmap(32, 48) };
            var root = new Grid();
            root.Children.Add(native);
            var attachment = NativeCoverHost.Attachment.TryCreate(native, Renderer(new ImageRotaterSettings()), new Game(), () => { });
            attachment.HoldDepartingNative(Bitmap(32, 48));
            attachment.Dispose();
            Assert.That(attachment.DepartingNativeSource, Is.Null);
        }

        [Test]
        public void NoCoverLoopPosterLayerRemains()
        {
            Assert.That(Renderer(new ImageRotaterSettings()).FindName("VideoLoopFrame"), Is.Null);
        }

        [Test]
        public void NewCoverGeometryIsUpdatedEvenWhenDisplayedStateRemainsTrue()
        {
            var settings = new ImageRotaterSettings { UseExperimentalNativeCovers = true };
            var renderer = Renderer(settings);
            var native = new Image { Height = 240, Stretch = Stretch.Uniform, Source = Bitmap() };
            var parent = new Grid(); parent.Children.Add(native);
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, new Game(), () => { });
            Ready(renderer);
            var current = (Image)renderer.FindName("DisplayImage");
            current.Source = Bitmap(64, 48);
            typeof(CoverImageControl).GetMethod("DisplayImage_TargetUpdated", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(renderer, new object[] { current, null });
            Assert.That(native.Source.Width / native.Source.Height, Is.EqualTo(16.0 / 24).Within(0.001), "The native source itself is never replaced");
            attachment.Dispose();
        }

        [Test]
        public void OutgoingWideCoverDoesNotShiftNewPortraitWhenTransitionFinishes()
        {
            var renderer = Renderer(new ImageRotaterSettings { UseExperimentalNativeCovers = true });
            var native = new Image { Height = 240, Stretch = Stretch.Uniform, Source = Bitmap() };
            var frame = new Grid { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            frame.Children.Add(native);
            var root = new Grid { Width = 800, Height = 300 }; root.Children.Add(frame);
            var attachment = NativeCoverHost.Attachment.TryCreate(native, renderer, new Game(), () => { });
            var current = (Image)renderer.FindName("DisplayImage");
            var previous = (Image)renderer.FindName("PreviousImage");
            current.Source = Bitmap(64, 48); current.Visibility = Visibility.Visible;
            renderer.NotifyNativeMediaReady(current);
            previous.Source = current.Source; previous.Visibility = Visibility.Visible;
            current.Source = Bitmap(32, 48);
            renderer.NotifyNativeMediaReady(current);
            root.Measure(new Size(800, 300)); root.Arrange(new Rect(0, 0, 800, 300)); root.UpdateLayout();
            double during = frame.ActualWidth;
            Assert.That(during, Is.EqualTo(160).Within(0.01));
            previous.Source = null; previous.Visibility = Visibility.Collapsed;
            root.Measure(new Size(800, 300)); root.Arrange(new Rect(0, 0, 800, 300)); root.UpdateLayout();
            Assert.That(frame.ActualWidth, Is.EqualTo(during).Within(0.01));
            attachment.Dispose();
        }

        [Test]
        public void LateNativeRebindingIsSuppressedAndNewestBindingRestored()
        {
            var image = new Image { Source = Bitmap() };
            var grid = new Grid(); grid.Children.Add(image);
            var renderer = Renderer(new ImageRotaterSettings { UseExperimentalNativeCovers = true });
            var attachment = NativeCoverHost.Attachment.TryCreate(image, renderer, new Game(), () => { });
            Ready(renderer);
            var current = new TextBlock { Tag = Bitmap(12, 20) };
            var binding = new Binding("Tag") { Source = current };
            BindingOperations.SetBinding(image, Image.SourceProperty, binding);
            Assert.That(image.Visibility, Is.EqualTo(Visibility.Hidden), "A late native decode must not paint before deferred suppression");
            image.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Assert.That(image.Source, Is.SameAs(current.Tag));
            Assert.That(image.Visibility, Is.EqualTo(Visibility.Hidden));
            attachment.Dispose();
            Assert.That(image.Source, Is.SameAs(current.Tag));
            Assert.That(BindingOperations.GetBindingBase(image, Image.SourceProperty), Is.SameAs(binding));
        }

        [Test]
        public void UnconfiguredParentIsUntouchedAndNativeCoverOptionDoesNotHideNativeThemeElements()
        {
            var original = Bitmap();
            var image = new Image { Source = original };
            Assert.That(NativeCoverHost.Attachment.TryCreate(image, Renderer(new ImageRotaterSettings()), new Game(), () => { }), Is.Null);
            Assert.That(image.Source, Is.SameAs(original));
            var settings = new ImageRotaterSettings { UseThemeIntegration = false,
                UseExperimentalFadeImage = false, RotateCovers = true, HasDataCover = true };
            settings.UseExperimentalNativeCovers = true;
            Assert.That(settings.UsesDirectRendering(ArtworkKind.Cover), Is.True);
            Assert.That(settings.UsesDirectRendering(ArtworkKind.Background), Is.True);
            Assert.That(settings.EnableCoverImage, Is.False);
            Assert.That(settings.CanConfigureCovers, Is.True);
            Assert.That(settings.HasDataCover, Is.False);
            settings.UseExperimentalNativeCovers = false;
            Assert.That(settings.UseExperimentalNativeCovers, Is.True);
            Assert.That(settings.EnableCoverImage, Is.False);
            Assert.That(settings.HasDataCover, Is.False);
            settings.EnableRotation = false;
            Assert.That(settings.CanConfigureCovers, Is.False);
        }

        [Test]
        public void OffscreenCoverIsExcludedFromViewport()
        {
            var image = new Image { Width = 40, Height = 60, Source = Bitmap(40, 60) };
            var canvas = new Canvas { Width = 100, Height = 500 };
            canvas.Children.Add(image);
            var viewport = new ScrollContentPresenter { Width = 100, Height = 100, Content = canvas };
            using (var presentation = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("IR cover viewport test")
                { Width = 100, Height = 100, PositionX = -32000, PositionY = -32000, WindowStyle = 0x10000000, ExtendedWindowStyle = 0x08000080 }))
            {
                presentation.RootVisual = viewport;
                viewport.ApplyTemplate();
                viewport.Measure(new Size(100, 100));
                viewport.Arrange(new Rect(0, 0, 100, 100));
                viewport.UpdateLayout();
                viewport.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
                canvas.Measure(new Size(100, 500));
                canvas.Arrange(new Rect(0, 0, 100, 500));
                image.Measure(new Size(40, 60));
                image.Arrange(new Rect(0, 0, 40, 60));
                Assert.That(NativeCoverHost.IsInViewport(image), Is.True);
                image.Arrange(new Rect(0, 250, 40, 60));
                Assert.That(NativeCoverHost.IsInViewport(image), Is.False);
            }
        }
    }
}
