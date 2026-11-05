using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageRotater.Controls;
using ImageRotater.Models;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ImageRotater.Services
{
    // Source-only adaptation keeps the theme's Image, layout, masks and effects.
    internal sealed class NativeLogoHost : IDisposable
    {
        private readonly Func<ImageRotaterSettings> settings;
        private readonly Func<LogoImageControl> create;
        private readonly Func<Guid, Game> lookup;
        private readonly Func<Window> getWindow;
        private readonly Func<Game, bool> hasArtwork;
        private readonly Dictionary<Image, Attachment> attached = new Dictionary<Image, Attachment>();
        private readonly DispatcherTimer timer;
        private Game selected;
        private bool disposed;
        private Queue<DependencyObject> scan;
        private HashSet<Image> seen;
        private Window scanWindow;
        private bool rescan;
        private Window hookedWindow;
        private IDisposable discoverySubscription;
        private bool initialScan;
        internal NativeLogoHost(Func<ImageRotaterSettings> settings, Func<LogoImageControl> create,
            Func<Guid, Game> lookup, Func<Window> getWindow, Func<Game, bool> hasArtwork = null)
        {
            this.settings = settings; this.create = create; this.lookup = lookup; this.getWindow = getWindow;
            this.hasArtwork = hasArtwork ?? (_ => true);
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += Tick;
        }
        internal static bool TryLogoGame(string path, out Guid id)
        {
            id = Guid.Empty;
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = Uri.UnescapeDataString(path).Replace('/', '\\');
            int index = normalized.LastIndexOf("\\ExtraMetadata\\games\\", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            string[] parts = normalized.Substring(index + "\\ExtraMetadata\\games\\".Length).Split('\\');
            return parts.Length == 2 && parts[1].Equals("logo.png", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(parts[0], out id);
        }
        internal static string LogoPath(Image image)
        {
            var binding = BindingOperations.GetBinding(image, Image.SourceProperty);
            if (!string.IsNullOrEmpty(binding?.ElementName))
            {
                var element = image.FindName(binding.ElementName) as TextBlock;
                if (element == null && image.TemplatedParent is FrameworkElement owner)
                    element = owner.FindName(binding.ElementName) as TextBlock;
                if (element != null) return element.Text;
            }
            return (image.Source as BitmapImage)?.UriSource?.ToString();
        }
        internal void Update(Game game)
        {
            selected = game;
            if (disposed) return;
            if (settings()?.UseExperimentalNativeLogos != true || !settings().CanConfigureLogos)
            { timer.Stop(); Unhook(); Clear(); return; }
            HookWindow();
            foreach (Image image in attached.Keys.ToArray()) VisitImage(image);
            timer.Start(); Scan();
        }
        private void Tick(object sender, EventArgs e) { HookWindow(); Scan(); }
        private void HookWindow()
        {
            Window current = getWindow();
            if (current == hookedWindow) return;
            Unhook(); Clear(); hookedWindow = current; initialScan = true;
            if (hookedWindow != null)
            {
                discoverySubscription = NativeImageDiscovery.Subscribe(hookedWindow, OnImageLoaded);
                hookedWindow.IsVisibleChanged += OnWindowVisibleChanged;
            }
        }
        private void Unhook()
        {
            discoverySubscription?.Dispose();
            discoverySubscription = null;
            if (hookedWindow != null) hookedWindow.IsVisibleChanged -= OnWindowVisibleChanged;
            hookedWindow = null;
        }
        private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (hookedWindow?.IsVisible == true) initialScan = true;
            Scan();
        }
        private void OnImageLoaded(Image image)
        {
            if (disposed || settings()?.UseExperimentalNativeLogos != true) return;
            try { VisitImage(image); }
            catch (Exception ex) { LogManager.GetLogger().Warn(ex, "ImageRotater: logo initialization failed"); }
        }
        private void Scan()
        {
            if (disposed) return;
            if (scan != null) { rescan = true; return; }
            scanWindow = getWindow();
            if (scanWindow == null || !scanWindow.IsVisible || scanWindow.WindowState == WindowState.Minimized)
            { Clear(); return; }
            scan = new Queue<DependencyObject>(); scan.Enqueue(scanWindow);
            seen = new HashSet<Image>();
            scanWindow.Dispatcher.BeginInvoke(new Action(ScanChunk), DispatcherPriority.Loaded);
        }
        private void ScanChunk()
        {
            if (disposed || scan == null) return;
            try
            {
                if (settings()?.UseExperimentalNativeLogos != true || scanWindow != getWindow() ||
                    !scanWindow.IsVisible || scanWindow.WindowState == WindowState.Minimized)
                { Clear(); return; }
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                int count = 0;
                while (scan.Count > 0 && count++ < 120 && elapsed.ElapsedMilliseconds < 3)
                {
                    DependencyObject node = scan.Dequeue();
                    if (node is LogoImageControl) continue;
                    if (node is Image image) VisitImage(image);
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) scan.Enqueue(VisualTreeHelper.GetChild(node, i));
                }
                if (scan.Count != 0)
                {
                    scanWindow.Dispatcher.BeginInvoke(new Action(ScanChunk),
                        initialScan ? DispatcherPriority.Loaded : DispatcherPriority.Background);
                    return;
                }
                foreach (Image image in attached.Keys.Where(i => !seen.Contains(i)).ToArray())
                { attached[image].Dispose(); attached.Remove(image); }
                scan = null; seen = null; initialScan = false;
                if (rescan) { rescan = false; Scan(); }
            }
            catch (Exception ex) { LogManager.GetLogger().Warn(ex, "ImageRotater: native logo adaptation failed"); Clear(); }
        }
        private void VisitImage(Image image)
        {
            FrameworkElement eml = null;
            for (DependencyObject parent = VisualTreeHelper.GetParent(image); parent != null;
                parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is LogoImageControl) return;
                if (parent.GetType().FullName == "ExtraMetadataLoader.LogoLoaderControl")
                { eml = (FrameworkElement)parent; break; }
            }
            Attachment old;
            attached.TryGetValue(image, out old);
            Guid id;
            string path = LogoPath(image);
            bool direct = TryLogoGame(path, out id);
            Game game = direct ? lookup(id) : eml != null
                ? (eml as Playnite.SDK.Controls.PluginUserControl)?.GameContext ?? selected : null;
            if (eml != null)
            {
                object options = eml.GetType().GetProperty("_Settings")?.GetValue(eml);
                if (options?.GetType().GetProperty("EnableLogos")?.GetValue(options) is bool enabled && !enabled)
                    game = null;
            }
            // An unbound direct image currently contains our in-memory bitmap.
            // Do not keep claiming it if its binding or native source changed
            // to unrelated artwork: restore that source immediately instead.
            if (game == null && eml == null && old?.OwnsSource == true &&
                string.IsNullOrEmpty(path) && BindingOperations.GetBinding(image, Image.SourceProperty) == null)
                game = NativeCoverHost.ResolveGame(image.DataContext, lookup) ?? old.CurrentGame;
            if (game == null || !settings().CanConfigureLogos ||
                !settings().IsArtworkRotationEnabled(game.Id, ArtworkKind.Logo) || !hasArtwork(game))
            {
                if (old != null) { attached.Remove(image); old.Dispose(); }
                return;
            }
            seen?.Add(image);
            if (old == null) attached.Add(image, old = new Attachment(image, create(), RefreshImage));
            old.Update(game);
        }
        private void RefreshImage(Image image)
        {
            if (disposed) return;
            try { VisitImage(image); }
            catch (Exception ex) { LogManager.GetLogger().Warn(ex, "ImageRotater: logo source update failed"); }
        }
        private void Clear()
        {
            scan = null; seen = null; rescan = false;
            foreach (var item in attached.Values) item.Dispose(); attached.Clear();
        }
        public void Dispose() { disposed = true; timer.Stop(); timer.Tick -= Tick; Unhook(); Clear(); }

        internal sealed class Attachment : IDisposable
        {
            private readonly Image image;
            private readonly LogoImageControl renderer;
            private readonly Image output;
            private readonly DependencyPropertyDescriptor descriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            private ImageSource original;
            private FrameworkElement visibilityHost;
            private bool writing, disposed, queued;
            private bool hidingNative;
            private BindingBase visibilityBinding;
            private object visibilityLocal;
            private DrawingImage placeholder;
            private readonly DependencyPropertyDescriptor visibilityDescriptor =
                DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Image));

            private void Trace(string action, Func<string> details = null)
            {
                renderer.TraceLogo("native-" + action, () =>
                    $"image={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(image):x} name={image.Name} " +
                    $"visible={image.Visibility}/{image.IsVisible} opacity={image.Opacity:0.###} " +
                    $"size={image.ActualWidth:0.##}x{image.ActualHeight:0.##} hiding={hidingNative} " +
                    $"source={LogoImageControl.DescribeLogoSource(image.Source)} " +
                    $"output={LogoImageControl.DescribeLogoSource(output.Source)} " +
                    $"outputGame={renderer.DisplayedGameId} current={renderer.GameContext?.Id} {details?.Invoke()}");
            }

            private string AncestorState()
            {
                var ancestors = new List<string>();
                for (DependencyObject node = image; node is FrameworkElement element && ancestors.Count < 12;
                    node = VisualTreeHelper.GetParent(node))
                    ancestors.Add($"{element.GetType().Name}:{element.Name}[cache={element.CacheMode?.GetType().Name ?? "none"},visibility={element.Visibility},opacity={element.Opacity:0.###},transform={element.RenderTransform?.Value}]");
                return "ancestors=" + string.Join(" > ", ancestors);
            }

            private void VisibilityChanged(object sender, EventArgs e) => Trace("visibility", AncestorState);
            private void EffectiveVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Trace("effective-visibility", AncestorState);
            private void SizeChanged(object sender, SizeChangedEventArgs e) => Trace("size", AncestorState);
            private readonly Action<Image> retarget;
            internal Game CurrentGame => renderer.GameContext;
            internal bool OwnsSource => ReferenceEquals(image.Source, output.Source) ||
                (placeholder != null && ReferenceEquals(image.Source, placeholder));
            internal Attachment(Image image, LogoImageControl renderer, Action<Image> retarget = null)
            {
                this.retarget = retarget;
                this.image = image; this.renderer = renderer; original = image.Source;
                output = (Image)renderer.FindName("DisplayImage");
                for (DependencyObject p = VisualTreeHelper.GetParent(image); p is FrameworkElement element;
                    p = VisualTreeHelper.GetParent(p))
                {
                    if (BindingOperations.GetBinding(element, UIElement.VisibilityProperty)?.Path?.Path == "ControlVisibility")
                    { visibilityHost = element; break; }
                    if (element is ContentControl && element.GetType().FullName == "ExtraMetadataLoader.LogoLoaderControl") break;
                }
                descriptor.AddValueChanged(output, OutputChanged);
                descriptor.AddValueChanged(image, NativeChanged);
                renderer.NativeLoadStateChanged += LoadStateChanged;
                visibilityDescriptor.AddValueChanged(image, VisibilityChanged);
                image.IsVisibleChanged += EffectiveVisibilityChanged;
                image.SizeChanged += SizeChanged;
                Trace("attach", AncestorState);
                renderer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            }
            private void LoadStateChanged() => OutputChanged(null, EventArgs.Empty);
            internal void Update(Game game)
            {
                renderer.Width = Math.Max(640, image.ActualWidth);
                if (renderer.GameContext?.Id != game.Id)
                {
                    Trace("context", () => $"old={renderer.GameContext?.Id} new={game.Id} width={renderer.Width:0.##}");
                    renderer.GameContext = game;
                }
            }
            private void OutputChanged(object sender, EventArgs e)
            {
                if (disposed) return;
                Trace("output", () => $"event={(ReferenceEquals(sender, output) ? "source" : "load-state-or-queued")} original={LogoImageControl.DescribeLogoSource(original)}");
                writing = true;
                try
                {
                    // During an async load, keep the IR bitmap only if it
                    // belongs to the CURRENT game. The old code reused any
                    // output.Source, briefly showing the previous game's
                    // logo after Playnite recycled this Image.
                    ImageSource currentLogo = renderer.HasCurrentGameLogo ? output.Source : null;

                    // Prefer the actual native artwork while loading the new
                    // game's IR logo. A transparent placeholder exposed the
                    // theme background (often black) for the entire decode.
                    // Reserve a transparent, correctly-sized fallback only
                    // for genuinely source-less native Images.
                    if (renderer.NativeLoading && currentLogo == null &&
                        original == null && placeholder == null)
                    {
                        placeholder = new DrawingImage(new GeometryDrawing(Brushes.Transparent, null,
                            new RectangleGeometry(new Rect(0, 0, 1, 1))));
                        placeholder.Freeze();
                    }
                    ImageSource next = currentLogo ?? original ??
                        (renderer.NativeLoading ? placeholder : null);
                    image.SetCurrentValue(Image.SourceProperty, next);
                    if (hidingNative)
                    {
                        hidingNative = false;
                        RestoreVisibility();
                    }
                    if (visibilityHost != null)
                    {
                        if (currentLogo != null || original != null)
                            visibilityHost.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                        else
                            BindingOperations.GetBindingExpression(visibilityHost, UIElement.VisibilityProperty)?.UpdateTarget();
                    }
                }
                finally { writing = false; }
                Trace("applied");
            }
            private void NativeChanged(object sender, EventArgs e)
            {
                if (writing || disposed) return;
                Trace("source-changed", AncestorState);
                original = image.Source;
                placeholder = null;
                if (output.Source == null && !renderer.NativeLoading)
                {
                    QueueNativeUpdate();
                    return;
                }
                // Source may be changing inside a Freezable notification. Hide
                // this one paint, then replace Source safely before rendering.
                if (!hidingNative)
                {
                    visibilityBinding = BindingOperations.GetBindingBase(image, UIElement.VisibilityProperty);
                    visibilityLocal = visibilityBinding == null ? image.ReadLocalValue(UIElement.VisibilityProperty) : DependencyProperty.UnsetValue;
                    hidingNative = true;
                    Trace("hide-before-replace");
                    image.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
                }
                QueueNativeUpdate();
            }
            private void RestoreVisibility()
            {
                if (visibilityBinding != null) BindingOperations.SetBinding(image, UIElement.VisibilityProperty, visibilityBinding);
                else if (visibilityLocal == DependencyProperty.UnsetValue) image.ClearValue(UIElement.VisibilityProperty);
                else image.SetValue(UIElement.VisibilityProperty, visibilityLocal);
            }
            private void QueueNativeUpdate()
            {
                if (queued) return;
                queued = true;
                Trace("replace-queued");
                image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    queued = false;
                    if (disposed) return;
                    Trace("replace-dispatched");
                    retarget?.Invoke(image);
                    OutputChanged(null, EventArgs.Empty);
                }), DispatcherPriority.Loaded);
            }
            public void Dispose()
            {
                if (disposed) return;
                Trace("detach", AncestorState);
                disposed = true;
                visibilityDescriptor.RemoveValueChanged(image, VisibilityChanged);
                image.IsVisibleChanged -= EffectiveVisibilityChanged;
                image.SizeChanged -= SizeChanged;
                descriptor.RemoveValueChanged(output, OutputChanged);
                descriptor.RemoveValueChanged(image, NativeChanged);
                renderer.NativeLoadStateChanged -= LoadStateChanged;
                renderer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                image.SetCurrentValue(Image.SourceProperty, original);
                if (hidingNative)
                {
                    RestoreVisibility();
                }
                if (visibilityHost != null)
                    BindingOperations.GetBindingExpression(visibilityHost, UIElement.VisibilityProperty)?.UpdateTarget();
            }
        }
    }
}
