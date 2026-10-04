using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ImageRotater.Services
{
    internal static class PixelateFrames
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Enough intermediate resolutions to make the hand-off feel progressive,
        // while keeping the coarsest frames distinctly retro and blocky.
        private static readonly int[] Divisors = { 2, 3, 5, 7, 10, 14, 20, 28, 38 };

        private sealed class CachedLevels
        {
            public IReadOnlyList<ImageSource> Levels;
        }

        private sealed class PendingLevels
        {
            public Task<IReadOnlyList<ImageSource>> Task;
        }

        // Pixelated variants are immutable once frozen. Reusing them prevents a
        // transition from rebuilding a stack of bitmaps every time the same art
        // comes back on screen, which is the main source of occasional hitches.
        private static readonly ConditionalWeakTable<ImageSource, CachedLevels> Cache =
            new ConditionalWeakTable<ImageSource, CachedLevels>();

        private static readonly ConditionalWeakTable<ImageSource, PendingLevels> Pending =
            new ConditionalWeakTable<ImageSource, PendingLevels>();

        public static bool TryGetLevels(ImageSource source, out IReadOnlyList<ImageSource> levels)
        {
            levels = null;
            if (source == null)
            {
                return false;
            }

            if (Cache.TryGetValue(source, out CachedLevels cached) && cached?.Levels != null)
            {
                levels = cached.Levels;
                return true;
            }

            return false;
        }

        public static IReadOnlyList<ImageSource> GetCachedOrSource(ImageSource source)
        {
            if (TryGetLevels(source, out IReadOnlyList<ImageSource> levels))
            {
                return levels;
            }

            QueuePrewarm(source);
            return source == null
                ? (IReadOnlyList<ImageSource>)Array.Empty<ImageSource>()
                : new[] { source };
        }

        public static void QueuePrewarm(ImageSource source)
        {
            if (source == null || TryGetLevels(source, out _))
            {
                return;
            }

            try
            {
                _ = GetLevelsAsync(source);
            }
            catch
            {
                // Prewarming is opportunistic. The synchronous fallback remains available.
            }
        }

        public static Task<IReadOnlyList<ImageSource>> GetLevelsAsync(ImageSource source)
        {
            if (source == null)
            {
                return Task.FromResult((IReadOnlyList<ImageSource>)Array.Empty<ImageSource>());
            }

            if (TryGetLevels(source, out IReadOnlyList<ImageSource> cached))
            {
                return Task.FromResult(cached);
            }

            PendingLevels pending = Pending.GetValue(source, key =>
            {
                BitmapSource snapshot = SnapshotForWorker(key);
                Task<IReadOnlyList<ImageSource>> task = snapshot == null
                    ? Task.FromResult((IReadOnlyList<ImageSource>)Array.Empty<ImageSource>())
                    : Task.Run<IReadOnlyList<ImageSource>>(() => BuildLevels(snapshot));

                task.ContinueWith(t =>
                {
                    try
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result != null && t.Result.Count > 0)
                        {
                            try
                            {
                                Cache.Add(key, new CachedLevels { Levels = t.Result });
                            }
                            catch (ArgumentException)
                            {
                            }
                        }
                    }
                    finally
                    {
                        Pending.Remove(key);
                    }
                }, TaskScheduler.Default);

                return new PendingLevels { Task = task };
            });

            return pending.Task;
        }

        public static IReadOnlyList<ImageSource> CreateLevels(ImageSource source)
        {
            if (source == null)
            {
                return Array.Empty<ImageSource>();
            }

            if (Cache.TryGetValue(source, out CachedLevels cached) && cached?.Levels != null)
            {
                return cached.Levels;
            }

            BitmapSource bitmap = EnsureBitmap(source);
            if (bitmap == null)
            {
                return Array.Empty<ImageSource>();
            }

            var result = BuildLevels(bitmap);
            try
            {
                Cache.Add(source, new CachedLevels { Levels = result });
            }
            catch (ArgumentException)
            {
                if (Cache.TryGetValue(source, out CachedLevels raced) && raced?.Levels != null)
                {
                    return raced.Levels;
                }
            }

            return result;
        }

        private static BitmapSource SnapshotForWorker(ImageSource source)
        {
            try
            {
                if (!(source is BitmapSource bitmap))
                {
                    return null;
                }

                if (bitmap.IsFrozen)
                {
                    return bitmap;
                }

                BitmapSource clone = bitmap.CloneCurrentValue();
                if (clone.CanFreeze)
                {
                    clone.Freeze();
                }
                return clone;
            }
            catch (Exception ex)
            {
                Logger.Debug($"ImageRotater: Pixelate prewarm snapshot skipped: {ex.Message}");
                return null;
            }
        }

        private static IReadOnlyList<ImageSource> BuildLevels(BitmapSource bitmap)
        {
            if (bitmap == null)
            {
                return Array.Empty<ImageSource>();
            }

            var levels = new List<ImageSource> { bitmap };
            foreach (int divisor in Divisors)
            {
                ImageSource level = CreatePixelated(bitmap, divisor);
                if (level != null)
                {
                    levels.Add(level);
                }
            }

            return levels.AsReadOnly();
        }

        public static ObjectAnimationUsingKeyFrames BuildOutgoingPhase(
            IReadOnlyList<ImageSource> levels,
            TimeSpan duration)
        {
            var animation = new ObjectAnimationUsingKeyFrames
            {
                Duration = new Duration(duration)
            };

            if (levels == null || levels.Count == 0)
            {
                return animation;
            }

            int max = levels.Count - 1;
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                levels[0], KeyTime.FromPercent(0.0)));

            for (int i = 1; i <= max; i++)
            {
                double u = i / (double)max;
                double eased = u * u * (3.0 - (2.0 * u));
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                    levels[i], KeyTime.FromPercent(eased)));
            }

            return animation;
        }

        public static ObjectAnimationUsingKeyFrames BuildIncomingPhase(
            IReadOnlyList<ImageSource> levels,
            TimeSpan duration,
            double holdFraction)
        {
            var animation = new ObjectAnimationUsingKeyFrames
            {
                Duration = new Duration(duration)
            };

            if (levels == null || levels.Count == 0)
            {
                return animation;
            }

            int max = levels.Count - 1;
            double hold = Math.Max(0.0, Math.Min(0.35, holdFraction));
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                levels[max], KeyTime.FromPercent(0.0)));
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                levels[max], KeyTime.FromPercent(hold)));

            for (int i = max - 1; i >= 0; i--)
            {
                double u = (max - i) / (double)Math.Max(1, max);
                double eased = u * u * (3.0 - (2.0 * u));
                double t = hold + ((1.0 - hold) * eased);
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                    levels[i], KeyTime.FromPercent(t)));
            }

            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                levels[0], KeyTime.FromPercent(1.0)));
            return animation;
        }

        public static ObjectAnimationUsingKeyFrames BuildCombinedSourceAnimation(
            IReadOnlyList<ImageSource> oldLevels,
            IReadOnlyList<ImageSource> newLevels,
            TimeSpan duration)
        {
            var animation = new ObjectAnimationUsingKeyFrames
            {
                Duration = new Duration(duration)
            };

            if (oldLevels == null || oldLevels.Count == 0 || newLevels == null || newLevels.Count == 0)
            {
                return animation;
            }

            int oldMax = oldLevels.Count - 1;
            int newMax = newLevels.Count - 1;

            // Start the visual effect almost immediately, then use nearly the
            // whole duration for the actual pixel progression. A tiny overlap
            // around the midpoint keeps the old/new hand-off from feeling like
            // a hard dropped frame.
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                oldLevels[0], KeyTime.FromPercent(0.00)));

            for (int i = 1; i <= oldMax; i++)
            {
                double u = i / (double)oldMax;
                double eased = u * u * (3.0 - (2.0 * u));
                double t = 0.018 + (0.462 * eased);
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                    oldLevels[i], KeyTime.FromPercent(t)));
            }

            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                newLevels[newMax], KeyTime.FromPercent(0.520)));

            for (int i = newMax - 1; i >= 0; i--)
            {
                double u = (newMax - i) / (double)newMax;
                double eased = u * u * (3.0 - (2.0 * u));
                double t = 0.520 + (0.462 * eased);
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                    newLevels[i], KeyTime.FromPercent(t)));
            }

            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                newLevels[0], KeyTime.FromPercent(1.00)));

            return animation;
        }

        public static ObjectAnimationUsingKeyFrames BuildOutgoingSourceAnimation(
            Image image,
            IReadOnlyList<ImageSource> levels,
            TimeSpan duration)
        {
            var animation = new ObjectAnimationUsingKeyFrames
            {
                Duration = new Duration(duration)
            };

            if (levels == null || levels.Count == 0)
            {
                return animation;
            }

            int max = levels.Count - 1;
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                levels[0], KeyTime.FromPercent(0.00)));

            for (int i = 1; i <= max; i++)
            {
                double u = i / (double)max;
                double eased = u * u * (3.0 - (2.0 * u));
                double t = 0.018 + (0.482 * eased);
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                    levels[i], KeyTime.FromPercent(t)));
            }

            return animation;
        }

        public static ObjectAnimationUsingKeyFrames BuildIncomingSourceAnimation(
            Image image,
            IReadOnlyList<ImageSource> levels,
            TimeSpan duration)
        {
            var animation = new ObjectAnimationUsingKeyFrames
            {
                Duration = new Duration(duration)
            };

            if (levels == null || levels.Count == 0)
            {
                return animation;
            }

            int max = levels.Count - 1;
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                levels[max], KeyTime.FromPercent(0.50)));

            for (int i = max - 1; i >= 0; i--)
            {
                double u = (max - i) / (double)max;
                double eased = u * u * (3.0 - (2.0 * u));
                double t = 0.50 + (0.482 * eased);
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                    levels[i], KeyTime.FromPercent(t)));
            }

            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                levels[0], KeyTime.FromPercent(1.00)));

            return animation;
        }

        private static BitmapSource EnsureBitmap(ImageSource source)
        {
            if (source == null)
            {
                return null;
            }

            if (source is BitmapSource bitmap)
            {
                if (bitmap.CanFreeze && !bitmap.IsFrozen)
                {
                    try { bitmap.Freeze(); } catch { }
                }
                return bitmap;
            }

            try
            {
                int width = Math.Max(1, (int)Math.Round(source.Width));
                int height = Math.Max(1, (int)Math.Round(source.Height));
                if (width <= 1 || height <= 1)
                {
                    width = 640;
                    height = 360;
                }

                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawImage(source, new Rect(0, 0, width, height));
                }

                var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(visual);
                if (rendered.CanFreeze)
                {
                    try { rendered.Freeze(); } catch { }
                }
                return rendered;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not create bitmap for Pixelate transition");
                return null;
            }
        }

        private static BitmapSource CreatePixelated(BitmapSource source, int divisor)
        {
            try
            {
                int targetWidth = Math.Max(1, source.PixelWidth / divisor);
                int targetHeight = Math.Max(1, source.PixelHeight / divisor);

                // TransformedBitmap is significantly lighter than rendering a new
                // DrawingVisual for every level. The tiny bitmap is then enlarged
                // by the Image control with NearestNeighbor during the animation.
                var transformed = new TransformedBitmap();
                transformed.BeginInit();
                transformed.Source = source;
                transformed.Transform = new ScaleTransform(
                    targetWidth / (double)Math.Max(1, source.PixelWidth),
                    targetHeight / (double)Math.Max(1, source.PixelHeight));
                transformed.EndInit();

                if (transformed.CanFreeze)
                {
                    try { transformed.Freeze(); } catch { }
                }

                return transformed;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not build Pixelate transition frame");
                return source;
            }
        }
    }
}
