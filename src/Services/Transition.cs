using System;
using System.Collections.Generic;
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
        SlideFromRight,     // Subtle slide from the right
        Cut,                // No animation at all
        Zoom,               // Incoming picture settles from a gentle zoom
        Focus,              // Incoming picture sharpens from a soft blur
        SideReveal,         // New picture is revealed softly from the side
        DiagonalReveal,     // New picture is revealed diagonally with a soft edge
        DepthShift,         // Old picture recedes while the new one settles forward
        Mosaic,        // New picture appears through a mosaic of digital blocks
        Pixelate        // Image pixelates into a retro 16-bit style hand-off
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
        // - Zoom: 650 ms
        // - Focus: 800 ms
        // - SideReveal: 650 ms
        // - DiagonalReveal: 650 ms
        // - DepthShift: 700 ms
        // - Mosaic: 700 ms
        // - Pixelate: 1250 ms
        // - Crossfade / FadeThroughBlack / FadeThroughWhite: 400 ms
        // - Cut: immediate
        private static readonly TimeSpan StandardDuration = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(800);
        private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(650);
        private static readonly TimeSpan FocusDuration = TimeSpan.FromMilliseconds(800);
        private static readonly TimeSpan SideRevealDuration = TimeSpan.FromMilliseconds(650);
        private static readonly TimeSpan DiagonalRevealDuration = TimeSpan.FromMilliseconds(650);
        private static readonly TimeSpan DepthShiftDuration = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan MosaicDuration = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan PixelateDuration = TimeSpan.FromMilliseconds(1250);

        public static TimeSpan DurationFor(TransitionStyle style)
        {
            if (style == TransitionStyle.Cut)
            {
                return TimeSpan.Zero;
            }

            if (style == TransitionStyle.SlideFromRight)
            {
                return SlideDuration;
            }

            if (style == TransitionStyle.Zoom)
            {
                return ZoomDuration;
            }

            if (style == TransitionStyle.Focus)
            {
                return FocusDuration;
            }

            if (style == TransitionStyle.SideReveal)
            {
                return SideRevealDuration;
            }

            if (style == TransitionStyle.DiagonalReveal)
            {
                return DiagonalRevealDuration;
            }

            if (style == TransitionStyle.DepthShift)
            {
                return DepthShiftDuration;
            }

            if (style == TransitionStyle.Mosaic)
            {
                return MosaicDuration;
            }

            if (style == TransitionStyle.Pixelate)
            {
                return PixelateDuration;
            }

            return StandardDuration;
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

        // Transitions an Image around a source swap without touching its visual tree:
        // the veil is an adorner, WPF's own overlay layer. The two-argument
        // overload uses the configured cover style; the explicit-style overload
        // is also used by the settings preview.
        //
        // Crossfade holds a snapshot of the current picture over the element,
        // swaps underneath it, and dissolves the snapshot. A flash raises a
        // colour, swaps behind it, and lowers it. Returns false when the
        // element has no adorner layer to draw in, so the caller can fall
        // back to something that needs none.
        public static bool Run(FrameworkElement target, Action swap)
        {
            return Run(target, swap, CoverStyle);
        }

        // Used by the settings preview so it can render any selected style
        // without changing the user's actual cover/background transition setting.
        public static bool Run(FrameworkElement target, Action swap, TransitionStyle style)
        {
            if (target == null || swap == null)
            {
                return false;
            }

            TimeSpan runDuration = DurationFor(style);
            TimeSpan runHalf = TimeSpan.FromMilliseconds(runDuration.TotalMilliseconds / 2);
            Trace($"run style={style} duration={runDuration.TotalMilliseconds:0}ms target={target.GetType().Name} size={target.ActualWidth:0}x{target.ActualHeight:0}");

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
                    var up = new DoubleAnimation(1.0, new Duration(runHalf));
                    up.Completed += (s, e) => SwapThen(image, swap, () => Lower(runHalf));
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
                            Interval = runDuration + TimeSpan.FromMilliseconds(300)
                        };
                        backstop.Tick += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup("backstop");
                        };
                        backstop.Start();

                        var move = new DoubleAnimation(0.0, 1.0, new Duration(runDuration))
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
                        Trace($"slide-draw animation-start generation={slideGeneration} duration={runDuration.TotalMilliseconds:0}ms");
                    });
                }
                else if (style == TransitionStyle.SideReveal)
                {
                    double nativeOpacity = image?.Opacity ?? 1.0;
                    ImageSource oldSource = image?.Source;
                    SlideState revealState = null;
                    int revealGeneration = 0;

                    if (image != null)
                    {
                        revealState = SlideStates.GetOrCreateValue(image);
                        revealGeneration = ++revealState.Generation;
                        image.Opacity = 0.0;
                    }

                    SwapThenStable(image, swap, () =>
                    {
                        ImageSource newSource = image?.Source;
                        if (oldSource == null || newSource == null)
                        {
                            if (image != null && revealState != null && revealState.Generation == revealGeneration)
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
                        AdornerLayer revealLayer = fixedOverlay ? layer : AdornerLayer.GetAdornerLayer(target);
                        if (revealLayer == null)
                        {
                            if (image != null && revealState != null && revealState.Generation == revealGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                            Remove();
                            return;
                        }

                        var reveal = new SideRevealAdorner(
                            adorned,
                            oldBrush,
                            newBrush,
                            fixedOverlay ? (Rect?)bounds : null);
                        revealLayer.Add(reveal);
                        Remove();

                        bool cleaned = false;
                        void Cleanup()
                        {
                            if (cleaned) return;
                            cleaned = true;
                            reveal.BeginAnimation(SideRevealAdorner.ProgressProperty, null);
                            try { revealLayer.Remove(reveal); } catch { }

                            if (image != null && revealState != null && revealState.Generation == revealGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                        }

                        var backstop = new DispatcherTimer
                        {
                            Interval = runDuration + TimeSpan.FromMilliseconds(300)
                        };
                        backstop.Tick += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        backstop.Start();

                        var sweep = new DoubleAnimation(0.0, 1.0, new Duration(runDuration))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                            FillBehavior = FillBehavior.Stop
                        };
                        sweep.Completed += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        reveal.BeginAnimation(SideRevealAdorner.ProgressProperty, sweep);
                    });
                }
                else if (style == TransitionStyle.DiagonalReveal)
                {
                    double nativeOpacity = image?.Opacity ?? 1.0;
                    ImageSource oldSource = image?.Source;
                    SlideState revealState = null;
                    int revealGeneration = 0;

                    if (image != null)
                    {
                        revealState = SlideStates.GetOrCreateValue(image);
                        revealGeneration = ++revealState.Generation;
                        image.Opacity = 0.0;
                    }

                    SwapThenStable(image, swap, () =>
                    {
                        ImageSource newSource = image?.Source;
                        if (oldSource == null || newSource == null)
                        {
                            if (image != null && revealState != null && revealState.Generation == revealGeneration)
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
                        AdornerLayer revealLayer = fixedOverlay ? layer : AdornerLayer.GetAdornerLayer(target);
                        if (revealLayer == null)
                        {
                            if (image != null && revealState != null && revealState.Generation == revealGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                            Remove();
                            return;
                        }

                        var reveal = new DiagonalRevealAdorner(
                            adorned,
                            oldBrush,
                            newBrush,
                            fixedOverlay ? (Rect?)bounds : null);
                        revealLayer.Add(reveal);
                        Remove();

                        bool cleaned = false;
                        void Cleanup()
                        {
                            if (cleaned) return;
                            cleaned = true;
                            reveal.BeginAnimation(DiagonalRevealAdorner.ProgressProperty, null);
                            try { revealLayer.Remove(reveal); } catch { }
                            if (image != null && revealState != null && revealState.Generation == revealGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                        }

                        var backstop = new DispatcherTimer
                        {
                            Interval = runDuration + TimeSpan.FromMilliseconds(300)
                        };
                        backstop.Tick += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        backstop.Start();

                        var sweep = new DoubleAnimation(0.0, 1.0, new Duration(runDuration))
                        {
                            FillBehavior = FillBehavior.Stop
                        };
                        sweep.Completed += (s2, e2) =>
                        {
                            backstop.Stop();
                            Application.Current?.Dispatcher.BeginInvoke(
                                DispatcherPriority.Background,
                                new Action(Cleanup));
                        };
                        reveal.BeginAnimation(DiagonalRevealAdorner.ProgressProperty, sweep);
                    });
                }
                else if (style == TransitionStyle.DepthShift)
                {
                    double nativeOpacity = image?.Opacity ?? 1.0;
                    ImageSource oldSource = image?.Source;
                    SlideState depthState = null;
                    int depthGeneration = 0;

                    if (image != null)
                    {
                        depthState = SlideStates.GetOrCreateValue(image);
                        depthGeneration = ++depthState.Generation;
                        image.Opacity = 0.0;
                    }

                    SwapThenStable(image, swap, () =>
                    {
                        ImageSource newSource = image?.Source;
                        if (oldSource == null || newSource == null)
                        {
                            if (image != null && depthState != null && depthState.Generation == depthGeneration)
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
                        AdornerLayer depthLayer = fixedOverlay ? layer : AdornerLayer.GetAdornerLayer(target);
                        if (depthLayer == null)
                        {
                            if (image != null && depthState != null && depthState.Generation == depthGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                            Remove();
                            return;
                        }

                        var depth = new DepthShiftAdorner(
                            adorned,
                            oldBrush,
                            newBrush,
                            fixedOverlay ? (Rect?)bounds : null);
                        depthLayer.Add(depth);
                        Remove();

                        bool cleaned = false;
                        void Cleanup()
                        {
                            if (cleaned) return;
                            cleaned = true;
                            depth.BeginAnimation(DepthShiftAdorner.ProgressProperty, null);
                            try { depthLayer.Remove(depth); } catch { }
                            if (image != null && depthState != null && depthState.Generation == depthGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                        }

                        var backstop = new DispatcherTimer
                        {
                            Interval = runDuration + TimeSpan.FromMilliseconds(300)
                        };
                        backstop.Tick += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        backstop.Start();

                        var settle = new DoubleAnimation(0.0, 1.0, new Duration(runDuration))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                            FillBehavior = FillBehavior.Stop
                        };
                        settle.Completed += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        depth.BeginAnimation(DepthShiftAdorner.ProgressProperty, settle);
                    });
                }
                else if (style == TransitionStyle.Mosaic)
                {
                    double nativeOpacity = image?.Opacity ?? 1.0;
                    ImageSource oldSource = image?.Source;
                    SlideState pixelState = null;
                    int pixelGeneration = 0;

                    if (image != null)
                    {
                        pixelState = SlideStates.GetOrCreateValue(image);
                        pixelGeneration = ++pixelState.Generation;
                        image.Opacity = 0.0;
                    }

                    SwapThenStable(image, swap, () =>
                    {
                        ImageSource newSource = image?.Source;
                        if (oldSource == null || newSource == null)
                        {
                            if (image != null && pixelState != null && pixelState.Generation == pixelGeneration)
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
                        AdornerLayer pixelLayer = fixedOverlay ? layer : AdornerLayer.GetAdornerLayer(target);
                        if (pixelLayer == null)
                        {
                            if (image != null && pixelState != null && pixelState.Generation == pixelGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                            Remove();
                            return;
                        }

                        var pixels = new MosaicAdorner(
                            adorned,
                            oldBrush,
                            newBrush,
                            fixedOverlay ? (Rect?)bounds : null);
                        pixelLayer.Add(pixels);
                        Remove();

                        bool cleaned = false;
                        void Cleanup()
                        {
                            if (cleaned) return;
                            cleaned = true;
                            pixels.BeginAnimation(MosaicAdorner.ProgressProperty, null);
                            try { pixelLayer.Remove(pixels); } catch { }
                            if (image != null && pixelState != null && pixelState.Generation == pixelGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                        }

                        var backstop = new DispatcherTimer
                        {
                            Interval = runDuration + TimeSpan.FromMilliseconds(300)
                        };
                        backstop.Tick += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        backstop.Start();

                        var reveal = new DoubleAnimation(0.0, 1.0, new Duration(runDuration))
                        {
                            FillBehavior = FillBehavior.Stop
                        };
                        reveal.Completed += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        pixels.BeginAnimation(MosaicAdorner.ProgressProperty, reveal);
                    });
                }
                else if (style == TransitionStyle.Pixelate)
                {
                    double nativeOpacity = image?.Opacity ?? 1.0;
                    ImageSource oldSource = image?.Source;
                    if (image == null || oldSource == null)
                    {
                        SwapThen(image, swap, () => Lower(runDuration));
                    }
                    else
                    {
                        IReadOnlyList<ImageSource> oldLevels = PixelateFrames.GetCachedOrSource(oldSource);
                        var oldWarm = PixelateFrames.GetLevelsAsync(oldSource);
                        if (oldLevels.Count == 0)
                        {
                            SwapThen(image, swap, () => Lower(runDuration));
                        }
                        else
                        {
                            SlideState pixelState = SlideStates.GetOrCreateValue(image);
                            int pixelGeneration = ++pixelState.Generation;
                            image.Opacity = 0.0;

                            Rect bounds = fixedOverlay ? overlayBounds : new Rect(target.RenderSize);
                            FrameworkElement adorned = fixedOverlay ? overlayRoot : target;
                            AdornerLayer pixelLayer = fixedOverlay ? layer : AdornerLayer.GetAdornerLayer(target);
                            if (pixelLayer == null)
                            {
                                image.Opacity = nativeOpacity;
                                SwapThen(image, swap, () => Lower(runDuration));
                            }
                            else
                            {
                                var pixelate = new PixelateAdorner(
                                    adorned,
                                    oldLevels,
                                    image.Stretch,
                                    fixedOverlay ? (Rect?)bounds : null);
                                pixelLayer.Add(pixelate);
                                Remove();

                                if (oldLevels.Count <= 1)
                                {
                                    _ = WarmOldLevelsAsync();
                                }

                                async System.Threading.Tasks.Task WarmOldLevelsAsync()
                                {
                                    IReadOnlyList<ImageSource> warmed = await oldWarm;
                                    if (pixelState.Generation != pixelGeneration || warmed == null || warmed.Count <= 1)
                                    {
                                        return;
                                    }

                                    pixelate.SetOldLevels(warmed);
                                }

                                bool cleaned = false;
                                void Cleanup()
                                {
                                    if (cleaned) return;
                                    cleaned = true;
                                    pixelate.BeginAnimation(PixelateAdorner.ProgressProperty, null);
                                    try { pixelLayer.Remove(pixelate); } catch { }

                                    if (pixelState.Generation == pixelGeneration)
                                    {
                                        image.Opacity = nativeOpacity;
                                    }
                                }

                                var backstop = new DispatcherTimer
                                {
                                    Interval = runDuration + TimeSpan.FromMilliseconds(350)
                                };
                                backstop.Tick += (s2, e2) =>
                                {
                                    backstop.Stop();
                                    Cleanup();
                                };
                                backstop.Start();

                                var progress = new DoubleAnimation(0.0, 1.0, new Duration(runDuration))
                                {
                                    FillBehavior = FillBehavior.Stop
                                };
                                progress.Completed += (s2, e2) =>
                                {
                                    backstop.Stop();
                                    Cleanup();
                                };
                                pixelate.BeginAnimation(PixelateAdorner.ProgressProperty, progress);

                                // Start the retro breakup immediately. The native
                                // source can resolve underneath the adorner while
                                // the old image is already visibly pixelating.
                                SwapThenStable(image, swap, () =>
                                {
                                    ImageSource newSource = image.Source;
                                    if (newSource == null)
                                    {
                                        return;
                                    }

                                    _ = WarmNewLevelsAsync(newSource);

                                    async System.Threading.Tasks.Task WarmNewLevelsAsync(ImageSource source)
                                    {
                                        IReadOnlyList<ImageSource> newLevels = await PixelateFrames.GetLevelsAsync(source);
                                        if (pixelState.Generation != pixelGeneration || newLevels == null || newLevels.Count == 0)
                                        {
                                            return;
                                        }

                                        pixelate.SetNewLevels(newLevels);
                                    }
                                });
                            }
                        }
                    }
                }
                else if (style == TransitionStyle.Zoom)
                {
                    double nativeOpacity = image?.Opacity ?? 1.0;
                    ImageSource oldSource = image?.Source;
                    SlideState zoomState = null;
                    int zoomGeneration = 0;

                    if (image != null)
                    {
                        zoomState = SlideStates.GetOrCreateValue(image);
                        zoomGeneration = ++zoomState.Generation;
                        image.Opacity = 0.0;
                    }

                    SwapThenStable(image, swap, () =>
                    {
                        ImageSource newSource = image?.Source;
                        if (oldSource == null || newSource == null)
                        {
                            if (image != null && zoomState != null && zoomState.Generation == zoomGeneration)
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
                        AdornerLayer zoomLayer = fixedOverlay ? layer : AdornerLayer.GetAdornerLayer(target);
                        if (zoomLayer == null)
                        {
                            if (image != null && zoomState != null && zoomState.Generation == zoomGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                            Remove();
                            return;
                        }

                        var zoom = new ZoomAdorner(
                            adorned,
                            oldBrush,
                            newBrush,
                            fixedOverlay ? (Rect?)bounds : null);
                        zoomLayer.Add(zoom);
                        Remove();

                        bool cleaned = false;
                        void Cleanup()
                        {
                            if (cleaned) return;
                            cleaned = true;
                            zoom.BeginAnimation(ZoomAdorner.ProgressProperty, null);
                            try { zoomLayer.Remove(zoom); } catch { }

                            if (image != null && zoomState != null && zoomState.Generation == zoomGeneration)
                            {
                                image.Opacity = nativeOpacity;
                            }
                        }

                        var backstop = new DispatcherTimer
                        {
                            Interval = runDuration + TimeSpan.FromMilliseconds(300)
                        };
                        backstop.Tick += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        backstop.Start();

                        var settle = new DoubleAnimation(0.0, 1.0, new Duration(runDuration))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                            FillBehavior = FillBehavior.Stop
                        };
                        settle.Completed += (s2, e2) =>
                        {
                            backstop.Stop();
                            Cleanup();
                        };
                        zoom.BeginAnimation(ZoomAdorner.ProgressProperty, settle);
                    });
                }
                else if (style == TransitionStyle.Focus)
                {
                    if (image == null || image.Effect != null)
                    {
                        veil.Opacity = 1.0;
                        SwapThen(image, swap, () => Lower(runDuration));
                    }
                    else
                    {
                        double nativeOpacity = image.Opacity;
                        SlideState focusState = SlideStates.GetOrCreateValue(image);
                        int focusGeneration = ++focusState.Generation;
                        image.Opacity = 0.0;

                        SwapThenStable(image, swap, () =>
                        {
                            if (image.Source == null)
                            {
                                if (focusState.Generation == focusGeneration)
                                {
                                    image.Opacity = nativeOpacity;
                                }
                                Remove();
                                return;
                            }

                            var blur = new System.Windows.Media.Effects.BlurEffect
                            {
                                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                                Radius = 26.0,
                                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                            };
                            var oldBlur = new System.Windows.Media.Effects.BlurEffect
                            {
                                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                                Radius = 0.0,
                                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                            };
                            image.Effect = blur;
                            veil.Effect = oldBlur;
                            image.Opacity = 0.0;

                            bool cleaned = false;
                            void Cleanup()
                            {
                                if (cleaned) return;
                                cleaned = true;

                                if (focusState.Generation == focusGeneration)
                                {
                                    image.BeginAnimation(UIElement.OpacityProperty, null);
                                    image.Opacity = nativeOpacity;
                                    blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                                    if (ReferenceEquals(image.Effect, blur))
                                    {
                                        image.Effect = null;
                                    }
                                }

                                oldBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                                if (ReferenceEquals(veil.Effect, oldBlur))
                                {
                                    veil.Effect = null;
                                }
                                Remove();
                            }

                            var backstop = new DispatcherTimer
                            {
                                Interval = runDuration + TimeSpan.FromMilliseconds(300)
                            };
                            backstop.Tick += (s2, e2) =>
                            {
                                backstop.Stop();
                                Cleanup();
                            };
                            backstop.Start();

                            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
                            var fadeIn = new DoubleAnimation(0.0, nativeOpacity, new Duration(runDuration))
                            {
                                EasingFunction = easeOut
                            };
                            var sharpen = new DoubleAnimationUsingKeyFrames
                            {
                                Duration = new Duration(runDuration)
                            };
                            sharpen.KeyFrames.Add(new LinearDoubleKeyFrame(
                                26.0, KeyTime.FromPercent(0.24)));
                            sharpen.KeyFrames.Add(new EasingDoubleKeyFrame(
                                0.0,
                                KeyTime.FromPercent(1.0),
                                easeOut));

                            var defocusOld = new DoubleAnimationUsingKeyFrames
                            {
                                Duration = new Duration(runDuration)
                            };
                            defocusOld.KeyFrames.Add(new LinearDoubleKeyFrame(
                                0.0, KeyTime.FromPercent(0.08)));
                            defocusOld.KeyFrames.Add(new EasingDoubleKeyFrame(
                                20.0,
                                KeyTime.FromPercent(0.48),
                                new CubicEase { EasingMode = EasingMode.EaseIn }));
                            defocusOld.KeyFrames.Add(new LinearDoubleKeyFrame(
                                20.0, KeyTime.FromPercent(1.0)));
                            var fadeOld = new DoubleAnimationUsingKeyFrames
                            {
                                Duration = new Duration(runDuration)
                            };
                            fadeOld.KeyFrames.Add(new LinearDoubleKeyFrame(
                                1.0, KeyTime.FromPercent(0.22)));
                            fadeOld.KeyFrames.Add(new EasingDoubleKeyFrame(
                                0.0,
                                KeyTime.FromPercent(0.86),
                                new CubicEase { EasingMode = EasingMode.EaseIn }));
                            fadeOld.Completed += (s2, e2) =>
                            {
                                backstop.Stop();
                                Cleanup();
                            };

                            image.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, sharpen);
                            oldBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, defocusOld);
                            veil.BeginAnimation(UIElement.OpacityProperty, fadeOld);
                        });
                    }
                }
                else
                {
                    veil.Opacity = 1.0;
                    SwapThen(image, swap, () => Lower(runDuration));
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

        private sealed class SideRevealAdorner : Adorner
        {
            public static readonly DependencyProperty ProgressProperty =
                DependencyProperty.Register(
                    nameof(Progress),
                    typeof(double),
                    typeof(SideRevealAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            private readonly Brush _oldBrush;
            private readonly Brush _newBrush;
            private readonly Rect? _fixedBounds;

            public double Progress
            {
                get => (double)GetValue(ProgressProperty);
                set => SetValue(ProgressProperty, value);
            }

            public SideRevealAdorner(
                UIElement adorned,
                Brush oldBrush,
                Brush newBrush,
                Rect? fixedBounds) : base(adorned)
            {
                _oldBrush = oldBrush;
                _newBrush = newBrush;
                _fixedBounds = fixedBounds;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect bounds = _fixedBounds ?? new Rect(AdornedElement.RenderSize);
                double p = Math.Max(0.0, Math.Min(1.0, Progress));
                double eased = p * p * (3.0 - (2.0 * p));
                double feather = Math.Max(1.0, bounds.Width * 0.34);
                double travel = bounds.Width + feather;
                double startX = bounds.Right - (travel * eased);
                double endX = startX + feather;

                var revealMask = new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(startX, bounds.Top),
                    EndPoint = new Point(endX, bounds.Top),
                    SpreadMethod = GradientSpreadMethod.Pad
                };
                revealMask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.0));
                revealMask.GradientStops.Add(new GradientStop(Colors.White, 1.0));
                revealMask.Freeze();

                dc.PushClip(new RectangleGeometry(bounds));
                dc.DrawRectangle(_oldBrush, null, bounds);
                dc.PushOpacityMask(revealMask);
                dc.DrawRectangle(_newBrush, null, bounds);
                dc.Pop();
                dc.Pop();
            }
        }

        private sealed class DiagonalRevealAdorner : Adorner
        {
            public static readonly DependencyProperty ProgressProperty =
                DependencyProperty.Register(
                    nameof(Progress),
                    typeof(double),
                    typeof(DiagonalRevealAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            private readonly Brush _oldBrush;
            private readonly Brush _newBrush;
            private readonly Rect? _fixedBounds;

            public double Progress
            {
                get => (double)GetValue(ProgressProperty);
                set => SetValue(ProgressProperty, value);
            }

            public DiagonalRevealAdorner(UIElement adorned, Brush oldBrush, Brush newBrush, Rect? fixedBounds)
                : base(adorned)
            {
                _oldBrush = oldBrush;
                _newBrush = newBrush;
                _fixedBounds = fixedBounds;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect bounds = _fixedBounds ?? new Rect(AdornedElement.RenderSize);
                double p = Math.Max(0.0, Math.Min(1.0, Progress));
                double responsive = 1.0 - Math.Pow(1.0 - p, 2.2);
                double eased = 0.055 + (1.095 * responsive);
                double feather = Math.Max(1.0, Math.Max(bounds.Width, bounds.Height) * 0.40);
                double travelX = bounds.Width + (feather * 1.18);
                double travelY = bounds.Height + (feather * 1.18);
                double sx = bounds.Right - (travelX * eased);
                double sy = bounds.Top - feather + (travelY * eased);
                double ex = sx + feather;
                double ey = sy + feather;

                dc.PushClip(new RectangleGeometry(bounds));

                var revealMask = new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(sx, sy),
                    EndPoint = new Point(ex, ey),
                    SpreadMethod = GradientSpreadMethod.Pad
                };
                revealMask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.0));
                revealMask.GradientStops.Add(new GradientStop(Colors.White, 1.0));
                revealMask.Freeze();

                dc.DrawRectangle(_oldBrush, null, bounds);
                dc.PushOpacityMask(revealMask);
                dc.DrawRectangle(_newBrush, null, bounds);
                dc.Pop();
                dc.Pop();
            }
        }

        private sealed class DepthShiftAdorner : Adorner
        {
            public static readonly DependencyProperty ProgressProperty =
                DependencyProperty.Register(
                    nameof(Progress),
                    typeof(double),
                    typeof(DepthShiftAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            private readonly Brush _oldBrush;
            private readonly Brush _newBrush;
            private readonly Rect? _fixedBounds;

            public double Progress
            {
                get => (double)GetValue(ProgressProperty);
                set => SetValue(ProgressProperty, value);
            }

            public DepthShiftAdorner(UIElement adorned, Brush oldBrush, Brush newBrush, Rect? fixedBounds)
                : base(adorned)
            {
                _oldBrush = oldBrush;
                _newBrush = newBrush;
                _fixedBounds = fixedBounds;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect bounds = _fixedBounds ?? new Rect(AdornedElement.RenderSize);
                double p = Math.Max(0.0, Math.Min(1.0, Progress));

                double SmoothStep(double edge0, double edge1, double value)
                {
                    if (edge1 <= edge0) return value >= edge1 ? 1.0 : 0.0;
                    double t = Math.Max(0.0, Math.Min(1.0, (value - edge0) / (edge1 - edge0)));
                    return t * t * (3.0 - (2.0 * t));
                }

                double incomingOpacity = SmoothStep(0.02, 0.74, p);
                double outgoingOpacity = 1.0 - SmoothStep(0.26, 0.84, p);
                double incomingScale = 1.045 - (0.045 * SmoothStep(0.0, 0.92, p));
                double outgoingScale = 1.0 - (0.035 * SmoothStep(0.0, 0.84, p));

                Rect ScaledRect(double scale)
                {
                    double w = bounds.Width * scale;
                    double h = bounds.Height * scale;
                    return new Rect(
                        bounds.X + ((bounds.Width - w) * 0.5),
                        bounds.Y + ((bounds.Height - h) * 0.5),
                        w,
                        h);
                }

                dc.PushClip(new RectangleGeometry(bounds));
                dc.PushOpacity(outgoingOpacity);
                dc.DrawRectangle(_oldBrush, null, ScaledRect(outgoingScale));
                dc.Pop();

                dc.PushOpacity(incomingOpacity);
                dc.DrawRectangle(_newBrush, null, ScaledRect(incomingScale));
                dc.Pop();
                dc.Pop();
            }
        }

        private sealed class PixelateAdorner : Adorner
        {
            public static readonly DependencyProperty ProgressProperty =
                DependencyProperty.Register(
                    nameof(Progress),
                    typeof(double),
                    typeof(PixelateAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            private IReadOnlyList<ImageSource> _oldLevels;
            private IReadOnlyList<ImageSource> _newLevels;
            private readonly ImageBrush _brush;
            private readonly Rect? _fixedBounds;

            public double Progress
            {
                get => (double)GetValue(ProgressProperty);
                set => SetValue(ProgressProperty, value);
            }

            public PixelateAdorner(
                UIElement adorned,
                IReadOnlyList<ImageSource> oldLevels,
                Stretch stretch,
                Rect? fixedBounds) : base(adorned)
            {
                _oldLevels = oldLevels;
                _fixedBounds = fixedBounds;
                _brush = new ImageBrush
                {
                    Stretch = stretch,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
                IsHitTestVisible = false;
            }

            public void SetOldLevels(IReadOnlyList<ImageSource> levels)
            {
                _oldLevels = levels;
                InvalidateVisual();
            }

            public void SetNewLevels(IReadOnlyList<ImageSource> levels)
            {
                _newLevels = levels;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect bounds = _fixedBounds ?? new Rect(AdornedElement.RenderSize);
                double p = Math.Max(0.0, Math.Min(1.0, Progress));
                dc.PushClip(new RectangleGeometry(bounds));

                if (p < 0.48 || _newLevels == null || _newLevels.Count == 0)
                {
                    DrawLevel(dc, bounds, _oldLevels, LevelIndex(_oldLevels, p / 0.48));
                }
                else if (p <= 0.54)
                {
                    double mix = (p - 0.48) / 0.06;
                    DrawLevel(dc, bounds, _oldLevels, _oldLevels.Count - 1, 1.0 - mix);
                    DrawLevel(dc, bounds, _newLevels, _newLevels.Count - 1, mix);
                }
                else
                {
                    double u = (p - 0.54) / 0.46;
                    int max = _newLevels.Count - 1;
                    int index = max - LevelIndex(_newLevels, u);
                    DrawLevel(dc, bounds, _newLevels, index);
                }

                dc.Pop();
            }

            private static int LevelIndex(IReadOnlyList<ImageSource> levels, double value)
            {
                if (levels == null || levels.Count <= 1)
                {
                    return 0;
                }

                double u = Math.Max(0.0, Math.Min(1.0, value));
                double smooth = u * u * (3.0 - (2.0 * u));
                return Math.Max(0, Math.Min(levels.Count - 1,
                    (int)Math.Round(smooth * (levels.Count - 1))));
            }

            private void DrawLevel(
                DrawingContext dc,
                Rect bounds,
                IReadOnlyList<ImageSource> levels,
                int index,
                double opacity = 1.0)
            {
                if (levels == null || levels.Count == 0 || opacity <= 0.0)
                {
                    return;
                }

                index = Math.Max(0, Math.Min(levels.Count - 1, index));
                _brush.ImageSource = levels[index];
                dc.PushOpacity(Math.Max(0.0, Math.Min(1.0, opacity)));
                dc.DrawRectangle(_brush, null, bounds);
                dc.Pop();
            }
        }

        private sealed class MosaicAdorner : Adorner
        {
            public static readonly DependencyProperty ProgressProperty =
                DependencyProperty.Register(
                    nameof(Progress),
                    typeof(double),
                    typeof(MosaicAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            private readonly Brush _oldBrush;
            private readonly Brush _newBrush;
            private readonly Rect? _fixedBounds;

            public double Progress
            {
                get => (double)GetValue(ProgressProperty);
                set => SetValue(ProgressProperty, value);
            }

            public MosaicAdorner(UIElement adorned, Brush oldBrush, Brush newBrush, Rect? fixedBounds)
                : base(adorned)
            {
                _oldBrush = oldBrush;
                _newBrush = newBrush;
                _fixedBounds = fixedBounds;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect bounds = _fixedBounds ?? new Rect(AdornedElement.RenderSize);
                double p = Math.Max(0.0, Math.Min(1.0, Progress));

                dc.PushClip(new RectangleGeometry(bounds));
                dc.DrawRectangle(_oldBrush, null, bounds);

                double cellWidth = bounds.Width / MosaicMask.Columns;
                double cellHeight = bounds.Height / MosaicMask.Rows;
                int index = 0;

                for (int row = 0; row < MosaicMask.Rows; row++)
                {
                    for (int column = 0; column < MosaicMask.Columns; column++)
                    {
                        double opacity = MosaicMask.TileOpacity(index, p);
                        if (opacity > 0.001)
                        {
                            double x = bounds.X + (column * cellWidth);
                            double y = bounds.Y + (row * cellHeight);
                            double right = column == MosaicMask.Columns - 1
                                ? bounds.Right
                                : x + cellWidth + 0.75;
                            double bottom = row == MosaicMask.Rows - 1
                                ? bounds.Bottom
                                : y + cellHeight + 0.75;
                            var tile = new Rect(x, y, Math.Max(0.0, right - x), Math.Max(0.0, bottom - y));

                            dc.PushClip(new RectangleGeometry(tile));
                            dc.PushOpacity(opacity);
                            dc.DrawRectangle(_newBrush, null, bounds);
                            dc.Pop();
                            dc.Pop();
                        }

                        index++;
                    }
                }

                dc.Pop();
            }
        }

        private sealed class ZoomAdorner : Adorner
        {
            public static readonly DependencyProperty ProgressProperty =
                DependencyProperty.Register(
                    nameof(Progress),
                    typeof(double),
                    typeof(ZoomAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            private readonly Brush _oldBrush;
            private readonly Brush _newBrush;
            private readonly Rect? _fixedBounds;

            public double Progress
            {
                get => (double)GetValue(ProgressProperty);
                set => SetValue(ProgressProperty, value);
            }

            public ZoomAdorner(UIElement adorned, Brush oldBrush, Brush newBrush, Rect? fixedBounds)
                : base(adorned)
            {
                _oldBrush = oldBrush;
                _newBrush = newBrush;
                _fixedBounds = fixedBounds;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                Rect bounds = _fixedBounds ?? new Rect(AdornedElement.RenderSize);
                double p = Math.Max(0.0, Math.Min(1.0, Progress));

                double SmoothStep(double edge0, double edge1, double value)
                {
                    if (edge1 <= edge0) return value >= edge1 ? 1.0 : 0.0;
                    double t = Math.Max(0.0, Math.Min(1.0, (value - edge0) / (edge1 - edge0)));
                    return t * t * (3.0 - (2.0 * t));
                }

                double settle = SmoothStep(0.0, 0.92, p);
                double scale = 1.08 - (0.08 * settle);
                double newOpacity = SmoothStep(0.02, 0.68, p);
                double oldOpacity = 1.0 - SmoothStep(0.34, 0.92, p);

                double width = bounds.Width * scale;
                double height = bounds.Height * scale;
                var zoomed = new Rect(
                    bounds.X + ((bounds.Width - width) * 0.5),
                    bounds.Y + ((bounds.Height - height) * 0.5),
                    width,
                    height);

                dc.PushClip(new RectangleGeometry(bounds));

                dc.PushOpacity(oldOpacity);
                dc.DrawRectangle(_oldBrush, null, bounds);
                dc.Pop();

                dc.PushOpacity(newOpacity);
                dc.DrawRectangle(_newBrush, null, zoomed);
                dc.Pop();

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
