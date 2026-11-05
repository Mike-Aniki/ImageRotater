using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ImageRotater.Services
{
    /// <summary>
    /// Single compositing policy for native -> IR, IR -> native and Desktop
    /// native -> native. Only temporary, tile-local stills are transformed.
    /// A playing MediaElement remains in its existing renderer and is never
    /// reparented, clipped or resized by the transition.
    /// </summary>
    internal static class NativeCoverRelayEffects
    {
        internal static bool Supports(TransitionStyle style) =>
            Enum.IsDefined(typeof(TransitionStyle), style);

        private static DoubleAnimation Tween(double from, double to, TimeSpan duration,
            TimeSpan? delay = null)
        {
            return new DoubleAnimation(from, to, new Duration(duration))
            {
                BeginTime = delay ?? TimeSpan.Zero,
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
        }

        private static void Fade(UIElement element, double from, double to, TimeSpan duration,
            TimeSpan? delay = null)
        {
            if (element != null)
                element.BeginAnimation(UIElement.OpacityProperty, Tween(from, to, duration, delay));
        }

        private static void Scale(FrameworkElement element, double from, double to, TimeSpan duration)
        {
            if (element == null) return;
            var t = new ScaleTransform(from, from);
            element.RenderTransformOrigin = new Point(.5, .5);
            element.RenderTransform = t;
            t.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(from, to, duration));
            t.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(from, to, duration));
        }

        private static void Shift(FrameworkElement element, double from, double to, TimeSpan duration)
        {
            if (element == null) return;
            var t = new TranslateTransform(from, 0);
            element.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.XProperty, Tween(from, to, duration));
        }

        private static void Blur(FrameworkElement element, double from, double to, TimeSpan duration)
        {
            if (element == null) return;
            var blur = new BlurEffect { Radius = from, RenderingBias = RenderingBias.Performance };
            element.Effect = blur;
            blur.BeginAnimation(BlurEffect.RadiusProperty, Tween(from, to, duration));
        }

        private static void SetStack(Image outgoing, Image incoming, bool incomingOnTop)
        {
            if (outgoing != null) { outgoing.Opacity = 1; Panel.SetZIndex(outgoing, incomingOnTop ? 1 : 2); }
            if (incoming != null) { incoming.Opacity = 1; Panel.SetZIndex(incoming, incomingOnTop ? 2 : 1); }
        }

        private static void Wipe(Image outgoing, Image incoming, bool diagonal,
            double width, double height, TimeSpan duration)
        {
            // The previous native implementation used a rotated hard-edged
            // RectangleGeometry. It exposed the full new cover abruptly (or
            // left a corner uncovered) when the native relay was removed.
            // Use exactly the same feathered gradient as the IR -> IR adorner.
            TransitionStyle style = diagonal ? TransitionStyle.DiagonalReveal : TransitionStyle.SideReveal;
            var bounds = new Rect(0, 0, width, height);
            if (incoming != null)
            {
                // Old still stays fully opaque UNDER the incoming soft mask.
                var mask = CoverRevealMask.Create(style, bounds, 0.0, true);
                incoming.OpacityMask = mask;
                CoverRevealMask.Animate(mask, style, bounds, duration);
            }
            else if (outgoing != null)
            {
                // Native -> live IR video: inverse mask on the outgoing still
                // reveals the running video without touching/reparenting it.
                var mask = CoverRevealMask.Create(style, bounds, 0.0, false);
                outgoing.OpacityMask = mask;
                CoverRevealMask.Animate(mask, style, bounds, duration);
            }
        }

        internal static void Apply(Grid layer, TransitionStyle style, Image outgoing,
            Image incoming, double width, double height, TimeSpan duration, Action started = null)
        {
            if (layer == null || (outgoing == null && incoming == null)) return;
            if (style == TransitionStyle.Pixelate &&
                (!IsPixelateReady(outgoing?.Source) || !IsPixelateReady(incoming?.Source)))
            {
                StartPixelateWhenReady(layer, outgoing, incoming, width, height,
                    duration, started);
                return;
            }
            ApplyCore(layer, style, outgoing, incoming, width, height, duration);
            started?.Invoke();
        }

        private static bool IsPixelateReady(ImageSource source)
        {
            // DrawingImage/non-bitmap sources cannot be sampled by
            // PixelateFrames and are intentionally treated as ready.
            return source == null || !(source is System.Windows.Media.Imaging.BitmapSource) ||
                PixelateFrames.TryGetLevels(source, out _);
        }

        private static async void StartPixelateWhenReady(Grid layer, Image outgoing,
            Image incoming, double width, double height, TimeSpan duration, Action started)
        {
            try
            {
                var jobs = new List<Task>();
                if (!IsPixelateReady(outgoing?.Source))
                    jobs.Add(PixelateFrames.GetLevelsAsync(outgoing.Source));
                if (!IsPixelateReady(incoming?.Source))
                    jobs.Add(PixelateFrames.GetLevelsAsync(incoming.Source));
                // Bounded asynchronous prewarm: no bitmap decode on the UI
                // thread and no indefinitely frozen outgoing cover.
                Task all = Task.WhenAll(jobs);
                Task winner = await Task.WhenAny(all, Task.Delay(850));
                if (layer.Parent == null) return; // canceled/recycled tile
                if (winner != all)
                    Transition.DebugLog?.Invoke("COVER native-pixelate prewarm-timeout; use warmed levels if available");
                else await all; // observe any worker failure
            }
            catch (Exception ex)
            {
                Transition.DebugLog?.Invoke("COVER native-pixelate prewarm-error " + ex.Message);
                if (layer.Parent == null) return;
            }
            if (layer.Parent == null) return;
            try
            {
                ApplyCore(layer, TransitionStyle.Pixelate, outgoing, incoming,
                    width, height, duration);
                started?.Invoke();
            }
            catch (Exception ex)
            {
                Transition.DebugLog?.Invoke("COVER native-pixelate effect-error " + ex.Message);
                // The host's watchdog will release the outgoing cover; do
                // not invoke a completion callback for a failed start.
            }
        }

        private static void ApplyCore(Grid layer, TransitionStyle style, Image outgoing,
            Image incoming, double width, double height, TimeSpan duration)
        {
            // Solid stills always cover Playnite's already-updated Source.
            // Crossfade and Zoom reveal the complete incoming still UNDER
            // an outgoing still; directional and focus effects reveal ABOVE.
            bool incomingOnTop = outgoing == null ||
                style == TransitionStyle.SideReveal || style == TransitionStyle.DiagonalReveal ||
                style == TransitionStyle.Focus;
            SetStack(outgoing, incoming, incomingOnTop);

            switch (style)
            {
                case TransitionStyle.FadeThroughBlack:
                case TransitionStyle.FadeThroughWhite:
                {
                    var veil = new Rectangle
                    {
                        Fill = new SolidColorBrush(style == TransitionStyle.FadeThroughBlack
                            ? Colors.Black : Colors.White),
                        IsHitTestVisible = false,
                        Opacity = 0
                    };
                    Panel.SetZIndex(veil, 20);
                    layer.Children.Add(veil);
                    var half = TimeSpan.FromTicks(duration.Ticks / 2);
                    var clock = new DoubleAnimationUsingKeyFrames
                    {
                        Duration = new Duration(duration), FillBehavior = FillBehavior.HoldEnd
                    };
                    clock.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    clock.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(half)));
                    clock.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration)));
                    veil.BeginAnimation(UIElement.OpacityProperty, clock);
                    // The veil completely covers the swap at half-time. A
                    // discrete switch cannot cause an unintended blend.
                    if (outgoing != null)
                    {
                        var switchOld = new DoubleAnimationUsingKeyFrames();
                        switchOld.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                        switchOld.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(half)));
                        outgoing.BeginAnimation(UIElement.OpacityProperty, switchOld);
                    }
                    if (incoming != null && outgoing == null)
                    {
                        var switchNew = new DoubleAnimationUsingKeyFrames();
                        switchNew.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                        switchNew.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(half)));
                        incoming.BeginAnimation(UIElement.OpacityProperty, switchNew);
                    }
                    break;
                }
                case TransitionStyle.SlideFromRight:
                    // Still-to-still: outgoing must be on top. With live
                    // video only the available still is animated.
                    if (incoming != null && outgoing != null)
                    {
                        // The moving incoming still may uncover a strip at the
                        // right. Keep a full opaque copy as the local base.
                        var baseImage = new Image
                        {
                            Source = incoming.Source, Stretch = incoming.Stretch,
                            StretchDirection = incoming.StretchDirection,
                            IsHitTestVisible = false, Opacity = 1
                        };
                        RenderOptions.SetBitmapScalingMode(baseImage,
                            RenderOptions.GetBitmapScalingMode(incoming));
                        Panel.SetZIndex(baseImage, 0);
                        layer.Children.Add(baseImage);
                    }
                    if (incoming != null) Shift(incoming, width * .32, 0, duration);
                    if (outgoing != null) Shift(outgoing, 0, -width * .12, duration);
                    if (outgoing != null) Fade(outgoing, 1, 0, duration);
                    else Fade(incoming, 0, 1, duration);
                    break;
                case TransitionStyle.Zoom:
                    if (incoming != null) Scale(incoming, 1.085, 1, duration);
                    else Scale(outgoing, 1, .965, duration);
                    if (outgoing != null) Fade(outgoing, 1, 0, duration);
                    else Fade(incoming, 0, 1, duration);
                    break;
                case TransitionStyle.Focus:
                    // Do not blur/fade the entire host: it contains a native
                    // Image whose Source may already have advanced.
                    if (incoming != null)
                    {
                        Blur(incoming, 14, 0, duration);
                        Fade(incoming, 0, 1, duration);
                    }
                    if (outgoing != null)
                    {
                        Blur(outgoing, 0, 10, duration);
                        if (incoming == null) Fade(outgoing, 1, 0, duration);
                    }
                    break;
                case TransitionStyle.SideReveal:
                case TransitionStyle.DiagonalReveal:
                    Wipe(outgoing, incoming, style == TransitionStyle.DiagonalReveal,
                        width, height, duration);
                    break;
                case TransitionStyle.DepthShift:
                    if (incoming != null) Scale(incoming, 1.045, 1, duration);
                    if (outgoing != null) Scale(outgoing, 1, .965, duration);
                    if (outgoing != null) Fade(outgoing, 1, 0, duration);
                    else Fade(incoming, 0, 1, duration);
                    break;
                case TransitionStyle.Mosaic:
                {
                    // V9 composition rule: the *entire* incoming bitmap must
                    // never show behind the tiles. NativeCoverMosaicVisual
                    // paints the old still in full and then only the new tiles.
                    Image template = incoming ?? outgoing;
                    var mosaic = NativeCoverMosaicVisual.Begin(layer, template,
                        outgoing?.Source, incoming?.Source, duration);
                    if (mosaic != null)
                    {
                        if (outgoing != null) outgoing.Opacity = 0;
                        if (incoming != null) incoming.Opacity = 0;
                    }
                    break;
                }
                case TransitionStyle.Pixelate:
                    ApplyPixelate(layer, outgoing, incoming, duration);
                    break;
                case TransitionStyle.Cut:
                    if (outgoing != null) outgoing.Opacity = 0;
                    if (incoming != null) incoming.Opacity = 1;
                    break;
                case TransitionStyle.Crossfade:
                default:
                    // One opaque base and one dissolving top. Never fade
                    // BOTH opaque stills simultaneously (transparency leak).
                    if (outgoing != null) Fade(outgoing, 1, 0, duration);
                    else Fade(incoming, 0, 1, duration);
                    break;
            }
        }

        private static void ApplyPixelate(Grid layer, Image outgoing, Image incoming, TimeSpan total)
        {
            // PixelateFrames decodes on workers and caches immutable levels.
            // Never synchronously decode extra images on Playnite's UI thread.
            // The available still is pixelated even when the other medium is
            // a live video (a MediaElement cannot be sampled as ImageSource).
            var oldSource = outgoing?.Source;
            var newSource = incoming?.Source;
            var first = TimeSpan.FromTicks((long)(total.Ticks * .48));
            var second = total - first;
            var blend = TimeSpan.FromTicks((long)(total.Ticks * .06));
            if (outgoing != null)
            {
                RenderOptions.SetBitmapScalingMode(outgoing, BitmapScalingMode.NearestNeighbor);
                PixelateFrames.QueuePrewarm(oldSource);
                IReadOnlyList<ImageSource> oldLevels = PixelateFrames.GetCachedOrSource(oldSource);
                if (oldLevels.Count > 1)
                    outgoing.BeginAnimation(Image.SourceProperty,
                        PixelateFrames.BuildOutgoingPhase(oldLevels, first));
                UpgradePixelate(layer, outgoing, oldSource, first, false);
                Fade(outgoing, 1, 0, blend, first - TimeSpan.FromTicks(blend.Ticks / 2));
            }
            if (incoming != null)
            {
                RenderOptions.SetBitmapScalingMode(incoming, BitmapScalingMode.NearestNeighbor);
                IReadOnlyList<ImageSource> levels = PixelateFrames.GetCachedOrSource(newSource);
                if (levels.Count > 1)
                {
                    incoming.Source = levels[levels.Count - 1];
                    incoming.BeginAnimation(Image.SourceProperty,
                        PixelateFrames.BuildIncomingPhase(levels, second, .1)
                            .WithBeginTime(first));
                }
                UpgradePixelate(layer, incoming, newSource, second, true, first);
                // With an outgoing still, preserve its full opacity until
                // pixelation crosses the seam; for video departure the
                // incoming still similarly remains hidden during phase one.
                if (outgoing == null)
                {
                    incoming.Opacity = 0;
                    Fade(incoming, 0, 1, blend, first - TimeSpan.FromTicks(blend.Ticks / 2));
                }
                // With two stills, the incoming image remains opaque as the
                // base. Only the outgoing image fades; otherwise blending
                // two 50% images would leak the host's native background.
            }
        }

        private static async void UpgradePixelate(Grid layer, Image image,
            ImageSource source, TimeSpan duration, bool incoming,
            TimeSpan? begin = null)
        {
            if (image == null || source == null) return;
            try
            {
                // Already cached levels are scheduled synchronously above.
                if (PixelateFrames.TryGetLevels(source, out _)) return;
                var levels = await PixelateFrames.GetLevelsAsync(source);
                if (layer.Parent == null || levels == null || levels.Count <= 1) return;
                // If the phase has already progressed, never restart the
                // animation or show another game's stale image.
                if (incoming)
                {
                    if (image.Source != source) return;
                    image.Source = levels[levels.Count - 1];
                    var animation = PixelateFrames.BuildIncomingPhase(levels, duration, .1);
                    animation.BeginTime = begin ?? TimeSpan.Zero;
                    image.BeginAnimation(Image.SourceProperty, animation);
                }
                else
                {
                    if (image.Source != source) return;
                    image.BeginAnimation(Image.SourceProperty,
                        PixelateFrames.BuildOutgoingPhase(levels, duration));
                }
            }
            catch (Exception ex)
            {
                Transition.DebugLog?.Invoke("COVER pixelate-prewarm-skipped " + ex.Message);
            }
        }

        private static ObjectAnimationUsingKeyFrames WithBeginTime(
            this ObjectAnimationUsingKeyFrames animation, TimeSpan begin)
        {
            animation.BeginTime = begin;
            return animation;
        }
    }
}
