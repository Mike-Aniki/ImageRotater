using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Brings Playnite's own background transition in line with the plugin's
    // background transition setting - and, for the default crossfade, stops
    // it dipping dark on every change.
    //
    // Playnite's FadeImage crossfades by running fade-in (0 to 1) and fade-out
    // (1 to 0) SIMULTANEOUSLY. Two stacked layers at opacity t and 1-t let the
    // backdrop bleed through by t*(1-t) - a quarter of it at the midpoint of
    // every single transition. Over a heavily blurred background there is no
    // structure to watch, so that luminance dip IS the visible event: the
    // background pulses dark and recovers on every game change, and with the
    // control's BitmapCache re-rendering the full-window blur every animation
    // frame, the pulse drops frames and reads as a pop.
    //
    // The fix is easing the two fades against each other - see the note on
    // the Ease method for why sequencing them was tried first and reverted.
    //
    // The fade-through-colour transitions add a veil: a Rectangle inside the
    // control's own ImageHolder, raised when the Source changes and lowered
    // when the new picture actually lands. Inside the holder, not over the
    // window, so it sits UNDER the interface like the background does, and
    // shares the background's blur and opacity mask.
    //
    // No reflection into internals. The four storyboards are ordinary entries
    // in the control's public Resources, and the control's internal fields
    // hold those same instances - retiming the resource retimes what Begin()
    // runs. The control is matched by type NAME, so this needs no reference
    // to Playnite's internals at all, and a Playnite update that renames
    // anything makes this a silent no-op rather than a break.
    public static class FadeImageTuner
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const string FadeImageTypeName = "Playnite.Controls.FadeImage";

        // The dip is removed with EASING, not with sequencing - and the
        // distinction was learned the hard way.
        //
        // The first version made the fades sequential: fade-in over the first
        // half, fade-out delayed by a BeginTime into the second. Zero bleed on
        // paper - but a storyboard waiting on its BeginTime is a PENDING
        // clock, and Playnite Stop()s and re-Begin()s these storyboards
        // freely on rapid changes. A pending clock stranded by that
        // interleaving never delivers a value, which left the outgoing image
        // frozen at full opacity underneath the new one: two backgrounds
        // stacked on screen indefinitely.
        //
        // Easing has no waiting state. Both fades run exactly when stock ones
        // do, for the same duration - the incoming image just rises fast
        // early (ease-out) while the outgoing holds high early (ease-in). At
        // the midpoint both sit near 0.875 instead of 0.5, which cuts the
        // backdrop bleed from 25% to under 2% - below what a radius-59 blur
        // makes visible. Any Stop/Begin interleaving behaves byte-for-byte
        // like stock, because structurally it IS stock.

        // What one FadeImage instance has been tuned to, plus the veil and
        // the hooks that drive it. Weak, so recycled or closed windows do not
        // pin dead controls for the session.
        private sealed class Tune
        {
            public TransitionStyle Style;
            public TimeSpan Duration;
            public Rectangle Veil;
            public bool VeilRising;
            public bool LowerPending;
            public int VeilGeneration;
            public DispatcherTimer Backstop;
            public Image Image1;
            public Image Image2;
            public DependencyPropertyDescriptor SourceDescriptor;
            public object LastSource;
            public EventHandler OnSourceChanged;
            public EventHandler OnSwap;
            public RoutedEventHandler OnUnloaded;
            public TransitionDirection Direction;
        }

        private static readonly ConditionalWeakTable<UserControl, Tune> Patched =
            new ConditionalWeakTable<UserControl, Tune>();

        // Walks the main window and tunes every FadeImage found. Idempotent
        // and cheap to repeat: instances already at the current style are
        // skipped, and a tree with no FadeImage just walks and returns.
        public static int Apply()
        {
            try
            {
                Window window = Application.Current?.MainWindow;

                if (window == null)
                {
                    return 0;
                }

                // A full visual-tree walk. Cheap on the default theme; a
                // heavy theme can make it not so, and the log should say.
                var timer = System.Diagnostics.Stopwatch.StartNew();
                int patched = Patch(window);
                timer.Stop();

                if (timer.ElapsedMilliseconds > 50)
                {
                    Logger.Info($"ImageRotater: fade retime walked the window in {timer.ElapsedMilliseconds} ms");
                }

                return patched;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not retime Playnite's background fade");
                return 0;
            }
        }

        private static int Patch(DependencyObject node)
        {
            int patched = 0;

            int count = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);

                if (child is UserControl control &&
                    control.GetType().FullName == FadeImageTypeName)
                {
                    if (TryRetime(control))
                    {
                        patched++;
                    }

                    // A FadeImage does not nest another, but its subtree is
                    // tiny either way - no reason to special-case the walk.
                }

                patched += Patch(child);
            }

            return patched;
        }

        // Attaches the blur BEFORE the first image ever loads.
        //
        // Playnite creates the BlurEffect lazily, inside the first image load:
        // its blur-setting callback returns early while Source is still null,
        // so a freshly built FadeImage carries NO effect until the first
        // LoadNewSource attaches one - after the image is decoded. A radius-59
        // full-window Gaussian is a real shader that takes visible time on its
        // first use, so the image lands SHARP for a beat and then snaps to
        // blurred. That is the "crop appears, then the blur arrives" artefact.
        //
        // Attaching the effect up front means it exists, compiled and warm,
        // before any image does - and Playnite's own lazy branch then sees a
        // non-null effect and skips creating one, so nothing is ever attached
        // twice.
        private static void EnsureEffect(UserControl fadeImage)
        {
            try
            {
                Type type = fadeImage.GetType();

                if (!(fadeImage.FindName("ImageHolder") is Grid holder))
                {
                    return;
                }

                if (holder.Effect != null)
                {
                    return;
                }

                if (!(ReadDp(fadeImage, type, "IsBlurEnabledProperty") is bool enabled) || !enabled)
                {
                    return;
                }

                int radius = ReadDp(fadeImage, type, "BlurAmountProperty") is int amount
                    ? amount
                    : 10;

                bool highQuality =
                    ReadDp(fadeImage, type, "HighQualityBlurProperty") is bool hq && hq;

                holder.Effect = new System.Windows.Media.Effects.BlurEffect
                {
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                    Radius = radius,
                    RenderingBias = highQuality
                        ? System.Windows.Media.Effects.RenderingBias.Quality
                        : System.Windows.Media.Effects.RenderingBias.Performance
                };

                Logger.Debug("ImageRotater: pre-attached the background blur");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not pre-attach the blur");
            }
        }

        // A dependency property found by the name of its public static field -
        // no compile-time reference to Playnite's internals.
        private static DependencyProperty FindDp(Type type, string fieldName)
        {
            return type.GetField(fieldName)?.GetValue(null) as DependencyProperty;
        }

        private static object ReadDp(UserControl control, Type type, string fieldName)
        {
            DependencyProperty dp = FindDp(type, fieldName);
            return dp != null ? control.GetValue(dp) : null;
        }

        // Longer debounce than Playnite's default 150ms, so rapid selection
        // scrolling coalesces into one transition instead of queueing several.
        // The veil's rise is timed to it: up by the time the load starts.
        private const double SourceDelayMs = 250;

        private static void SetSourceDelay(UserControl fadeImage)
        {
            try
            {
                fadeImage.GetType()
                    .GetProperty("SourceUpdateDelay")
                    ?.SetValue(fadeImage, SourceDelayMs);
            }
            catch (Exception)
            {
                // A rename makes this a no-op, never a break.
            }
        }

        private static bool TryRetime(UserControl fadeImage)
        {
            // These are re-asserted on every scan, cheaply: Playnite rewrites
            // SourceUpdateDelay when views change, and a rebuilt template can
            // arrive with no effect attached again.
            EnsureEffect(fadeImage);
            SetSourceDelay(fadeImage);

            bool known = Patched.TryGetValue(fadeImage, out Tune tune);

            bool isSlide = Transition.BackgroundStyle == TransitionStyle.SlideFromRight;

            TimeSpan requestedDuration =
                Transition.BackgroundStyle == TransitionStyle.Cut
                    ? TimeSpan.Zero
                    : Transition.BackgroundDuration;

            TransitionDirection requestedDirection = TransitionDirection.FromRight;

            // Duration is part of the tuning state too. Previously changing the
            // duration while keeping the same style was ignored, because an
            // already-patched FadeImage was considered finished solely by its
            // style. That made the duration slider look broken until Playnite
            // rebuilt the view/control.
            if (known && tune.Style == Transition.BackgroundStyle &&
                tune.Duration == requestedDuration &&
                (!isSlide || tune.Direction == requestedDirection))
            {
                return false;
            }

            try
            {
                // Slide transitions must replace Playnite's private fade
                // storyboards, not merely retime the public resource copies.
                // This is the same extension point used by CustomFadeAnim and
                // is why the first implementation appeared to do nothing in
                // Desktop mode: only ImageRotater-hosted controls knew how to
                // slide, while Playnite's native FadeImage still ran its stock
                // crossfade.
                bool ok;
                if (isSlide)
                {
                    ok = ApplySlideStoryboards(fadeImage, requestedDirection, Transition.BackgroundDuration);
                }
                else
                {
                    // Cut and flash hide the native dissolve. The overlay is the visible transition in flash modes.
                    TimeSpan duration = requestedDuration;
                    ok =
                        Ease(fadeImage, "Image1FadeIn", EasingMode.EaseOut, duration) &
                        Ease(fadeImage, "Image2FadeIn", EasingMode.EaseOut, duration) &
                        Ease(fadeImage, "Image1FadeOut", EasingMode.EaseIn, duration) &
                        Ease(fadeImage, "Image2FadeOut", EasingMode.EaseIn, duration);
                }

                if (!ok)
                {
                    return false;
                }

                if (!known)
                {
                    tune = new Tune();
                    Patched.Add(fadeImage, tune);

                    // A view switch throws the control away; the hooks below
                    // hold strong references to it and must not outlive it.
                    tune.OnUnloaded = (s, e) =>
                    {
                        RemoveVeil(fadeImage, tune);
                        fadeImage.Unloaded -= tune.OnUnloaded;
                        Patched.Remove(fadeImage);
                    };
                    fadeImage.Unloaded += tune.OnUnloaded;
                }

                tune.Style = Transition.BackgroundStyle;
                tune.Duration = requestedDuration;
                tune.Direction = requestedDirection;

                if (Transition.IsFlash(Transition.BackgroundStyle))
                {
                    AddVeil(fadeImage, tune);
                }
                else
                {
                    RemoveVeil(fadeImage, tune);
                }

                Logger.Debug($"ImageRotater: tuned a FadeImage to {Transition.BackgroundStyle}");
                return true;
            }
            catch (Exception ex)
            {
                // A sealed storyboard, a renamed resource - either way, this
                // instance keeps stock behaviour and nothing is harmed.
                Logger.Warn(ex, "ImageRotater: FadeImage retime skipped");
                return false;
            }
        }

        private static bool ApplySlideStoryboards(
            UserControl fadeImage,
            TransitionDirection direction,
            TimeSpan duration)
        {
            try
            {
                Type type = fadeImage.GetType();
                Image image1 = fadeImage.FindName("Image1") as Image;
                Image image2 = fadeImage.FindName("Image2") as Image;
                if (image1 == null || image2 == null)
                {
                    return false;
                }

                EnsureSlideTransform(image1);
                EnsureSlideTransform(image2);

                Storyboard in1 = BuildSlideStoryboard(image1, true, direction, duration);
                Storyboard in2 = BuildSlideStoryboard(image2, true, direction, duration);
                Storyboard out1 = BuildSlideStoryboard(image1, false, direction, duration);
                Storyboard out2 = BuildSlideStoryboard(image2, false, direction, duration);

                FieldInfo fIn1 = type.GetField("Image1FadeIn", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo fIn2 = type.GetField("Image2FadeIn", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo fOut1 = type.GetField("Image1FadeOut", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo fOut2 = type.GetField("Image2FadeOut", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fIn1 == null || fIn2 == null || fOut1 == null || fOut2 == null)
                {
                    return false;
                }

                fIn1.SetValue(fadeImage, in1);
                fIn2.SetValue(fadeImage, in2);
                fOut1.SetValue(fadeImage, out1);
                fOut2.SetValue(fadeImage, out2);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not install background slide storyboards");
                return false;
            }
        }

        private static void EnsureSlideTransform(Image image)
        {
            if (image == null)
            {
                return;
            }

            // FadeImage backgrounds do not normally carry a RenderTransform.
            // Keep a stable TransformGroup so the private storyboards can move
            // only the translation component and never disturb layout/blur.
            if (image.RenderTransform is TransformGroup group &&
                group.Children.Count >= 2 && group.Children[1] is TranslateTransform)
            {
                return;
            }

            var transforms = new TransformGroup();
            transforms.Children.Add(new ScaleTransform(1.0, 1.0));
            transforms.Children.Add(new TranslateTransform(0.0, 0.0));
            image.RenderTransform = transforms;
            image.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        private static Storyboard BuildSlideStoryboard(
            Image image,
            bool incoming,
            TransitionDirection direction,
            TimeSpan duration)
        {
            var storyboard = new Storyboard();
            double distance = Math.Max(90.0, Math.Min(220.0, image.ActualWidth * 0.10));
            bool horizontal = direction == TransitionDirection.FromLeft || direction == TransitionDirection.FromRight;
            double sign = direction == TransitionDirection.FromLeft || direction == TransitionDirection.FromTop ? -1.0 : 1.0;

            var opacity = incoming
                ? new DoubleAnimation(0.15, 1.0, new Duration(duration))
                : new DoubleAnimation(1.0, 0.0, new Duration(duration));
            opacity.EasingFunction = new CubicEase
            {
                EasingMode = incoming ? EasingMode.EaseOut : EasingMode.EaseIn
            };
            Storyboard.SetTarget(opacity, image);
            Storyboard.SetTargetProperty(opacity, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(opacity);

            double from = incoming ? sign * distance : 0.0;
            double to = incoming ? 0.0 : -sign * distance * 0.45;
            var move = new DoubleAnimation(from, to, new Duration(duration))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = incoming ? EasingMode.EaseOut : EasingMode.EaseIn
                },
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(move, image);
            Storyboard.SetTargetProperty(
                move,
                new PropertyPath(horizontal
                    ? "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)"
                    : "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));
            storyboard.Children.Add(move);
            return storyboard;
        }

        private static bool Ease(UserControl fadeImage, string key, EasingMode mode, TimeSpan duration)
        {
            if (!(fadeImage.Resources[key] is Storyboard storyboard) ||
                storyboard.IsSealed)
            {
                return false;
            }

            foreach (Timeline timeline in storyboard.Children)
            {
                if (timeline is DoubleAnimation animation && !animation.IsSealed)
                {
                    // BeginTime stays zero, deliberately: no pending-clock
                    // state for Playnite's Stop/Begin churn to strand. The
                    // duration is safe to change - the clock still starts
                    // the moment Begin() is called.
                    animation.BeginTime = TimeSpan.Zero;
                    animation.Duration = new Duration(duration);
                    animation.EasingFunction = new CubicEase { EasingMode = mode };
                }
            }

            return true;
        }

        // The veil goes up when the Source changes and comes down when the
        // picture lands in Image1 or Image2 - the only moment that says the
        // load, and Playnite's own debounce before it, are actually done.
        //
        // Both are watched through DependencyPropertyDescriptor, which is
        // public WPF and needs no hook into Playnite's code. A backstop lowers
        // the veil if no picture ever lands: Playnite skips a load whose
        // source equals the current one, and a veil raised for that would
        // otherwise stay up.
        private static void AddVeil(UserControl fadeImage, Tune tune)
        {
            if (tune.Veil != null)
            {
                tune.Veil.Fill = new SolidColorBrush(Transition.FlashColor(Transition.BackgroundStyle));
                return;
            }

            if (!(fadeImage.FindName("ImageHolder") is Grid holder))
            {
                return;
            }

            DependencyProperty sourceDp = FindDp(fadeImage.GetType(), "SourceProperty");
            if (sourceDp == null)
            {
                return;
            }

            var veil = new Rectangle
            {
                Fill = new SolidColorBrush(Transition.FlashColor(Transition.BackgroundStyle)),
                Opacity = 0.0,
                IsHitTestVisible = false
            };

            // Themes fade the background out towards an edge with this mask;
            // the veil must fade with it or it flashes where no picture is.
            BindingOperations.SetBinding(veil, UIElement.OpacityMaskProperty,
                new Binding("ImageOpacityMask") { Source = fadeImage });

            holder.Children.Add(veil);

            tune.Veil = veil;
            tune.Image1 = fadeImage.FindName("Image1") as Image;
            tune.Image2 = fadeImage.FindName("Image2") as Image;

            tune.Backstop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            tune.Backstop.Tick += (s, e) => Lower(tune);

            // Raised only for a source FadeImage will actually load. Themes
            // hand it a fresh BitmapLoadProperties on every notification, and
            // it compares those by VALUE and skips an equal one - a veil
            // raised for that would sit up until the backstop.
            tune.LastSource = fadeImage.GetValue(sourceDp);
            tune.OnSourceChanged = (s, e) =>
            {
                object source = fadeImage.GetValue(sourceDp);
                if (source == null || Equals(source, tune.LastSource))
                {
                    return;
                }

                tune.LastSource = source;
                Raise(tune);
            };
            tune.SourceDescriptor = DependencyPropertyDescriptor.FromProperty(sourceDp, fadeImage.GetType());
            tune.SourceDescriptor.AddValueChanged(fadeImage, tune.OnSourceChanged);

            // Fade-out completion clears the outgoing layer's Source to null;
            // only a picture ARRIVING is the swap.
            tune.OnSwap = (s, e) =>
            {
                if ((s as Image)?.Source != null)
                {
                    Lower(tune);
                }
            };

            var imageSource = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            if (tune.Image1 != null) imageSource.AddValueChanged(tune.Image1, tune.OnSwap);
            if (tune.Image2 != null) imageSource.AddValueChanged(tune.Image2, tune.OnSwap);
        }

        private static void RemoveVeil(UserControl fadeImage, Tune tune)
        {
            if (tune.Veil == null)
            {
                return;
            }

            try
            {
                tune.Backstop?.Stop();
                tune.SourceDescriptor?.RemoveValueChanged(fadeImage, tune.OnSourceChanged);

                var imageSource = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
                if (tune.Image1 != null) imageSource.RemoveValueChanged(tune.Image1, tune.OnSwap);
                if (tune.Image2 != null) imageSource.RemoveValueChanged(tune.Image2, tune.OnSwap);

                tune.Veil.BeginAnimation(UIElement.OpacityProperty, null);
                (tune.Veil.Parent as Grid)?.Children.Remove(tune.Veil);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not remove the background veil");
            }

            tune.Veil = null;
            tune.Backstop = null;
            tune.VeilRising = false;
            tune.LowerPending = false;
        }

        private static void Raise(Tune tune)
        {
            if (tune?.Veil == null)
            {
                return;
            }

            int generation = ++tune.VeilGeneration;
            tune.VeilRising = true;
            tune.LowerPending = false;

            var up = new DoubleAnimation(1.0, new Duration(Transition.BackgroundHalf));
            up.Completed += (s, e) =>
            {
                if (generation != tune.VeilGeneration)
                {
                    return;
                }

                tune.VeilRising = false;
                if (tune.LowerPending)
                {
                    tune.LowerPending = false;
                    Lower(tune);
                }
            };

            tune.Veil.BeginAnimation(UIElement.OpacityProperty, up);

            if (tune.Backstop != null)
            {
                tune.Backstop.Stop();
                // Do not tear down a deliberately slow transition too early.
                // The backstop is only a safety net for a source that never
                // produces a frame, not part of the normal animation timing.
                double ms = Math.Max(2000.0, Transition.BackgroundDuration.TotalMilliseconds + 1000.0);
                tune.Backstop.Interval = TimeSpan.FromMilliseconds(ms);
                tune.Backstop.Start();
            }
        }

        private static void Lower(Tune tune)
        {
            if (tune?.Veil == null)
            {
                return;
            }

            tune.Backstop?.Stop();

            // A cached background can arrive before the colour veil has even
            // finished rising. Lowering immediately in that case made the
            // black/white transition reverse halfway up and often look like a
            // tiny flicker (or no transition at all). Finish the first half,
            // then lower it over the new image.
            if (tune.VeilRising)
            {
                tune.LowerPending = true;
                return;
            }

            ++tune.VeilGeneration;
            tune.LowerPending = false;
            tune.Veil.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.0, new Duration(Transition.BackgroundHalf)));
        }
    }
}
