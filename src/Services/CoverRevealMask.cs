using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ImageRotater.Services
{
    /// <summary>
    /// Shared, feathered reveal geometry for IR -> IR adorners and native
    /// cover relays. The moving edge is fully outside the cover at both
    /// endpoints, so removing the temporary visual cannot create a hard cut.
    /// Reveal direction is always right-to-left (Side) or top-right to
    /// bottom-left (Diagonal), independent of the previous/native cover path.
    /// </summary>
    internal static class CoverRevealMask
    {
        internal struct Edge
        {
            internal readonly Point Start;
            internal readonly Point End;

            internal Edge(Point start, Point end)
            {
                Start = start;
                End = end;
            }
        }

        internal static Edge At(TransitionStyle style, Rect bounds, double progress)
        {
            // Caller controls easing. Clamp for interrupted/restarted clocks.
            double p = Math.Max(0.0, Math.Min(1.0, progress));
            double width = Math.Max(0.0, bounds.Width);
            double height = Math.Max(0.0, bounds.Height);

            if (style == TransitionStyle.SideReveal)
            {
                double feather = Math.Max(1.0, width * 0.34);
                double cutoff = (width + feather) * (1.0 - p);
                return new Edge(
                    new Point(bounds.Left + cutoff - feather, bounds.Top),
                    new Point(bounds.Left + cutoff, bounds.Top));
            }

            if (style != TransitionStyle.DiagonalReveal)
                throw new ArgumentOutOfRangeException(nameof(style));

            // Project the cover onto a constant top-right -> bottom-left
            // diagonal. Using the (x - y) axis keeps a true soft gradient,
            // unlike a rotated RectangleGeometry.
            const double unit = 0.7071067811865475; // 1 / sqrt(2)
            double diagonalFeather = Math.Max(1.0, Math.Max(width, height) * 0.40);
            double minProjection = -height * unit;
            double maxProjection = width * unit;
            double projectionRange = maxProjection - minProjection;
            double diagonalCutoff = minProjection + (projectionRange + diagonalFeather) * (1.0 - p);
            double from = diagonalCutoff - diagonalFeather;
            double to = diagonalCutoff;
            return new Edge(
                new Point(bounds.Left + from * unit, bounds.Top - from * unit),
                new Point(bounds.Left + to * unit, bounds.Top - to * unit));
        }

        internal static LinearGradientBrush Create(TransitionStyle style, Rect bounds,
            double progress, bool revealingIncoming)
        {
            Edge edge = At(style, bounds, progress);
            var result = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                SpreadMethod = GradientSpreadMethod.Pad,
                StartPoint = edge.Start,
                EndPoint = edge.End
            };
            // Incoming new image starts invisible and is unveiled right-to-left.
            // For a live incoming video, the outgoing still gets the INVERSE
            // mask so the video is uncovered rather than blended with native.
            result.GradientStops.Add(new GradientStop(
                revealingIncoming ? Colors.Transparent : Colors.White, 0.0));
            result.GradientStops.Add(new GradientStop(
                revealingIncoming ? Colors.White : Colors.Transparent, 1.0));
            return result;
        }

        internal static void Animate(LinearGradientBrush brush, TransitionStyle style,
            Rect bounds, TimeSpan duration, Action completed = null)
        {
            if (brush == null) return;
            Edge end = At(style, bounds, 1.0);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var startMove = new PointAnimation(end.Start, new Duration(duration))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            var endMove = new PointAnimation(end.End, new Duration(duration))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            if (completed != null)
                endMove.Completed += (sender, args) => completed();
            brush.BeginAnimation(LinearGradientBrush.StartPointProperty, startMove);
            brush.BeginAnimation(LinearGradientBrush.EndPointProperty, endMove);
        }
    }
}
