using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ImageRotater.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ImageRotater.Services
{
    // Experimental background-only adapter. Reuses the direct renderer inside
    // Playnite's ImageHolder; no native game metadata is rewritten.
    internal sealed class NativeFadeImageHost : IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly Func<ImageRotaterSettings> settings;
        private readonly Func<BackgroundImageControl> createRenderer;
        private readonly Func<Window> getWindow;
        private readonly DispatcherTimer timer;
        private Attachment attachment;
        private Game game;
        private bool disposed;
        private bool queued;
        private int discoveryPasses;

        // A theme with no standard background part would otherwise be walked
        // in full every second for the whole session. After a few fruitless
        // walks the watchdog only walks every IdleWalkInterval-th tick; any
        // selection, view, window or background-load signal restores the pace.
        internal const int IdleMissesBeforeBackoff = 5;
        internal const int IdleWalkInterval = 5;
        private int idleMisses;
        private int idleTicks;
        private string lastDiagnostic;
        private Window hookedWindow;
        private IDisposable discoverySubscription;

        private void Report(string diagnostic)
        {
            if (diagnostic == lastDiagnostic) return;
            lastDiagnostic = diagnostic;
            Logger.Info("ImageRotater native FadeImage: " + diagnostic);
        }

        internal NativeFadeImageHost(Func<ImageRotaterSettings> settings,
            Func<BackgroundImageControl> createRenderer, Func<Window> getWindow)
        {
            this.settings = settings;
            this.createRenderer = createRenderer;
            this.getWindow = getWindow;
            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += OnTick;
        }

        internal void Update(Game selected)
        {
            game = selected;
            if (disposed) return;
            if (settings()?.UseExperimentalFadeImage != true)
            {
                SetThemeFallback(false);
                timer.Stop();
                UnhookWindow();
                Detach();
                return;
            }
            timer.Start();
            HookWindow();
            discoveryPasses = 2;
            ResetIdleBackoff();
            if (attachment != null && attachment.Renderer.GameContext?.Id != game?.Id)
                attachment.Renderer.GameContext = game;
            Dispatcher dispatcher = getWindow()?.Dispatcher;
            if (dispatcher == null || queued) return;
            queued = true;
            dispatcher.BeginInvoke(new Action(() =>
            {
                queued = false;
                if (!disposed) Refresh();
            }), DispatcherPriority.Loaded);
        }

        private void OnTick(object sender, EventArgs e)
        {
            HookWindow();
            if (ShouldSkipIdleWalk()) return;
            Refresh();
        }

        internal bool ShouldSkipIdleWalk()
        {
            if (attachment != null || discoveryPasses > 0 || idleMisses < IdleMissesBeforeBackoff) return false;
            return ++idleTicks % IdleWalkInterval != 0;
        }

        internal void NoteWalk(bool attached)
        {
            if (attached) ResetIdleBackoff();
            else if (idleMisses < IdleMissesBeforeBackoff) idleMisses++;
        }

        private void ResetIdleBackoff()
        {
            idleMisses = 0;
            idleTicks = 0;
        }

        private void HookWindow()
        {
            Window current = getWindow();
            if (current == hookedWindow) return;
            UnhookWindow();
            ResetIdleBackoff();
            hookedWindow = current;
            if (hookedWindow != null)
            {
                hookedWindow.LayoutUpdated += OnBackgroundLayoutUpdated;
                discoverySubscription = NativeImageDiscovery.Subscribe(hookedWindow, OnNativeImageLoaded);
            }
        }

        private void UnhookWindow()
        {
            if (hookedWindow != null) hookedWindow.LayoutUpdated -= OnBackgroundLayoutUpdated;
            discoverySubscription?.Dispose();
            discoverySubscription = null;
            hookedWindow = null;
        }

        private void OnBackgroundLayoutUpdated(object sender, EventArgs e)
        {
            if (disposed || queued || discoveryPasses == 0) return;
            // A theme may build its background after the first discovery pass.
            // Loaded is a direct event, so a handler on Window cannot observe
            // child controls loading. Discover after layout, before paint, instead of
            // waiting for the one-second watchdog and then replacing that frame.
            queued = true;
            hookedWindow?.Dispatcher.BeginInvoke(new Action(() =>
            {
                queued = false;
                if (!disposed && settings()?.UseExperimentalFadeImage == true) Refresh();
            }), DispatcherPriority.Loaded);
        }

        private void OnNativeImageLoaded(Image image)
        {
            for (DependencyObject node = image; node is FrameworkElement element;
                node = VisualTreeHelper.GetParent(node))
            {
                if (!IsBackgroundTemplatePart(element)) continue;
                discoveryPasses = 2;
                ResetIdleBackoff();
                OnBackgroundLayoutUpdated(null, EventArgs.Empty);
                return;
            }
        }

        internal void Refresh()
        {
            try
            {
                ImageRotaterSettings current = settings();
                if (disposed || current?.UseExperimentalFadeImage != true ||
                    game == null || game.IsRunning || !current.ShouldRenderBackground(game.Id))
                {
                    Detach();
                    return;
                }

                Window window = getWindow();
                if (window == null) { Detach(); return; }
                // Keep the adapter installed while minimized; the renderer's
                // normal suspension policy releases media without loading native art.
                if (!window.IsVisible || window.WindowState == WindowState.Minimized) return;

                // Avoid scanning the whole library on every watchdog tick.
                if (discoveryPasses == 0 && attachment?.Host.IsLoaded == true &&
                    attachment.IsVisible && Window.GetWindow(attachment.Host) == window) return;
                if (discoveryPasses > 0) discoveryPasses--;

                var candidates = new List<FrameworkElement>();
                int registered = 0;
                FindBackgrounds(window, candidates, ref registered);
                bool wasFallback = current.UseThemeBackgroundFallback;
                SetThemeFallback(registered == 0 && current.UseThemeIntegration);
                if (current.UseThemeBackgroundFallback)
                {
                    Detach();
                    NoteWalk(false);
                    Report("no standard background part; using existing theme integration");
                    return;
                }
                if (wasFallback && candidates.Count == 0 && registered > 0)
                {
                    window.Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Loaded);
                    return;
                }
                FrameworkElement target = SelectTarget(candidates);
                if (attachment?.Host == target && target != null) { NoteWalk(true); return; }
                Detach();
                if (target == null)
                {
                    NoteWalk(false);
                    Report("no visible standard background found");
                    return;
                }
                attachment = Attachment.TryCreate(target, createRenderer(), game);
                NoteWalk(attachment != null);
                Report(attachment == null ? "background found but ImageHolder contract is unsupported" :
                    "attached to " + target.GetType().Name + " background (runtime name=" + target.Name +
                    ", game overview=" + IsGameOverview(target) + ")");
            }
            catch (Exception ex)
            {
                Detach();
                Logger.Warn(ex, "ImageRotater: experimental FadeImage attachment failed; native background restored");
            }
        }

        internal static FrameworkElement SelectTarget(IEnumerable<FrameworkElement> candidates) =>
            candidates.OrderByDescending(IsGameOverview).FirstOrDefault();

        private static bool IsGameOverview(FrameworkElement control)
        {
            for (DependencyObject parent = VisualTreeHelper.GetParent(control); parent != null;
                parent = VisualTreeHelper.GetParent(parent))
                if (parent.GetType().Name.EndsWith("GameOverview", StringComparison.Ordinal)) return true;
            return false;
        }

        internal static bool IsBackgroundTemplatePart(FrameworkElement control)
            => IsTemplatePart(control, "PART_ImageBackground");

        internal static bool IsTemplatePart(FrameworkElement control, string name)
        {
            if (control.Name == name) return true;
            // FadeImage.InitializeComponent sets Name to ControlRoot. The outer
            // template still registers it as PART_ImageBackground in its scope.
            var owner = control.TemplatedParent as Control;
            return owner?.Template != null &&
                ReferenceEquals(owner.Template.FindName(name, owner), control);
        }

        private void SetThemeFallback(bool enabled)
        {
            ImageRotaterSettings current = settings();
            if (current == null || current.UseThemeBackgroundFallback == enabled) return;
            current.UseThemeBackgroundFallback = enabled;
            BackgroundImageControl.NotifyPlaybackPolicyChanged();
        }

        private void FindBackgrounds(DependencyObject node, List<FrameworkElement> results, ref int registered)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is UserControl control &&
                    IsBackgroundTemplatePart(control) &&
                    control.GetType().FullName == "Playnite.Controls.FadeImage")
                {
                    registered++;
                    if (control.IsVisible) results.Add(control);
                }
                else if (child is Image image && IsBackgroundTemplatePart(image))
                {
                    registered++;
                    if (image.IsVisible || (attachment?.Host == image && attachment.IsVisible)) results.Add(image);
                }
                FindBackgrounds(child, results, ref registered);
            }
        }

        private void Detach()
        {
            Attachment previous = attachment;
            attachment = null;
            previous?.Dispose();
        }

        public void Dispose()
        {
            disposed = true;
            SetThemeFallback(false);
            timer.Stop();
            timer.Tick -= OnTick;
            UnhookWindow();
            Detach();
            game = null;
        }

        private sealed class SavedProperty
        {
            private readonly DependencyObject target;
            private readonly DependencyProperty property;
            private readonly BindingBase binding;
            private readonly object local;

            internal SavedProperty(DependencyObject target, DependencyProperty property)
            {
                this.target = target;
                this.property = property;
                binding = BindingOperations.GetBindingBase(target, property);
                local = binding == null ? target.ReadLocalValue(property) : DependencyProperty.UnsetValue;
            }

            internal void Restore()
            {
                if (binding != null) BindingOperations.SetBinding(target, property, binding);
                else if (local == DependencyProperty.UnsetValue) target.ClearValue(property);
                else target.SetValue(property, local);
            }
        }

        internal sealed class Attachment : IDisposable
        {
            internal FrameworkElement Host { get; }
            internal BackgroundImageControl Renderer { get; }
            internal bool IsVisible => Host is Image ? Renderer.IsVisible : Host.IsVisible;
            private readonly Grid holder;
            private readonly List<SavedProperty> saved = new List<SavedProperty>();
            private DependencyPropertyDescriptor sourceDescriptor;
            private SavedProperty sourceState;
            private readonly List<Image> nativeImages = new List<Image>();
            private readonly HashSet<Image> pendingNativeClears = new HashSet<Image>();
            private DependencyPropertyDescriptor imageSourceDescriptor;
            private bool disposed;
            private bool sourceClearQueued;
            private bool nativeSuppressed;
            private DependencyProperty animationProperty;

            private Attachment(FrameworkElement host, Grid holder, BackgroundImageControl renderer)
            {
                Host = host;
                this.holder = holder;
                Renderer = renderer;
                Renderer.NativeBackgroundReady += OnRendererReady;
            }

            internal static Attachment TryCreate(FrameworkElement host, BackgroundImageControl renderer, Game game)
            {
                if (host is Image plainImage) return TryCreateImage(plainImage, renderer, game);
                var holder = host.FindName("ImageHolder") as Grid;
                var image1 = host.FindName("Image1") as Image;
                var image2 = host.FindName("Image2") as Image;
                DependencyProperty source = DependencyPropertyDescriptor.FromName(
                    "Source", host.GetType(), host.GetType())?.DependencyProperty;
                DependencyProperty animation = DependencyPropertyDescriptor.FromName(
                    "AnimationEnabled", host.GetType(), host.GetType())?.DependencyProperty;
                if (holder == null || image1 == null || image2 == null || source == null || animation == null)
                    return null;

                var result = new Attachment(host, holder, renderer);
                try
                {
                    result.saved.Add(new SavedProperty(host, animation));
                    result.sourceState = new SavedProperty(host, source);
                    result.saved.Add(new SavedProperty(image1, UIElement.VisibilityProperty));
                    result.saved.Add(new SavedProperty(image2, UIElement.VisibilityProperty));
                    result.saved.Add(new SavedProperty(host, UIElement.CacheModeProperty));
                    result.animationProperty = animation;
                    host.SetValue(UIElement.CacheModeProperty, null);
                    result.sourceDescriptor = DependencyPropertyDescriptor.FromProperty(source, host.GetType());
                    result.sourceDescriptor.AddValueChanged(host, result.OnNativeSourceChanged);
                    result.imageSourceDescriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
                    foreach (Image image in new[] { image1, image2 })
                    {
                        result.nativeImages.Add(image);
                        result.imageSourceDescriptor.AddValueChanged(image, result.OnNativeImageChanged);
                    }
                    renderer.HorizontalAlignment = HorizontalAlignment.Stretch;
                    renderer.VerticalAlignment = VerticalAlignment.Stretch;
                    renderer.GameContext = game;
                    foreach (string name in new[] { "SlotAImage", "SlotBImage", "SlotAVideo", "SlotBVideo" })
                    {
                        var media = (FrameworkElement)renderer.FindName(name);
                        var stretch = DependencyPropertyDescriptor.FromName("Stretch", media.GetType(), media.GetType());
                        BindingOperations.SetBinding(media, stretch.DependencyProperty, new Binding("Stretch") { Source = host });
                        BindingOperations.SetBinding(media, FrameworkElement.HorizontalAlignmentProperty,
                            new Binding("HorizontalAlignment") { Source = host });
                        BindingOperations.SetBinding(media, FrameworkElement.VerticalAlignmentProperty,
                            new Binding("VerticalAlignment") { Source = host });
                    }
                    BindingOperations.SetBinding(renderer, UIElement.OpacityMaskProperty,
                        new Binding("ImageOpacityMask") { Source = host });
                    Grid.SetRowSpan(renderer, Math.Max(1, holder.RowDefinitions.Count));
                    Grid.SetColumnSpan(renderer, Math.Max(1, holder.ColumnDefinitions.Count));
                    // Keep the native darkening overlay above the injected media.
                    holder.Children.Insert(Math.Min(2, holder.Children.Count), renderer);
                    return result;
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            }

            private static Attachment TryCreateImage(Image image, BackgroundImageControl renderer, Game game)
            {
                var parent = VisualTreeHelper.GetParent(image) as Grid;
                if (parent == null) return null;
                var result = new Attachment(image, parent, renderer);
                try
                {
                    result.sourceState = new SavedProperty(image, Image.SourceProperty);
                    result.saved.Add(new SavedProperty(image, UIElement.VisibilityProperty));
                    result.sourceDescriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
                    result.sourceDescriptor.AddValueChanged(image, result.OnNativeSourceChanged);
                    foreach (string property in new[] { "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
                        "Margin", "HorizontalAlignment", "VerticalAlignment", "Opacity", "OpacityMask", "Effect" })
                    {
                        var descriptor = DependencyPropertyDescriptor.FromName(property, typeof(BackgroundImageControl), typeof(BackgroundImageControl));
                        BindingOperations.SetBinding(renderer, descriptor.DependencyProperty, new Binding(property) { Source = image });
                    }
                    foreach (string name in new[] { "SlotAImage", "SlotBImage", "SlotAVideo", "SlotBVideo" })
                    {
                        var media = (FrameworkElement)renderer.FindName(name);
                        var stretch = DependencyPropertyDescriptor.FromName("Stretch", media.GetType(), media.GetType());
                        BindingOperations.SetBinding(media, stretch.DependencyProperty, new Binding("Stretch") { Source = image });
                    }
                    Grid.SetRow(renderer, Grid.GetRow(image));
                    Grid.SetColumn(renderer, Grid.GetColumn(image));
                    Grid.SetRowSpan(renderer, Grid.GetRowSpan(image));
                    Grid.SetColumnSpan(renderer, Grid.GetColumnSpan(image));
                    Panel.SetZIndex(renderer, Panel.GetZIndex(image));
                    renderer.GameContext = game;
                    parent.Children.Insert(parent.Children.IndexOf(image) + 1, renderer);
                    return result;
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            }

            private void OnRendererReady()
            {
                if (disposed || nativeSuppressed) return;
                nativeSuppressed = true;
                // Only stop Playnite's decoder once the first IR slot is visible.
                // Until then its real image (and binding) remains untouched.
                sourceState = new SavedProperty(Host, sourceDescriptor.DependencyProperty);
                if (animationProperty != null) Host.SetValue(animationProperty, false);
                Host.SetValue(sourceDescriptor.DependencyProperty, null);
                if (Host is Image plain) plain.Visibility = Visibility.Collapsed;
                foreach (Image image in nativeImages)
                {
                    image.Visibility = Visibility.Collapsed;
                    image.Source = null;
                }
            }

            private void OnNativeSourceChanged(object sender, EventArgs e)
            {
                if (disposed || !nativeSuppressed || Host.GetValue(sourceDescriptor.DependencyProperty) == null) return;
                // Playnite can rebind Source when switching layouts/settings.
                // Remember its latest binding before stopping the duplicate decoder.
                sourceState = new SavedProperty(Host, sourceDescriptor.DependencyProperty);
                if (!(Host is Image)) Host.SetValue(sourceDescriptor.DependencyProperty, null);
                else if (!sourceClearQueued)
                {
                    sourceClearQueued = true;
                    Host.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        sourceClearQueued = false;
                        if (!disposed) Host.SetValue(sourceDescriptor.DependencyProperty, null);
                    }), DispatcherPriority.Loaded);
                }
            }

            private void OnNativeImageChanged(object sender, EventArgs e)
            {
                // An old async native load may finish after Source was cleared.
                var image = (Image)sender;
                if (disposed || !nativeSuppressed || image.Source == null || !pendingNativeClears.Add(image)) return;
                // Do not mutate a Freezable source reentrantly while WPF is
                // still assigning its inheritance context.
                image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    pendingNativeClears.Remove(image);
                    if (!disposed) image.Source = null;
                }), DispatcherPriority.Loaded);
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Renderer.NativeBackgroundReady -= OnRendererReady;
                sourceDescriptor?.RemoveValueChanged(Host, OnNativeSourceChanged);
                foreach (Image image in nativeImages)
                    imageSourceDescriptor.RemoveValueChanged(image, OnNativeImageChanged);
                nativeImages.Clear();
                // Release before removing: an async decode must not finish into a
                // detached visual, even if WPF delivers Unloaded a little later.
                Renderer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                holder.Children.Remove(Renderer);
                foreach (SavedProperty property in saved) property.Restore();
                if (nativeSuppressed) sourceState?.Restore();
                saved.Clear();
            }
        }
    }
}
