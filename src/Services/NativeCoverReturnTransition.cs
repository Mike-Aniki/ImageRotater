using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImageRotater.Controls;

namespace ImageRotater.Services
{
    // A tile-local, cancellable IR -> native handoff. No native Source/parent/binding is changed.
    // In particular, no full-window adorner may outlive a recycled Playnite grid item.
    internal sealed class NativeCoverReturnTransition : IDisposable
    {
        private readonly Grid host;
        private readonly Grid layer;
        private readonly CoverImageControl renderer;
        private readonly DispatcherTimer backstop;
        private readonly Action completed;
        private bool finished;
        internal bool UsesLiveRenderer { get; private set; }

        internal static TransitionStyle EffectiveStyle(TransitionStyle configured) =>
            NativeCoverRelayEffects.Supports(configured) ? configured : TransitionStyle.Crossfade;

        internal static ImageSource VisibleStill(CoverImageControl renderer)
        {
            if (renderer == null) return null;
            // PreviousImage is painted ABOVE DisplayImage in the renderer's
            // XAML. Taking DisplayImage first captured an already-replaced
            // frame when a native return interrupted an IR -> IR transition.
            foreach (string name in new[] { "UnfocusedStillImage", "PreviousImage", "DisplayImage" })
            {
                if (renderer.FindName(name) is Image image &&
                    image.Visibility == Visibility.Visible && image.Source != null &&
                    image.Opacity > .01 &&
                    !((image.Source is System.Windows.Media.Imaging.BitmapImage bitmap) && bitmap.IsDownloading))
                    return image.Source;
            }
            return null; // A live video is handled without inventing a still frame.
        }

        private NativeCoverReturnTransition(Grid host, Grid layer, CoverImageControl renderer,
            DispatcherTimer backstop, Action completed)
        {
            this.host = host;
            this.layer = layer;
            this.renderer = renderer;
            this.backstop = backstop;
            this.completed = completed;
        }

        internal static NativeCoverReturnTransition Start(Grid host, Image native,
            CoverImageControl renderer, Action completed)
        {
            if (host == null || native == null || renderer == null || completed == null ||
                native.Source == null || native.ActualWidth <= 0 || native.ActualHeight <= 0)
                return null;

            // A still may remain visible as a WPF element underneath a playing
            // MediaElement. Do not mistake that stale bitmap for the outgoing
            // frame: keep the actual video playing under the mosaic instead.
            var media = renderer.FindName("DisplayVideo") as MediaElement;
            bool videoPlaying = media != null && media.Visibility == Visibility.Visible &&
                media.Source != null && media.Opacity >= .999;
            ImageSource outgoing = videoPlaying ? null : VisibleStill(renderer);
            ImageSource incoming = native.Source;
            TransitionStyle style = EffectiveStyle(Transition.CoverStyle);
            if (style == TransitionStyle.Cut) return null;

            // The two images are prepared while the live IR renderer is still visible.
            // Then the renderer is hidden in the *same* UI turn: no exposed native frame.
            var layer = new Grid { IsHitTestVisible = false, ClipToBounds = true };
            foreach (string name in new[] { "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
                "Margin", "HorizontalAlignment", "VerticalAlignment", "RenderTransform", "RenderTransformOrigin",
                "LayoutTransform", "Clip", "OpacityMask" })
            {
                var dp = System.ComponentModel.DependencyPropertyDescriptor.FromName(
                    name, typeof(FrameworkElement), typeof(FrameworkElement))?.DependencyProperty;
                if (dp != null)
                    System.Windows.Data.BindingOperations.SetBinding(layer, dp,
                        new System.Windows.Data.Binding(name) { Source = native });
            }
            var arriving = new Image
            {
                Source = incoming, Stretch = native.Stretch, StretchDirection = native.StretchDirection,
                Opacity = 0, IsHitTestVisible = false
            };
            Image oldStill = outgoing == null ? null : new Image
            {
                Source = outgoing, Stretch = native.Stretch, StretchDirection = native.StretchDirection,
                IsHitTestVisible = false
            };
            FrameworkElement departing = oldStill ?? (FrameworkElement)renderer;
            RenderOptions.SetBitmapScalingMode(arriving, RenderOptions.GetBitmapScalingMode(native));
            Grid.SetZIndex(arriving, 2);
            layer.Children.Add(arriving);
            if (oldStill != null)
            {
                RenderOptions.SetBitmapScalingMode(oldStill, RenderOptions.GetBitmapScalingMode(native));
                Grid.SetZIndex(oldStill, 1);
                layer.Children.Add(oldStill);
            }

            TimeSpan duration = Transition.DurationFor(style);
            var timer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = duration + TimeSpan.FromMilliseconds(style == TransitionStyle.Pixelate ? 1200 : 250) };
            var transition = new NativeCoverReturnTransition(host, layer, renderer, timer, completed)
                { UsesLiveRenderer = oldStill == null };
            try
            {
                host.Children.Add(layer);
                // Arrange once without disturbing the native image or the parent's tree.
                layer.Measure(new Size(native.ActualWidth, native.ActualHeight));
                layer.Arrange(new Rect(0, 0, native.ActualWidth, native.ActualHeight));
                if (oldStill != null)
                    renderer.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0.0, new Duration(TimeSpan.Zero)));
                // When the old media is a video, keep the live MediaElement
                // beneath the new image and animate it out directly. No frame
                // capture, media reparent, or poster decode is required.
                transition.Animate(style, arriving, departing, native.ActualWidth, native.ActualHeight, duration);
                timer.Tick += (s, e) => transition.Finish(true);
                timer.Start();
                return transition;
            }
            catch (Exception ex)
            {
                Transition.DebugLog?.Invoke($"COVER native-return-error style={style} reason={ex.GetType().Name}: {ex.Message}");
                transition.Dispose();
                renderer.BeginAnimation(UIElement.OpacityProperty, null);
                return null; // Caller uses an explicit emergency opacity fallback.
            }
        }

        private void Animate(TransitionStyle style, Image arriving, FrameworkElement departing,
            double width, double height, TimeSpan duration)
        {
            NativeCoverRelayEffects.Apply(layer, style, departing as Image, arriving,
                width, height, duration, () =>
                {
                    if (finished) return;
                    var marker = new DoubleAnimation(1, 1, new Duration(duration));
                    marker.Completed += (s, e) => Finish(true);
                    layer.BeginAnimation(UIElement.OpacityProperty, marker);
                });
        }

        private void Finish(bool notify)
        {
            if (finished) return;
            finished = true;
            backstop.Stop();
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            if (host.Children.Contains(layer)) host.Children.Remove(layer);
            if (notify) completed();
        }

        public void Dispose() => Finish(false);
    }
}
