using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ImageRotater.Controls;
using ImageRotater.Models;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ImageRotater.Services
{
    // Native covers are adapted independently of backgrounds. Each realized
    // PART_ImageCover keeps its own game and its original source binding.
    internal sealed class NativeCoverHost : IDisposable
    {
        private readonly Func<ImageRotaterSettings> settings;
        private readonly Func<CoverImageControl> createRenderer;
        private readonly Func<Guid, Game> lookupGame;
        private readonly Func<Game, bool> hasArtwork;
        private readonly Func<Window> getWindow;
        private readonly Dictionary<Image, Attachment> attachments = new Dictionary<Image, Attachment>();
        // Ordinary Playnite-to-Playnite covers: only the reusable Desktop overview
        // Image is observed. Library tiles and Fullscreen covers are never tracked.
        private readonly Dictionary<Image, NativeDetailsTracker> nativeDetails = new Dictionary<Image, NativeDetailsTracker>();
        private readonly HashSet<Image> knownCovers = new HashSet<Image>();
        private readonly DispatcherTimer watchdog;
        private Window window;
        private IDisposable discoverySubscription;
        private Guid selected;
        private Queue<DependencyObject> scan;
        private HashSet<Image> seen;
        private HashSet<Image> discovered;
        private bool rescan;
        private bool disposed;
        private int scanGeneration;
        private string lastDiagnostic;
        private bool initialScan;
        private bool startupSelectionSeen;
        private bool userNavigatedAfterStartup;

        // A Loaded-priority visual-tree scan can keep WPF from presenting its
        // first rendered grid frame. A loaded Image is still claimed immediately
        // via OnImageLoaded; the full-tree pass is only a fallback.
        internal static DispatcherPriority VisualScanPriority => DispatcherPriority.Background;

        internal static bool CanAnimateNativeEntry(bool userNavigated, bool initialScanInProgress) =>
            userNavigated && !initialScanInProgress;

        // The details overview follows the selected game in one persistent Image.
        // Unlike a library tile, it needs an entry even when unfocused IR covers
        // are enabled. Never animate first paint or background grid discovery.
        internal static bool ShouldAnimateEntry(bool details, bool showUnfocusedCovers,
            bool userNavigated, bool initialScanInProgress) =>
            (details || !showUnfocusedCovers) &&
            CanAnimateNativeEntry(userNavigated, initialScanInProgress);

        internal static bool ShouldReturnDetailsToNative(bool details, bool gameChanged,
            bool artworkAvailable, bool oldIrVisible, bool userNavigated,
            bool initialScanInProgress) =>
            details && gameChanged && !artworkAvailable && oldIrVisible &&
            CanAnimateNativeEntry(userNavigated, initialScanInProgress);

        // Unlike IsDetailsRegion (also used by Fullscreen GameDetails), this
        // matches only Playnite Desktop's two reusable overview controls.
        internal static bool IsDesktopOverviewRegion(DependencyObject element)
        {
            for (DependencyObject parent = VisualTreeHelper.GetParent(element); parent != null;
                parent = VisualTreeHelper.GetParent(parent))
            {
                for (Type type = parent.GetType(); type != null; type = type.BaseType)
                {
                    if (type.Name == "GridViewGameOverview" || type.Name == "DetailsViewGameOverview")
                        return true;
                }
            }
            return false;
        }

        internal static bool ShouldTrackNativeOverview(bool desktopOverview, bool transitionsEnabled,
            TransitionStyle coverStyle) => desktopOverview && transitionsEnabled &&
            coverStyle != TransitionStyle.Cut;

        private bool ShouldObserveNativeDetails(Image image) =>
            ShouldTrackNativeOverview(IsDesktopOverviewRegion(image),
                settings()?.EnableTransitions == true, Transition.CoverStyle);

        private void TrackNativeDetails(Image image)
        {
            if (!ShouldObserveNativeDetails(image))
            {
                RemoveNativeDetails(image);
                return;
            }
            seen?.Add(image);
            if (!nativeDetails.TryGetValue(image, out var tracker))
            {
                tracker = new NativeDetailsTracker(image, selected);
                nativeDetails.Add(image, tracker);
                Transition.DebugLog?.Invoke("COVER details-native-tracker-attached");
            }
            tracker.Select(selected, CanAnimateNativeEntry(userNavigatedAfterStartup, initialScan));
        }

        private void RemoveNativeDetails(Image image)
        {
            if (!nativeDetails.TryGetValue(image, out var tracker)) return;
            nativeDetails.Remove(image);
            tracker.Dispose();
        }

        private void ReportState()
        {
            string diagnostic = "selected=" + selected + " active=" + attachments.Count +
                " native-overviews=" + nativeDetails.Count + " scope=" +
                (settings()?.ShowUnfocusedCovers == true ? "all-visible" : "selected");
            if (diagnostic == lastDiagnostic) return;
            lastDiagnostic = diagnostic;
            LogManager.GetLogger().Info("ImageRotater native covers: " + diagnostic);
        }

        internal NativeCoverHost(Func<ImageRotaterSettings> settings, Func<CoverImageControl> createRenderer,
            Func<Guid, Game> lookupGame, Func<Game, bool> hasArtwork, Func<Window> getWindow)
        {
            this.settings = settings;
            this.createRenderer = createRenderer;
            this.lookupGame = lookupGame;
            this.hasArtwork = hasArtwork;
            this.getWindow = getWindow;
            watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            watchdog.Tick += OnTick;
        }

        internal static bool ShouldRender(ImageRotaterSettings settings, Game game, Guid selected) =>
            settings?.UseExperimentalNativeCovers == true && settings.EnableRotation && game != null &&
            !game.IsRunning && settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Cover) &&
            (settings.ShowUnfocusedCovers || game.Id == selected);

        internal static Game ResolveGame(object context, Func<Guid, Game> lookup)
        {
            for (int depth = 0; context != null && depth < 4; depth++)
            {
                if (context is Game game) return game;
                Type type = context.GetType();
                var nested = type.GetProperty("Game");
                if (nested != null)
                {
                    object next = nested.GetValue(context);
                    if (next != null && !ReferenceEquals(next, context)) { context = next; continue; }
                }
                object id = type.GetProperty("Id")?.GetValue(context);
                return id is Guid guid && guid != Guid.Empty ? lookup(guid) : null;
            }
            return null;
        }

        // Reading the id directly avoids a second database lookup from a
        // SourceChanged callback and also supports Playnite view models that
        // expose only Id instead of a Game property.
        internal static Guid ResolveContextGameId(object context)
        {
            for (int depth = 0; context != null && depth < 4; depth++)
            {
                if (context is Game game) return game.Id;
                Type type = context.GetType();
                object nested = type.GetProperty("Game")?.GetValue(context);
                if (nested != null && !ReferenceEquals(nested, context))
                {
                    context = nested;
                    continue;
                }
                object id = type.GetProperty("Id")?.GetValue(context);
                return id is Guid guid ? guid : Guid.Empty;
            }
            return Guid.Empty;
        }

        internal static bool IsCoverObjectBinding(Binding binding) =>
            binding?.Path?.Path == "CoverImageObject" && binding.Source == null &&
            binding.RelativeSource == null && string.IsNullOrEmpty(binding.ElementName) &&
            binding.Mode != BindingMode.OneWayToSource;

        internal static bool IsCoverImage(Image image) =>
            NativeFadeImageHost.IsTemplatePart(image, "PART_ImageCover") ||
            IsCoverObjectBinding(BindingOperations.GetBinding(image, Image.SourceProperty));

        internal void Update(Game game)
        {
            Guid nextSelection = game?.Id ?? Guid.Empty;
            if (nextSelection != Guid.Empty)
            {
                if (!startupSelectionSeen) startupSelectionSeen = true;
                else if (selected != nextSelection) userNavigatedAfterStartup = true;
            }
            selected = nextSelection;
            if (disposed) return;
            if (settings()?.CanConfigureCovers != true &&
                !(settings()?.UseExperimentalNativeCovers == true &&
                  settings()?.EnableTransitions == true && Transition.CoverStyle != TransitionStyle.Cut))
            {
                watchdog.Stop();
                UnhookWindow();
                Clear();
                return;
            }
            HookWindow();
            watchdog.Start();
            // Rebind known covers in the selection turn, before another full
            // library scan. Otherwise the native cover paints first.
            foreach (Image image in knownCovers.ToArray())
            {
                try { Visit(image); }
                catch (Exception ex)
                {
                    LogManager.GetLogger().Warn(ex, "ImageRotater: could not update native cover; restoring its binding");
                    Remove(image);
                }
            }
            RequestScan();
        }

        private void HookWindow()
        {
            Window current = getWindow();
            if (current == window) return;
            UnhookWindow();
            Clear();
            window = current;
            if (window == null) return;
            initialScan = true;
            window.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll), true);
            discoverySubscription = NativeImageDiscovery.Subscribe(window, OnImageLoaded);
            window.SizeChanged += OnSizeChanged;
            window.IsVisibleChanged += OnWindowVisibleChanged;
        }

        private void OnTick(object sender, EventArgs e) { HookWindow(); RequestScan(); }
        private void OnScroll(object sender, ScrollChangedEventArgs e) => RequestScan();
        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RequestScan();
        private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (window?.IsVisible == true) initialScan = true;
            RequestScan();
        }
        private void OnImageLoaded(Image image)
        {
            if (!IsCoverImage(image)) return;
            // Loaded covers already have their layout. Claim them before the first
            // frame, rather than waiting behind rendering for a background scan.
            if (!disposed && window != null && window.IsVisible && window.WindowState != WindowState.Minimized)
            {
                try
                {
                    knownCovers.Add(image);
                    Visit(image);
                }
                catch (Exception ex)
                {
                    LogManager.GetLogger().Warn(ex, "ImageRotater: could not initialize native cover");
                    Remove(image);
                }
            }
            RequestScan();
        }

        private void RequestScan()
        {
            if (disposed || window == null) return;
            if (!window.IsVisible || window.WindowState == WindowState.Minimized)
            {
                Clear();
                return;
            }
            if (scan != null) { rescan = true; return; }
            scan = new Queue<DependencyObject>();
            scan.Enqueue(window);
            seen = new HashSet<Image>();
            discovered = new HashSet<Image>();
            int generation = ++scanGeneration;
            window.Dispatcher.BeginInvoke(new Action(() => ScanChunk(generation)),
                VisualScanPriority);
        }

        private void ScanChunk(int generation)
        {
            if (disposed || scan == null || generation != scanGeneration) return;
            try
            {
                var elapsed = Stopwatch.StartNew();
                int count = 0;
                while (scan.Count != 0 && count++ < 120 && elapsed.ElapsedMilliseconds < 3)
                {
                    DependencyObject node = scan.Dequeue();
                    if (node is CoverImageControl) continue;
                    if (node is Image image && (attachments.ContainsKey(image) || IsCoverImage(image)))
                    {
                        discovered.Add(image);
                        knownCovers.Add(image);
                        Visit(image);
                    }
                    int children = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < children; i++) scan.Enqueue(VisualTreeHelper.GetChild(node, i));
                }
                if (scan.Count != 0)
                {
                    window.Dispatcher.BeginInvoke(new Action(() => ScanChunk(generation)),
                        VisualScanPriority);
                    return;
                }
                foreach (Image image in attachments.Keys.Where(i => !seen.Contains(i)).ToArray()) Remove(image);
                foreach (Image image in nativeDetails.Keys.Where(i => !seen.Contains(i)).ToArray()) RemoveNativeDetails(image);
                knownCovers.IntersectWith(discovered);
                initialScan = false;
                ReportState();
                scan = null;
                seen = null;
                discovered = null;
                if (rescan) { rescan = false; RequestScan(); }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, "ImageRotater: native cover scan failed; restoring covers");
                Clear();
            }
        }

        private void Visit(Image image)
        {
            Attachment existing;
            attachments.TryGetValue(image, out existing);
            // Adapt only direct bindings to this element's own game context.
            // Explicit Source/ElementName/ancestor bindings may refer to another
            // game and must not fall back to the selected game.
            if (!NativeFadeImageHost.IsTemplatePart(image, "PART_ImageCover") &&
                !IsCoverObjectBinding(BindingOperations.GetBinding(image, Image.SourceProperty) ??
                    existing?.OriginalSourceBinding as Binding))
            {
                Remove(image);
                RemoveNativeDetails(image);
                return;
            }
            Game game = ResolveGame(image.DataContext, lookupGame);
            FrameworkElement bounds = existing?.Wrapper ?? (FrameworkElement)image;
            bool details = IsDetailsRegion(image);
            bool desktopOverview = IsDesktopOverviewRegion(image);
            if (existing != null && game != null && existing.Renderer.GameContext?.Id == game.Id &&
                settings()?.EnableRotation == true && settings().IsArtworkRotationEnabled(game.Id, ArtworkKind.Cover) &&
                !settings().ShowUnfocusedCovers && !details && game.Id != selected && IsInViewport(bounds))
            {
                seen?.Add(image);
                existing.ReturnToNative(() =>
                {
                    if (attachments.TryGetValue(image, out var current) && current == existing && existing.ReturningToNative)
                        Remove(image);
                });
                return;
            }
            bool inViewport = IsInViewport(bounds);
            bool canRender = ShouldRender(settings(), game, details && game != null ? game.Id : selected);
            bool artworkAvailable = canRender && inViewport && hasArtwork(game);
            if (!artworkAvailable)
            {
                // Details overview reuses PART_ImageCover for every game. Do not
                // dispose the previous IR media while Playnite is still resolving
                // the new game's native Source: that produces a one-frame flash.
                // Keep the outgoing IR visible and return to native only when the
                // new Image.Source is ready. Ordinary grid tiles still remove now.
                if (existing != null && game != null && canRender && inViewport &&
                    ShouldReturnDetailsToNative(details,
                        existing.Renderer.GameContext?.Id != game.Id,
                        artworkAvailable, existing.Renderer.IsCoverDisplayed,
                        userNavigatedAfterStartup, initialScan))
                {
                    seen?.Add(image);
                    existing.ReturnDetailsToNative(game.Id, () =>
                    {
                        if (attachments.TryGetValue(image, out var current) && current == existing)
                        {
                            Remove(image);
                            // After IR -> native completes, keep observing the
                            // now-native overview for future native -> native.
                            if (ShouldObserveNativeDetails(image)) TrackNativeDetails(image);
                        }
                    });
                    return;
                }
                Remove(image);
                if (desktopOverview) TrackNativeDetails(image);
                else RemoveNativeDetails(image);
                return;
            }
            // Native -> IR in the Desktop overview: Playnite swaps the shared
            // Image to the new game's own cover before the IR pick is ready.
            // Keep showing the cover the user left until the entry starts.
            ImageSource departingNative = null;
            if (existing == null && nativeDetails.TryGetValue(image, out var tracker) &&
                tracker.LastSelected != game.Id &&
                CanAnimateNativeEntry(userNavigatedAfterStartup, initialScan))
                departingNative = tracker.VisibleSource;
            RemoveNativeDetails(image);
            seen?.Add(image);
            if (existing != null)
            {
                existing.CancelDetailsNativeReturn();
                existing.CancelNativeReturn();
                if (existing.Renderer.GameContext?.Id != game.Id) existing.UpdateGame(game, details);
                return;
            }
            Attachment added = Attachment.TryCreate(image, createRenderer(), game, RequestScan,
                changed => Visit(changed.NativeImage), details,
                // No temporary transition layers during first paint. The
                // overview also needs entries when all-visible covers is ON,
                // because its one PART_ImageCover represents multiple games.
                selectedOnly: ShouldAnimateEntry(details, settings().ShowUnfocusedCovers,
                    userNavigatedAfterStartup, initialScan), departingNative: departingNative);
            if (added != null) attachments.Add(image, added);
        }

        private static bool IsDetailsRegion(DependencyObject element)
        {
            for (DependencyObject parent = VisualTreeHelper.GetParent(element); parent != null;
                parent = VisualTreeHelper.GetParent(parent))
            {
                for (Type type = parent.GetType(); type != null; type = type.BaseType)
                    if (type.Name.EndsWith("GameOverview", StringComparison.Ordinal) || type.Name == "GameDetails") return true;
            }
            return false;
        }

        internal static bool IsInViewport(FrameworkElement element)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            try
            {
                for (DependencyObject parent = VisualTreeHelper.GetParent(element); parent != null;
                    parent = VisualTreeHelper.GetParent(parent))
                {
                    if (!(parent is ScrollContentPresenter viewport)) continue;
                    Rect bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
                    Rect visible = Rect.Intersect(bounds, new Rect(viewport.RenderSize));
                    if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
                }
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        private void Remove(Image image)
        {
            Attachment existing;
            if (!attachments.TryGetValue(image, out existing)) return;
            attachments.Remove(image);
            existing.Dispose();
        }
        private void Clear()
        {
            ++scanGeneration;
            scan = null;
            seen = null;
            discovered = null;
            knownCovers.Clear();
            rescan = false;
            foreach (Image image in attachments.Keys.ToArray()) Remove(image);
            foreach (Image image in nativeDetails.Keys.ToArray()) RemoveNativeDetails(image);
        }
        private void UnhookWindow()
        {
            if (window == null) return;
            window.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
            discoverySubscription?.Dispose();
            discoverySubscription = null;
            window.SizeChanged -= OnSizeChanged;
            window.IsVisibleChanged -= OnWindowVisibleChanged;
            window = null;
        }
        public void Dispose()
        {
            disposed = true;
            watchdog.Stop();
            watchdog.Tick -= OnTick;
            UnhookWindow();
            Clear();
        }

        // A lightweight watcher: no CoverImageControl, no Playnite binding changes,
        // no grid-wide image copies. It observes only the Desktop overview's
        // persistent PART_ImageCover while both games use native artwork.
        internal sealed class NativeDetailsTracker : IDisposable
        {
            private readonly Image image;
            private readonly DependencyPropertyDescriptor sourceDescriptor =
                DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            private readonly DispatcherTimer timeout;
            private Guid lastSelected;
            private ImageSource lastSource;
            private ImageSource departingSource;
            private Grid holding;
            private NativeCoverNativeTransition transition;
            private int generation;
            private bool disposed;
            private Guid pendingGame;

            internal NativeDetailsTracker(Image image, Guid selectedGame)
            {
                this.image = image;
                lastSelected = selectedGame;
                lastSource = image.Source;
                sourceDescriptor.AddValueChanged(image, OnSourceChanged);
                image.DataContextChanged += OnContextChanged;
                timeout = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                timeout.Tick += OnTimeout;
            }

            internal Guid LastSelected => lastSelected;

            // The native cover the user is looking at right now: the held
            // outgoing frame while a native -> native relay waits for its
            // source, otherwise the last settled source.
            internal ImageSource VisibleSource =>
                transition == null && holding != null ? departingSource : lastSource;

            internal void Select(Guid nextGame, bool animate)
            {
                if (disposed || nextGame == lastSelected) return;
                // One selection = one transition; never animate the initial
                // paint, navigation to no game, or a disabled transition.
                Cancel();
                ImageSource previous = lastSource;
                lastSelected = nextGame;
                if (!animate || nextGame == Guid.Empty || previous == null ||
                    Transition.CoverStyle == TransitionStyle.Cut)
                {
                    lastSource = image.Source;
                    return;
                }
                departingSource = previous;
                holding = NativeCoverNativeTransition.CreateHoldingLayer(image, previous);
                if (holding == null)
                {
                    lastSource = image.Source;
                    departingSource = null;
                    return;
                }
                pendingGame = nextGame;
                ++generation;
                Transition.DebugLog?.Invoke($"COVER details-native-native-pending game={nextGame}");
                timeout.Start();
                TryStart();
            }

            private void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (disposed) return;
                TryStart();
            }

            private void OnSourceChanged(object sender, EventArgs e)
            {
                if (disposed) return;
                if (pendingGame != Guid.Empty)
                {
                    TryStart();
                    return;
                }
                // Do not overwrite the outgoing bitmap during Playnite's
                // Source/DataContext propagation. Source can change BEFORE
                // its new context and before Update() reaches this tracker.
                // Defer source bookkeeping one dispatcher turn so Select()
                // can preserve the outgoing game's frame for the relay.
                ImageSource observed = image.Source;
                int observedGeneration = generation;
                image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (disposed || pendingGame != Guid.Empty ||
                        generation != observedGeneration || !ReferenceEquals(image.Source, observed))
                        return;
                    Guid context = ResolveContextGameId(image.DataContext);
                    if (context == Guid.Empty || context == lastSelected)
                        lastSource = observed;
                }), DispatcherPriority.Background);
            }

            private void TryStart()
            {
                if (disposed || transition != null || pendingGame == Guid.Empty || holding == null ||
                    image.Source == null || ReferenceEquals(departingSource, image.Source)) return;
                Guid context = ResolveContextGameId(image.DataContext);
                if (context != pendingGame) return;
                int run = generation;
                ImageSource arriving = image.Source;
                transition = NativeCoverNativeTransition.Start(image, holding, arriving, () =>
                {
                    if (disposed || run != generation) return;
                    Transition.DebugLog?.Invoke($"COVER details-native-native-complete game={pendingGame}");
                    transition = null;
                    holding = null;
                    timeout.Stop();
                    pendingGame = Guid.Empty;
                    departingSource = null;
                    lastSource = image.Source;
                });
                if (transition == null)
                {
                    Cancel();
                    lastSource = arriving;
                }
                else
                {
                    // From this moment Playnite already has the incoming
                    // image. A second quick selection must depart from THIS
                    // cover, not the source from two selections ago. Mosaic's
                    // full old-image base makes stale bookkeeping very visible.
                    lastSource = arriving;
                    Transition.DebugLog?.Invoke($"COVER details-native-native style={NativeCoverNativeTransition.EffectiveStyle(Transition.CoverStyle)} game={pendingGame}");
                    timeout.Stop();
                }
            }

            private void OnTimeout(object sender, EventArgs e)
            {
                if (pendingGame == Guid.Empty || transition != null) return;
                Transition.DebugLog?.Invoke($"COVER details-native-native-timeout game={pendingGame}");
                Cancel();
                lastSource = image.Source;
            }

            private void Cancel()
            {
                ++generation;
                timeout?.Stop();
                transition?.Dispose();
                transition = null;
                if (holding?.Parent is Grid grid) grid.Children.Remove(holding);
                holding = null;
                pendingGame = Guid.Empty;
                departingSource = null;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                sourceDescriptor.RemoveValueChanged(image, OnSourceChanged);
                image.DataContextChanged -= OnContextChanged;
                Cancel();
            }
        }

        internal sealed class Attachment : IDisposable
        {
            internal Grid Wrapper { get; } = new Grid();
            internal CoverImageControl Renderer { get; }
            internal Image NativeImage => image;
            private readonly Image image;
            private readonly Action requestScan;
            private readonly Action<Attachment> retarget;
            private readonly List<INotifyPropertyChanged> observedContexts = new List<INotifyPropertyChanged>();
            private readonly DependencyPropertyDescriptor state = DependencyPropertyDescriptor.FromProperty(CoverImageControl.IsCoverDisplayedProperty, typeof(CoverImageControl));
            private readonly DependencyPropertyDescriptor source = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            private BindingBase originalBinding;
            internal BindingBase OriginalSourceBinding => originalBinding;
            private Action restoreParent;
            private bool disposed;
            private bool handoff;
            private bool nativeTemporarilyHidden;
            private BindingBase visibilityBinding;
            private object visibilityLocal;
            private bool retargetQueued;
            private int nativeReturnGeneration;
            private NativeCoverReturnTransition nativeReturnTransition;
            private NativeCoverEntryTransition nativeEntryTransition;
            private bool entryAttempted;
            private bool entrySelectedOnly; // grid selected-only OR details overview entry
            private readonly bool detailsRegion;
            private Guid pendingDetailsNativeGame;
            private int pendingDetailsReturnGeneration;
            private DispatcherTimer detailsReturnWait;
            private Action detailsReturnFinished;
            private ImageSource previousDetailsNativeSource;
            internal bool ReturningToNative { get; private set; }
            private Grid departingHold;
            private DispatcherTimer departingHoldTimeout;
            internal ImageSource DepartingNativeSource => (departingHold?.Children[0] as Image)?.Source;

            // Covers the incoming game's own Playnite cover with the one being
            // left, so native -> IR departs from what was actually on screen.
            // Never held longer than a slow IR load: native then shows as before.
            internal void HoldDepartingNative(ImageSource departing)
            {
                ReleaseDepartingNative();
                if (departing == null || ReferenceEquals(departing, image.Source)) return;
                var layer = new Grid { IsHitTestVisible = false, ClipToBounds = true };
                Grid.SetZIndex(layer, 99);
                foreach (string name in new[] { "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
                    "Margin", "HorizontalAlignment", "VerticalAlignment", "RenderTransform", "RenderTransformOrigin",
                    "LayoutTransform", "Clip", "OpacityMask" })
                {
                    var dp = DependencyPropertyDescriptor.FromName(
                        name, typeof(FrameworkElement), typeof(FrameworkElement))?.DependencyProperty;
                    if (dp != null) BindingOperations.SetBinding(layer, dp, new Binding(name) { Source = image });
                }
                var still = new Image
                {
                    Source = departing, Stretch = image.Stretch,
                    StretchDirection = image.StretchDirection, IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(still, RenderOptions.GetBitmapScalingMode(image));
                layer.Children.Add(still);
                Wrapper.Children.Add(layer);
                departingHold = layer;
                departingHoldTimeout = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                departingHoldTimeout.Tick += (s, e) =>
                {
                    Transition.DebugLog?.Invoke("COVER native-entry-hold-timeout");
                    ReleaseDepartingNative();
                };
                departingHoldTimeout.Start();
                Transition.DebugLog?.Invoke("COVER native-entry-hold");
            }

            private void ReleaseDepartingNative()
            {
                departingHoldTimeout?.Stop();
                departingHoldTimeout = null;
                if (departingHold != null) Wrapper.Children.Remove(departingHold);
                departingHold = null;
            }

            // This is only used by an overview whose Image.DataContext changes
            // between games. The grid's selected-only return path is unchanged.
            internal void ReturnDetailsToNative(Guid targetGameId, Action completed)
            {
                if (disposed || targetGameId == Guid.Empty || completed == null) return;
                if (pendingDetailsNativeGame == targetGameId || ReturningToNative) return;
                CancelDetailsNativeReturn();
                CancelNativeEntry();
                pendingDetailsNativeGame = targetGameId;
                detailsReturnFinished = completed;
                int generation = ++pendingDetailsReturnGeneration;
                Transition.DebugLog?.Invoke($"COVER details-native-return-pending game={targetGameId}");
                // Give Playnite's async cover binding a turn to supply the
                // destination source. The outgoing IR remains on screen.
                image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation == pendingDetailsReturnGeneration)
                        TryFinishDetailsNativeReturn();
                }), DispatcherPriority.Loaded);
                detailsReturnWait = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                detailsReturnWait.Tick += (s, e) =>
                {
                    if (generation != pendingDetailsReturnGeneration) return;
                    // Never hold a stale game hostage if Playnite has no cover.
                    if (!TryFinishDetailsNativeReturn())
                    {
                        Action finish = detailsReturnFinished;
                        CancelDetailsNativeReturn();
                        Transition.DebugLog?.Invoke($"COVER details-native-return-timeout game={targetGameId}");
                        finish?.Invoke();
                    }
                };
                detailsReturnWait.Start();
            }

            internal void CancelDetailsNativeReturn()
            {
                ++pendingDetailsReturnGeneration;
                pendingDetailsNativeGame = Guid.Empty;
                detailsReturnFinished = null;
                detailsReturnWait?.Stop();
                detailsReturnWait = null;
            }

            private bool TryFinishDetailsNativeReturn()
            {
                if (disposed || pendingDetailsNativeGame == Guid.Empty || ReturningToNative)
                    return false;
                if (image.Source == null || image.ActualWidth <= 0 || image.ActualHeight <= 0)
                    return false;
                // Playnite may retain the previous game's Source while its
                // async binding is fetching the new one. Do not animate toward
                // that old cover. If the images are identical, the watchdog
                // will eventually restore native without a misleading fade.
                if (ReferenceEquals(image.Source, previousDetailsNativeSource))
                    return false;
                // Source may arrive after DataContext, but must not belong to
                // another game following rapid navigation.
                if (NativeCoverHost.ResolveContextGameId(image.DataContext) != pendingDetailsNativeGame)
                    return false;
                Guid target = pendingDetailsNativeGame;
                Action finish = detailsReturnFinished;
                CancelDetailsNativeReturn();
                Transition.DebugLog?.Invoke($"COVER details-native-return-ready game={target} style={NativeCoverReturnTransition.EffectiveStyle(Transition.CoverStyle)}");
                ReturnToNative(finish, sameGame: false);
                return true;
            }

            // sameGame: native will show this game's own cover again (grid
            // tile losing selection), not another game's (details overview).
            internal void ReturnToNative(Action completed, bool sameGame = true)
            {
                if (disposed || ReturningToNative) return;
                ReturningToNative = true;
                CancelNativeEntry();
                ReleaseDepartingNative();
                int generation = ++nativeReturnGeneration;
                RestoreNativeVisibility();
                if (!Renderer.IsCoverDisplayed || Transition.CoverStyle == TransitionStyle.Cut ||
                    (sameGame && Renderer.ShowsNativeOriginal))
                { completed(); return; }
                // When only the selected tile may show IR, preserve the user's
                // cover effect on the way BACK to Playnite's native still.
                // All configured still effects now share the native relay engine.
                // For live videos, apply the effect to the available still and
                // keep the MediaElement in its original parent.
                nativeReturnTransition = NativeCoverReturnTransition.Start(Wrapper, image, Renderer, () =>
                {
                    if (!disposed && ReturningToNative && generation == nativeReturnGeneration)
                        completed();
                });
                if (nativeReturnTransition != null)
                {
                    Transition.DebugLog?.Invoke($"COVER native-return style={NativeCoverReturnTransition.EffectiveStyle(Transition.CoverStyle)} source={(nativeReturnTransition.UsesLiveRenderer ? "video" : "still")} mode={(detailsRegion ? "details" : "selected-only")}");
                    return;
                }
                Transition.DebugLog?.Invoke($"COVER native-return fallback=opacity reason=native-layer-unavailable style={Transition.CoverStyle}");
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, new Duration(
                    Transition.DurationFor(NativeCoverReturnTransition.EffectiveStyle(Transition.CoverStyle))));
                fade.Completed += (sender, args) =>
                {
                    if (!disposed && generation == nativeReturnGeneration && ReturningToNative) completed();
                };
                // Some themes require the native Image inside this wrapper.
                // Fade only IR, leaving the restored native cover visible beneath it.
                Renderer.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            internal void CancelNativeReturn()
            {
                if (!ReturningToNative) return;
                ReturningToNative = false;
                ++nativeReturnGeneration;
                nativeReturnTransition?.Dispose();
                nativeReturnTransition = null;
                Transition.DebugLog?.Invoke("COVER native-return cancel");
                Renderer.BeginAnimation(UIElement.OpacityProperty, null);
                // A rapid re-selection cancels an unfinished native return.
                // It is still an entry: animate native -> IR rather than
                // switching the tile instantly back to the IR frame.
                entryAttempted = false;
                TakeOverNativeWhenReady();
            }

            private Attachment(Image image, CoverImageControl renderer, Action requestScan,
                Action<Attachment> retarget, bool selectedOnly, bool detailsRegion)
            {
                this.image = image;
                Renderer = renderer;
                this.requestScan = requestScan;
                this.retarget = retarget;
                entrySelectedOnly = selectedOnly;
                this.detailsRegion = detailsRegion;
            }

            internal static Attachment TryCreate(Image image, CoverImageControl renderer, Game game, Action requestScan,
                Action<Attachment> retarget = null, bool details = false, bool selectedOnly = false,
            ImageSource departingNative = null)
            {
                var result = new Attachment(image, renderer, requestScan, retarget, selectedOnly, details);
                result.CaptureSource();
                if (!result.Wrap()) return null;
                try
                {
                    renderer.IsHitTestVisible = false;
                    foreach (string property in new[] { "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Margin",
                        "HorizontalAlignment", "VerticalAlignment", "Opacity", "OpacityMask", "Effect", "RenderTransform", "RenderTransformOrigin", "LayoutTransform", "Clip" })
                    {
                        var dp = DependencyPropertyDescriptor.FromName(property, typeof(CoverImageControl), typeof(CoverImageControl)).DependencyProperty;
                        BindingOperations.SetBinding(renderer, dp, new Binding(property) { Source = image });
                    }
                    foreach (string name in new[] { "DisplayImage", "PreviousImage", "DisplayVideo", "UnfocusedStillImage" })
                    {
                        var media = (FrameworkElement)renderer.FindName(name);
                        var stretch = DependencyPropertyDescriptor.FromName("Stretch", media.GetType(), media.GetType()).DependencyProperty;
                        BindingOperations.SetBinding(media, stretch, new Binding("Stretch") { Source = image });
                        if (media is Image still) BindingOperations.SetBinding(still, Image.StretchDirectionProperty,
                            new Binding("StretchDirection") { Source = image });
                    }
                    foreach (string name in new[] { "DisplayImage", "PreviousImage", "DisplayVideo", "UnfocusedStillImage" })
                    {
                        var media = (FrameworkElement)renderer.FindName(name);
                        BindingOperations.SetBinding(media, RenderOptions.BitmapScalingModeProperty,
                            new Binding { Source = image, Path = new PropertyPath(RenderOptions.BitmapScalingModeProperty) });
                    }
                    result.Wrapper.Children.Add(renderer);
                    if (selectedOnly) result.HoldDepartingNative(departingNative);
                    result.state.AddValueChanged(renderer, result.OnStateChanged);
                    result.source.AddValueChanged(image, result.OnSourceChanged);
                    renderer.NativeFallbackRequested += result.OnNativeFallback;
                    renderer.NativeMediaReady += result.OnMediaReady;
                    renderer.NativeCoverReady += result.OnMediaReady;
                    image.DataContextChanged += result.OnContextChanged;
                    result.ObserveContext();
                    result.UpdateGame(game, details);
                    return result;
                }
                catch { result.Dispose(); throw; }
            }

            internal void UpdateGame(Game game, bool preserveOutgoing)
            {
                if (Renderer.GameContext?.Id != game?.Id)
                {
                    CancelDetailsNativeReturn();
                    CancelNativeEntry();
                    ReleaseDepartingNative();
                    entryAttempted = false;
                }
                handoff = true;
                CaptureSource();
                if (!preserveOutgoing || !Renderer.IsCoverDisplayed) RestoreNativeVisibility();
                try { Renderer.SetNativeGameContext(game, preserveOutgoing); }
                finally
                {
                    handoff = false;
                    if (detailsRegion) previousDetailsNativeSource = image.Source;
                    if (!Renderer.HasNativeMedia) OnNativeFallback();
                }
            }

            private bool Wrap()
            {
                DependencyObject parent = VisualTreeHelper.GetParent(image);
                Grid.SetRow(Wrapper, Grid.GetRow(image)); Grid.SetColumn(Wrapper, Grid.GetColumn(image));
                Grid.SetRowSpan(Wrapper, Grid.GetRowSpan(image)); Grid.SetColumnSpan(Wrapper, Grid.GetColumnSpan(image));
                Panel.SetZIndex(Wrapper, Panel.GetZIndex(image));
                DockPanel.SetDock(Wrapper, DockPanel.GetDock(image));
                Canvas.SetLeft(Wrapper, Canvas.GetLeft(image)); Canvas.SetTop(Wrapper, Canvas.GetTop(image));
                Canvas.SetRight(Wrapper, Canvas.GetRight(image)); Canvas.SetBottom(Wrapper, Canvas.GetBottom(image));
                if (parent is Panel panel)
                {
                    int index = panel.Children.IndexOf(image);
                    if (panel is Grid || panel is Canvas)
                    {
                        // These panels already support overlays. Keep the
                        // native template part in its original tree and scope.
                        panel.Children.Insert(index + 1, Wrapper);
                        restoreParent = () => panel.Children.Remove(Wrapper);
                        return true;
                    }
                    MoveNativeKeepingContext(() =>
                    {
                        panel.Children.Remove(image);
                        Wrapper.Children.Add(image);
                        panel.Children.Insert(index, Wrapper);
                    });
                    restoreParent = () => MoveNativeKeepingContext(() =>
                    {
                        int current = panel.Children.IndexOf(Wrapper);
                        Wrapper.Children.Remove(image);
                        if (current >= 0) { panel.Children.Remove(Wrapper); panel.Children.Insert(current, image); }
                    });
                    return true;
                }
                if (parent is Decorator decorator && decorator.Child == image)
                {
                    MoveNativeKeepingContext(() =>
                    {
                        decorator.Child = null;
                        Wrapper.Children.Add(image);
                        decorator.Child = Wrapper;
                    });
                    restoreParent = () => MoveNativeKeepingContext(() =>
                    {
                        Wrapper.Children.Remove(image);
                        if (decorator.Child == Wrapper) decorator.Child = image;
                    });
                    return true;
                }
                if (parent is ContentControl content && content.Content == image &&
                    !BindingOperations.IsDataBound(content, ContentControl.ContentProperty))
                {
                    MoveNativeKeepingContext(() =>
                    {
                        content.Content = null;
                        Wrapper.Children.Add(image);
                        content.Content = Wrapper;
                    });
                    restoreParent = () => MoveNativeKeepingContext(() =>
                    {
                        Wrapper.Children.Remove(image);
                        if (content.Content == Wrapper) content.Content = image;
                    });
                    return true;
                }
                return false;
            }

            private void MoveNativeKeepingContext(Action move)
            {
                // Disconnecting an inherited DataContext restarts Playnite's
                // async PriorityBinding and briefly displays its platform cover.
                // Protect only the move; do not freeze context during playback.
                bool inherited = image.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue &&
                    BindingOperations.GetBindingBase(image, FrameworkElement.DataContextProperty) == null;
                object context = image.DataContext;
                if (inherited) image.SetValue(FrameworkElement.DataContextProperty, context);
                try { move(); }
                finally
                {
                    if (inherited && ReferenceEquals(image.ReadLocalValue(FrameworkElement.DataContextProperty), context))
                        image.ClearValue(FrameworkElement.DataContextProperty);
                }
            }

            private void CaptureSource()
            {
                originalBinding = BindingOperations.GetBindingBase(image, Image.SourceProperty);
            }

            private void CancelNativeEntry()
            {
                nativeEntryTransition?.Dispose();
                nativeEntryTransition = null;
            }

            private void TakeOverNativeWhenReady()
            {
                CaptureSource();
                // Same picture on both sides: swap silently. A held cover from
                // the previous game still needs its relay to this one.
                bool sameCover = DepartingNativeSource == null && Renderer.ShowsNativeOriginal;
                if (entrySelectedOnly && !entryAttempted && !ReturningToNative &&
                    !nativeTemporarilyHidden && Renderer.CanHideNativeCover && !sameCover &&
                    image.Source != null && Transition.CoverStyle != TransitionStyle.Cut)
                {
                    // A zero-sized template part is not a failed effect:
                    // Playnite has not finished layout yet. Keep native
                    // visible and retry instead of marking the entry done
                    // and cutting directly to IR.
                    if (image.ActualWidth <= 0 || image.ActualHeight <= 0)
                    {
                        Transition.DebugLog?.Invoke("COVER native-entry-deferred reason=layout-not-ready");
                        return;
                    }
                    entryAttempted = true;
                    nativeEntryTransition = NativeCoverEntryTransition.Start(Wrapper, image, Renderer, () =>
                    {
                        nativeEntryTransition = null;
                        Transition.DebugLog?.Invoke("COVER native-entry-complete");
                    }, DepartingNativeSource);
                    if (nativeEntryTransition != null)
                        Transition.DebugLog?.Invoke($"COVER native-entry style={NativeCoverEntryTransition.EffectiveStyle(Transition.CoverStyle)} source={(nativeEntryTransition.UsesVideo ? "video" : "still")} mode={(detailsRegion ? "details" : "selected-only")}");
                    else
                        Transition.DebugLog?.Invoke($"COVER native-entry fallback=immediate style={Transition.CoverStyle} reason=layer-unavailable");
                }
                // Outgoing native copy is now above the ready IR renderer.
                // Hiding the actual Playnite image never exposes an empty tile.
                if (Renderer.CanHideNativeCover) ReleaseDepartingNative();
                HideNativeWhenReady();
            }

            private void HideNativeWhenReady()
            {
                if (nativeTemporarilyHidden || !Renderer.CanHideNativeCover) return;
                visibilityBinding = BindingOperations.GetBindingBase(image, UIElement.VisibilityProperty);
                visibilityLocal = visibilityBinding == null ? image.ReadLocalValue(UIElement.VisibilityProperty) : DependencyProperty.UnsetValue;
                nativeTemporarilyHidden = true;
                // Hidden retains layout and the live native source. Never
                // restart its async binding when the selected game changes.
                if (detailsRegion)
                {
                    // A theme binding can overwrite SetCurrentValue while IR
                    // owns the details cover. Restore the saved binding on release.
                    image.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
                }
                else
                {
                    image.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
                }
            }

            private void RestoreNativeVisibility()
            {
                if (!nativeTemporarilyHidden) return;
                nativeTemporarilyHidden = false;
                if (visibilityBinding != null) BindingOperations.SetBinding(image, UIElement.VisibilityProperty, visibilityBinding);
                else if (visibilityLocal == DependencyProperty.UnsetValue) image.ClearValue(UIElement.VisibilityProperty);
                else image.SetValue(UIElement.VisibilityProperty, visibilityLocal);
            }
            private void OnStateChanged(object sender, EventArgs e)
            {
                if (disposed || ReturningToNative) return;
                if (Renderer.IsCoverDisplayed) TakeOverNativeWhenReady();
            }

            private void OnNativeFallback()
            {
                if (disposed || handoff) return;
                CancelNativeEntry();
                ReleaseDepartingNative();
                entryAttempted = false;
                RestoreNativeVisibility();
            }

            private void OnMediaReady()
            {
                if (disposed || ReturningToNative) return;
                TakeOverNativeWhenReady();
                Renderer.LogNativeLayout(image);
            }
            private void OnSourceChanged(object sender, EventArgs e)
            {
                if (disposed) return;
                CaptureSource();
                if (detailsRegion && pendingDetailsNativeGame == Guid.Empty && !ReturningToNative)
                {
                    Guid current = NativeCoverHost.ResolveContextGameId(image.DataContext);
                    if (current != Guid.Empty && current == Renderer.GameContext?.Id)
                        if (detailsRegion) previousDetailsNativeSource = image.Source;
                }
                if (pendingDetailsNativeGame != Guid.Empty)
                {
                    // Source callbacks run during Playnite binding updates;
                    // continue only after DataContext propagation settles.
                    int generation = pendingDetailsReturnGeneration;
                    image.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation == pendingDetailsReturnGeneration)
                            TryFinishDetailsNativeReturn();
                    }), DispatcherPriority.Loaded);
                }
            }
            private void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                // Never reparent controls or force bindings from an inherited
                // DataContext notification. WPF is still walking the view's tree.
                // DataBind runs before Render, after that propagation finishes.
                if (disposed || retargetQueued) return;
                retargetQueued = true;
                image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    retargetQueued = false;
                    if (disposed) return;
                    try
                    {
                        ObserveContext();
                        if (retarget != null) { retarget(this); return; }
                        Renderer.ReleaseNativeRendering();
                        Renderer.GameContext = null;
                        requestScan();
                    }
                    catch (Exception ex)
                    {
                        LogManager.GetLogger().Warn(ex, "ImageRotater: native cover context update failed; restoring native rendering");
                        Dispose();
                        requestScan();
                    }
                }), DispatcherPriority.DataBind);
            }

            private void ObserveContext()
            {
                foreach (INotifyPropertyChanged context in observedContexts) context.PropertyChanged -= OnContextPropertyChanged;
                observedContexts.Clear();
                object current = image.DataContext;
                for (int depth = 0; current != null && !(current is Game) && depth < 4; depth++)
                {
                    if (current is INotifyPropertyChanged observable && !observedContexts.Contains(observable))
                    {
                        observedContexts.Add(observable);
                        observable.PropertyChanged += OnContextPropertyChanged;
                    }
                    object next = current.GetType().GetProperty("Game")?.GetValue(current);
                    if (ReferenceEquals(next, current)) break;
                    current = next;
                }
            }

            private void OnContextPropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != "Game" && e.PropertyName != "Id") return;
                if (!image.Dispatcher.CheckAccess())
                {
                    image.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!disposed) OnContextChanged(sender, default(DependencyPropertyChangedEventArgs));
                    }));
                    return;
                }
                OnContextChanged(sender, default(DependencyPropertyChangedEventArgs));
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                CancelDetailsNativeReturn();
                ++nativeReturnGeneration;
                ReturningToNative = false;
                nativeReturnTransition?.Dispose();
                nativeReturnTransition = null;
                CancelNativeEntry();
                ReleaseDepartingNative();
                Renderer.BeginAnimation(UIElement.OpacityProperty, null);
                image.DataContextChanged -= OnContextChanged;
                foreach (INotifyPropertyChanged context in observedContexts) context.PropertyChanged -= OnContextPropertyChanged;
                observedContexts.Clear();
                Renderer.NativeFallbackRequested -= OnNativeFallback;
                Renderer.NativeMediaReady -= OnMediaReady;
                Renderer.NativeCoverReady -= OnMediaReady;
                state.RemoveValueChanged(Renderer, OnStateChanged);
                source.RemoveValueChanged(image, OnSourceChanged);
                Renderer.ReleaseNativeRendering();
                Renderer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Wrapper.Children.Remove(Renderer);
                restoreParent?.Invoke();
                RestoreNativeVisibility();
            }
        }
    }
}
