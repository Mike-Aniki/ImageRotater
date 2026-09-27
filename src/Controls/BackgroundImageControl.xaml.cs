using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    public partial class BackgroundImageControl : PluginUserControl
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private enum SlotKind
        {
            None,
            Still,
            Gif,
            Video
        }

        private sealed class RenderSlot
        {
            public RenderSlot(Grid container, Image image, MediaElement video, string name)
            {
                Container = container;
                Image = image;
                Video = video;
                Name = name;
            }

            public Grid Container { get; }
            public Image Image { get; }
            public MediaElement Video { get; }
            public string Name { get; }

            public SlotKind Kind;
            public string Path;
            public Guid GameId = Guid.Empty;
            public string GameName;
            public int RequestToken;
            public int Bucket;
            public Stopwatch VideoOpenWatch;
        }

        private readonly IBackgroundImageSource _source;
        private readonly ImageSelector _selector;
        private readonly ImageLoader _loader;
        private readonly Func<ImageRotaterSettings> _settings;
        private readonly FileLogger _fileLogger;
        private readonly Func<string, string> _resolveFullPath;

        private readonly HashSet<string> _loggedFailures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private RenderSlot _slotA;
        private RenderSlot _slotB;
        private RenderSlot _activeSlot;
        private RenderSlot _pendingSlot;

        private string _previousPick;
        private int _requestToken;
        private int _transitionToken;
        private int _currentBucket;

        // Debounce sustained navigation without delaying normal taps.
        private static readonly TimeSpan RapidSelectionWindow = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan RapidSelectionSettle = TimeSpan.FromMilliseconds(200);
        private readonly DispatcherTimer _rapidSelectionTimer;
        private long _lastSelectionStamp;
        private int _rapidSelectionStreak;
        private Guid _deferredGameId = Guid.Empty;

        private static readonly object SharedPickLock = new object();
        private static Guid SharedPickGameId = Guid.Empty;
        private static string SharedPickPath;
        private static readonly Dictionary<Guid, string> LastEverySelectionPick =
            new Dictionary<Guid, string>();

        public BackgroundImageControl(
            IBackgroundImageSource source,
            ImageSelector selector,
            ImageLoader loader,
            Func<ImageRotaterSettings> settings,
            FileLogger fileLogger = null,
            Func<string, string> resolveFullPath = null)
        {
            InitializeComponent();

            _source = source;
            _selector = selector;
            _loader = loader;
            _settings = settings;
            _fileLogger = fileLogger;
            _resolveFullPath = resolveFullPath;

            _slotA = new RenderSlot(SlotA, SlotAImage, SlotAVideo, "A");
            _slotB = new RenderSlot(SlotB, SlotBImage, SlotBVideo, "B");

            _rapidSelectionTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = RapidSelectionSettle
            };
            _rapidSelectionTimer.Tick += RapidSelectionTimer_Tick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public static event Action<Guid> BackgroundRotated;

        public static void NotifyBackgroundRotated(Guid gameId)
        {
            lock (SharedPickLock)
            {
                SharedPickGameId = gameId;
                SharedPickPath = null;
            }

            Action<Guid> handler = BackgroundRotated;
            if (handler == null)
            {
                return;
            }

            Application app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => handler(gameId)));
                return;
            }

            handler(gameId);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SizeChanged += OnSizeChanged;
            BackgroundRotated += OnBackgroundRotated;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SizeChanged -= OnSizeChanged;
            BackgroundRotated -= OnBackgroundRotated;

            _rapidSelectionTimer.Stop();
            _deferredGameId = Guid.Empty;
            _lastSelectionStamp = 0;
            _rapidSelectionStreak = 0;

            _requestToken++;
            _transitionToken++;
            StopAnimations();
            ClearSlot(_slotA);
            ClearSlot(_slotB);
            _activeSlot = null;
            _pendingSlot = null;
        }

        private void OnBackgroundRotated(Guid gameId)
        {
            if (GameContext == null || GameContext.Id != gameId)
            {
                return;
            }

            _previousPick = null;
            Refresh();
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            // Keep the outgoing slot mounted until the incoming media is ready.
            _previousPick = null;

            // Reset the shared EverySelection pick on every game change, including native-only games.
            Guid newGameId = newContext?.Id ?? Guid.Empty;
            lock (SharedPickLock)
            {
                if (SharedPickGameId != newGameId)
                {
                    SharedPickGameId = newGameId;
                    SharedPickPath = null;
                }
            }

            long now = Stopwatch.GetTimestamp();
            double elapsedMs = _lastSelectionStamp == 0
                ? double.MaxValue
                : (now - _lastSelectionStamp) * 1000.0 / Stopwatch.Frequency;
            _lastSelectionStamp = now;

            if (elapsedMs <= RapidSelectionWindow.TotalMilliseconds)
            {
                _rapidSelectionStreak++;
            }
            else
            {
                // Require three fast selections before treating input as sustained navigation.
                _rapidSelectionStreak = 1;
            }

            if (_rapidSelectionStreak >= 3)
            {
                // Skip intermediate media while key-repeat is active.
                _requestToken++;
                NormalizeTransitionState();

                _deferredGameId = newContext?.Id ?? Guid.Empty;
                _rapidSelectionTimer.Stop();
                _rapidSelectionTimer.Start();

                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _fileLogger.Log(
                        $"BG PERF scroll-debounce game=\"{newContext?.Name}\" " +
                        $"gap={elapsedMs:0}ms streak={_rapidSelectionStreak} " +
                        $"settle={RapidSelectionSettle.TotalMilliseconds:0}ms");
                }
                return;
            }

            _rapidSelectionTimer.Stop();
            _deferredGameId = Guid.Empty;
            Refresh();
        }

        private void RapidSelectionTimer_Tick(object sender, EventArgs e)
        {
            _rapidSelectionTimer.Stop();

            Game game = GameContext;
            if (game == null || _deferredGameId == Guid.Empty || game.Id != _deferredGameId)
            {
                return;
            }

            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log($"BG PERF scroll-settle game=\"{game.Name}\"");
            }

            _deferredGameId = Guid.Empty;
            _rapidSelectionStreak = 0;
            _lastSelectionStamp = 0;
            Refresh();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            int bucket = WidthBucket.ForWidth(ActualWidth);
            if (bucket != _currentBucket)
            {
                Refresh();
            }
        }

        private async void Refresh()
        {
            Stopwatch total = _fileLogger != null && _fileLogger.IsEnabled
                ? Stopwatch.StartNew()
                : null;
            Stopwatch step = total != null ? Stopwatch.StartNew() : null;
            long listMs = 0;
            long selectMs = 0;

            try
            {
                int token = ++_requestToken;
                int bucket = WidthBucket.ForWidth(ActualWidth);
                _currentBucket = bucket;

                ImageRotaterSettings settings = _settings != null ? _settings() : null;
                if (settings != null && (!settings.EnableRotation || !settings.RotateBackgrounds))
                {
                    TransitionToNothing(token);
                    return;
                }

                Game game = GameContext;
                if (game == null)
                {
                    TransitionToNothing(token);
                    return;
                }

                IReadOnlyList<string> candidates = _source != null
                    ? _source.GetImagePaths(game)
                    : null;

                if (step != null)
                {
                    listMs = step.ElapsedMilliseconds;
                    step.Restart();
                }

                string path = null;
                bool isNativeFallback = false;

                if (candidates != null && candidates.Count > 0 && _selector != null)
                {
                    SelectionMode mode = settings != null
                        ? settings.SelectionMode
                        : SelectionMode.Session;

                    path = SelectPath(game, candidates, mode);
                    if (step != null)
                    {
                        selectMs = step.ElapsedMilliseconds;
                    }

                    _previousPick = path;

                    if (total != null)
                    {
                        string type = string.IsNullOrEmpty(path)
                            ? "none"
                            : PosterFrame.IsVideo(path)
                                ? "video"
                                : PosterFrame.IsAnimated(path) ? "gif" : "still";

                        _fileLogger.Log(
                            $"BG PERF control \"{game.Name}\" type={type} total={total.ElapsedMilliseconds}ms " +
                            $"list={listMs}ms select={selectMs}ms candidates={candidates.Count} mode={mode} path={path}");
                    }
                }
                else
                {
                    path = ResolveNativeBackground(game);
                    isNativeFallback = !string.IsNullOrEmpty(path);

                    if (total != null)
                    {
                        _fileLogger.Log(
                            $"BG PERF control \"{game.Name}\" " +
                            (isNativeFallback
                                ? $"type=native-still total={total.ElapsedMilliseconds}ms list={listMs}ms path={path}"
                                : $"skip=no-candidates total={total.ElapsedMilliseconds}ms list={listMs}ms"));
                    }
                }

                if (token != _requestToken || GameContext == null || GameContext.Id != game.Id)
                {
                    return;
                }

                if (string.IsNullOrEmpty(path))
                {
                    TransitionToNothing(token);
                    return;
                }

                if (PosterFrame.IsVideo(path))
                {
                    PrepareVideo(game, path, token);
                    ImageDiagnostics.LogApplied(game.Name, path, _settings, bucket, 0);
                    return;
                }

                if (PosterFrame.IsAnimated(path))
                {
                    PrepareGif(game, path, token);
                    ImageDiagnostics.LogApplied(game.Name, path, _settings, bucket, 0);
                    return;
                }

                await PrepareStillAsync(game, path, bucket, token, isNativeFallback);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater refresh failed");
                if (total != null)
                {
                    _fileLogger.Log(
                        $"BG PERF control failed total={total.ElapsedMilliseconds}ms " +
                        $"error={ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private string SelectPath(Game game, IReadOnlyList<string> candidates, SelectionMode mode)
        {
            if (mode != SelectionMode.EverySelection)
            {
                return _selector.Select(game.Id, candidates, _previousPick, mode);
            }

            lock (SharedPickLock)
            {
                if (SharedPickGameId != game.Id)
                {
                    SharedPickGameId = game.Id;
                    SharedPickPath = null;
                }

                if (!string.IsNullOrEmpty(SharedPickPath))
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (string.Equals(
                            candidates[i], SharedPickPath,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return SharedPickPath;
                        }
                    }
                }

                string previous;
                LastEverySelectionPick.TryGetValue(game.Id, out previous);

                SharedPickPath = _selector.Select(
                    game.Id, candidates, previous, mode);

                if (!string.IsNullOrEmpty(SharedPickPath))
                {
                    LastEverySelectionPick[game.Id] = SharedPickPath;
                }

                return SharedPickPath;
            }
        }

        private string ResolveNativeBackground(Game game)
        {
            if (game == null || string.IsNullOrEmpty(game.BackgroundImage) || _resolveFullPath == null)
            {
                return null;
            }

            try
            {
                string full = _resolveFullPath(game.BackgroundImage);
                return !string.IsNullOrEmpty(full) && File.Exists(full) ? full : null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not resolve native background for '{game.Name}'");
                return null;
            }
        }

        private async Task PrepareStillAsync(
            Game game,
            string path,
            int bucket,
            int token,
            bool nativeFallback)
        {
            RenderSlot same = FindSlot(path, SlotKind.Still);
            if (same != null && same.Bucket == bucket &&
                (same == _activeSlot || same == _pendingSlot))
            {
                same.GameId = game.Id;
                same.GameName = game.Name;
                same.RequestToken = token;
                return;
            }

            Stopwatch watch = _fileLogger != null && _fileLogger.IsEnabled
                ? Stopwatch.StartNew()
                : null;

            BitmapSource bitmap = _loader != null
                ? await _loader.LoadAsync(path, bucket)
                : null;

            if (token != _requestToken || GameContext == null || GameContext.Id != game.Id)
            {
                return;
            }

            if (bitmap == null)
            {
                ReportLoadFailure(path, "still");
                ShowPlaceholder();
                return;
            }

            NormalizeTransitionState();
            RenderSlot incoming = GetInactiveSlot();
            PrepareSlotBase(incoming, game, path, SlotKind.Still, token, bucket);
            incoming.Image.Source = bitmap;
            incoming.Image.Visibility = Visibility.Visible;

            if (watch != null)
            {
                _fileLogger.Log(
                    $"BG PERF slot-ready \"{game.Name}\" kind={(nativeFallback ? "native-still" : "still")} " +
                    $"slot={incoming.Name} {watch.ElapsedMilliseconds}ms bucket={bucket} path={path}");
            }

            BeginTransition(incoming);

            if (!nativeFallback)
            {
                ImageDiagnostics.LogApplied(game.Name, path, _settings, bucket, 0);
            }
        }

        private void PrepareGif(Game game, string path, int token)
        {
            RenderSlot same = FindSlot(path, SlotKind.Gif);
            if (same != null && (same == _activeSlot || same == _pendingSlot))
            {
                same.GameId = game.Id;
                same.GameName = game.Name;
                same.RequestToken = token;
                return;
            }

            NormalizeTransitionState();
            RenderSlot incoming = GetInactiveSlot();
            PrepareSlotBase(incoming, game, path, SlotKind.Gif, token, 0);

            try
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(incoming.Image, new Uri(path));
                incoming.Image.Visibility = Visibility.Visible;

                // GIF has no MediaOpened equivalent. Queue the transition at
                // Render priority so WPF gets one render pass to create the
                // animation source before the old slot starts leaving.
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (token == _requestToken &&
                            GameContext != null && GameContext.Id == game.Id &&
                            incoming.RequestToken == token)
                        {
                            BeginTransition(incoming);
                        }
                    }),
                    System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not load GIF background: {path}");
                ReportLoadFailure(path, "gif");
                ClearSlot(incoming);
                ShowPlaceholder();
            }
        }

        private void PrepareVideo(Game game, string path, int token)
        {
            RenderSlot same = FindSlot(path, SlotKind.Video);
            if (same != null)
            {
                same.GameId = game.Id;
                same.GameName = game.Name;
                same.RequestToken = token;

                if (same == _activeSlot)
                {
                    try
                    {
                        same.Video.Play();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "ImageRotater: could not resume reused background video");
                    }

                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _fileLogger.Log($"BG PERF video-reuse \"{game.Name}\" slot={same.Name} path={path}");
                    }
                    return;
                }

                // The same video is already opening in the pending slot. Update
                // its ownership so MediaOpened is valid for the newest refresh.
                if (same == _pendingSlot)
                {
                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _fileLogger.Log($"BG PERF video-pending-reuse \"{game.Name}\" slot={same.Name} path={path}");
                    }
                    return;
                }
            }

            NormalizeTransitionState();
            RenderSlot incoming = GetInactiveSlot();
            PrepareSlotBase(incoming, game, path, SlotKind.Video, token, 0);

            incoming.VideoOpenWatch = _fileLogger != null && _fileLogger.IsEnabled
                ? Stopwatch.StartNew()
                : null;

            incoming.Video.Source = new Uri(path);
            incoming.Video.Visibility = Visibility.Visible;
            incoming.Container.Visibility = Visibility.Visible;
            incoming.Container.Opacity = 0.0;
            _pendingSlot = incoming;

            try
            {
                incoming.Video.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not start background video");
            }
        }

        private void PrepareSlotBase(
            RenderSlot slot,
            Game game,
            string path,
            SlotKind kind,
            int token,
            int bucket)
        {
            if (slot == null)
            {
                return;
            }

            ClearSlot(slot);

            slot.Kind = kind;
            slot.Path = path;
            slot.GameId = game?.Id ?? Guid.Empty;
            slot.GameName = game?.Name;
            slot.RequestToken = token;
            slot.Bucket = bucket;

            slot.Container.BeginAnimation(OpacityProperty, null);
            slot.Container.Opacity = 0.0;
            slot.Container.Visibility = Visibility.Visible;

            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
        }

        private RenderSlot GetInactiveSlot()
        {
            if (_activeSlot == _slotA)
            {
                return _slotB;
            }

            if (_activeSlot == _slotB)
            {
                return _slotA;
            }

            return _slotA;
        }

        private RenderSlot FindSlot(string path, SlotKind kind)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (_slotA.Kind == kind &&
                string.Equals(_slotA.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return _slotA;
            }

            if (_slotB.Kind == kind &&
                string.Equals(_slotB.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return _slotB;
            }

            return null;
        }

        private void BeginTransition(RenderSlot incoming)
        {
            if (incoming == null || incoming.RequestToken != _requestToken)
            {
                return;
            }

            if (GameContext == null || GameContext.Id != incoming.GameId)
            {
                ClearSlot(incoming);
                return;
            }

            RenderSlot outgoing = _activeSlot;

            if (outgoing == incoming)
            {
                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;
                incoming.Container.Visibility = Visibility.Visible;
                _pendingSlot = null;
                return;
            }

            int transitionToken = ++_transitionToken;
            _pendingSlot = incoming;

            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = outgoing == null ? 1.0 : 0.0;
            incoming.Container.Visibility = Visibility.Visible;

            TransitionStyle style = Transition.BackgroundStyle;

            if (outgoing == null || style == TransitionStyle.Cut)
            {
                if (outgoing != null)
                {
                    ClearSlot(outgoing);
                }

                incoming.Container.Opacity = 1.0;
                _activeSlot = incoming;
                _pendingSlot = null;
                LogTransition(outgoing, incoming, style);
                return;
            }

            if (Transition.IsFlash(style))
            {
                StartFlashTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            // Fade the incoming slot over an opaque outgoing slot so the native background cannot show through.
            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(
                0.0, 1.0, new Duration(Transition.Duration));

            fadeIn.Completed += (s, e) =>
            {
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;
                ClearSlot(outgoing);
                _activeSlot = incoming;
                _pendingSlot = null;
            };

            incoming.Container.BeginAnimation(OpacityProperty, fadeIn);
            LogTransition(outgoing, incoming, style);
        }

        private void StartFlashTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            FlashOverlay.BeginAnimation(OpacityProperty, null);
            FlashOverlay.Background = new SolidColorBrush(Transition.FlashColor(style));
            FlashOverlay.Opacity = 0.0;
            FlashOverlay.Visibility = Visibility.Visible;

            var up = new DoubleAnimation(
                0.0, 1.0, new Duration(Transition.Half));

            up.Completed += (s, e) =>
            {
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                outgoing.Container.BeginAnimation(OpacityProperty, null);
                outgoing.Container.Opacity = 0.0;
                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;

                ClearSlot(outgoing);
                _activeSlot = incoming;
                _pendingSlot = null;

                var down = new DoubleAnimation(
                    1.0, 0.0, new Duration(Transition.Half));

                down.Completed += (s2, e2) =>
                {
                    if (transitionToken == _transitionToken)
                    {
                        FlashOverlay.BeginAnimation(OpacityProperty, null);
                        FlashOverlay.Opacity = 0.0;
                        FlashOverlay.Visibility = Visibility.Collapsed;
                    }
                };

                FlashOverlay.BeginAnimation(OpacityProperty, down);
            };

            FlashOverlay.BeginAnimation(OpacityProperty, up);
            LogTransition(outgoing, incoming, style);
        }

        private void TransitionToNothing(int requestToken)
        {
            if (requestToken != _requestToken)
            {
                return;
            }

            NormalizeTransitionState();

            RenderSlot outgoing = _activeSlot;
            if (outgoing == null)
            {
                return;
            }

            int transitionToken = ++_transitionToken;
            TransitionStyle style = Transition.BackgroundStyle;

            if (style == TransitionStyle.Cut)
            {
                ClearSlot(outgoing);
                _activeSlot = null;
                _pendingSlot = null;
                return;
            }

            if (Transition.IsFlash(style))
            {
                FlashOverlay.BeginAnimation(OpacityProperty, null);
                FlashOverlay.Background = new SolidColorBrush(Transition.FlashColor(style));
                FlashOverlay.Opacity = 0.0;
                FlashOverlay.Visibility = Visibility.Visible;

                var up = new DoubleAnimation(0.0, 1.0, new Duration(Transition.Half));
                up.Completed += (s, e) =>
                {
                    if (transitionToken != _transitionToken)
                    {
                        return;
                    }

                    ClearSlot(outgoing);
                    _activeSlot = null;

                    var down = new DoubleAnimation(1.0, 0.0, new Duration(Transition.Half));
                    down.Completed += (s2, e2) =>
                    {
                        if (transitionToken == _transitionToken)
                        {
                            FlashOverlay.BeginAnimation(OpacityProperty, null);
                            FlashOverlay.Visibility = Visibility.Collapsed;
                            FlashOverlay.Opacity = 0.0;
                        }
                    };
                    FlashOverlay.BeginAnimation(OpacityProperty, down);
                };
                FlashOverlay.BeginAnimation(OpacityProperty, up);
                return;
            }

            var fade = new DoubleAnimation(
                outgoing.Container.Opacity, 0.0,
                new Duration(Transition.Duration));

            fade.Completed += (s, e) =>
            {
                if (transitionToken == _transitionToken)
                {
                    ClearSlot(outgoing);
                    _activeSlot = null;
                }
            };

            outgoing.Container.BeginAnimation(OpacityProperty, fade);
        }

        private void NormalizeTransitionState()
        {
            double a = _slotA.Container.Visibility == Visibility.Visible
                ? _slotA.Container.Opacity
                : 0.0;
            double b = _slotB.Container.Visibility == Visibility.Visible
                ? _slotB.Container.Opacity
                : 0.0;

            _transitionToken++;
            StopAnimations();

            RenderSlot keep = null;
            if (a > 0.001 || b > 0.001)
            {
                if (Math.Abs(a - b) < 0.001 && _pendingSlot != null)
                {
                    keep = _pendingSlot;
                }
                else
                {
                    keep = a >= b ? _slotA : _slotB;
                }
            }

            RenderSlot drop = keep == _slotA ? _slotB : _slotA;

            if (keep != null && keep.Kind != SlotKind.None)
            {
                keep.Container.Visibility = Visibility.Visible;
                keep.Container.Opacity = 1.0;
                _activeSlot = keep;
            }
            else
            {
                _activeSlot = null;
            }

            if (drop != null)
            {
                ClearSlot(drop);
            }

            _pendingSlot = null;
            FlashOverlay.Visibility = Visibility.Collapsed;
            FlashOverlay.Opacity = 0.0;
        }

        private void StopAnimations()
        {
            double a = _slotA?.Container.Opacity ?? 0.0;
            double b = _slotB?.Container.Opacity ?? 0.0;
            double flash = FlashOverlay?.Opacity ?? 0.0;

            _slotA?.Container.BeginAnimation(OpacityProperty, null);
            _slotB?.Container.BeginAnimation(OpacityProperty, null);
            FlashOverlay?.BeginAnimation(OpacityProperty, null);

            if (_slotA != null) _slotA.Container.Opacity = a;
            if (_slotB != null) _slotB.Container.Opacity = b;
            if (FlashOverlay != null) FlashOverlay.Opacity = flash;
        }

        private void ClearSlot(RenderSlot slot)
        {
            if (slot == null)
            {
                return;
            }

            slot.Container.BeginAnimation(OpacityProperty, null);
            slot.Container.Opacity = 0.0;
            slot.Container.Visibility = Visibility.Collapsed;

            try
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(slot.Image, null);
            }
            catch
            {
                // Best effort cleanup only.
            }

            slot.Image.Source = null;
            slot.Image.Visibility = Visibility.Collapsed;

            if (slot.Video.Source != null)
            {
                try { slot.Video.Stop(); } catch { }
                try { slot.Video.Close(); } catch { }
            }

            slot.Video.Source = null;
            slot.Video.Visibility = Visibility.Collapsed;

            slot.Kind = SlotKind.None;
            slot.Path = null;
            slot.GameId = Guid.Empty;
            slot.GameName = null;
            slot.RequestToken = 0;
            slot.Bucket = 0;
            slot.VideoOpenWatch = null;
        }

        private void ShowPlaceholder()
        {
            _transitionToken++;
            StopAnimations();
            ClearSlot(_slotA);
            ClearSlot(_slotB);
            _activeSlot = null;
            _pendingSlot = null;
            MissingImagePlaceholder.Visibility = Visibility.Visible;
        }

        private void ReportLoadFailure(string path, string kind)
        {
            if (!string.IsNullOrEmpty(path) && _loggedFailures.Add(path))
            {
                Logger.Warn($"ImageRotater: could not load background {kind}: {path}");
            }
        }

        private RenderSlot SlotForVideo(MediaElement video)
        {
            if (ReferenceEquals(video, _slotA.Video)) return _slotA;
            if (ReferenceEquals(video, _slotB.Video)) return _slotB;
            return null;
        }

        private void SlotVideo_Loaded(object sender, RoutedEventArgs e)
        {
            RenderSlot slot = SlotForVideo(sender as MediaElement);
            if (slot == null || slot.Kind != SlotKind.Video || slot.Video.Source == null)
            {
                return;
            }

            try
            {
                slot.Video.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not resume background video after reload");
            }
        }

        private void SlotVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            RenderSlot slot = SlotForVideo(sender as MediaElement);
            if (slot == null)
            {
                return;
            }

            bool stale =
                slot.Kind != SlotKind.Video ||
                slot.RequestToken != _requestToken ||
                GameContext == null ||
                GameContext.Id != slot.GameId ||
                slot.Video.Source == null ||
                string.IsNullOrEmpty(slot.Path) ||
                !string.Equals(slot.Video.Source.LocalPath, slot.Path, StringComparison.OrdinalIgnoreCase);

            if (slot.VideoOpenWatch != null && _fileLogger != null)
            {
                _fileLogger.Log(
                    $"BG PERF video-open \"{slot.GameName}\" {slot.VideoOpenWatch.ElapsedMilliseconds}ms " +
                    $"slot={slot.Name} size={slot.Video.NaturalVideoWidth}x{slot.Video.NaturalVideoHeight} " +
                    $"stale={stale} path={slot.Path}");
                slot.VideoOpenWatch = null;
            }

            if (stale)
            {
                if (slot != _activeSlot)
                {
                    ClearSlot(slot);
                    if (slot == _pendingSlot) _pendingSlot = null;
                }
                return;
            }

            BeginTransition(slot);
        }

        private void SlotVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            RenderSlot slot = SlotForVideo(sender as MediaElement);
            if (slot == null || slot.Kind != SlotKind.Video || slot.Video.Source == null)
            {
                return;
            }

            try
            {
                slot.Video.Position = TimeSpan.Zero;
                slot.Video.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not loop background video");
            }
        }

        private void SlotVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            RenderSlot slot = SlotForVideo(sender as MediaElement);
            if (slot == null)
            {
                return;
            }

            string path = slot.Path ?? slot.Video.Source?.LocalPath;
            if (slot.VideoOpenWatch != null && _fileLogger != null)
            {
                _fileLogger.Log(
                    $"BG PERF video-open failed \"{slot.GameName}\" {slot.VideoOpenWatch.ElapsedMilliseconds}ms " +
                    $"slot={slot.Name} path={path} error={e.ErrorException?.Message}");
                slot.VideoOpenWatch = null;
            }

            ReportLoadFailure(path, "video");

            bool wasPending = slot == _pendingSlot;
            ClearSlot(slot);
            if (wasPending)
            {
                _pendingSlot = null;
                // Keep the outgoing active slot visible. A failed incoming codec
                // must never uncover a transient native background underneath.
                if (_activeSlot == null)
                {
                    ShowPlaceholder();
                }
            }
            else if (slot == _activeSlot)
            {
                _activeSlot = null;
                ShowPlaceholder();
            }
        }

        private void LogTransition(RenderSlot outgoing, RenderSlot incoming, TransitionStyle style)
        {
            if (_fileLogger == null || !_fileLogger.IsEnabled)
            {
                return;
            }

            _fileLogger.Log(
                $"BG PERF transition style={style} " +
                $"from={(outgoing == null ? "native" : outgoing.Kind + ":" + outgoing.Name)} " +
                $"to={(incoming == null ? "native" : incoming.Kind + ":" + incoming.Name)} " +
                $"game=\"{incoming?.GameName ?? GameContext?.Name}\"");
        }
    }
}
