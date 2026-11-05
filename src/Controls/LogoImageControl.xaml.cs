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
        private readonly bool _nativeInjected;
        private int _generation;
        // The asynchronous renderer can retain a bitmap while a new game's
        // artwork is loading. Keep ownership explicit so an injected native
        // Image never paints a decoded logo from the previous game.
        internal Guid DisplayedGameId { get; private set; }
        internal bool HasCurrentGameLogo =>
            GameContext != null && DisplayedGameId == GameContext.Id &&
            DisplayImage.Source != null;

        private int _lastBucket;
        private readonly FileLogger _fileLogger;
        internal void TraceLogo(string action, Func<string> details = null)
        {
            if (_fileLogger?.IsEnabled != true) return;
            try
            {
                _fileLogger.Log($"LOGO {action} renderer={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):x} game={GameContext?.Id} generation={_generation} loading={NativeLoading} {details?.Invoke()}");
            }
            catch (Exception) { /* Diagnostics must not affect rendering. */ }
        }

        internal static string DescribeLogoSource(ImageSource source)
        {
            if (source == null) return "null";
            string identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source).ToString("x");
            if (source is BitmapSource bitmap)
                return $"{source.GetType().Name}#{identity}[{bitmap.PixelWidth}x{bitmap.PixelHeight},dpi={bitmap.DpiX:0.##}/{bitmap.DpiY:0.##},format={bitmap.Format},frozen={bitmap.IsFrozen}]";
            return $"{source.GetType().Name}#{identity}[{source.Width:0.##}x{source.Height:0.##}]";
        }

        internal bool NativeLoading { get; private set; }
        internal event Action NativeLoadStateChanged;

        private void SetNativeLoading(bool loading)
        {
            if (!_nativeInjected) return;
            NativeLoading = loading;
            TraceLogo("load-state");
            NativeLoadStateChanged?.Invoke();
        }

        public static event Action<Guid> ArtworkChanged;
        public static event Action SettingsChanged;

        public LogoImageControl(
            IBackgroundImageSource source,
            ImageSelector selector,
            ImageLoader loader,
            Func<ImageRotaterSettings> settings, bool nativeInjected = false, FileLogger fileLogger = null)
        {
            InitializeComponent();
            _source = source;
            _selector = selector;
            _loader = loader;
            _settings = settings;
            _nativeInjected = nativeInjected;
            _fileLogger = fileLogger;
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
            TraceLogo("loaded", () => $"injected={_nativeInjected}");
            ApplyThemeLogoLayout();
            ArtworkChanged += OnArtworkChanged;
            SettingsChanged += OnSettingsChanged;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TraceLogo("unloaded");
            ArtworkChanged -= OnArtworkChanged;
            SettingsChanged -= OnSettingsChanged;
            _generation++;
            // Ensure a reused native renderer cannot report an old bitmap as
            // belonging to its next game after detach and reattach.
            DisplayedGameId = Guid.Empty;
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
            TraceLogo("context-change", () => $"old={oldContext?.Id} new={newContext?.Id}");
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
            // Refresh is invoked by WPF events, so its void entry point must
            // observe async errors. Otherwise a source/selection exception
            // can terminate Playnite's UI dispatcher.
            int requestGeneration = _generation + 1;
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Playnite.SDK.LogManager.GetLogger().Warn(ex,
                    "ImageRotater: logo refresh failed");
                if (requestGeneration != _generation) return;
                DisplayedGameId = Guid.Empty;
                DisplayImage.Source = null;
                SetNativeLoading(false);
            }
        }

        private async Task RefreshAsync()
        {
            int generation = ++_generation;
            var watch = _fileLogger?.IsEnabled == true ? System.Diagnostics.Stopwatch.StartNew() : null;
            TraceLogo("refresh", () => $"injected={_nativeInjected} width={(_nativeInjected ? Width : ActualWidth):0.##}");
            if (_loader?.IsSuspended == true)
            {
                TraceLogo("suspended");
                DisplayedGameId = Guid.Empty;
                DisplayImage.Source = null;
                SetNativeLoading(false);
                return;
            }
            Game game = GameContext;
            ImageRotaterSettings settings = _settings != null ? _settings() : null;

            bool enabled = settings != null && (_nativeInjected
                ? settings.UseExperimentalNativeLogos && settings.CanConfigureLogos
                : settings.EnableLogoImage);
            if (game == null || !enabled || !settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Logo))
            {
                TraceLogo("disabled", () => $"enabled={enabled}");
                DisplayedGameId = Guid.Empty;
                DisplayImage.Source = null;
                SetNativeLoading(false);
                if (settings != null && !_nativeInjected)
                {
                    settings.HasDataLogo = false;
                }
                return;
            }

            List<string> candidates = (_source?.GetImagePaths(game) ?? new List<string>())
                .Where(IsSupportedLogo)
                .Where(File.Exists)
                .ToList();

            if (!_nativeInjected) settings.HasDataLogo = candidates.Count > 0;
            if (candidates.Count == 0)
            {
                TraceLogo("no-artwork");
                DisplayedGameId = Guid.Empty;
                DisplayImage.Source = null;
                SetNativeLoading(false);
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
                TraceLogo("no-pick", () => $"candidates={candidates.Count} mode={mode} order={order}");
                DisplayedGameId = Guid.Empty;
                DisplayImage.Source = null;
                SetNativeLoading(false);
                return;
            }

            int bucket = WidthBucket.ForWidth(_nativeInjected ? Width : ActualWidth);
            _lastBucket = bucket;
            TraceLogo("load-start", () => $"candidates={candidates.Count} mode={mode} order={order} bucket={bucket} path={path}");
            SetNativeLoading(true);
            BitmapSource bitmap = await _loader.LoadAsync(path, bucket);
            if (generation != _generation || GameContext?.Id != game.Id)
            {
                TraceLogo("load-stale", () => $"request={generation} requestedGame={game.Id} elapsed={watch?.ElapsedMilliseconds}ms path={path}");
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

                    TraceLogo("load-fallback", () => $"request={generation} path={candidate}");
                    bitmap = await _loader.LoadAsync(candidate, bucket);
                    if (generation != _generation || GameContext?.Id != game.Id)
                    {
                        TraceLogo("load-stale", () => $"request={generation} requestedGame={game.Id} elapsed={watch?.ElapsedMilliseconds}ms path={candidate}");
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
                TraceLogo("load-failed", () => $"request={generation} elapsed={watch?.ElapsedMilliseconds}ms path={path}");
                if (!_nativeInjected) settings.HasDataLogo = false;
                DisplayedGameId = Guid.Empty;
                DisplayImage.Source = null;
                SetNativeLoading(false);
                return;
            }

            TraceLogo("load-ready", () => $"request={generation} elapsed={watch?.ElapsedMilliseconds}ms path={path} source={DescribeLogoSource(bitmap)}");
            // Set the owner BEFORE raising Image.Source changes: the native
            // injection listens synchronously and reads HasCurrentGameLogo.
            DisplayedGameId = game.Id;
            DisplayImage.Source = bitmap;
            SetNativeLoading(false);

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
