using System;
using System.ComponentModel;
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
        Cut                 // No animation at all
    }

    // Shared transition style and timing for native and plugin renderers.
    //
    // Covers and backgrounds are chosen separately: a flash that reads as a
    // beat on a small tile is a full-screen strobe on a background.
    public static class Transition
    {
        public static TransitionStyle CoverStyle { get; set; } = TransitionStyle.Crossfade;
        public static TransitionStyle BackgroundStyle { get; set; } = TransitionStyle.Crossfade;

        // One duration for every renderer, so a cover and a background
        // changing together finish together. A flash spends half going up
        // and half coming down.
        public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(400);
        public static TimeSpan Half => TimeSpan.FromMilliseconds(Duration.TotalMilliseconds / 2);

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

            if (style == TransitionStyle.Cut)
            {
                swap();
                return true;
            }

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null)
            {
                return false;
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
            var veil = new VeilAdorner(target, brush);
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
                    var up = new DoubleAnimation(1.0, new Duration(Half));
                    up.Completed += (s, e) => SwapThen(image, swap, () => Lower(Half));
                    veil.BeginAnimation(UIElement.OpacityProperty, up);
                }
                else
                {
                    veil.Opacity = 1.0;
                    SwapThen(image, swap, () => Lower(Duration));
                }
            }
            catch
            {
                Remove();
                throw;
            }

            return true;
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

        // A flat brush drawn over exactly the adorned element's bounds. Follows
        // the element's transforms (a theme's selected-tile scale, for one)
        // because that is what an adorner layer does.
        private sealed class VeilAdorner : Adorner
        {
            private readonly Brush _brush;

            public VeilAdorner(UIElement adorned, Brush brush) : base(adorned)
            {
                _brush = brush;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(_brush, null, new Rect(AdornedElement.RenderSize));
            }
        }
    }
}
