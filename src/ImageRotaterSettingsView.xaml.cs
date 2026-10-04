using ImageRotater.Services;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageRotater
{
    // Keep code-behind limited to view-only navigation and animation preview.
    // Settings state, tool detection and validation remain owned by the view model.
    public partial class ImageRotaterSettingsView : UserControl
    {
        private ImageRotaterSettings _observedSettings;
        private BitmapImage _backgroundPreviewA;
        private BitmapImage _backgroundPreviewB;
        private BitmapImage _coverPreviewA;
        private BitmapImage _coverPreviewB;
        private bool _backgroundPreviewShowsA = true;
        private bool _coverPreviewShowsA = true;
        private int _backgroundPreviewGeneration;
        private int _coverPreviewGeneration;

        public ImageRotaterSettingsView()
        {
            InitializeComponent();
            Loaded += ImageRotaterSettingsView_Loaded;
            Unloaded += ImageRotaterSettingsView_Unloaded;
        }

        private void ImageRotaterSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            (DataContext as ImageRotaterSettingsViewModel)?.EnsureThemeSupportLoaded();
            EnsurePreviewImages();
            ObserveSettings();
        }

        private void ImageRotaterSettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_observedSettings != null)
            {
                _observedSettings.PropertyChanged -= PreviewSettings_PropertyChanged;
                _observedSettings = null;
            }
        }

        private void EnsurePreviewImages()
        {
            if (_backgroundPreviewA != null)
            {
                return;
            }

            _backgroundPreviewA = LoadPreviewImage("Assets/Preview/background-1.jpg");
            _backgroundPreviewB = LoadPreviewImage("Assets/Preview/background-2.jpg");
            _coverPreviewA = LoadPreviewImage("Assets/Preview/cover-1.png");
            _coverPreviewB = LoadPreviewImage("Assets/Preview/cover-2.jpg");

            BackgroundAnimationPreview.Source = _backgroundPreviewA;
            CoverAnimationPreview.Source = _coverPreviewA;

            // Pixelate is the only preview that needs prepared image levels.
            // Warm the four tiny demo sources while the settings page is idle so
            // choosing Pixelate does not introduce a first-run hitch.
            PixelateFrames.QueuePrewarm(_backgroundPreviewA);
            PixelateFrames.QueuePrewarm(_backgroundPreviewB);
            PixelateFrames.QueuePrewarm(_coverPreviewA);
            PixelateFrames.QueuePrewarm(_coverPreviewB);
        }

        private static BitmapImage LoadPreviewImage(string relativePath)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(
                $"pack://application:,,,/ImageRotater;component/{relativePath}",
                UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private void ObserveSettings()
        {
            var settings = (DataContext as ImageRotaterSettingsViewModel)?.Settings;
            if (ReferenceEquals(settings, _observedSettings))
            {
                return;
            }

            if (_observedSettings != null)
            {
                _observedSettings.PropertyChanged -= PreviewSettings_PropertyChanged;
            }

            _observedSettings = settings;
            if (_observedSettings != null)
            {
                _observedSettings.PropertyChanged += PreviewSettings_PropertyChanged;
            }
        }

        private void PreviewSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImageRotaterSettings.BackgroundTransition))
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(PlayBackgroundPreview));
            }
            else if (e.PropertyName == nameof(ImageRotaterSettings.CoverTransition))
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(PlayCoverPreview));
            }
        }

        private void PlayBackgroundPreview()
        {
            if (!IsLoaded || _observedSettings == null || _backgroundPreviewA == null)
            {
                return;
            }

            BitmapImage next = _backgroundPreviewShowsA
                ? _backgroundPreviewB
                : _backgroundPreviewA;

            TransitionStyle style = _observedSettings.BackgroundTransition;
            bool started;

            if (style == TransitionStyle.DepthShift)
            {
                int token = ++_backgroundPreviewGeneration;
                started = PlayCinematicPreview(
                    BackgroundAnimationPreview,
                    BackgroundAnimationPreviewPrevious,
                    next,
                    token,
                    true);
            }
            else if (style == TransitionStyle.DiagonalReveal)
            {
                int token = ++_backgroundPreviewGeneration;
                started = PlayDiagonalPreview(
                    BackgroundAnimationPreview,
                    BackgroundAnimationPreviewPrevious,
                    next,
                    token,
                    true);
            }
            else
            {
                ++_backgroundPreviewGeneration;
                ResetDedicatedPreview(
                    BackgroundAnimationPreview,
                    BackgroundAnimationPreviewPrevious);

                started = Transition.Run(
                    BackgroundAnimationPreview,
                    () => BackgroundAnimationPreview.Source = next,
                    style);
            }

            if (!started)
            {
                ++_backgroundPreviewGeneration;
                ResetDedicatedPreview(
                    BackgroundAnimationPreview,
                    BackgroundAnimationPreviewPrevious);
                BackgroundAnimationPreview.Source = next;
            }

            _backgroundPreviewShowsA = !_backgroundPreviewShowsA;
        }

        private void PlayCoverPreview()
        {
            if (!IsLoaded || _observedSettings == null || _coverPreviewA == null)
            {
                return;
            }

            BitmapImage next = _coverPreviewShowsA
                ? _coverPreviewB
                : _coverPreviewA;

            TransitionStyle style = _observedSettings.CoverTransition;
            bool started;

            if (style == TransitionStyle.DepthShift)
            {
                int token = ++_coverPreviewGeneration;
                started = PlayCinematicPreview(
                    CoverAnimationPreview,
                    CoverAnimationPreviewPrevious,
                    next,
                    token,
                    false);
            }
            else if (style == TransitionStyle.DiagonalReveal)
            {
                int token = ++_coverPreviewGeneration;
                started = PlayDiagonalPreview(
                    CoverAnimationPreview,
                    CoverAnimationPreviewPrevious,
                    next,
                    token,
                    false);
            }
            else
            {
                ++_coverPreviewGeneration;
                ResetDedicatedPreview(
                    CoverAnimationPreview,
                    CoverAnimationPreviewPrevious);

                started = Transition.Run(
                    CoverAnimationPreview,
                    () => CoverAnimationPreview.Source = next,
                    style);
            }

            if (!started)
            {
                ++_coverPreviewGeneration;
                ResetDedicatedPreview(
                    CoverAnimationPreview,
                    CoverAnimationPreviewPrevious);
                CoverAnimationPreview.Source = next;
            }

            _coverPreviewShowsA = !_coverPreviewShowsA;
        }

        // Cinematic has a dedicated settings preview. Using the generic adorner
        // makes the blur too subtle on a 150-300 px card. Two ordinary Image
        // layers are both safer and much closer to what the full-size effect is
        // trying to communicate: old image recedes/softens, new image resolves.
        private bool PlayCinematicPreview(
            Image current,
            Image previous,
            ImageSource next,
            int token,
            bool background)
        {
            if (current == null || previous == null || current.Source == null || next == null)
            {
                return false;
            }

            ImageSource old = current.Source;
            ResetPreviewImage(current);
            ResetPreviewImage(previous);

            previous.Source = old;
            previous.Opacity = 1.0;
            previous.Visibility = Visibility.Visible;
            current.Source = next;
            current.Opacity = 0.0;
            current.Visibility = Visibility.Visible;

            var oldScale = new ScaleTransform(1.0, 1.0);
            var newScale = new ScaleTransform(1.07, 1.07);
            previous.RenderTransformOrigin = new Point(0.5, 0.5);
            current.RenderTransformOrigin = new Point(0.5, 0.5);
            previous.RenderTransform = oldScale;
            current.RenderTransform = newScale;

            var oldBlur = new BlurEffect
            {
                Radius = 0.0,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance
            };
            var newBlur = new BlurEffect
            {
                Radius = 14.0,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance
            };
            previous.Effect = oldBlur;
            current.Effect = newBlur;

            TimeSpan duration = TimeSpan.FromMilliseconds(820);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            var oldOpacity = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
            oldOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0.18)));
            oldOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(0.82), ease));

            var newOpacity = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
            newOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.06)));
            newOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.78), easeOut));

            var oldBlurAnim = new DoubleAnimation(0.0, 11.0, new Duration(duration)) { EasingFunction = ease };
            var newBlurAnim = new DoubleAnimation(14.0, 0.0, new Duration(duration)) { EasingFunction = easeOut };
            var oldScaleAnim = new DoubleAnimation(1.0, 0.955, new Duration(duration)) { EasingFunction = ease };
            var newScaleAnim = new DoubleAnimation(1.07, 1.0, new Duration(duration)) { EasingFunction = easeOut };

            newOpacity.Completed += (s, e) =>
            {
                int currentGeneration = background
                    ? _backgroundPreviewGeneration
                    : _coverPreviewGeneration;
                if (token != currentGeneration)
                {
                    return;
                }

                ResetPreviewImage(previous);
                previous.Source = null;
                previous.Visibility = Visibility.Collapsed;
                ResetPreviewImage(current);
                current.Source = next;
                current.Opacity = 1.0;
                current.Visibility = Visibility.Visible;
            };

            previous.BeginAnimation(UIElement.OpacityProperty, oldOpacity);
            current.BeginAnimation(UIElement.OpacityProperty, newOpacity);
            oldBlur.BeginAnimation(BlurEffect.RadiusProperty, oldBlurAnim);
            newBlur.BeginAnimation(BlurEffect.RadiusProperty, newBlurAnim);
            oldScale.BeginAnimation(ScaleTransform.ScaleXProperty, oldScaleAnim);
            oldScale.BeginAnimation(ScaleTransform.ScaleYProperty, oldScaleAnim);
            newScale.BeginAnimation(ScaleTransform.ScaleXProperty, newScaleAnim);
            newScale.BeginAnimation(ScaleTransform.ScaleYProperty, newScaleAnim);
            return true;
        }

        // Dedicated diagonal preview: the new image is already in place and only
        // its opacity mask moves. There is no adorner to remove at the end, so
        // there is no final one-frame hitch/snap in the compact settings card.
        private bool PlayDiagonalPreview(
            Image current,
            Image previous,
            ImageSource next,
            int token,
            bool background)
        {
            if (current == null || previous == null || current.Source == null || next == null)
            {
                return false;
            }

            ImageSource old = current.Source;
            ResetPreviewImage(current);
            ResetPreviewImage(previous);

            previous.Source = old;
            previous.Opacity = 1.0;
            previous.Visibility = Visibility.Visible;
            current.Source = next;
            current.Opacity = 1.0;
            current.Visibility = Visibility.Visible;

            var mask = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                StartPoint = new Point(1.20, -0.20),
                EndPoint = new Point(1.50, 0.10),
                SpreadMethod = GradientSpreadMethod.Pad
            };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.0));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.34));
            mask.GradientStops.Add(new GradientStop(Colors.White, 0.66));
            mask.GradientStops.Add(new GradientStop(Colors.White, 1.0));
            current.OpacityMask = mask;

            TimeSpan duration = TimeSpan.FromMilliseconds(650);
            var startMove = new PointAnimation(
                new Point(1.20, -0.20),
                new Point(-0.50, 0.90),
                new Duration(duration))
            {
                FillBehavior = FillBehavior.HoldEnd
            };
            var endMove = new PointAnimation(
                new Point(1.50, 0.10),
                new Point(-0.20, 1.20),
                new Duration(duration))
            {
                FillBehavior = FillBehavior.HoldEnd
            };

            endMove.Completed += (s, e) =>
            {
                int currentGeneration = background
                    ? _backgroundPreviewGeneration
                    : _coverPreviewGeneration;
                if (token != currentGeneration)
                {
                    return;
                }

                mask.BeginAnimation(LinearGradientBrush.StartPointProperty, null);
                mask.BeginAnimation(LinearGradientBrush.EndPointProperty, null);
                current.OpacityMask = null;
                previous.Source = null;
                previous.Visibility = Visibility.Collapsed;
                current.Opacity = 1.0;
            };

            mask.BeginAnimation(LinearGradientBrush.StartPointProperty, startMove);
            mask.BeginAnimation(LinearGradientBrush.EndPointProperty, endMove);
            return true;
        }

        private static void ResetDedicatedPreview(
            Image current,
            Image previous)
        {
            ResetPreviewImage(current);
            ResetPreviewImage(previous);
            if (previous != null)
            {
                previous.Source = null;
                previous.Visibility = Visibility.Collapsed;
            }
            if (current != null)
            {
                current.Opacity = 1.0;
                current.Visibility = Visibility.Visible;
            }
        }

        private static void ResetPreviewImage(Image image)
        {
            if (image == null)
            {
                return;
            }

            image.BeginAnimation(UIElement.OpacityProperty, null);
            image.OpacityMask = null;
            image.Effect = null;
            image.RenderTransform = Transform.Identity;
            image.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        private void ReplayBackgroundPreview_Click(object sender, RoutedEventArgs e)
        {
            PlayBackgroundPreview();
        }

        private void ReplayCoverPreview_Click(object sender, RoutedEventArgs e)
        {
            PlayCoverPreview();
        }

        private void MainSettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Nested strip TabControls also raise SelectionChanged and bubble it
            // upward. Only react to the main left-side navigation.
            if (!ReferenceEquals(e.Source, MainSettingsTabs)
                || !ReferenceEquals(MainSettingsTabs.SelectedItem, ToolsTab))
            {
                return;
            }

            (DataContext as ImageRotaterSettingsViewModel)?.EnsureToolStatusLoaded();
        }

        private void OpenBackgrounds_Click(object sender, RoutedEventArgs e)
        {
            MainSettingsTabs.SelectedItem = BackgroundsTab;
        }

        private void OpenCovers_Click(object sender, RoutedEventArgs e)
        {
            MainSettingsTabs.SelectedItem = CoversTab;
        }

        private void OpenTools_Click(object sender, RoutedEventArgs e)
        {
            MainSettingsTabs.SelectedItem = ToolsTab;
        }
    }
}
