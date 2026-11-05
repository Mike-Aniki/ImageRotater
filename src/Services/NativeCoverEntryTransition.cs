using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImageRotater.Controls;

namespace ImageRotater.Services
{
    // Selected-only Grid mode: animate native -> IR after the new medium is READY.
    // Only temporary tile-local images are manipulated. Native Image.Source,
    // DataContext, bindings, size and parent are left untouched.
    internal sealed class NativeCoverEntryTransition : IDisposable
    {
        private readonly Grid host;
        private readonly Grid layer;
        private readonly CoverImageControl renderer;
        private readonly DispatcherTimer watchdog;
        private readonly Action completed;
        private readonly bool rendererHidden;
        private bool finished;
        internal bool UsesVideo { get; private set; }

        internal static TransitionStyle EffectiveStyle(TransitionStyle style) =>
            NativeCoverReturnTransition.EffectiveStyle(style);

        private NativeCoverEntryTransition(Grid host, Grid layer, CoverImageControl renderer,
            DispatcherTimer watchdog, Action completed, bool rendererHidden, bool usesVideo)
        {
            this.host = host;
            this.layer = layer;
            this.renderer = renderer;
            this.watchdog = watchdog;
            this.completed = completed;
            this.rendererHidden = rendererHidden;
            UsesVideo = usesVideo;
        }

        internal static NativeCoverEntryTransition Start(Grid host, Image native,
            CoverImageControl renderer, Action completed, ImageSource departing = null)
        {
            if (host == null || native == null || renderer == null || completed == null ||
                native.Source == null || native.ActualWidth <= 0 || native.ActualHeight <= 0 ||
                !renderer.CanHideNativeCover || EffectiveStyle(Transition.CoverStyle) == TransitionStyle.Cut)
                return null;

            var style = EffectiveStyle(Transition.CoverStyle);
            // The departing cover is what was on screen: a held previous-game
            // cover when Playnite has already swapped native to the new game.
            var currentNative = departing ?? native.Source;
            // A video can have a stale DisplayImage.Source behind it; do not
            // accidentally animate that old bitmap as the incoming cover.
            var liveVideo = renderer.FindName("DisplayVideo") as MediaElement;
            bool video = liveVideo != null && liveVideo.Visibility == Visibility.Visible &&
                liveVideo.Source != null && liveVideo.Opacity >= .999;
            var irStill = video ? null : NativeCoverReturnTransition.VisibleStill(renderer);
            video = video || irStill == null;
            var layer = new Grid { IsHitTestVisible = false, ClipToBounds = true };
            Grid.SetZIndex(layer, 100);
            foreach (string name in new[] { "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
                "Margin", "HorizontalAlignment", "VerticalAlignment", "RenderTransform", "RenderTransformOrigin",
                "LayoutTransform", "Clip", "OpacityMask" })
            {
                var dp = System.ComponentModel.DependencyPropertyDescriptor.FromName(
                    name, typeof(FrameworkElement), typeof(FrameworkElement))?.DependencyProperty;
                if (dp != null)
                    BindingOperations.SetBinding(layer, dp, new Binding(name) { Source = native });
            }

            var outgoing = new Image
            {
                Source = currentNative, Stretch = native.Stretch,
                StretchDirection = native.StretchDirection, Opacity = 1,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(outgoing, RenderOptions.GetBitmapScalingMode(native));
            Grid.SetZIndex(outgoing, 2);
            layer.Children.Add(outgoing);
            Image incoming = null;
            if (!video)
            {
                incoming = new Image
                {
                    Source = irStill, Stretch = native.Stretch,
                    StretchDirection = native.StretchDirection, Opacity = 0,
                    IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(incoming, RenderOptions.GetBitmapScalingMode(native));
                Grid.SetZIndex(incoming, 3);
                layer.Children.Add(incoming);
            }
            TimeSpan duration = Transition.DurationFor(style);
            var watchdog = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = duration + TimeSpan.FromMilliseconds(style == TransitionStyle.Pixelate ? 1200 : 250) };
            var result = new NativeCoverEntryTransition(host, layer, renderer,
                watchdog, completed, !video, video);
            try
            {
                host.Children.Add(layer);
                layer.Measure(new Size(native.ActualWidth, native.ActualHeight));
                layer.Arrange(new Rect(0, 0, native.ActualWidth, native.ActualHeight));
                // For stills, animate the ready IR bitmap above a temporary
                // copy of native; reveal the live renderer only at completion.
                // Video is different: the renderer remains live below native.
                if (!video)
                    renderer.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0, new Duration(TimeSpan.Zero)));
                result.Animate(style, outgoing, incoming, native.ActualWidth, native.ActualHeight, duration);
                watchdog.Tick += (s, e) => result.Finish(true);
                watchdog.Start();
                return result;
            }
            catch (Exception ex)
            {
                Transition.DebugLog?.Invoke($"COVER native-entry-error style={style} reason={ex.GetType().Name}: {ex.Message}");
                result.Dispose();
                return null;
            }
        }

        private void Animate(TransitionStyle style, Image outgoing, Image incoming,
            double width, double height, TimeSpan duration)
        {
            NativeCoverRelayEffects.Apply(layer, style, outgoing, incoming, width, height, duration, () =>
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
            watchdog.Stop();
            if (rendererHidden) renderer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            if (host.Children.Contains(layer)) host.Children.Remove(layer);
            if (notify) completed();
        }
        public void Dispose() => Finish(false);
    }
}
