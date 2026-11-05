using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageRotater.Controls;
using ImageRotater.Models;
using ImageRotater.Services;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class NativeFadeImageHostTests
    {
        private sealed class TestGameOverview : Grid { }
        // Matches the public contract the adapter requires, without launching Playnite.
        private sealed class FakeFadeImage : UserControl
        {
            public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
                nameof(Source), typeof(object), typeof(FakeFadeImage));
            public object Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
            public static readonly DependencyProperty AnimationEnabledProperty = DependencyProperty.Register(
                nameof(AnimationEnabled), typeof(bool), typeof(FakeFadeImage), new PropertyMetadata(true));
            public bool AnimationEnabled { get => (bool)GetValue(AnimationEnabledProperty); set => SetValue(AnimationEnabledProperty, value); }
            public Brush ImageOpacityMask { get; set; }
            public Stretch Stretch { get; set; } = Stretch.UniformToFill;
            internal Grid Holder { get; } = new Grid();
            internal Image First { get; } = new Image();
            internal Image Second { get; } = new Image();

            internal FakeFadeImage()
            {
                NameScope.SetNameScope(this, new NameScope());
                RegisterName("ImageHolder", Holder);
                RegisterName("Image1", First);
                RegisterName("Image2", Second);
                Holder.Children.Add(First);
                Holder.Children.Add(Second);
                Holder.Children.Add(new Border());
                Content = Holder;
                CacheMode = new BitmapCache();
            }
        }

        private sealed class EmptySource : IBackgroundImageSource
        {
            public IReadOnlyList<string> GetImagePaths(Game game) => new string[0];
        }

        private static BackgroundImageControl Renderer(ImageRotaterSettings settings, bool injected = true) =>
            new BackgroundImageControl(new EmptySource(),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), () => settings, nativeInjected: injected);

        private static void MakeFirstImageReady(BackgroundImageControl renderer)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object slot = typeof(BackgroundImageControl).GetField("_slotA", flags).GetValue(renderer);
            Type type = slot.GetType();
            type.GetField("RequestToken").SetValue(slot,
                typeof(BackgroundImageControl).GetField("_requestToken", flags).GetValue(renderer));
            type.GetField("GameId").SetValue(slot, renderer.GameContext.Id);
            type.GetField("Kind").SetValue(slot, Enum.Parse(type.GetField("Kind").FieldType, "Still"));
            var image = (Image)type.GetProperty("Image").GetValue(slot);
            image.Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            image.Visibility = Visibility.Visible;
            typeof(BackgroundImageControl).GetMethod("BeginTransition", flags).Invoke(renderer, new[] { slot });
        }

        [Test]
        public void DetachBeforeFirstFrameKeepsLatestNativeBindingAndSource()
        {
            var host = new FakeFadeImage();
            var source = new TextBlock { Text = "initial.png" };
            var binding = new Binding("Text") { Source = source };
            BindingOperations.SetBinding(host, FakeFadeImage.SourceProperty, binding);
            var renderer = Renderer(new ImageRotaterSettings());
            var attachment = NativeFadeImageHost.Attachment.TryCreate(host, renderer, new Game());
            source.Text = "latest.png";
            attachment.Dispose();
            Assert.That(host.Source, Is.EqualTo("latest.png"));
            Assert.That(BindingOperations.GetBindingBase(host, FakeFadeImage.SourceProperty), Is.SameAs(binding));
            Assert.That(host.First.Visibility, Is.EqualTo(Visibility.Visible));
        }

        [Test]
        public void TransitionsOnlyKeepBackgroundAdapterUntilTransitionsAreAlsoDisabled()
        {
            var settings = new ImageRotaterSettings { EnableRotation = false, EnableTransitions = true };
            var window = new Window();
            var image = new Image { Name = "PART_ImageBackground" };
            var grid = new Grid(); grid.Children.Add(image);
            var game = new Game { Id = Guid.NewGuid() };
            var renderer = Renderer(settings);
            var attachment = NativeFadeImageHost.Attachment.TryCreate(image, renderer, game);
            using (var host = new NativeFadeImageHost(() => settings, () => Renderer(settings), () => window))
            {
                var field = typeof(NativeFadeImageHost).GetField("attachment", BindingFlags.Instance | BindingFlags.NonPublic);
                field.SetValue(host, attachment);
                typeof(NativeFadeImageHost).GetField("game", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(host, game);
                host.Refresh();
                Assert.That(field.GetValue(host), Is.SameAs(attachment));
                settings.EnableTransitions = false;
                host.Refresh();
                Assert.That(field.GetValue(host), Is.Null);
            }
        }

        [Test]
        public void LocalOverviewImageTakesPriorityOverLibraryFadeImage()
        {
            var overview = new TestGameOverview();
            var local = new Image { Name = "PART_ImageBackground" };
            overview.Children.Add(local);
            var global = new FakeFadeImage();
            Assert.That(NativeFadeImageHost.SelectTarget(new FrameworkElement[] { global, local }), Is.SameAs(local));
        }

        // Playnite's "Use game background image as window background" off:
        // Playnite leaves the window FadeImage empty, and some themes (Harmony)
        // keep it visible. The window background must stay untouched, while
        // the game overview's own background still rotates.
        [Test]
        public void WindowBackgroundSettingOffLeavesOnlyOverviewBackgrounds()
        {
            var overview = new TestGameOverview();
            var local = new Image { Name = "PART_ImageBackground" };
            overview.Children.Add(local);
            var global = new FakeFadeImage();

            Assert.That(NativeFadeImageHost.SelectTarget(
                NativeFadeImageHost.FilterWindowBackgrounds(new FrameworkElement[] { global }, false)), Is.Null);
            Assert.That(NativeFadeImageHost.SelectTarget(
                NativeFadeImageHost.FilterWindowBackgrounds(new FrameworkElement[] { global, local }, false)), Is.SameAs(local));
            Assert.That(NativeFadeImageHost.SelectTarget(
                NativeFadeImageHost.FilterWindowBackgrounds(new FrameworkElement[] { global }, true)), Is.SameAs(global));
        }

        [TestCase("{\"ShowBackgroundImageOnWindow\": false, \"X\": 1}", false)]
        [TestCase("{\"ShowBackgroundImageOnWindow\":true}", true)]
        [TestCase("{\"BackgroundImage\": false}", null)]
        [TestCase("", null)]
        public void WindowBackgroundSettingIsReadFromConfig(string json, bool? expected)
        {
            Assert.That(PlayniteWindowBackgroundSetting.Parse(json), Is.EqualTo(expected));
        }

        [Test]
        public void LocalImageAdapterPreservesGridPlacementAndRestoresSourceBinding()
        {
            var parent = new Grid();
            var image = new Image { Name = "PART_ImageBackground", Stretch = Stretch.Uniform, Margin = new Thickness(12), MaxWidth = 960 };
            Grid.SetRow(image, 2);
            Grid.SetColumnSpan(image, 3);
            parent.Children.Add(image);
            var original = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            original.Freeze();
            var source = new TextBlock { Tag = original };
            var binding = new Binding("Tag") { Source = source };
            BindingOperations.SetBinding(image, Image.SourceProperty, binding);
            var renderer = Renderer(new ImageRotaterSettings { UseExperimentalFadeImage = true });
            var attachment = NativeFadeImageHost.Attachment.TryCreate(image, renderer, new Game());
            Assert.That(attachment, Is.Not.Null);
            Assert.That(image.Source, Is.SameAs(original), "Keep native visible during initial IR load");
            Assert.That(image.Visibility, Is.EqualTo(Visibility.Visible));
            MakeFirstImageReady(renderer);
            Assert.That(image.Source, Is.Null);
            Assert.That(Grid.GetRow(renderer), Is.EqualTo(2));
            Assert.That(Grid.GetColumnSpan(renderer), Is.EqualTo(3));
            Assert.That(renderer.MaxWidth, Is.EqualTo(960));
            Assert.That(renderer.Margin, Is.EqualTo(new Thickness(12)));
            Assert.That(((Image)renderer.FindName("SlotAImage")).Stretch, Is.EqualTo(Stretch.Uniform));
            source.Tag = null;
            source.Tag = original;
            image.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Background);
            Assert.That(image.Source, Is.Null);
            attachment.Dispose();
            Assert.That(parent.Children.Count, Is.EqualTo(1));
            Assert.That(image.Source, Is.SameAs(original));
            Assert.That(image.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(BindingOperations.GetBindingBase(image, Image.SourceProperty), Is.SameAs(binding));
        }

        [Test]
        public void BackgroundPartIsFoundEvenWhenUserControlOverwritesItsRuntimeName()
        {
            var owner = new ContentControl
            {
                Template = new ControlTemplate(typeof(ContentControl))
                {
                    VisualTree = new FrameworkElementFactory(typeof(UserControl), "PART_ImageBackground")
                }
            };
            owner.ApplyTemplate();
            var part = (UserControl)owner.Template.FindName("PART_ImageBackground", owner);
            part.Name = "ControlRoot";
            Assert.That(NativeFadeImageHost.IsBackgroundTemplatePart(part), Is.True);
            Assert.That(NativeFadeImageHost.IsBackgroundTemplatePart(new UserControl { Name = "ControlRoot" }), Is.False);
            Assert.That(NativeFadeImageHost.IsBackgroundTemplatePart(new Image { Name = "TopBackground" }), Is.False);
        }

        [Test]
        public void NativeBackgroundUsesThemeStretchAndIgnoresOutgoingImageForAutoWidth()
        {
            var settings = new ImageRotaterSettings { UseExperimentalFadeImage = true };
            var renderer = new BackgroundImageControl(new EmptySource(),
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()),
                new ImageLoader(new ImageCache(4096)), () => settings, nativeInjected: true);
            var host = new FakeFadeImage { Height = 240, Stretch = Stretch.Uniform };
            var game = new Game { Id = Guid.NewGuid() };
            var attachment = NativeFadeImageHost.Attachment.TryCreate(host, renderer, game);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object oldSlot = typeof(BackgroundImageControl).GetField("_slotA", flags).GetValue(renderer);
            object newSlot = typeof(BackgroundImageControl).GetField("_slotB", flags).GetValue(renderer);
            foreach (object slot in new[] { oldSlot, newSlot })
            {
                Type type = slot.GetType();
                type.GetField("Ready").SetValue(slot, true);
                type.GetField("Kind").SetValue(slot, Enum.Parse(type.GetField("Kind").FieldType, "Still"));
                var image = (Image)type.GetProperty("Image").GetValue(slot);
                int width = slot == oldSlot ? 64 : 32;
                image.Source = BitmapSource.Create(width, 48, 96, 96, PixelFormats.Bgra32, null, new byte[width * 48 * 4], width * 4);
                image.Visibility = Visibility.Visible;
                ((Grid)type.GetProperty("Container").GetValue(slot)).Visibility = Visibility.Visible;
                Assert.That(image.Stretch, Is.EqualTo(Stretch.Uniform));
            }
            typeof(BackgroundImageControl).GetField("_activeSlot", flags).SetValue(renderer, oldSlot);
            typeof(BackgroundImageControl).GetField("_pendingSlot", flags).SetValue(renderer, newSlot);
            host.Measure(new Size(800, 240)); host.Arrange(new Rect(0, 0, host.DesiredSize.Width, 240));
            Assert.That(host.DesiredSize.Width, Is.EqualTo(160).Within(0.01));
            attachment.Dispose();
        }

        [Test]
        public void AttachStopsNativeLoadingAndDetachRestoresBindingsAndVisuals()
        {
            var settings = new ImageRotaterSettings { UseExperimentalFadeImage = true };
            var host = new FakeFadeImage();
            var source = new TextBlock { Text = "original.png" };
            var binding = new Binding("Text") { Source = source };
            BindingOperations.SetBinding(host, FakeFadeImage.SourceProperty, binding);
            var cache = host.CacheMode;
            var renderer = Renderer(settings);
            var attachment = NativeFadeImageHost.Attachment.TryCreate(host, renderer, new Game());
            Assert.That(attachment, Is.Not.Null);
            Assert.That(host.Source, Is.EqualTo("original.png"));
            Assert.That(host.AnimationEnabled, Is.True);
            Assert.That(host.First.Visibility, Is.EqualTo(Visibility.Visible));
            MakeFirstImageReady(renderer);
            Assert.That(host.Source, Is.Null);
            Assert.That(host.AnimationEnabled, Is.False);
            Assert.That(host.CacheMode, Is.Null);
            Assert.That(host.First.Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(host.Holder.Children.IndexOf(renderer), Is.EqualTo(2));
            host.First.Source = BitmapSource.Create(1, 1, 96, 96,
                PixelFormats.Bgra32, null, new byte[4], 4);
            host.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Background);
            Assert.That(host.First.Source, Is.Null, "Late native decodes must not retain a duplicate background");
            attachment.Dispose();
            Assert.That(host.Source, Is.EqualTo("original.png"));
            Assert.That(BindingOperations.GetBindingBase(host, FakeFadeImage.SourceProperty), Is.SameAs(binding));
            Assert.That(host.AnimationEnabled, Is.True);
            Assert.That(host.CacheMode, Is.SameAs(cache));
            Assert.That(host.First.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(host.Holder.Children.Count, Is.EqualTo(3));
            source.Text = "next.png";
            Assert.That(host.Source, Is.EqualTo("next.png"));
            attachment.Dispose(); // repeated disposal is harmless
        }

        [Test]
        public void PlayniteRebindingCannotRestartNativeDecoderAndLatestBindingIsRestored()
        {
            var host = new FakeFadeImage { Source = "before.png" };
            var attachment = NativeFadeImageHost.Attachment.TryCreate(host,
                Renderer(new ImageRotaterSettings { UseExperimentalFadeImage = true }), new Game());
            MakeFirstImageReady(attachment.Renderer);
            var binding = new Binding("Text") { Source = new TextBlock { Text = "rebound.png" } };
            BindingOperations.SetBinding(host, FakeFadeImage.SourceProperty, binding);
            Assert.That(host.Source, Is.Null);
            attachment.Dispose();
            Assert.That(host.Source, Is.EqualTo("rebound.png"));
            Assert.That(BindingOperations.GetBindingBase(host, FakeFadeImage.SourceProperty), Is.SameAs(binding));
        }

        [Test]
        public void IncompatibleHostIsLeftUntouched()
        {
            var host = new UserControl { Content = new TextBlock { Text = "native" } };
            var original = host.Content;
            Assert.That(NativeFadeImageHost.Attachment.TryCreate(host,
                Renderer(new ImageRotaterSettings()), new Game()), Is.Null);
            Assert.That(host.Content, Is.SameAs(original));
        }

        [Test]
        public void RetiredOptionsCannotReactivateLegacyRendering()
        {
            var settings = new ImageRotaterSettings { UseThemeIntegration = false,
                UseExperimentalFadeImage = false, UseExperimentalNativeCovers = false };
            Assert.That(settings.UseExperimentalFadeImage, Is.True);
            settings.UseExperimentalFadeImage = true;
            Assert.That(settings.UsesDirectRendering(ArtworkKind.Background), Is.True);
            Assert.That(settings.UsesDirectRendering(ArtworkKind.Cover), Is.True);
            Assert.That(settings.UsesDirectRendering(ArtworkKind.Logo), Is.True);
        }

        [Test]
        public void ExperimentalStateDoesNotMakeThemeHideItsOwnNativeHost()
        {
            var settings = new ImageRotaterSettings { UseExperimentalFadeImage = true, UseThemeIntegration = false };
            var renderer = Renderer(settings);
            renderer.GameContext = new Game { Id = Guid.NewGuid() };
            object slot = typeof(BackgroundImageControl).GetField("_slotA", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(renderer);
            var type = slot.GetType();
            type.GetField("GameId").SetValue(slot, renderer.GameContext.Id);
            type.GetField("RequestToken").SetValue(slot, typeof(BackgroundImageControl)
                .GetField("_requestToken", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(renderer));
            type.GetField("Kind").SetValue(slot, Enum.Parse(type.GetField("Kind").FieldType, "Still"));
            ((Image)type.GetProperty("Image").GetValue(slot)).Source = BitmapSource.Create(
                1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            typeof(BackgroundImageControl).GetMethod("BeginTransition", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(renderer, new[] { slot });
            Assert.That(renderer.IsBackgroundDisplayed, Is.True);
            Assert.That(settings.IsBackgroundDisplayed, Is.False);
            renderer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }

        [Test]
        public void FruitlessBackgroundSearchesSlowDownAndResumeOnSuccess()
        {
            using (var host = new NativeFadeImageHost(() => new ImageRotaterSettings(), () => null, () => null))
            {
                for (int i = 0; i < NativeFadeImageHost.IdleMissesBeforeBackoff; i++)
                {
                    Assert.That(host.ShouldSkipIdleWalk(), Is.False, "the first misses keep the normal pace");
                    host.NoteWalk(false);
                }

                int walks = 0;
                for (int tick = 0; tick < NativeFadeImageHost.IdleWalkInterval * 3; tick++)
                    if (!host.ShouldSkipIdleWalk()) walks++;
                Assert.That(walks, Is.EqualTo(3), "a backed-off host still walks, just less often");

                host.NoteWalk(true);
                Assert.That(host.ShouldSkipIdleWalk(), Is.False, "a found background restores the pace");
            }
        }
    }
}
