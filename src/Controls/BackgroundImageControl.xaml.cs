using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly Func<IEnumerable<Game>> _filteredGames;

        // Theme Integration renders Playnite's native background inside this
        // control when a game has no ImageRotater backgrounds. GameContext does
        // not change when the user replaces that native artwork, so listen to
        // the selected Game itself and refresh only when BackgroundImage changes.
        private Game _observedNativeBackgroundGame;

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

        // Wait for the selection to remain stable before beginning the
        // background transition. This prevents slides from starting while the
        // user is still scrolling through the game list.
        private static readonly TimeSpan RapidSelectionSettle = TimeSpan.FromMilliseconds(400);
        private readonly DispatcherTimer _rapidSelectionTimer;
        private readonly DispatcherTimer _initialLayoutTimer;
        private bool _initialLayoutReady;
        private int _initialLayoutAttempts;

        // WPF can briefly report ActualWidth=0 while a fullscreen theme is still
        // measuring this control. WidthBucket intentionally maps width 0 to 480
        // as a generic safety fallback, but for a fullscreen background that can
        // cause an unnecessary 480 decode followed by the real 1920/3840 decode.
        // Prefer the control width when available, otherwise use the host window
        // (or the WPF primary-screen width as a last resort) for the initial decode.
        // This keeps Huddini's bucketed decode strategy while avoiding the known
        // duplicate low-resolution first pass.
        private const double MinimumInitialLayoutWidth = 640.0;
        private const int MaximumInitialLayoutAttempts = 8;
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
            Func<string, string> resolveFullPath = null,
            Func<IEnumerable<Game>> filteredGames = null)
        {
            InitializeComponent();

            _source = source;
            _selector = selector;
            _loader = loader;
            _settings = settings;
            _fileLogger = fileLogger;
            _resolveFullPath = resolveFullPath;
            _filteredGames = filteredGames;

            _slotA = new RenderSlot(SlotA, SlotAImage, SlotAVideo, "A");
            _slotB = new RenderSlot(SlotB, SlotBImage, SlotBVideo, "B");

            _rapidSelectionTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = RapidSelectionSettle
            };
            _rapidSelectionTimer.Tick += RapidSelectionTimer_Tick;

            // On first load WPF can report a tiny/temporary ActualWidth and
            // then expand the control to its final width a few frames later.
            // Decoding at that temporary bucket and immediately decoding the
            // same 4K PNG again at 1920/3840 was visible in debug traces as a
            // duplicate 1-2 second load. Wait briefly for layout to settle
            // before the first still-image decode. Normal game changes after
            // that are not delayed.
            _initialLayoutTimer = new DispatcherTimer(DispatcherPriority.Loaded)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _initialLayoutTimer.Tick += InitialLayoutTimer_Tick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public static event Action<Guid> BackgroundRotated;
        public static event Action PlaybackPolicyChanged;

        public static void NotifyPlaybackPolicyChanged()
        {
            Action handler = PlaybackPolicyChanged;
            if (handler == null) return;
            Application app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => handler()));
                return;
            }
            handler();
        }

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
            PlaybackPolicyChanged += OnPlaybackPolicyChanged;
            ObserveNativeBackground(GameContext);

            _initialLayoutReady = false;
            _initialLayoutAttempts = 0;
            ScheduleInitialLayoutRefresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SizeChanged -= OnSizeChanged;
            BackgroundRotated -= OnBackgroundRotated;
            PlaybackPolicyChanged -= OnPlaybackPolicyChanged;
            ObserveNativeBackground(null);

            _rapidSelectionTimer.Stop();
            _initialLayoutTimer.Stop();
            _initialLayoutReady = false;
            _initialLayoutAttempts = 0;
            _deferredGameId = Guid.Empty;

            _requestToken++;
            _transitionToken++;
            StopAnimations();
            ClearSlot(_slotA);
            ClearSlot(_slotB);
            _activeSlot = null;
            _pendingSlot = null;
        }

        private void ScheduleInitialLayoutRefresh()
        {
            _initialLayoutTimer.Stop();
            _initialLayoutTimer.Start();
        }

        private void InitialLayoutTimer_Tick(object sender, EventArgs e)
        {
            _initialLayoutTimer.Stop();
            _initialLayoutAttempts++;

            string widthSource;
            double effectiveWidth = GetEffectiveDecodeWidth(out widthSource);

            // ActualWidth is the best signal, but in some fullscreen themes the
            // host window already has its final size while this control still
            // reports 0. In that case there is no reason to decode at 480 first.
            if (effectiveWidth >= MinimumInitialLayoutWidth)
            {
                CompleteInitialLayout(
                    ActualWidth >= MinimumInitialLayoutWidth ? "timer-ready" : "host-ready");
                return;
            }

            if (_initialLayoutAttempts < MaximumInitialLayoutAttempts)
            {
                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _fileLogger.Log(
                        $"BG PERF initial-layout wait width={ActualWidth:0} " +
                        $"effective={effectiveWidth:0} source={widthSource} " +
                        $"attempt={_initialLayoutAttempts}/{MaximumInitialLayoutAttempts}");
                }

                ScheduleInitialLayoutRefresh();
                return;
            }

            CompleteInitialLayout("timer-fallback");
        }

        private double GetEffectiveDecodeWidth(out string source)
        {
            double width = ActualWidth;
            if (!double.IsNaN(width) && !double.IsInfinity(width) && width >= MinimumInitialLayoutWidth)
            {
                source = "control";
                return width;
            }

            try
            {
                Window host = Window.GetWindow(this);
                if (host != null)
                {
                    double hostWidth = host.ActualWidth;
                    if (!double.IsNaN(hostWidth) && !double.IsInfinity(hostWidth) &&
                        hostWidth >= MinimumInitialLayoutWidth)
                    {
                        source = "window";
                        return hostWidth;
                    }
                }
            }
            catch
            {
                // Fall through to the WPF screen metric.
            }

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            if (!double.IsNaN(screenWidth) && !double.IsInfinity(screenWidth) &&
                screenWidth >= MinimumInitialLayoutWidth)
            {
                source = "screen";
                return screenWidth;
            }

            source = "bucket-default";
            return width;
        }

        private int GetCurrentDecodeBucket(out double effectiveWidth, out string widthSource)
        {
            effectiveWidth = GetEffectiveDecodeWidth(out widthSource);
            return WidthBucket.ForWidth(effectiveWidth);
        }

        private void CompleteInitialLayout(string reason)
        {
            if (_initialLayoutReady)
            {
                return;
            }

            _initialLayoutTimer.Stop();
            _initialLayoutReady = true;

            double effectiveWidth;
            string widthSource;
            int bucket = GetCurrentDecodeBucket(out effectiveWidth, out widthSource);

            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log(
                    $"BG PERF initial-layout {reason} width={ActualWidth:0} " +
                    $"effective={effectiveWidth:0} source={widthSource} " +
                    $"bucket={bucket} attempts={_initialLayoutAttempts}");
            }

            Refresh();
        }

        private void OnPlaybackPolicyChanged()
        {
            Refresh();
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

        private void ObserveNativeBackground(Game game)
        {
            if (ReferenceEquals(_observedNativeBackgroundGame, game))
            {
                return;
            }

            if (_observedNativeBackgroundGame != null)
            {
                _observedNativeBackgroundGame.PropertyChanged -= OnObservedGamePropertyChanged;
            }

            _observedNativeBackgroundGame = game;

            if (_observedNativeBackgroundGame != null)
            {
                _observedNativeBackgroundGame.PropertyChanged += OnObservedGamePropertyChanged;
            }
        }

        private void OnObservedGamePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnObservedGamePropertyChanged(sender, e)));
                return;
            }

            if (!string.IsNullOrEmpty(e?.PropertyName) &&
                !string.Equals(e.PropertyName, nameof(Game.BackgroundImage), StringComparison.Ordinal))
            {
                return;
            }

            Game game = sender as Game;
            if (!IsLoaded || game == null || GameContext == null || game.Id != GameContext.Id)
            {
                return;
            }

            // Compatibility mode owns Game.BackgroundImage while rotating and
            // already has its own refresh path. Reacting to those writes here
            // would only duplicate work. This listener exists for the live
            // Playnite fallback used by Theme Integration.
            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings?.UseThemeIntegration != true)
            {
                return;
            }

            Refresh();
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            ObserveNativeBackground(IsLoaded ? newContext : null);

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

            // Delay every selection before starting background work.
            // If the user keeps scrolling, each new selection simply restarts
            // this timer. No intermediate background gets a chance to begin a
            // slide. Only the game that remains selected for the full settle
            // period is rendered and animated.
            _requestToken++;
            NormalizeTransitionState();

            _deferredGameId = newContext?.Id ?? Guid.Empty;
            _rapidSelectionTimer.Stop();
            _rapidSelectionTimer.Start();

            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log(
                    $"BG PERF selection-settle-pending game=\"{newContext?.Name}\" " +
                    $"settle={RapidSelectionSettle.TotalMilliseconds:0}ms");
            }

            return;
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
                _fileLogger.Log($"BG PERF selection-settle game=\"{game.Name}\"");
            }

            _deferredGameId = Guid.Empty;
            Refresh();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_initialLayoutReady)
            {
                // Prefer the control's own measured width, but allow the host
                // window/screen fallback to make the first fullscreen decode at
                // the correct bucket even while ActualWidth is still 0.
                string widthSource;
                double effectiveWidth = GetEffectiveDecodeWidth(out widthSource);
                if (effectiveWidth >= MinimumInitialLayoutWidth)
                {
                    CompleteInitialLayout(
                        ActualWidth >= MinimumInitialLayoutWidth ? "size-ready" : "size-host-ready");
                }
                else
                {
                    ScheduleInitialLayoutRefresh();
                }
                return;
            }

            double resizedEffectiveWidth;
            string resizedWidthSource;
            int bucket = GetCurrentDecodeBucket(out resizedEffectiveWidth, out resizedWidthSource);
            if (bucket != _currentBucket)
            {
                Refresh();
            }
        }

        private async void Refresh()
        {
            if (!_initialLayoutReady)
            {
                ScheduleInitialLayoutRefresh();
                return;
            }

            Stopwatch total = _fileLogger != null && _fileLogger.IsEnabled
                ? Stopwatch.StartNew()
                : null;
            Stopwatch step = total != null ? Stopwatch.StartNew() : null;
            long listMs = 0;
            long selectMs = 0;

            try
            {
                int token = ++_requestToken;
                double effectiveWidth;
                string widthSource;
                int bucket = GetCurrentDecodeBucket(out effectiveWidth, out widthSource);
                _currentBucket = bucket;

                if (total != null && ActualWidth < MinimumInitialLayoutWidth)
                {
                    _fileLogger.Log(
                        $"BG PERF decode-width width={ActualWidth:0} effective={effectiveWidth:0} " +
                        $"source={widthSource} bucket={bucket}");
                }

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

                if (game.IsRunning)
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
                        ? settings.GetSelectionMode(game.Id, ArtworkKind.Background, settings.SelectionMode)
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

                // The file can be removed manually after the candidate list was
                // built but before this control gets to render it. Do not flash a
                // placeholder or let the load fail: move directly to another
                // candidate that still exists. The next folder rebuild will also
                // remove the missing path from the pool permanently.
                if (!IsUsable(path))
                {
                    path = isNativeFallback ? null : FirstUsable(candidates, path);
                    if (!isNativeFallback)
                    {
                        _previousPick = path;
                    }
                }

                if (string.IsNullOrEmpty(path))
                {
                    TransitionToNothing(token);
                    return;
                }

                if (PosterFrame.IsVideo(path))
                {
                    PrepareVideo(game, path, token);
                    ImageDiagnostics.LogApplied(game.Name, path, _settings, _fileLogger, bucket, 0);
                    return;
                }

                if (PosterFrame.IsAnimated(path))
                {
                    PrepareGif(game, path, token);
                    ImageDiagnostics.LogApplied(game.Name, path, _settings, _fileLogger, bucket, 0);
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
                return _selector.Select(
                    game.Id, candidates, _previousPick, mode,
                    _settings != null
                        ? _settings().GetSelectionOrder(game.Id, ArtworkKind.Background, _settings().BackgroundSelectionOrder)
                        : SelectionOrder.Random);
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
                    game.Id, candidates, previous, mode,
                    _settings != null
                        ? _settings().GetSelectionOrder(game.Id, ArtworkKind.Background, _settings().BackgroundSelectionOrder)
                        : SelectionOrder.Random);

                if (!string.IsNullOrEmpty(SharedPickPath))
                {
                    LastEverySelectionPick[game.Id] = SharedPickPath;
                }

                return SharedPickPath;
            }
        }

        private static bool IsUsable(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static string FirstUsable(IReadOnlyList<string> candidates, string skip)
        {
            if (candidates == null)
            {
                return null;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                if (string.Equals(candidate, skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUsable(candidate))
                {
                    return candidate;
                }
            }

            return null;
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
            if (Transition.BackgroundStyle == TransitionStyle.Pixelate)
            {
                PixelateFrames.QueuePrewarm(bitmap);
            }

            if (watch != null)
            {
                _fileLogger.Log(
                    $"BG PERF slot-ready \"{game.Name}\" kind={(nativeFallback ? "native-still" : "still")} " +
                    $"slot={incoming.Name} {watch.ElapsedMilliseconds}ms bucket={bucket} path={path}");
            }

            BeginTransition(incoming);

            // Session and Fixed are deterministic for the lifetime of the
            // relevant choice, so once the current background is ready we can
            // safely warm the two adjacent games in the visible library order.
            // EverySelection is intentionally excluded: choosing its artwork
            // before focus would subtly change that mode's semantics.
            PreloadNeighbourBackgrounds(game, bucket, path);

            if (!nativeFallback)
            {
                ImageDiagnostics.LogApplied(game.Name, path, _settings, _fileLogger, bucket, 0);
            }
        }

        private async void PreloadNeighbourBackgrounds(Game currentGame, int bucket, string currentPath)
        {
            try
            {
                ImageRotaterSettings settings = _settings != null ? _settings() : null;
                if (currentGame == null || settings == null || _loader == null || _filteredGames == null ||
                    settings.GetSelectionMode(currentGame.Id, ArtworkKind.Background, settings.SelectionMode) == SelectionMode.EverySelection)
                {
                    return;
                }

                IEnumerable<Game> sourceGames = _filteredGames();
                if (sourceGames == null)
                {
                    return;
                }

                List<Game> games = sourceGames.ToList();
                int index = games.FindIndex(g => g != null && g.Id == currentGame.Id);
                if (index < 0)
                {
                    return;
                }

                int[] neighbourIndexes = { index - 1, index + 1 };
                foreach (int neighbourIndex in neighbourIndexes)
                {
                    if (neighbourIndex < 0 || neighbourIndex >= games.Count)
                    {
                        continue;
                    }

                    Game neighbour = games[neighbourIndex];
                    if (neighbour == null)
                    {
                        continue;
                    }

                    string path = null;
                    IReadOnlyList<string> candidates = _source != null
                        ? _source.GetImagePaths(neighbour)
                        : null;

                    if (candidates != null && candidates.Count > 0 && _selector != null)
                    {
                        path = _selector.Select(
                            neighbour.Id, candidates, null,
                            settings.GetSelectionMode(neighbour.Id, ArtworkKind.Background, settings.SelectionMode),
                            settings.GetSelectionOrder(neighbour.Id, ArtworkKind.Background, settings.BackgroundSelectionOrder));
                    }
                    else
                    {
                        path = ResolveNativeBackground(neighbour);
                    }

                    if (string.IsNullOrEmpty(path) ||
                        string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase) ||
                        PosterFrame.IsVideo(path) || PosterFrame.IsAnimated(path))
                    {
                        continue;
                    }

                    Stopwatch watch = _fileLogger != null && _fileLogger.IsEnabled
                        ? Stopwatch.StartNew()
                        : null;

                    BitmapSource warmed = await _loader.LoadAsync(path, bucket);
                    if (warmed != null && watch != null)
                    {
                        _fileLogger.Log(
                            $"BG PERF preload-neighbour from=\"{currentGame.Name}\" game=\"{neighbour.Name}\" " +
                            $"{watch.ElapsedMilliseconds}ms bucket={bucket} mode={settings.SelectionMode} path={path}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: neighbour background preload failed");
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
                if (Transition.BackgroundStyle == TransitionStyle.SlideFromRight)
                {
                    // Keep the already-active image at the same static overscan used
                    // by subsequent slides. Applying it here prevents the first real
                    // transition from visibly snapping the current image larger.
                    ApplyFixedSlideOverscan(incoming, 54.0, TransitionDirection.FromRight);
                }
                else
                {
                    ResetSlotMediaScale(incoming);
                }

                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;
                incoming.Container.Visibility = Visibility.Visible;
                _pendingSlot = null;
                return;
            }

            int transitionToken = ++_transitionToken;
            _pendingSlot = incoming;

            // SlotB is declared after SlotA in XAML, so without an explicit
            // Z-order it is ALWAYS drawn on top. That means transitions look
            // correct only every other swap: when SlotA is incoming, its slide
            // happens behind SlotB. Always put the incoming slot above the
            // outgoing slot so A->B and B->A render identically.
            Panel.SetZIndex(incoming.Container, 2);
            if (outgoing != null)
            {
                Panel.SetZIndex(outgoing.Container, 1);
            }

            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = outgoing == null ? 1.0 : 0.0;
            incoming.Container.Visibility = Visibility.Visible;

            TransitionStyle style = Transition.BackgroundStyle;

            ResetSlotFocusEffect(incoming);
            ResetSlotFocusEffect(outgoing);
            if (incoming?.Container != null)
            {
                incoming.Container.OpacityMask = null;
            }
            if (outgoing?.Container != null)
            {
                outgoing.Container.OpacityMask = null;
            }

            if (style != TransitionStyle.SlideFromRight)
            {
                ResetSlotMediaScale(incoming);
                ResetSlotMediaScale(outgoing);
            }

            if (outgoing == null || style == TransitionStyle.Cut)
            {
                if (outgoing != null)
                {
                    ClearSlot(outgoing);
                }

                // The first image has no transition to hide a scale change. Prepare
                // the slide overscan now, in the same UI pass that makes it visible,
                // so the first later slide starts from the exact same scale as all
                // following ones and never produces a one-off zoom.
                if (outgoing == null && style == TransitionStyle.SlideFromRight)
                {
                    ApplyFixedSlideOverscan(incoming, 54.0, TransitionDirection.FromRight);
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

            if (style == TransitionStyle.SlideFromRight)
            {
                StartSlideTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            if (style == TransitionStyle.SideReveal)
            {
                StartSideRevealTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            if (style == TransitionStyle.DiagonalReveal)
            {
                StartDiagonalRevealTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            if (style == TransitionStyle.DepthShift)
            {
                StartDepthShiftTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            if (style == TransitionStyle.Mosaic)
            {
                StartMosaicTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            if (style == TransitionStyle.Pixelate)
            {
                StartPixelateTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            if (style == TransitionStyle.Zoom)
            {
                StartZoomTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            if (style == TransitionStyle.Focus)
            {
                StartFocusTransition(outgoing, incoming, style, transitionToken);
                return;
            }

            // Fade the incoming slot over an opaque outgoing slot so the native background cannot show through.
            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(
                0.0, 1.0, new Duration(Transition.BackgroundDuration));

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

        private void StartSlideTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            TransitionDirection dir = TransitionDirection.FromRight;

            // Reset translations before rebuilding a slide so a previous
            // interrupted animation can never leak its X/Y offset into the next transition.
            ResetSlotTranslation(incoming);
            ResetSlotTranslation(outgoing);

            // Theme-hosted fullscreen backgrounds use THIS renderer, not
            // FadeImageTuner. Keep the slide compact and make the outgoing image
            // disappear substantially sooner so it doesn't dominate the hand-off.
            double distance = 54.0;
            var incomingT = new TranslateTransform();
            var outgoingT = new TranslateTransform();
            incoming.Container.RenderTransform = incomingT;
            outgoing.Container.RenderTransform = outgoingT;

            // Keep both slide layers slightly oversized for the whole slide.
            // The scale is static (never animated), so the translation cannot
            // expose an empty strip at the moving edge and there is no zoom/dezoom motion.
            ApplyFixedSlideOverscan(incoming, distance, dir);
            ApplyFixedSlideOverscan(outgoing, distance, dir);
            incoming.Container.OpacityMask = null;
            outgoing.Container.OpacityMask = null;

            double inX = 0.0, inY = 0.0, outX = 0.0, outY = 0.0;
            switch (dir)
            {
                case TransitionDirection.FromLeft:
                    inX = -distance; outX = distance * 0.55; break;
                case TransitionDirection.FromRight:
                    inX = distance; outX = -distance * 0.55; break;
                case TransitionDirection.FromTop:
                    inY = -distance; outY = distance * 0.55; break;
                case TransitionDirection.FromBottom:
                    inY = distance; outY = -distance * 0.55; break;
            }

            incomingT.X = inX;
            incomingT.Y = inY;
            incoming.Container.Opacity = 0.0;
            outgoing.Container.Opacity = 1.0;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

            // Complete the incoming layer early enough that the outgoing layer
            // still covers the edges while the new image is translated. This
            // prevents the theme/native background underneath from becoming
            // visible as a duplicate strip, without zooming or treating native
            // artwork differently.
            TimeSpan incomingFadeDuration = TimeSpan.FromMilliseconds(
                Math.Max(1.0, Transition.BackgroundDuration.TotalMilliseconds * 0.52));
            TimeSpan incomingMoveDuration = TimeSpan.FromMilliseconds(
                Math.Max(1.0, Transition.BackgroundDuration.TotalMilliseconds * 0.60));

            var incomingFade = new DoubleAnimation(0.0, 1.0, new Duration(incomingFadeDuration))
            {
                EasingFunction = easeOut
            };
            incomingFade.Completed += (s, e) =>
            {
                if (transitionToken != _transitionToken) return;
                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;
                incoming.Container.OpacityMask = null;
                ResetSlotTranslation(incoming);
                ResetSlotTranslation(outgoing);
                ClearSlot(outgoing);
                _activeSlot = incoming;
                _pendingSlot = null;
            };

            incoming.Container.BeginAnimation(OpacityProperty, incomingFade);

            // Keep the current/background image perfectly still during the
            // opening part of the hand-off. The incoming image gets roughly
            // 30% of the transition to establish itself first; only then does
            // the outgoing image begin to slide/fade away.
            //
            // Use keyframes rather than BeginTime so an interrupted transition
            // always has an explicit visual value during the hold phase.
            var outgoingFade = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.BackgroundDuration)
            };
            outgoingFade.KeyFrames.Add(new LinearDoubleKeyFrame(
                1.0,
                KeyTime.FromPercent(0.30)));
            outgoingFade.KeyFrames.Add(new EasingDoubleKeyFrame(
                0.0,
                KeyTime.FromPercent(0.72),
                easeIn));

            outgoing.Container.BeginAnimation(OpacityProperty, outgoingFade);

            if (inX != 0.0)
            {
                incomingT.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(inX, 0.0, new Duration(incomingMoveDuration))
                    {
                        EasingFunction = easeOut
                    });
            }

            if (inY != 0.0)
            {
                incomingT.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(inY, 0.0, new Duration(incomingMoveDuration))
                    {
                        EasingFunction = easeOut
                    });
            }

            // Diagnostic variant: keep the previous image physically fixed.
            // It may still fade out, but it never translates. This lets us
            // confirm whether the persistent offset bug is caused by the
            // outgoing slot movement itself.

            LogTransition(outgoing, incoming, style);
        }

        private void StartSideRevealTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            ResetSlotTranslation(incoming);
            ResetSlotTranslation(outgoing);
            ResetSlotMediaScale(incoming);
            ResetSlotMediaScale(outgoing);
            ResetSlotFocusEffect(incoming);
            ResetSlotFocusEffect(outgoing);

            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;

            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = 1.0;
            incoming.Container.Visibility = Visibility.Visible;

            var mask = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                StartPoint = new Point(1.0, 0.5),
                EndPoint = new Point(1.34, 0.5),
                SpreadMethod = GradientSpreadMethod.Pad
            };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.0));
            mask.GradientStops.Add(new GradientStop(Colors.White, 1.0));
            incoming.Container.OpacityMask = mask;

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var startMove = new PointAnimation
            {
                From = new Point(1.0, 0.5),
                To = new Point(-0.34, 0.5),
                Duration = new Duration(Transition.BackgroundDuration),
                EasingFunction = ease
            };
            var endMove = new PointAnimation
            {
                From = new Point(1.34, 0.5),
                To = new Point(0.0, 0.5),
                Duration = new Duration(Transition.BackgroundDuration),
                EasingFunction = ease
            };

            endMove.Completed += (s, e) =>
            {
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                mask.BeginAnimation(LinearGradientBrush.StartPointProperty, null);
                mask.BeginAnimation(LinearGradientBrush.EndPointProperty, null);
                if (ReferenceEquals(incoming.Container.OpacityMask, mask))
                {
                    incoming.Container.OpacityMask = null;
                }

                incoming.Container.Opacity = 1.0;
                ClearSlot(outgoing);
                _activeSlot = incoming;
                _pendingSlot = null;
            };

            mask.BeginAnimation(LinearGradientBrush.StartPointProperty, startMove);
            mask.BeginAnimation(LinearGradientBrush.EndPointProperty, endMove);
            LogTransition(outgoing, incoming, style);
        }

        private void StartDiagonalRevealTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            ResetSlotTranslation(incoming);
            ResetSlotTranslation(outgoing);
            ResetSlotMediaScale(incoming);
            ResetSlotMediaScale(outgoing);
            ResetSlotFocusEffect(incoming);
            ResetSlotFocusEffect(outgoing);

            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;

            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = 1.0;
            incoming.Container.Visibility = Visibility.Visible;

            var mask = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                StartPoint = new Point(0.94, -0.04),
                EndPoint = new Point(1.28, 0.30),
                SpreadMethod = GradientSpreadMethod.Pad
            };
            var leadingStop = new GradientStop(Colors.Transparent, 0.0);
            var trailingStop = new GradientStop(Colors.White, 1.0);
            mask.GradientStops.Add(leadingStop);
            mask.GradientStops.Add(trailingStop);
            incoming.Container.OpacityMask = mask;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var startMove = new PointAnimation
            {
                From = new Point(0.94, -0.04),
                To = new Point(-0.82, 1.42),
                Duration = new Duration(Transition.BackgroundDuration),
                EasingFunction = ease
            };
            var endMove = new PointAnimation
            {
                From = new Point(1.28, 0.30),
                To = new Point(-0.48, 1.76),
                Duration = new Duration(Transition.BackgroundDuration),
                EasingFunction = ease
            };

            endMove.Completed += (s, e) =>
            {
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (transitionToken != _transitionToken)
                    {
                        return;
                    }

                    mask.BeginAnimation(LinearGradientBrush.StartPointProperty, null);
                    mask.BeginAnimation(LinearGradientBrush.EndPointProperty, null);
                    if (ReferenceEquals(incoming.Container.OpacityMask, mask))
                    {
                        incoming.Container.OpacityMask = null;
                    }

                    incoming.Container.Opacity = 1.0;
                    ClearSlot(outgoing);
                    _activeSlot = incoming;
                    _pendingSlot = null;
                }));
            };

            mask.BeginAnimation(LinearGradientBrush.StartPointProperty, startMove);
            mask.BeginAnimation(LinearGradientBrush.EndPointProperty, endMove);
            LogTransition(outgoing, incoming, style);
        }

        private void StartDepthShiftTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            ResetSlotTranslation(incoming);
            ResetSlotTranslation(outgoing);
            ResetSlotMediaScale(incoming);
            ResetSlotMediaScale(outgoing);
            ResetSlotFocusEffect(incoming);
            ResetSlotFocusEffect(outgoing);

            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;
            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = 0.0;
            incoming.Container.Visibility = Visibility.Visible;

            PrepareSlotZoomScale(incoming, 1.05);
            PrepareSlotZoomScale(outgoing, 1.0);

            var incomingBlur = new System.Windows.Media.Effects.BlurEffect
            {
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                Radius = 12.0,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            var outgoingBlur = new System.Windows.Media.Effects.BlurEffect
            {
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                Radius = 0.0,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            incoming.Container.Effect = incomingBlur;
            outgoing.Container.Effect = outgoingBlur;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

            var incomingFade = new DoubleAnimation(
                0.0, 1.0, new Duration(Transition.BackgroundDuration))
            {
                EasingFunction = easeOut
            };
            incomingFade.Completed += (s, e) =>
            {
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;
                incomingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                outgoingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                if (ReferenceEquals(incoming.Container.Effect, incomingBlur))
                {
                    incoming.Container.Effect = null;
                }
                if (ReferenceEquals(outgoing.Container.Effect, outgoingBlur))
                {
                    outgoing.Container.Effect = null;
                }
                ResetSlotMediaScale(incoming);
                ResetSlotMediaScale(outgoing);
                ClearSlot(outgoing);
                _activeSlot = incoming;
                _pendingSlot = null;
            };

            var outgoingFade = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.BackgroundDuration)
            };
            outgoingFade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0.20)));
            outgoingFade.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(0.84), easeIn));

            var sharpen = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.BackgroundDuration)
            };
            sharpen.KeyFrames.Add(new LinearDoubleKeyFrame(12.0, KeyTime.FromPercent(0.18)));
            sharpen.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), easeOut));

            var defocus = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.BackgroundDuration)
            };
            defocus.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
            defocus.KeyFrames.Add(new EasingDoubleKeyFrame(9.0, KeyTime.FromPercent(0.52), easeIn));
            defocus.KeyFrames.Add(new LinearDoubleKeyFrame(9.0, KeyTime.FromPercent(1.0)));

            incoming.Container.BeginAnimation(OpacityProperty, incomingFade);
            outgoing.Container.BeginAnimation(OpacityProperty, outgoingFade);
            incomingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, sharpen);
            outgoingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, defocus);
            AnimateSlotZoomToNormal(incoming, Transition.BackgroundDuration, easeOut);
            AnimateSlotZoomToScale(outgoing, 0.965, Transition.BackgroundDuration, new CubicEase { EasingMode = EasingMode.EaseInOut });
            LogTransition(outgoing, incoming, style);
        }

        private void StartMosaicTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            ResetSlotTranslation(incoming);
            ResetSlotTranslation(outgoing);
            ResetSlotMediaScale(incoming);
            ResetSlotMediaScale(outgoing);
            ResetSlotFocusEffect(incoming);
            ResetSlotFocusEffect(outgoing);

            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;

            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = 1.0;
            incoming.Container.Visibility = Visibility.Visible;

            MosaicMaskState mask = MosaicMask.Create();
            MosaicMask.SetVisible(mask, false);
            incoming.Container.OpacityMask = mask.Brush;

            Storyboard reveal = MosaicMask.BuildStoryboard(
                incoming.Container,
                mask,
                true,
                Transition.BackgroundDuration);

            reveal.Completed += (s, e) =>
            {
                MosaicMask.Stop(mask);
                if (ReferenceEquals(incoming.Container.OpacityMask, mask.Brush))
                {
                    incoming.Container.OpacityMask = null;
                }

                try
                {
                    reveal.Remove(incoming.Container);
                }
                catch
                {
                }

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

            reveal.Begin(incoming.Container, HandoffBehavior.SnapshotAndReplace, true);
            LogTransition(outgoing, incoming, style);
        }


        private async void StartPixelateTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            ResetSlotTranslation(incoming);
            ResetSlotTranslation(outgoing);
            ResetSlotMediaScale(incoming);
            ResetSlotMediaScale(outgoing);
            ResetSlotFocusEffect(incoming);
            ResetSlotFocusEffect(outgoing);

            ImageSource oldSource = outgoing?.Image?.Source;
            ImageSource newSource = incoming?.Image?.Source;
            if (oldSource == null || newSource == null ||
                outgoing?.Video?.Visibility == Visibility.Visible ||
                incoming?.Video?.Visibility == Visibility.Visible)
            {
                StartPixelateFallbackFade(outgoing, incoming, style, transitionToken);
                return;
            }

            BitmapScalingMode oldScaling = RenderOptions.GetBitmapScalingMode(outgoing.Image);
            BitmapScalingMode newScaling = RenderOptions.GetBitmapScalingMode(incoming.Image);

            IReadOnlyList<ImageSource> oldLevels = PixelateFrames.GetCachedOrSource(oldSource);
            Task<IReadOnlyList<ImageSource>> oldWarm = PixelateFrames.GetLevelsAsync(oldSource);
            Task<IReadOnlyList<ImageSource>> newWarm = PixelateFrames.GetLevelsAsync(newSource);

            TimeSpan total = Transition.BackgroundDuration;
            TimeSpan oldPhase = TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.48);
            TimeSpan newPhase = TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.52);
            TimeSpan blend = TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.06);
            double incomingHold = blend.TotalMilliseconds / Math.Max(1.0, newPhase.TotalMilliseconds);

            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;
            outgoing.Image.BeginAnimation(Image.SourceProperty, null);
            outgoing.Image.Source = oldSource;
            RenderOptions.SetBitmapScalingMode(outgoing.Image, BitmapScalingMode.NearestNeighbor);

            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = 0.0;
            incoming.Container.Visibility = Visibility.Visible;
            incoming.Image.BeginAnimation(Image.SourceProperty, null);
            incoming.Image.Source = newSource;
            RenderOptions.SetBitmapScalingMode(incoming.Image, BitmapScalingMode.NearestNeighbor);

            if (oldLevels.Count > 1)
            {
                outgoing.Image.BeginAnimation(
                    Image.SourceProperty,
                    PixelateFrames.BuildOutgoingPhase(oldLevels, oldPhase),
                    HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                // The current image is normally already prewarmed. On the very
                // first Pixelate use, let the transition start immediately and
                // attach the prepared levels as soon as the worker finishes.
                _ = UpgradeOutgoingPixelateAsync();
            }

            async Task UpgradeOutgoingPixelateAsync()
            {
                IReadOnlyList<ImageSource> warmed = await oldWarm;
                if (transitionToken != _transitionToken || warmed == null || warmed.Count <= 1)
                {
                    return;
                }

                outgoing.Image.BeginAnimation(
                    Image.SourceProperty,
                    PixelateFrames.BuildOutgoingPhase(warmed, oldPhase),
                    HandoffBehavior.SnapshotAndReplace);
            }

            try
            {
                await Task.Delay(oldPhase);
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                IReadOnlyList<ImageSource> newLevels = await newWarm;
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                if (newLevels == null || newLevels.Count == 0)
                {
                    newLevels = new[] { newSource };
                }

                incoming.Image.BeginAnimation(Image.SourceProperty, null);
                incoming.Image.Source = newLevels[newLevels.Count - 1];
                incoming.Image.BeginAnimation(
                    Image.SourceProperty,
                    PixelateFrames.BuildIncomingPhase(newLevels, newPhase, incomingHold),
                    HandoffBehavior.SnapshotAndReplace);

                var fadeIn = new DoubleAnimation(0.0, 1.0, new Duration(blend))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                var fadeOut = new DoubleAnimation(1.0, 0.0, new Duration(blend))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                incoming.Container.BeginAnimation(OpacityProperty, fadeIn);
                outgoing.Container.BeginAnimation(OpacityProperty, fadeOut);

                await Task.Delay(newPhase);
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                outgoing.Image.BeginAnimation(Image.SourceProperty, null);
                incoming.Image.BeginAnimation(Image.SourceProperty, null);
                incoming.Image.Source = newSource;
                RenderOptions.SetBitmapScalingMode(outgoing.Image, oldScaling);
                RenderOptions.SetBitmapScalingMode(incoming.Image, newScaling);
                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;
                ClearSlot(outgoing);
                _activeSlot = incoming;
                _pendingSlot = null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: Pixelate background transition failed");
                if (transitionToken == _transitionToken)
                {
                    outgoing.Image.BeginAnimation(Image.SourceProperty, null);
                    incoming.Image.BeginAnimation(Image.SourceProperty, null);
                    incoming.Image.Source = newSource;
                    RenderOptions.SetBitmapScalingMode(outgoing.Image, oldScaling);
                    RenderOptions.SetBitmapScalingMode(incoming.Image, newScaling);
                    incoming.Container.Opacity = 1.0;
                    ClearSlot(outgoing);
                    _activeSlot = incoming;
                    _pendingSlot = null;
                }
            }

            LogTransition(outgoing, incoming, style);
        }

        private void StartPixelateFallbackFade(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0.0, 1.0, new Duration(Transition.BackgroundDuration));
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

        private void StartZoomTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            ResetSlotTranslation(incoming);
            ResetSlotTranslation(outgoing);
            ResetSlotMediaScale(incoming);
            ResetSlotMediaScale(outgoing);

            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;
            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = 0.0;
            incoming.Container.Visibility = Visibility.Visible;

            PrepareSlotZoomScale(incoming, 1.08);

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

            var incomingFade = new DoubleAnimation(
                0.0, 1.0, new Duration(Transition.BackgroundDuration))
            {
                EasingFunction = easeOut
            };
            incomingFade.Completed += (s, e) =>
            {
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;
                ResetSlotMediaScale(incoming);
                ClearSlot(outgoing);
                _activeSlot = incoming;
                _pendingSlot = null;
            };

            var outgoingFade = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.BackgroundDuration)
            };
            outgoingFade.KeyFrames.Add(new LinearDoubleKeyFrame(
                1.0, KeyTime.FromPercent(0.34)));
            outgoingFade.KeyFrames.Add(new EasingDoubleKeyFrame(
                0.0, KeyTime.FromPercent(0.92), easeIn));

            incoming.Container.BeginAnimation(OpacityProperty, incomingFade);
            outgoing.Container.BeginAnimation(OpacityProperty, outgoingFade);
            AnimateSlotZoomToNormal(incoming, Transition.BackgroundDuration, easeOut);
            LogTransition(outgoing, incoming, style);
        }

        private void StartFocusTransition(
            RenderSlot outgoing,
            RenderSlot incoming,
            TransitionStyle style,
            int transitionToken)
        {
            ResetSlotTranslation(incoming);
            ResetSlotTranslation(outgoing);
            ResetSlotMediaScale(incoming);
            ResetSlotMediaScale(outgoing);
            ResetSlotFocusEffect(incoming);
            ResetSlotFocusEffect(outgoing);

            outgoing.Container.BeginAnimation(OpacityProperty, null);
            outgoing.Container.Opacity = 1.0;
            outgoing.Container.Visibility = Visibility.Visible;
            incoming.Container.BeginAnimation(OpacityProperty, null);
            incoming.Container.Opacity = 0.0;
            incoming.Container.Visibility = Visibility.Visible;

            var incomingBlur = new System.Windows.Media.Effects.BlurEffect
            {
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                Radius = 26.0,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            var outgoingBlur = new System.Windows.Media.Effects.BlurEffect
            {
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                Radius = 0.0,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            incoming.Container.Effect = incomingBlur;
            outgoing.Container.Effect = outgoingBlur;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

            var incomingFade = new DoubleAnimation(
                0.0, 1.0, new Duration(Transition.BackgroundDuration))
            {
                EasingFunction = easeOut
            };
            incomingFade.Completed += (s, e) =>
            {
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                incoming.Container.BeginAnimation(OpacityProperty, null);
                incoming.Container.Opacity = 1.0;
                incomingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                if (ReferenceEquals(incoming.Container.Effect, incomingBlur))
                {
                    incoming.Container.Effect = null;
                }
                outgoingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                ClearSlot(outgoing);
                _activeSlot = incoming;
                _pendingSlot = null;
            };

            var outgoingFade = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.BackgroundDuration)
            };
            outgoingFade.KeyFrames.Add(new LinearDoubleKeyFrame(
                1.0, KeyTime.FromPercent(0.22)));
            outgoingFade.KeyFrames.Add(new EasingDoubleKeyFrame(
                0.0, KeyTime.FromPercent(0.86), easeIn));

            var sharpen = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.BackgroundDuration)
            };
            sharpen.KeyFrames.Add(new LinearDoubleKeyFrame(
                26.0, KeyTime.FromPercent(0.24)));
            sharpen.KeyFrames.Add(new EasingDoubleKeyFrame(
                0.0, KeyTime.FromPercent(1.0), easeOut));

            var defocus = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.BackgroundDuration)
            };
            defocus.KeyFrames.Add(new LinearDoubleKeyFrame(
                0.0, KeyTime.FromPercent(0.08)));
            defocus.KeyFrames.Add(new EasingDoubleKeyFrame(
                20.0, KeyTime.FromPercent(0.48), easeIn));
            defocus.KeyFrames.Add(new LinearDoubleKeyFrame(
                20.0, KeyTime.FromPercent(1.0)));

            incoming.Container.BeginAnimation(OpacityProperty, incomingFade);
            outgoing.Container.BeginAnimation(OpacityProperty, outgoingFade);
            incomingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, sharpen);
            outgoingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, defocus);
            LogTransition(outgoing, incoming, style);
        }

        private static void ResetSlotFocusEffect(RenderSlot slot)
        {
            if (slot?.Container == null)
            {
                return;
            }

            if (slot.Container.Effect is System.Windows.Media.Effects.BlurEffect blur)
            {
                blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                slot.Container.Effect = null;
            }
        }

        private static void PrepareSlotZoomScale(RenderSlot slot, double scale)
        {
            if (slot == null)
            {
                return;
            }

            PrepareElementZoomScale(slot.Image, scale);
            PrepareElementZoomScale(slot.Video, scale);
        }

        private static void PrepareElementZoomScale(FrameworkElement element, double scale)
        {
            if (element == null)
            {
                return;
            }

            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new ScaleTransform(scale, scale);
        }

        private static void AnimateSlotZoomToNormal(
            RenderSlot slot,
            TimeSpan duration,
            IEasingFunction easing)
        {
            if (slot == null)
            {
                return;
            }

            AnimateElementZoomToNormal(slot.Image, duration, easing);
            AnimateElementZoomToNormal(slot.Video, duration, easing);
        }

        private static void AnimateElementZoomToNormal(
            FrameworkElement element,
            TimeSpan duration,
            IEasingFunction easing)
        {
            if (!(element?.RenderTransform is ScaleTransform scale))
            {
                return;
            }

            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.0, new Duration(duration))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.0, new Duration(duration))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
        }

        private static void AnimateSlotZoomToScale(
            RenderSlot slot,
            double targetScale,
            TimeSpan duration,
            IEasingFunction easing)
        {
            if (slot == null)
            {
                return;
            }

            AnimateElementZoomToScale(slot.Image, targetScale, duration, easing);
            AnimateElementZoomToScale(slot.Video, targetScale, duration, easing);
        }

        private static void AnimateElementZoomToScale(
            FrameworkElement element,
            double targetScale,
            TimeSpan duration,
            IEasingFunction easing)
        {
            if (!(element?.RenderTransform is ScaleTransform scale))
            {
                return;
            }

            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(targetScale, new Duration(duration))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(targetScale, new Duration(duration))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
        }

        private static void ApplyFixedSlideOverscan(
            RenderSlot slot,
            double slideDistance,
            TransitionDirection direction)
        {
            if (slot == null)
            {
                return;
            }

            bool horizontal = direction == TransitionDirection.FromLeft ||
                              direction == TransitionDirection.FromRight;

            double extent = horizontal
                ? slot.Container.ActualWidth
                : slot.Container.ActualHeight;

            if (extent <= 0.0)
            {
                extent = horizontal ? 1920.0 : 1080.0;
            }

            double scale = (extent + (2.0 * Math.Abs(slideDistance))) / extent;
            SetSlotMediaScale(slot, scale);
        }

        private static void SetSlotMediaScale(RenderSlot slot, double scale)
        {
            if (slot == null)
            {
                return;
            }

            ApplyScale(slot.Image, scale);
            ApplyScale(slot.Video, scale);
        }

        private static void ApplyScale(FrameworkElement element, double scale)
        {
            if (element == null)
            {
                return;
            }

            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = Math.Abs(scale - 1.0) < 0.0001
                ? Transform.Identity
                : new ScaleTransform(scale, scale);
        }

        private static void ResetSlotMediaScale(RenderSlot slot)
        {
            SetSlotMediaScale(slot, 1.0);
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
                0.0, 1.0, new Duration(Transition.BackgroundHalf));

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
                    1.0, 0.0, new Duration(Transition.BackgroundHalf));

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

                var up = new DoubleAnimation(0.0, 1.0, new Duration(Transition.BackgroundHalf));
                up.Completed += (s, e) =>
                {
                    if (transitionToken != _transitionToken)
                    {
                        return;
                    }

                    ClearSlot(outgoing);
                    _activeSlot = null;

                    var down = new DoubleAnimation(1.0, 0.0, new Duration(Transition.BackgroundHalf));
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
                new Duration(Transition.BackgroundDuration));

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

        private static void ResetSlotTranslation(RenderSlot slot)
        {
            if (slot?.Container == null)
            {
                return;
            }

            if (slot.Container.RenderTransform is TranslateTransform translate)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.X = 0.0;
                translate.Y = 0.0;
            }
            else if (slot.Container.RenderTransform is TransformGroup group)
            {
                foreach (Transform transform in group.Children)
                {
                    if (transform is TranslateTransform tt)
                    {
                        tt.BeginAnimation(TranslateTransform.XProperty, null);
                        tt.BeginAnimation(TranslateTransform.YProperty, null);
                        tt.X = 0.0;
                        tt.Y = 0.0;
                    }
                }
            }

            slot.Container.RenderTransform = Transform.Identity;
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
            Guid currentGameId = GameContext?.Id ?? Guid.Empty;

            // If a slide is interrupted, keep the already-active/outgoing slot
            // rather than the partially-arrived pending slot. In the V70 slide
            // the outgoing image is physically fixed, so this gives us a clean
            // cancellation point with no positional snap. The half-transitioned
            // incoming slot is discarded and rebuilt only after the 400 ms settle.
            if (_pendingSlot != null &&
                _activeSlot != null &&
                _activeSlot != _pendingSlot &&
                _activeSlot.Kind != SlotKind.None)
            {
                keep = _activeSlot;

                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _fileLogger.Log(
                        $"BG PERF transition-cancel keep={_activeSlot.Name} " +
                        $"drop={_pendingSlot.Name} game=\"{_activeSlot.GameName}\"");
                }
            }

            // During rapid 1-2-1-2 navigation a transition can be interrupted
            // while the outgoing slot is still more opaque than the incoming
            // slot. The old logic kept whichever slot happened to be brighter,
            // which could preserve the PREVIOUS game's background and make it
            // look permanently wrong. Prefer a valid slot that already belongs
            // to the current game; only fall back to opacity when neither slot
            // belongs to the current selection.
            RenderSlot currentA = _slotA.GameId == currentGameId && _slotA.Kind != SlotKind.None
                ? _slotA : null;
            RenderSlot currentB = _slotB.GameId == currentGameId && _slotB.Kind != SlotKind.None
                ? _slotB : null;

            if (keep == null && (currentA != null || currentB != null))
            {
                if (currentA != null && currentB != null)
                {
                    if (_pendingSlot == currentA || _pendingSlot == currentB)
                    {
                        keep = _pendingSlot;
                    }
                    else
                    {
                        keep = a >= b ? currentA : currentB;
                    }
                }
                else
                {
                    keep = currentA ?? currentB;
                }
            }
            else if (keep == null && (a > 0.001 || b > 0.001))
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
                ResetSlotTranslation(keep);
                if (Transition.BackgroundStyle == TransitionStyle.Zoom)
                {
                    ResetSlotMediaScale(keep);
                }
                ResetSlotFocusEffect(keep);
                keep.Container.OpacityMask = null;
                keep.Container.Visibility = Visibility.Visible;
                keep.Container.Opacity = 1.0;
                Panel.SetZIndex(keep.Container, 2);
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
            _slotA?.Image.BeginAnimation(Image.SourceProperty, null);
            _slotB?.Image.BeginAnimation(Image.SourceProperty, null);
            if (_slotA?.Image != null) RenderOptions.SetBitmapScalingMode(_slotA.Image, BitmapScalingMode.HighQuality);
            if (_slotB?.Image != null) RenderOptions.SetBitmapScalingMode(_slotB.Image, BitmapScalingMode.HighQuality);
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
            ResetSlotTranslation(slot);
            slot.Container.OpacityMask = null;
            slot.Container.Opacity = 0.0;
            slot.Container.Visibility = Visibility.Collapsed;
            slot.Container.RenderTransform = Transform.Identity;
            ResetSlotMediaScale(slot);
            ResetSlotFocusEffect(slot);

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
                $"BG PERF transition style={style} duration={Transition.BackgroundDuration.TotalMilliseconds:0}ms " +
                $"from={(outgoing == null ? "native" : outgoing.Kind + ":" + outgoing.Name)} " +
                $"to={(incoming == null ? "native" : incoming.Kind + ":" + incoming.Name)} " +
                $"game=\"{incoming?.GameName ?? GameContext?.Name}\"");
        }
    }
}
