using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    public partial class LogoImageControl : PluginUserControl
    {
        private readonly IBackgroundImageSource _source;
        private readonly ImageSelector _selector;
        private readonly ImageLoader _loader;
        private readonly Func<ImageRotaterSettings> _settings;
        private readonly Dictionary<Guid, string> _lastPickByGame = new Dictionary<Guid, string>();
        private string _previousPick;
        private int _generation;
        private int _lastBucket;

        public static event Action<Guid> ArtworkChanged;
        public static event Action SettingsChanged;

        public LogoImageControl(
            IBackgroundImageSource source,
            ImageSelector selector,
            ImageLoader loader,
            Func<ImageRotaterSettings> settings)
        {
            InitializeComponent();
            _source = source;
            _selector = selector;
            _loader = loader;
            _settings = settings;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
        }

        public static void NotifyArtworkChanged(Guid gameId)
        {
            Action<Guid> handler = ArtworkChanged;
            if (handler == null) return;
            Application app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => handler(gameId)));
                return;
            }
            handler(gameId);
        }

        public static void NotifySettingsChanged()
        {
            Action handler = SettingsChanged;
            if (handler == null) return;
            Application app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(handler));
                return;
            }
            handler();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyThemeLogoLayout();
            ArtworkChanged += OnArtworkChanged;
            SettingsChanged += OnSettingsChanged;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ArtworkChanged -= OnArtworkChanged;
            SettingsChanged -= OnSettingsChanged;
            _generation++;
        }


        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            int bucket = WidthBucket.ForWidth(ActualWidth);
            if (IsLoaded && bucket != _lastBucket)
            {
                Refresh();
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _previousPick = null;
            _generation++;
            Refresh();
        }

        private void OnArtworkChanged(Guid gameId)
        {
            Game game = GameContext;
            if (game != null && game.Id == gameId)
            {
                _previousPick = null;
                Refresh();
            }
        }

        private void OnSettingsChanged()
        {
            Refresh();
        }

        private async void Refresh()
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            int generation = ++_generation;
            Game game = GameContext;
            ImageRotaterSettings settings = _settings != null ? _settings() : null;

            if (game == null || settings == null || !settings.EnableLogoImage)
            {
                DisplayImage.Source = null;
                if (settings != null)
                {
                    settings.HasDataLogo = false;
                }
                return;
            }

            List<string> candidates = (_source?.GetImagePaths(game) ?? new List<string>())
                .Where(IsSupportedLogo)
                .Where(File.Exists)
                .ToList();

            settings.HasDataLogo = candidates.Count > 0;
            if (candidates.Count == 0)
            {
                DisplayImage.Source = null;
                return;
            }

            SelectionMode mode = settings.GetSelectionMode(game.Id, ArtworkKind.Logo, settings.LogoSelectionMode);
            SelectionOrder order = settings.GetSelectionOrder(game.Id, ArtworkKind.Logo, settings.LogoSelectionOrder);
            Guid selectionKey = SelectionKey(game.Id);
            string previousPick = _previousPick;
            if (string.IsNullOrEmpty(previousPick))
            {
                _lastPickByGame.TryGetValue(game.Id, out previousPick);
            }

            string path = _selector.Select(selectionKey, candidates, previousPick, mode, order);

            if (string.IsNullOrEmpty(path))
            {
                DisplayImage.Source = null;
                return;
            }

            int bucket = WidthBucket.ForWidth(ActualWidth);
            _lastBucket = bucket;
            BitmapSource bitmap = await _loader.LoadAsync(path, bucket);
            if (generation != _generation || GameContext?.Id != game.Id)
            {
                return;
            }

            if (bitmap == null)
            {
                foreach (string candidate in candidates)
                {
                    if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bitmap = await _loader.LoadAsync(candidate, WidthBucket.ForWidth(ActualWidth));
                    if (generation != _generation || GameContext?.Id != game.Id)
                    {
                        return;
                    }

                    if (bitmap != null)
                    {
                        path = candidate;
                        break;
                    }
                }
            }

            if (bitmap == null)
            {
                settings.HasDataLogo = false;
                DisplayImage.Source = null;
                return;
            }

            DisplayImage.Source = bitmap;

            _previousPick = path;
            _lastPickByGame[game.Id] = path;
        }

        // A theme can use a full-screen ImageRotater_Logo host, but control
        // the inner Image instead of being forced to resize the whole host.
        // Everything is opt-in; missing/invalid Tag keeps the old rendering.
        private void ApplyThemeLogoLayout()
        {
            DisplayImage.Stretch = Stretch.Uniform;
            DisplayImage.StretchDirection = StretchDirection.Both;
            DisplayImage.MaxWidth = double.PositiveInfinity;
            DisplayImage.MaxHeight = double.PositiveInfinity;
            DisplayImage.HorizontalAlignment = HorizontalAlignment.Stretch;
            DisplayImage.VerticalAlignment = VerticalAlignment.Stretch;

            FrameworkElement current = this;
            while (current != null)
            {
                var host = current as ContentControl;
                if (host != null && string.Equals(host.Name, "ImageRotater_Logo", StringComparison.Ordinal))
                {
                    string value = host.Tag as string;
                    if (string.IsNullOrWhiteSpace(value) ||
                        !value.StartsWith("ImageRotater:", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    string options = value.Substring("ImageRotater:".Length);
                    foreach (string rawPart in options.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] pair = rawPart.Split(new[] { '=' }, 2);
                        if (pair.Length != 2) continue;

                        string key = pair[0].Trim();
                        string raw = pair[1].Trim();
                        if (key.Equals("Stretch", StringComparison.OrdinalIgnoreCase))
                        {
                            Stretch parsed;
                            if (Enum.TryParse(raw, true, out parsed) && Enum.IsDefined(typeof(Stretch), parsed))
                                DisplayImage.Stretch = parsed;
                        }
                        else if (key.Equals("StretchDirection", StringComparison.OrdinalIgnoreCase))
                        {
                            StretchDirection parsed;
                            if (Enum.TryParse(raw, true, out parsed) && Enum.IsDefined(typeof(StretchDirection), parsed))
                                DisplayImage.StretchDirection = parsed;
                        }
                        else if (key.Equals("MaxWidth", StringComparison.OrdinalIgnoreCase) ||
                                 key.Equals("MaxHeight", StringComparison.OrdinalIgnoreCase))
                        {
                            double dimension;
                            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out dimension) &&
                                !double.IsNaN(dimension) && !double.IsInfinity(dimension) && dimension > 0)
                            {
                                if (key.Equals("MaxWidth", StringComparison.OrdinalIgnoreCase))
                                    DisplayImage.MaxWidth = dimension;
                                else
                                    DisplayImage.MaxHeight = dimension;
                            }
                        }
                        else if (key.Equals("HorizontalAlignment", StringComparison.OrdinalIgnoreCase))
                        {
                            HorizontalAlignment parsed;
                            if (Enum.TryParse(raw, true, out parsed) && Enum.IsDefined(typeof(HorizontalAlignment), parsed))
                                DisplayImage.HorizontalAlignment = parsed;
                        }
                        else if (key.Equals("VerticalAlignment", StringComparison.OrdinalIgnoreCase))
                        {
                            VerticalAlignment parsed;
                            if (Enum.TryParse(raw, true, out parsed) && Enum.IsDefined(typeof(VerticalAlignment), parsed))
                                DisplayImage.VerticalAlignment = parsed;
                        }
                    }
                    return;
                }

                DependencyObject parent = null;
                try { parent = VisualTreeHelper.GetParent(current); }
                catch (InvalidOperationException) { }
                if (parent == null) parent = LogicalTreeHelper.GetParent(current);
                current = parent as FrameworkElement;
            }
        }

        private static Guid SelectionKey(Guid gameId)
        {
            byte[] bytes = gameId.ToByteArray();
            bytes[0] ^= 0x6A;
            return new Guid(bytes);
        }

        private static bool IsSupportedLogo(string path)
        {
            string ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp";
        }
    }
}
