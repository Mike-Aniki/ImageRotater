using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ImageRotater.Services
{
    internal sealed class PixelRevealMaskState
    {
        public PixelRevealMaskState(VisualBrush brush, List<Rectangle> tiles)
        {
            Brush = brush;
            Tiles = tiles;
        }

        public VisualBrush Brush { get; }
        public List<Rectangle> Tiles { get; }
    }

    internal static class PixelRevealMask
    {
        public const int Columns = 10;
        public const int Rows = 6;
        public const int TileCount = Columns * Rows;

        private static readonly int[] TileRanks = BuildTileRanks();

        public static PixelRevealMaskState Create()
        {
            // A VisualBrush is deliberately used instead of a DrawingBrush.
            // The opacity of real WPF Rectangle elements updates live while the
            // brush is used as an OpacityMask, which makes the mosaic animation
            // reliable on both theme-hosted images and Playnite FadeImage.
            var canvas = new Canvas
            {
                Width = Columns,
                Height = Rows,
                Background = Brushes.Transparent,
                IsHitTestVisible = false
            };

            var tiles = new List<Rectangle>(TileCount);
            const double overlap = 0.035;

            for (int row = 0; row < Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    var tile = new Rectangle
                    {
                        Width = 1.0 + overlap,
                        Height = 1.0 + overlap,
                        Fill = Brushes.White,
                        Opacity = 1.0,
                        SnapsToDevicePixels = false,
                        IsHitTestVisible = false
                    };

                    Canvas.SetLeft(tile, column - (overlap * 0.5));
                    Canvas.SetTop(tile, row - (overlap * 0.5));
                    canvas.Children.Add(tile);
                    tiles.Add(tile);
                }
            }

            var brush = new VisualBrush(canvas)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0.0, 0.0, Columns, Rows),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0.0, 0.0, 1.0, 1.0),
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };

            return new PixelRevealMaskState(brush, tiles);
        }

        public static void SetVisible(PixelRevealMaskState state, bool visible)
        {
            if (state == null)
            {
                return;
            }

            double opacity = visible ? 1.0 : 0.0;
            foreach (Rectangle tile in state.Tiles)
            {
                tile.BeginAnimation(UIElement.OpacityProperty, null);
                tile.Opacity = opacity;
            }
        }

        public static Storyboard BuildStoryboard(
            FrameworkElement target,
            PixelRevealMaskState state,
            bool incoming,
            TimeSpan duration)
        {
            var storyboard = new Storyboard
            {
                Duration = new Duration(duration)
            };

            // This target animation doubles as the transition clock. The visual
            // change itself comes from the independently animated mask tiles.
            Timeline opacity;
            if (incoming)
            {
                opacity = new DoubleAnimation(1.0, 1.0, new Duration(duration));
            }
            else
            {
                var hideAtEnd = new DoubleAnimationUsingKeyFrames
                {
                    Duration = new Duration(duration)
                };
                hideAtEnd.KeyFrames.Add(new LinearDoubleKeyFrame(
                    1.0,
                    KeyTime.FromPercent(0.985)));
                hideAtEnd.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                    0.0,
                    KeyTime.FromPercent(1.0)));
                opacity = hideAtEnd;
            }

            Storyboard.SetTarget(opacity, target);
            Storyboard.SetTargetProperty(opacity, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(opacity);

            for (int i = 0; i < state.Tiles.Count; i++)
            {
                double rank = TileRanks[i] / (double)(TileCount - 1);

                // Start almost immediately, distribute the blocks across most of
                // the transition, then leave a short fully-revealed settle at end.
                double start = 0.01 + (rank * 0.68);
                double end = Math.Min(0.92, start + 0.20);
                double initial = incoming ? 0.0 : 1.0;
                double final = incoming ? 1.0 : 0.0;

                var animation = new DoubleAnimationUsingKeyFrames
                {
                    Duration = new Duration(duration)
                };
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                    initial,
                    KeyTime.FromPercent(0.0)));
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                    initial,
                    KeyTime.FromPercent(start)));
                animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                    final,
                    KeyTime.FromPercent(end),
                    new CubicEase { EasingMode = EasingMode.EaseOut }));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(
                    final,
                    KeyTime.FromPercent(1.0)));

                Storyboard.SetTarget(animation, state.Tiles[i]);
                Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
                storyboard.Children.Add(animation);
            }

            return storyboard;
        }

        public static double TileOpacity(int tileIndex, double progress)
        {
            if (tileIndex < 0 || tileIndex >= TileCount)
            {
                return 0.0;
            }

            double p = Math.Max(0.0, Math.Min(1.0, progress));
            double rank = TileRanks[tileIndex] / (double)(TileCount - 1);
            double start = 0.01 + (rank * 0.68);
            double end = Math.Min(0.92, start + 0.20);

            if (p <= start)
            {
                return 0.0;
            }

            if (p >= end)
            {
                return 1.0;
            }

            double t = (p - start) / Math.Max(0.0001, end - start);
            double inv = 1.0 - t;
            return 1.0 - (inv * inv * inv);
        }

        public static void Stop(PixelRevealMaskState state)
        {
            if (state == null)
            {
                return;
            }

            SetVisible(state, true);
        }

        private static int[] BuildTileRanks()
        {
            var order = new int[TileCount];
            var ranks = new int[TileCount];

            for (int i = 0; i < TileCount; i++)
            {
                order[i] = i;
            }

            // Stable pseudo-random order: every transition has the same polished
            // rhythm instead of visibly reshuffling on every image change.
            uint state = 0x4D595DF4u;
            for (int i = TileCount - 1; i > 0; i--)
            {
                state = (state * 1664525u) + 1013904223u;
                int j = (int)(state % (uint)(i + 1));
                int swap = order[i];
                order[i] = order[j];
                order[j] = swap;
            }

            for (int rank = 0; rank < TileCount; rank++)
            {
                ranks[order[rank]] = rank;
            }

            return ranks;
        }
    }
}
