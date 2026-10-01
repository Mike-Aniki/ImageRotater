using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ImageRotater.Services
{
    // How a still replaces the still before it.
    public enum TransitionStyle
    {
        Crossfade,          // The old picture dissolves into the new one
        FadeThroughBlack,   // Dip to black between the two
        FadeThroughWhite,   // Flash to white between the two
        SlideFromRight,     // PS5-style subtle slide from the right
        Cut                 // No animation at all
    }

    public enum TransitionDirection
    {
        FromLeft,
        FromRight,
        FromTop,
        FromBottom
    }

    // Shared transition style and timing for native and plugin renderers.
    //
    // Covers and backgrounds are chosen separately: a flash that reads as a
    // beat on a small tile is a full-screen strobe on a background.
    public static class Transition
    {
        public static TransitionStyle CoverStyle { get; set; } = TransitionStyle.Crossfade;
        public static TransitionStyle BackgroundStyle { get; set; } = TransitionStyle.Crossfade;
        public static TransitionDirection NavigationDirection { get; private set; } = TransitionDirection.FromRight;
        public static Action<string> DebugLog { get; set; }

        // A cover transition can still be cleaning up when the user moves to
        // another game. Keep a tiny per-Image generation token so an older
        // transition can remove its own overlays without restoring the native
        // Image opacity in the middle of the newer transition.
        private sealed class SlideState
        {
            public int Generation;
        }

        private static readonly ConditionalWeakTable<Image, SlideState> SlideStates =
            new ConditionalWeakTable<Image, SlideState>();

        private static void Trace(string message)
        {
            try { DebugLog?.Invoke("COVER TRANSITION " + message); } catch { }
        }

        public static void SetNavigationDirection(TransitionDirection direction)
        {
            NavigationDirection = direction;
        }

        // Transition timing is fixed by style:
        // - SlideFromRight: 800 ms
        // - Crossfade / FadeThroughBlack / FadeThroughWhite: 400 ms
        // - Cut: immediate
        private static readonly TimeSpan StandardDuration = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(800);

        public static TimeSpan DurationFor(TransitionStyle style)
        {
            if (style == TransitionStyle.Cut)
            {
                return TimeSpan.Zero;
            }

            return style == TransitionStyle.SlideFromRight
                ? SlideDuration
                : StandardDuration;
        }

        // Keep setters for source compatibility with older callers, but timing
        // is intentionally no longer user-configurable.
        public static TimeSpan CoverDuration
        {
            get => DurationFor(CoverStyle);
            set { }
        }

        public static TimeSpan BackgroundDuration
        {
            get => DurationFor(BackgroundStyle);
            set { }
        }

        public static TimeSpan CoverHalf => TimeSpan.FromMilliseconds(CoverDuration.TotalMilliseconds / 2);
        public static TimeSpan BackgroundHalf => TimeSpan.FromMilliseconds(BackgroundDuration.TotalMilliseconds / 2);

        // Backwards-compatible aliases kept for existing callers/tests that
        // pre-date the split between cover and background transition timing.
        // The original Transition.Run API is cover-specific, so these aliases
        // intentionally map to the cover timing.
        public static TimeSpan Duration
        {
            get => CoverDuration;
            set => CoverDuration = value;
        }

        public static TimeSpan Half => CoverHalf;

        public static bool IsFlash(TransitionStyle style) =>
            style == TransitionStyle.FadeThroughBlack || style == TransitionStyle.FadeThroughWhite;

        public static Color FlashColor(TransitionStyle style) =>
            style == TransitionStyle.FadeThroughWhite ? Colors.White : Colors.Black;

        // Transitions an element the plugin does not own - Playnite's cover
        // Image - around a swap of its source, without touching the element's
        // tree: the veil is an adorner, WPF's own overlay layer. Covers only,
        // so it reads CoverStyle.
        //
        // Crossfade holds a snapshot of the current picture over the element,
        // swaps underneath it, and dissolves the snapshot. A flash raises a
        // colour, swaps behind it, and lowers it. Returns false when the
        // element has no adorner layer to draw in, so the caller can fall
        // back to something that needs none.
        public static bool Run(FrameworkElement target, Action swap)
        {
            if (target == null || swap == null)
            {
                return false;
            }

            TransitionStyle style = CoverStyle;
            Trace($"run style={style} duration={CoverDuration.TotalMilliseconds:0}ms target={target.GetType().Name} size={target.ActualWidth:0}x{target.ActualHeight:0}");

            if (style == TransitionStyle.Cut)
            {
                swap();
                return true;
            }

            // Prefer an overlay anchored to the window content instead of an
            // adorner attached directly to the cover. Playnite themes commonly
            // scale the selected tile. A target-bound adorner inherits that
            // transform, which made the outgoing cover visibly zoom/de-zoom
            // just before/during the fade. The fixed overlay keeps the exact
            // on-screen bounds captured at transition start and therefore only
            // animates opacity. It reuses the already-decoded ImageSource, so
            // this does not add another image decode.
            FrameworkElement overlayRoot = null;
            AdornerLayer layer = null;
            Rect overlayBounds = Rect.Empty;

            try
            {
                Window window = Application.Current?.MainWindow;
                overlayRoot = window?.Content as FrameworkElement;

                if (overlayRoot != null && overlayRoot.IsAncestorOf(target))
                {
                    layer = AdornerLayer.GetAdornerLayer(overlayRoot);
                    if (layer != null)
                    {
                        GeneralTransform toRoot = target.TransformToAncestor(overlayRoot);
                        overlayBounds = toRoot.TransformBounds(new Rect(target.RenderSize));
                    }
                }
            }
            catch
            {
                layer = null;
                overlayRoot = null;
                overlayBounds = Rect.Empty;
            }

            // Some themes do not expose a root adorner layer. Keep the old
            // target-bound path as a compatibility fallback.
            bool fixedOverlay = layer != null && overlayRoot != null && !overlayBounds.IsEmpty;
            Trace($"overlay fixed={fixedOverlay} bounds={overlayBounds.X:0},{overlayBounds.Y:0},{overlayBounds.Width:0}x{overlayBounds.Height:0} layer={(layer != null)}");
            if (!fixedOverlay)
            {
                layer = AdornerLayer.GetAdornerLayer(target);
                if (layer == null)
                {
                    Trace("abort no-adorner-layer");
                    return false;
                }
            }

            var image = target as Image;

            Brush brush;
            if (IsFlash(style))
            {
                brush = new SolidColorBrush(FlashColor(style));
            }
            else
            {
                // A crossfade needs the OLD picture, and only an Image has one
                // to snapshot.
                if (image?.Source == null)
                {
                    return false;
                }

                brush = new ImageBrush(image.Source) { Stretch = image.Stretch };
            }

            brush.Freeze();
            VeilAdorner veil = fixedOverlay
                ? new VeilAdorner(overlayRoot, brush, overlayBounds)
                : new VeilAdorner(target, brush);
            layer.Add(veil);

            void Remove()
            {
                veil.BeginAnimation(UIElement.OpacityProperty, null);
                layer.Remove(veil);
            }

            void Lower(TimeSpan over)
            {
                var down = new DoubleAnimation(0.0, new Duration(over));
                down.Completed += (s, e) => Remove();
                veil.BeginAnimation(UIElement.OpacityProperty, down);
            }

            try
            {
                if (IsFlash(style))
                {
                    veil.Opacity = 0.0;
                    var up = new DoubleAnimation(1.0, new Duration(CoverHalf));
                    up.Completed += (s, e) => SwapThen(image, swap, () => Lower(CoverHalf));
                    veil.BeginAnimation(UIElement.OpacityProperty, up);
                }
                else if (style == TransitionStyle.SlideFromRight)
                {
                    // Draw both covers inside ONE adorner and animate a single
                    // progress value. RenderTransform on separate adorners proved
                    // unreliable on Playnite's virtualised cover grid: the clocks
                    // ran, but the translated adorners were not visibly moving.
                    // The same custom-drawing approach keeps the slide independent from the native control.
                    TransitionDirection dir = TransitionDirection.FromRight;

                    double nativeOpacity = image?.Opacity ?? 1.0;
                    ImageSource oldSource = image?.Source;
                    SlideState slideState = null;
                    int slideGeneration = 0;

                    if (image != null)
                    {
                        slideState = SlideStates.GetOrCreateValue(image);
                        slideGeneration = ++slideState.Generation;
                        image.Opacity = 0.0;
                    }

                    Trace($"slide-draw begin dir={dir} generation={slideGeneration} old={(oldSource != null ? oldSource.GetType().Name : "null")} fixed={fixedOverlay} bounds={(fixedOverlay ? $"{overlayBounds.X:0},{overlayBounds.Y:0},{overlayBounds.Width:0}x{overlayBounds.Height:0}" : $"0,0,{target.ActualWidth:0}x{target.ActualHeight:0}")}");

                    SwapThenStable(image, swap, () =>
                    {
                        ImageSource newSource = image?.Source;
                        Trace($"slide-draw stable generation={slideGeneration} new={(newSource != null ? newSource.GetType().Name : "null")} opacity={(image?.Opacity ?? -1):0.00}");

                        if (oldSource == null || newSource == null)
                        {
                            if (image != null && slideState != null && slideState.Generation == slideGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                            Remove();
                            return;
                        }

                        var oldBrush = new ImageBrush(oldSource) { Stretch = image.Stretch };
                        var newBrush = new ImageBrush(newSource) { Stretch = image.Stretch };
                        oldBrush.Freeze();
                        newBrush.Freeze();

                        Rect bounds = fixedOverlay ? overlayBounds : new Rect(target.RenderSize);
                        FrameworkElement adorned = fixedOverlay ? overlayRoot : target;
                        AdornerLayer slideLayer = fixedOverlay ? layer : AdornerLayer.GetAdornerLayer(target);
                        if (slideLayer == null)
                        {
                            if (image != null && slideState != null && slideState.Generation == slideGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                            Remove();
                            return;
                        }

                        var slider = new SlideAdorner(
                            adorned,
                            oldBrush,
                            newBrush,
                            dir,
                            fixedOverlay ? (Rect?)bounds : null);
                        slideLayer.Add(slider);

                        // Slider starts at progress 0, so it already paints the
                        // old cover at its original position. It is now safe to
                        // remove the temporary static veil.
                        Remove();

                        Trace($"slide-draw adorner-added generation={slideGeneration} progress={slider.Progress:0.00} profile=premium51 stronger-incoming-fade");

                        bool cleaned = false;
                        void Cleanup(string reason)
                        {
                            if (cleaned) return;
                            cleaned = true;
                            slider.BeginAnimation(SlideAdorner.ProgressProperty, null);
                            try { slideLayer.Remove(slider); } catch { }

                            bool ownsNativeOpacity = image != null && slideState != null && slideState.Generation == slideGeneration;
                            if (ownsNativeOpacity)
                            {
                                image.Opacity = nativeOpacity;
                            }
                            Trace($"slide-draw cleanup generation={slideGeneration} current={(slideState?.Generation ?? -1)} reason={reason} restoreNative={ownsNativeOpacity}");
                        }

                        var backstop = new DispatcherTimer
                        {
                            Interval = CoverDuration + TimeSpan.FromMilliseconds(300)
                        };
                        backstop.Tick += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup("backstop");
                        };
                        backstop.Start();

                        var move = new DoubleAnimation(0.0, 1.0, new Duration(CoverDuration))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                            FillBehavior = FillBehavior.Stop
                        };
                        move.Completed += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup("completed");
                        };
                        slider.BeginAnimation(SlideAdorner.ProgressProperty, move);
                        Trace($"slide-draw animation-start generation={slideGeneration} duration={CoverDuration.TotalMilliseconds:0}ms");
                    });
                }
                else
                {
                    veil.Opacity = 1.0;
                    SwapThen(image, swap, () => Lower(CoverDuration));
                }
            }
            catch
            {
                Remove();
                throw;
            }

            return true;
        }

        // Same idea as SwapThen, but waits until the new Image.Source has
        // stopped changing briefly before revealing it. Playnite can expose its
        // temporary "no image" placeholder while an async cover binding resolves;
        // using the first Source change made slide transitions animate that
        // placeholder instead of the real cover. This is dispatcher-only and does
        // not decode or copy any bitmap.
        private static void SwapThenStable(Image image, Action swap, Action then)
        {
            ImageSource before = image?.Source;
            Trace($"stable wait begin before={(before != null ? before.GetType().Name : "null")}");

            try
            {
                swap();
            }
            catch
            {
                then();
                throw;
            }

            if (image == null)
            {
                then();
                return;
            }

            var descriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            var backstop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            EventHandler onChanged = null;
            bool finished = false;
            ImageSource last = image.Source;

            void Finish()
            {
                if (finished) return;
                finished = true;
                settle.Stop();
                backstop.Stop();
                descriptor.RemoveValueChanged(image, onChanged);
                Trace($"stable finish changed={!ReferenceEquals(image?.Source, before)} source={(image?.Source != null ? image.Source.GetType().Name : "null")}");
                then();
            }

            void ArmSettle()
            {
                settle.Stop();
                settle.Start();
            }

            settle.Tick += (s, e) =>
            {
                settle.Stop();
                // Only reveal after we have moved away from the old source and
                // the latest source stayed unchanged for one short settle window.
                if (!ReferenceEquals(image.Source, before) && ReferenceEquals(image.Source, last))
                {
                    Finish();
                }
                else
                {
                    last = image.Source;
                    ArmSettle();
                }
            };

            onChanged = (s, e) =>
            {
                last = image.Source;
                ArmSettle();
            };

            descriptor.AddValueChanged(image, onChanged);
            backstop.Tick += (s, e) => Finish();
            backstop.Start();

            if (!ReferenceEquals(image.Source, before))
            {
                last = image.Source;
                ArmSettle();
            }
        }

        // Runs the swap, then the continuation once the new picture is
        // actually on the element.
        //
        // Playnite binds its cover tiles with IsAsync, so the write that
        // swap() makes reaches the Image's Source a beat later, from a worker
        // thread. Continuing straight after the swap uncovered the OLD cover
        // and the new one then popped in mid-fade - exactly the hard cut the
        // transition exists to hide. A backstop keeps a tile from staying
        // veiled if the source never changes: the write failed, or the same
        // picture was picked again.
        public static void SwapThen(Image image, Action swap, Action then)
        {
            ImageSource before = image?.Source;

            try
            {
                swap();
            }
            catch
            {
                then();
                throw;
            }

            if (image == null || !ReferenceEquals(image.Source, before))
            {
                then();
                return;
            }

            var descriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            var backstop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            EventHandler onChanged = null;

            void Done()
            {
                backstop.Stop();
                descriptor.RemoveValueChanged(image, onChanged);
                then();
            }

            onChanged = (s, e) =>
            {
                if (!ReferenceEquals(image.Source, before))
                {
                    Done();
                }
            };

            backstop.Tick += (s, e) => Done();
            descriptor.AddValueChanged(image, onChanged);
            backstop.Start();
        }

        // A flat brush used as the outgoing transition layer. When a fixed
        // rectangle is supplied the adorner is attached to the window content,
        // not to the cover itself, so later tile focus/scale animations cannot
        // resize the outgoing picture mid-fade.
        private sealed class VeilAdorner : Adorner
        {
            private readonly Brush _brush;
            private readonly Rect? _fixedBounds;

            public VeilAdorner(UIElement adorned, Brush brush) : this(adorned, brush, null)
            {
            }

            public VeilAdorner(UIElement adorned, Brush brush, Rect? fixedBounds) : base(adorned)
            {
                _brush = brush;
                _fixedBounds = fixedBounds;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect bounds = _fixedBounds ?? new Rect(AdornedElement.RenderSize);
                dc.PushClip(new RectangleGeometry(bounds));
                dc.DrawRectangle(_brush, null, bounds);
                dc.Pop();
            }
        }

        private sealed class SlideAdorner : Adorner
        {
            public static readonly DependencyProperty ProgressProperty =
                DependencyProperty.Register(
                    nameof(Progress),
                    typeof(double),
                    typeof(SlideAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            private readonly Brush _oldBrush;
            private readonly Brush _newBrush;
            private readonly TransitionDirection _direction;
            private readonly Rect? _fixedBounds;

            public double Progress
            {
                get => (double)GetValue(ProgressProperty);
                set => SetValue(ProgressProperty, value);
            }

            public SlideAdorner(
                UIElement adorned,
                Brush oldBrush,
                Brush newBrush,
                TransitionDirection direction,
                Rect? fixedBounds) : base(adorned)
            {
                _oldBrush = oldBrush;
                _newBrush = newBrush;
                _direction = direction;
                _fixedBounds = fixedBounds;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect bounds = _fixedBounds ?? new Rect(AdornedElement.RenderSize);
                double p = Math.Max(0.0, Math.Min(1.0, Progress));
                double oldX = 0.0, oldY = 0.0, newX = 0.0, newY = 0.0;

                // Premium hand-off slide.
                // The previous versions kept two offset covers strongly visible
                // for too long, which creates a muddy/double-image look on small
                // cover tiles. Instead, keep the old cover stable, let the new
                // one glide in softly, then perform a short clean hand-off.
                const double incomingTravel = 0.28;
                const double outgoingTravel = 0.055;

                double SmoothStep(double edge0, double edge1, double value)
                {
                    if (edge1 <= edge0) return value >= edge1 ? 1.0 : 0.0;
                    double t = Math.Max(0.0, Math.Min(1.0, (value - edge0) / (edge1 - edge0)));
                    return t * t * (3.0 - (2.0 * t));
                }

                // Incoming motion settles early so the opacity hand-off happens
                // while both artworks are nearly aligned.
                double incomingMove = 1.0 - SmoothStep(0.00, 0.72, p);
                double outgoingMove = SmoothStep(0.34, 0.92, p);

                switch (_direction)
                {
                    case TransitionDirection.FromLeft:
                        oldX = bounds.Width * outgoingTravel * outgoingMove;
                        newX = -bounds.Width * incomingTravel * incomingMove;
                        break;
                    case TransitionDirection.FromRight:
                        oldX = -bounds.Width * outgoingTravel * outgoingMove;
                        newX = bounds.Width * incomingTravel * incomingMove;
                        break;
                    case TransitionDirection.FromTop:
                        oldY = bounds.Height * outgoingTravel * outgoingMove;
                        newY = -bounds.Height * incomingTravel * incomingMove;
                        break;
                    case TransitionDirection.FromBottom:
                        oldY = -bounds.Height * outgoingTravel * outgoingMove;
                        newY = bounds.Height * incomingTravel * incomingMove;
                        break;
                }

                // Incoming-first fade:
                // the new cover must NOT appear fully solid while it is still
                // offset. It now starts completely transparent and fades in
                // progressively during the slide. The outgoing cover remains
                // solid longer, then fades only during the final hand-off.
                // Stronger incoming fade: keep the new cover visibly ghosted
                // for longer, then let it reach full opacity closer to the end.
                // This makes the blend much more obvious while preserving slide.
                double incomingFade = SmoothStep(0.12, 0.82, p);
                double handoff = SmoothStep(0.46, 0.86, p);

                double newOpacity = incomingFade;
                double oldOpacity = 1.0 - handoff;

                // Tiny settle only on the incoming image. No outgoing zoom.
                double oldScale = 1.0;
                double newScale = 1.015 - (0.015 * SmoothStep(0.0, 0.78, p));

                Rect ScaledRect(double x, double y, double scale)
                {
                    double w = bounds.Width * scale;
                    double h = bounds.Height * scale;
                    return new Rect(
                        x + ((bounds.Width - w) * 0.5),
                        y + ((bounds.Height - h) * 0.5),
                        w,
                        h);
                }

                dc.PushClip(new RectangleGeometry(bounds));

                dc.PushOpacity(oldOpacity);
                dc.DrawRectangle(
                    _oldBrush,
                    null,
                    ScaledRect(bounds.X + oldX, bounds.Y + oldY, oldScale));
                dc.Pop();

                dc.PushOpacity(newOpacity);
                dc.DrawRectangle(
                    _newBrush,
                    null,
                    ScaledRect(bounds.X + newX, bounds.Y + newY, newScale));
                dc.Pop();

                dc.Pop();
            }
        }

    }
}
