using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ImageRotater.Services
{
    // Transition between two ordinary Playnite cover sources. The original Image,
    // Source binding and layout are never modified. Temporary visuals live only
    // inside the details cover's Grid (never in a window-wide adorner).
    internal sealed class NativeCoverNativeTransition : IDisposable
    {
        private readonly Grid parent;
        private readonly Grid layer;
        private readonly DispatcherTimer backstop;
        private readonly Action completed;
        private bool finished;

        internal static TransitionStyle EffectiveStyle(TransitionStyle style) =>
            NativeCoverReturnTransition.EffectiveStyle(style);

        private NativeCoverNativeTransition(Grid parent, Grid layer, DispatcherTimer backstop, Action completed)
        {
            this.parent = parent;
            this.layer = layer;
            this.backstop = backstop;
            this.completed = completed;
        }

        private static Image MakeImage(Image template, ImageSource source, double opacity)
        {
            var image = new Image
            {
                Source = source,
                Stretch = template.Stretch,
                StretchDirection = template.StretchDirection,
                Opacity = opacity,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(image, RenderOptions.GetBitmapScalingMode(template));
            return image;
        }

        internal static Grid CreateHoldingLayer(Image native, ImageSource previous)
        {
            if (native == null || previous == null || !(VisualTreeHelper.GetParent(native) is Grid parent) ||
                native.ActualWidth <= 0 || native.ActualHeight <= 0) return null;

            var layer = new Grid { IsHitTestVisible = false, ClipToBounds = true };
            Grid.SetRow(layer, Grid.GetRow(native));
            Grid.SetColumn(layer, Grid.GetColumn(native));
            Grid.SetRowSpan(layer, Grid.GetRowSpan(native));
            Grid.SetColumnSpan(layer, Grid.GetColumnSpan(native));
            Panel.SetZIndex(layer, Math.Max(Panel.GetZIndex(native) + 1, 100));
            foreach (string name in new[] { "Width", "Height", "MinWidth", "MinHeight", "MaxWidth",
                "MaxHeight", "Margin", "HorizontalAlignment", "VerticalAlignment", "OpacityMask",
                "Clip", "RenderTransformOrigin" })
            {
                var dp = System.ComponentModel.DependencyPropertyDescriptor.FromName(
                    name, typeof(FrameworkElement), typeof(FrameworkElement))?.DependencyProperty;
                if (dp != null) BindingOperations.SetBinding(layer, dp, new Binding(name) { Source = native });
            }
            layer.Children.Add(MakeImage(native, previous, 1));
            parent.Children.Add(layer);
            return layer;
        }

        internal static NativeCoverNativeTransition Start(Image native, Grid layer,
            ImageSource arriving, Action completed)
        {
            if (native == null || layer == null || arriving == null || completed == null ||
                !(layer.Parent is Grid parent) || layer.Children.Count == 0 ||
                native.ActualWidth <= 0 || native.ActualHeight <= 0) return null;
            var style = EffectiveStyle(Transition.CoverStyle);
            if (style == TransitionStyle.Cut) return null;
            var departing = layer.Children[0] as Image;
            if (departing == null || departing.Source == null) return null;
            var incoming = MakeImage(native, arriving, 0);
            Grid.SetZIndex(incoming, 2);
            layer.Children.Add(incoming);
            var duration = Transition.DurationFor(style);
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = duration + TimeSpan.FromMilliseconds(style == TransitionStyle.Pixelate ? 1200 : 250)
            };
            var transition = new NativeCoverNativeTransition(parent, layer, timer, completed);
            try
            {
                layer.Measure(new Size(native.ActualWidth, native.ActualHeight));
                layer.Arrange(new Rect(0, 0, native.ActualWidth, native.ActualHeight));
                transition.Animate(style, incoming, departing, native.ActualWidth, native.ActualHeight, duration);
                timer.Tick += (s, e) => transition.Finish(true);
                timer.Start();
                return transition;
            }
            catch (Exception ex)
            {
                Transition.DebugLog?.Invoke($"COVER native-native-error style={style} reason={ex.GetType().Name}: {ex.Message}");
                transition.Dispose();
                return null;
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
            if (parent.Children.Contains(layer)) parent.Children.Remove(layer);
            if (notify) completed();
        }

        public void Dispose() => Finish(false);
    }
}
