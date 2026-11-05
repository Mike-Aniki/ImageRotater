using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ImageRotater.Services
{
    /// <summary>
    /// The same 10x6, ranked mosaic used by Transition.Mosaic, but confined to
    /// a single native cover host. Renders the outgoing frame as a solid base
    /// and progressively reveals the incoming frame tile-by-tile. Neither
    /// Playnite's Image.Source nor the live IR renderer is modified.
    ///
    /// With only an outgoing still, its tiles dissolve to reveal the live
    /// incoming video below. With only an incoming still, its tiles reveal
    /// that image over the live outgoing video below.
    /// </summary>
    internal sealed class NativeCoverMosaicVisual : FrameworkElement
    {
        internal static readonly DependencyProperty ProgressProperty =
            DependencyProperty.Register(nameof(Progress), typeof(double), typeof(NativeCoverMosaicVisual),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        private readonly ImageBrush oldBrush;
        private readonly ImageBrush newBrush;

        internal double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        private NativeCoverMosaicVisual(Image template, ImageSource oldSource, ImageSource newSource)
        {
            IsHitTestVisible = false;
            oldBrush = MakeBrush(template, oldSource);
            newBrush = MakeBrush(template, newSource);
        }

        private static ImageBrush MakeBrush(Image template, ImageSource source)
        {
            if (source == null) return null;
            return new ImageBrush(source)
            {
                Stretch = template.Stretch,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
        }

        internal static NativeCoverMosaicVisual Begin(Grid layer, Image template, ImageSource outgoing,
            ImageSource incoming, TimeSpan duration)
        {
            if (layer == null || template == null || (outgoing == null && incoming == null))
                return null;

            var visual = new NativeCoverMosaicVisual(template, outgoing, incoming);
            Panel.SetZIndex(visual, 10);
            layer.Children.Add(visual);

            var animation = new DoubleAnimation(0.0, 1.0, new Duration(duration))
            {
                FillBehavior = FillBehavior.HoldEnd
            };
            visual.BeginAnimation(ProgressProperty, animation);
            return visual;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var bounds = new Rect(RenderSize);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            double progress = Math.Max(0, Math.Min(1, Progress));
            dc.PushClip(new RectangleGeometry(bounds));

            // Both stills: completely cover the already-updated native control
            // with the departing still. Never fade the departing still globally.
            if (oldBrush != null && newBrush != null)
                dc.DrawRectangle(oldBrush, null, bounds);

            double w = bounds.Width / MosaicMask.Columns;
            double h = bounds.Height / MosaicMask.Rows;
            int i = 0;
            for (int y = 0; y < MosaicMask.Rows; y++)
            for (int x = 0; x < MosaicMask.Columns; x++, i++)
            {
                double opacity = MosaicMask.TileOpacity(i, progress);
                // Outgoing still -> incoming video: fade away the old tiles.
                // Incoming still -> outgoing video: reveal the new tiles.
                Brush brush = newBrush ?? oldBrush;
                if (newBrush == null) opacity = 1.0 - opacity;
                if (opacity <= .001) continue;

                double left = x * w;
                double top = y * h;
                double right = x == MosaicMask.Columns - 1 ? bounds.Right : left + w + .75;
                double bottom = y == MosaicMask.Rows - 1 ? bounds.Bottom : top + h + .75;
                dc.PushClip(new RectangleGeometry(new Rect(left, top, right - left, bottom - top)));
                dc.PushOpacity(opacity);
                dc.DrawRectangle(brush, null, bounds);
                dc.Pop();
                dc.Pop();
            }
            dc.Pop();
        }
    }
}
