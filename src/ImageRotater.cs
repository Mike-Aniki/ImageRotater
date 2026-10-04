using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ImageRotater.Controls;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater
{
    public class ImageRotater : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private Window _navigationWindow;

        // 150 MB of decoded bitmaps. Playnite is a 32-bit process sharing
        // ~2 GB of address space with Chromium, and decoded frames are large
        // contiguous allocations, so this budget is a fragmentation control as
        // much as a memory cap.
        private const long CacheBudgetBytes = 150L * 1024 * 1024;

        private readonly ImageRotaterSettingsViewModel _settingsViewModel;
        private readonly ImageCache _cache;
        private readonly ImageLoader _loader;
        private readonly ImageSelector _selector;
        private readonly SessionSelectionCache _sessionCache;
        private readonly GameImageStore _store;
        private readonly ImageMenuHandler _menuHandler;
        private readonly ISteamGridDbClient _steamGridDb;
        private readonly ArtworkDownloader _downloader;
        private readonly IBackgroundImageSource _imageSource;
        private readonly IBackgroundImageSource _coverSource;
        private readonly PlayniteBackgroundWriter _writer;
        private readonly OriginalArtPreserver _preserver;
        private readonly BackgroundRotationService _rotationService;
        private readonly FileLogger _fileLogger;
        private readonly ArtworkPublisher _publisher;
        private readonly CoverTileTransition _coverTransition;

        // Native still-cover transitions need to know whether this is the first
        // visit to a game in the current Playnite session. EverySelection
        // animates on every visit; the other modes only need an arrival
        // transition when their session/day/slideshow pick can first be applied.
        private readonly HashSet<Guid> _coverTransitionVisited = new HashSet<Guid>();
        private Guid? _lastCoverTransitionGameId;

        // Slideshow state: the game currently selected, and when each kind is
        // next due. One coarse timer serves both kinds - sub-second precision
        // is meaningless for something that ticks in tens of seconds.
        private System.Windows.Threading.DispatcherTimer _slideshowTimer;
        private Game _slideshowGame;

        // Bumped on every selection. A queued background write compares the
        // value it captured against this one to tell whether the user has moved
        // on again since - see PlayniteBackgroundWriter.SelectionGeneration.
        private int _selectionGeneration;

        // Session cover choices are primed once at startup. Visible library
        // items are handled first; the remaining games are filled in later in
        // small dispatcher batches so Playnite startup is not blocked on all
        // configured covers.
        private readonly HashSet<Guid> _startupStableCoverPrimeDone = new HashSet<Guid>();
        private bool _startupStableCoverPrimeStopping;
        private const int DeferredStableCoverPrimeBatchSize = 3;

        private DateTime _backgroundDue = DateTime.MaxValue;
        private DateTime _lastFadeRetime = DateTime.MinValue;
        private DateTime _coverDue = DateTime.MaxValue;

        // Fullscreen rebuilds its visual tree when switching between List,
        // Details and other views. The FadeImage instance tuned a moment ago
        // can therefore disappear and be replaced by a fresh stock instance.
        // A small generation + one-shot retry makes re-binding deterministic
        // without polling or scanning continuously.
        private int _fadeRebindGeneration;
        private System.Windows.Threading.DispatcherTimer _fadeRebindTimer;

        // Slideshow intervals are user-selectable from 1 to 60 seconds.
        private const int MinimumSlideshowSeconds = 1;

        // Delay background pre-staging briefly so fast scrolling skips intermediate games.
        private const int BackgroundSettleMilliseconds = 250;

        // Background pre-staging state for the settled selection.
        private Game _settling;
        private Guid _settledGameId;
        private System.Windows.Threading.DispatcherTimer _settleTimer;

        private System.Windows.Threading.DispatcherTimer SettleTimer
        {
            get
            {
                if (_settleTimer == null)
                {
                    _settleTimer = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Normal)
                    {
                        Interval = TimeSpan.FromMilliseconds(BackgroundSettleMilliseconds)
                    };

                    _settleTimer.Tick += OnSelectionSettled;
                }

                return _settleTimer;
            }
        }

        // Marks the selection as settled for background pre-staging.
        private void OnSelectionSettled(object sender, EventArgs e)
        {
            SettleTimer.Stop();

            Game game = _settling;
            if (game == null)
            {
                return;
            }

            _settledGameId = game.Id;
        }


        private int _lastLoggedLogicalScreenWidth = -1;
        private int _lastLoggedPhysicalScreenWidth = -1;
        private int _lastLoggedDpiPercent = -1;

        [DllImport("user32.dll", EntryPoint = "GetDpiForSystem")]
        private static extern uint GetDpiForSystem();

        private int GetPhysicalPrimaryScreenWidth()
        {
            try
            {
                double logicalWidth = SystemParameters.PrimaryScreenWidth;
                double scale = 1.0;
                string scaleSource = "fallback-1x";

                // Best source once Playnite's WPF window has a presentation source.
                // TransformToDevice maps DIPs to real device pixels and therefore
                // matches the unit used by the artwork files themselves.
                try
                {
                    Window mainWindow = Application.Current == null ? null : Application.Current.MainWindow;
                    PresentationSource source = mainWindow == null ? null : PresentationSource.FromVisual(mainWindow);
                    if (source != null && source.CompositionTarget != null)
                    {
                        double candidate = source.CompositionTarget.TransformToDevice.M11;
                        if (candidate > 0.1)
                        {
                            scale = candidate;
                            scaleSource = "wpf-transform";
                        }
                    }
                }
                catch
                {
                }

                // Very early in startup there may not be a PresentationSource yet.
                // On supported Windows versions this gives us a reliable system DPI
                // instead of silently falling back to the logical/DIP width.
                if (scale <= 1.001)
                {
                    try
                    {
                        uint dpi = GetDpiForSystem();
                        if (dpi >= 96)
                        {
                            scale = dpi / 96.0;
                            scaleSource = "system-dpi";
                        }
                    }
                    catch
                    {
                    }
                }

                int logicalRounded = (int)Math.Round(logicalWidth);
                int physicalWidth = (int)Math.Round(logicalWidth * scale);
                int dpiPercent = (int)Math.Round(scale * 100.0);

                if (_fileLogger != null && _fileLogger.IsEnabled &&
                    (logicalRounded != _lastLoggedLogicalScreenWidth ||
                     physicalWidth != _lastLoggedPhysicalScreenWidth ||
                     dpiPercent != _lastLoggedDpiPercent))
                {
                    _lastLoggedLogicalScreenWidth = logicalRounded;
                    _lastLoggedPhysicalScreenWidth = physicalWidth;
                    _lastLoggedDpiPercent = dpiPercent;
                    _fileLogger.Log(
                        $"BG PERF normalise-screen logicalDip={logicalRounded} physicalPx={physicalWidth} " +
                        $"dpiScale={scale:0.###} ({dpiPercent}%) source={scaleSource}");
                }

                return physicalWidth > 0 ? physicalWidth : logicalRounded;
            }
            catch (Exception ex)
            {
                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _fileLogger.Log($"BG PERF normalise-screen failed error={ex.GetType().Name}: {ex.Message}");
                }
                return 0;
            }
        }

        public override Guid Id { get; } = Guid.Parse("72b7d457-0621-429b-8368-665bc53ff896");

        // Public, not private, deliberately: this is the SettingsRoot that
        // theme {PluginSettings Plugin=ImageRotater, Path=...} bindings resolve
        // through (see AddSettingsSupport in the constructor), and WPF bindings
        // cannot read private members.
        public ImageRotaterSettings Settings => _settingsViewModel?.Settings;

        public ImageRotater(IPlayniteAPI api) : base(api)
        {
            _settingsViewModel = new ImageRotaterSettingsViewModel(this);

            _cache = new ImageCache(CacheBudgetBytes);
            _loader = new ImageLoader(_cache);
            _sessionCache = new SessionSelectionCache(System.IO.Path.Combine(GetPluginUserDataPath(), "session-last-picks.json"));
            _selector = new ImageSelector(new ImagePicker(), _sessionCache);

            _store = new GameImageStore(GetPluginUserDataPath());

            // Told once at startup and again whenever settings are saved. The
            // converter holds a static path rather than the settings object,
            // which keeps it usable from the bulk conversion and the tests
            // without dragging the whole plugin along.
            GifConverter.ConfiguredPath = Settings?.FfmpegPath;
            Transition.CoverStyle = Settings?.CoverTransition ?? TransitionStyle.Crossfade;
            Transition.BackgroundStyle = Settings?.BackgroundTransition ?? TransitionStyle.Crossfade;

            // A getter, not a snapshot: a settings save replaces the whole
            // object, so a captured one would leave YouTube downloads using
            // tool paths the user has since corrected.
            ArtworkDownloader.SettingsSource = () => Settings;
            // The logger owns the enabled check, so no call site repeats it.
            _fileLogger = new FileLogger(
                GetPluginUserDataPath(), () => Settings?.EnableDebugLogging == true);
            Transition.DebugLog = message => _fileLogger?.Log(message);

            // The key is read through an accessor: a settings save replaces the
            // whole settings object, so a captured string would go stale and the
            // user's newly entered key would appear not to work.
            _steamGridDb = new SteamGridDbClient(() => Settings?.SteamGridDbApiKey);
            _downloader = new ArtworkDownloader(_steamGridDb, _store, _sessionCache);

            // ImageRotater files live in the plugin folder. The user's Playnite
            // artwork is added as one logical Original candidate by FolderImageSource.
            //
            // Theme Integration can safely use the live Playnite file directly
            // because it never replaces Game.CoverImage / BackgroundImage.
            // Compatibility mode does replace those fields, so OriginalArtPreserver
            // keeps one safety copy and the source uses that stable file instead.
            // This avoids duplicate native files in Theme Integration while keeping
            // Compatibility safe from Playnite reclaiming an unreferenced original.
            _writer = new PlayniteBackgroundWriter(api, GetPluginUserDataPath(), _fileLogger);

            // A background write is queued to the UI thread, so it commits
            // after the selection that triggered it - and during fast scrolling
            // after the NEXT one too, which is what showed as another game's
            // background appearing under the selected one.
            //
            // The writer captures this when the write starts and asks again
            // when it commits. A background rotation is FOR the game being
            // left, so the departing game is never the current selection and an
            // id comparison would suppress every background write there is - a
            // counter is the only thing that distinguishes "one selection
            // behind", which is normal, from "two or more", which is the bug.
            _writer.SelectionGeneration = () => _selectionGeneration;

            // Backgrounds are levelled to one width per game, because Playnite
            // blurs at a fixed radius after scaling - see NormaliseIfBackground.
            _writer.NormaliseBackgrounds = () => Settings?.NormaliseBackgroundSize == true;

            // IMPORTANT: Background normalisation works in physical image pixels,
            // not WPF device-independent pixels (DIPs). SystemParameters.PrimaryScreenWidth
            // returns DIPs, so on a 1920px display at 125% scaling it reports 1536.
            // Using that value for cache names/normalisation creates unnecessary .w1536
            // variants and invalidates the target-width cache. Convert the WPF width back
            // to physical pixels before handing it to the background writer.
            _writer.ScreenWidth = GetPhysicalPrimaryScreenWidth;
            // The writer is handed over so the preserver can recognise artwork
            // this plugin wrote and leave it alone.
            _preserver = new OriginalArtPreserver(api, _store, _writer);

            // Candidate sources expose one coherent pool to every selection mode:
            // plugin files + live virtual Original in Theme Integration, or
            // plugin files + preserved Original in Compatibility.
            _imageSource = new FolderImageSource(
                _store, ArtworkKind.Background, _preserver, () => Settings);
            _coverSource = new FolderImageSource(
                _store, ArtworkKind.Cover, _preserver, () => Settings);

            _publisher = new ArtworkPublisher(_store, _fileLogger);
            _coverTransition = new CoverTileTransition(_fileLogger);

            _rotationService = new BackgroundRotationService(
                _imageSource, _coverSource, _selector, _writer, _preserver, _store, () => Settings,
                _fileLogger, _publisher);

            // Adding or removing images changes what a game can rotate to, so
            // the rotation service must drop its once-per-game guard for it -
            // otherwise new artwork would not appear on the game currently
            // selected until the user navigated away and back.
            _menuHandler = new ImageMenuHandler(
                api, _store, _sessionCache, _steamGridDb, _downloader, _preserver,
                () => Settings,
                gameId => _rotationService.Forget(gameId),
                () =>
                {
                    SavePluginSettings(Settings);
                    NotifySettingsSaved();
                });

            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            // Without this, {PluginSettings Plugin=ImageRotater, Path=...} in
            // theme XAML silently binds to nothing: Playnite resolves that
            // markup through a SettingsSupportList that only contains plugins
            // which registered here (verified in Playnite's own
            // Markup/PluginSettings.cs - unregistered plugins fall into the
            // "wait for ExtensionsLoaded" branch and never bind). Every
            // theme-side gate and cover binding depends on this call.
            AddSettingsSupport(new AddSettingsSupportArgs
            {
                SourceName = "ImageRotater",
                SettingsRoot = nameof(Settings)
            });

            // Publish where per-game artwork lives so theme XAML can join it
            // with a tile's own game id. This is what lets a grid work: every
            // other published value describes only the selected game.
            if (Settings != null)
            {
                Settings.ImagesRoot = _store.ImagesRoot;
            }

            // Themes place these as <ContentControl x:Name="ImageRotater_Background" />
            // and <ContentControl x:Name="ImageRotater_Cover" />.
            //
            // Cover is the only way a Fullscreen grid tile can rotate: those
            // tiles bind Playnite's native PART_ImageCover, which caches its
            // decoded bitmap and never re-reads Game.CoverImage.
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "ImageRotater",
                ElementList = new List<string> { "Background", "Cover" }
            });

        }

        // Write mode has no control to react to selection, so the plugin drives
        // it from the selection event instead.
        // B closes the search dialog.
        //
        // Playnite turns the rest of the pad into real key messages posted to
        // the active window - D-pad becomes the arrow keys, A becomes Enter -
        // so navigating and picking artwork needs no code from us at all. B is
        // the exception: Playnite maps it to nothing, and a source comment in
        // its own input handling concedes nobody remembers why. Without this a
        // controller user can open the dialog and then has no way out of it.
        //
        // Nothing happens when no dialog is open, so this cannot interfere with
        // B anywhere else in Playnite.
        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            if (args?.Button == ControllerInput.B && args.State == ControllerInputState.Released)
            {
                Controls.SteamGridDbSearchView.CloseOpenDialog();
            }
        }

        // Selection timing is collected only when debug logging is enabled.
        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            base.OnGameStarted(args);
            // Always suspend ImageRotater animation/slideshow work while a game is running.
            // This is a performance policy rather than a user preference.
            _gameRunningPaused = true;
            CoverImageControl.NotifyPlaybackPolicyChanged();
            BackgroundImageControl.NotifyPlaybackPolicyChanged();
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            base.OnGameStopped(args);
            _gameRunningPaused = false;
            CoverImageControl.NotifyPlaybackPolicyChanged();
            BackgroundImageControl.NotifyPlaybackPolicyChanged();
            ScheduleSlideshow();
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            if (_fileLogger == null || !_fileLogger.IsEnabled)
            {
                HandleGameSelected(args);
                return;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            Array.Clear(_selectionPhases, 0, _selectionPhases.Length);
            try
            {
                HandleGameSelected(args);
            }
            finally
            {
                timer.Stop();

                string gameName = args?.NewValue?.FirstOrDefault()?.Name ?? "<none>";
                _fileLogger.Log(
                    $"PERF selection game=\"{gameName}\" total={timer.ElapsedMilliseconds}ms "
                    + $"fade={_selectionPhases[0]}ms cover={_selectionPhases[1]}ms "
                    + $"background={_selectionPhases[2]}ms");
            }
        }

        // Populated only while verbose diagnostics are enabled.
        private readonly long[] _selectionPhases = new long[3];

        private static long Timed(Action action)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            action();
            return timer.ElapsedMilliseconds;
        }

        private void HandleGameSelected(OnGameSelectedEventArgs args)
        {
            // Views rebuild their FadeImages when the user switches layouts,
            // and a rebuilt instance carries stock timing again. Rescanning is
            // idempotent and skips patched instances, so a light throttle is
            // all the restraint it needs.
            if (Settings?.UseThemeIntegration != true &&
                (DateTime.UtcNow - _lastFadeRetime).TotalSeconds > 10)
            {
                _lastFadeRetime = DateTime.UtcNow;
                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    int patchedFadeImages = 0;
                    _selectionPhases[0] = Timed(() => patchedFadeImages = FadeImageTuner.Apply());
                    _fileLogger.Log(
                        $"background transition tuner patched={patchedFadeImages} " +
                        $"style={Transition.BackgroundStyle} " +
                        $"duration={Transition.BackgroundDuration.TotalMilliseconds:0}ms");
                }
                else
                {
                    FadeImageTuner.Apply();
                }
            }

            Game selected = args?.NewValue?.FirstOrDefault();

            // Advance the generation before rotation so queued background writes can detect stale selections.
            _selectionGeneration++;

            if (selected != null)
            {
                // Tell themes whether this game has a plugin cover to show, so
                // a tile only collapses its native cover when we can replace
                // it. Without this, games the user never set up would render
                // blank.
                UpdateHasDataCover(selected);

                // Avoid formatting selection diagnostics unless debug logging is enabled.
                if (_fileLogger.IsEnabled)
                {
                    _fileLogger.Log(
                        $"selected \"{selected.Name}\" ({selected.Id}) hasDataCover={Settings?.HasDataCover}");
                }

                // Covers are resolved lazily when a game is first visited.
                // Route native still-cover changes through CoverTileTransition as well:
                // previously that transition service was only used by slideshow ticks,
                // so "Every Selection" could hard-cut even when a fade was configured.
                // Compatibility mode must update Playnite's own CoverImage.
                // Theme integration does not: the hosted cover control reads
                // directly from ImageRotater's folder when the selection
                // notification below arrives. Avoiding this call also avoids a
                // second EverySelection pick for the same tile.
                if (Settings?.UseThemeIntegration != true)
                {
                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _selectionPhases[1] = Timed(() => ApplyCoverForSelection(selected));
                    }
                    else
                    {
                        ApplyCoverForSelection(selected);
                    }
                }
            }

            // Notify after publishing so theme controls read the same cover choice.
            CoverImageControl.NotifySelectionChanged(selected?.Id ?? Guid.Empty);

            // The two kinds rotate at different moments, deliberately.
            //
            // BACKGROUNDS rotate for the game being LEFT. Writing the arriving
            // game's background landed ~11ms after Playnite had started
            // rendering its stored image, so every switch painted it twice -
            // the second swap mid-transition was the crop/blur flash. Rotating
            // on the way out pre-stages the next image, and arrivals render
            // once.
            //
            // COVERS rotate for the game ARRIVED AT - the user wants to watch
            // the tile change, not discover later that it did. A cover swap is
            // not part of the switch transition, so it has no flash to cause,
            // and the write alone updates the tile: Playnite notifies the
            // property both Desktop and Fullscreen tiles bind.
            //
            // Backgrounds rotate only after the selection settles; covers rotate on arrival.
            Game left = args?.OldValue?.FirstOrDefault();
            if (Settings?.UseThemeIntegration != true &&
                left != null && left.Id == _settledGameId)
            {
                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _selectionPhases[2] = Timed(() => _rotationService.ApplyTo(left, ArtworkKind.Background));
                }
                else
                {
                    _rotationService.ApplyTo(left, ArtworkKind.Background);
                }
            }

            _settledGameId = Guid.Empty;
            _settling = Settings?.UseThemeIntegration == true ? null : selected;

            if (selected != null && Settings?.UseThemeIntegration != true)
            {
                SettleTimer.Stop();
                SettleTimer.Start();
            }
            else if (_settleTimer != null)
            {
                _settleTimer.Stop();
            }

            // Restart the slideshow clock for a NEW selection: a slideshow
            // interval measures "how long the user has been LOOKING at this
            // game", not wall time.
            //
            // Only for a new one. Playnite re-raises this event for the game
            // already selected - Fullscreen does it on view changes and focus
            // shifts - and resetting on those restarted the clock mid-interval,
            // so a change due in two seconds was pushed out to eight. That is
            // the "slideshow timing is inconsistent" complaint.
            bool changed = selected?.Id != _slideshowGame?.Id;
            _slideshowGame = selected;

            if (changed || _slideshowTimer == null)
            {
                ScheduleSlideshow();
            }
        }

        private void ApplyCoverForSelection(Game game)
        {
            if (game == null || _rotationService == null)
            {
                return;
            }

            ImageRotaterSettings settings = Settings;
            if (settings == null || !settings.EnableRotation || !settings.RotateCovers)
            {
                _rotationService.ApplyTo(game, ArtworkKind.Cover);
                return;
            }

            // Theme integration renders directly in CoverImageControl. This
            // method is the native/compatibility path and must not create a
            // second selection or any Playnite metadata work.
            if (settings.UseThemeIntegration)
            {
                return;
            }

            SelectionMode mode = settings.GetSelectionMode(
                game.Id, ArtworkKind.Cover, settings.CoverSelectionMode);


            bool firstVisit = _coverTransitionVisited.Add(game.Id);
            int candidateCount = 0;
            try
            {
                candidateCount = _coverSource != null
                    ? _coverSource.GetImagePaths(game).Count
                    : 0;
            }
            catch (Exception)
            {
                candidateCount = 0;
            }

            bool hasArtwork = candidateCount > 0;
            bool hasAlternatives = candidateCount > 1;
            bool changedGame = !_lastCoverTransitionGameId.HasValue ||
                _lastCoverTransitionGameId.Value != game.Id;
            _lastCoverTransitionGameId = game.Id;

            // Only animate when selecting the game can actually rotate its
            // artwork. Session / Daily / Fixed choose a stable cover and keep it,
            // so running the custom transition while merely navigating between
            // games is wasted UI work. Slideshow transitions are handled by the
            // slideshow timer below when the cover really changes.
            //
            // EverySelection is the only selection-driven mode that can change
            // the current cover here, and it needs at least two candidates.
            bool animate = !CoverImageControl.IsHostedByTheme &&
                mode == SelectionMode.EverySelection &&
                hasAlternatives;

            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log(
                    $"cover transition request game=\"{game.Name}\" mode={mode} " +
                    $"firstVisit={firstVisit} changedGame={changedGame} candidates={candidateCount} " +
                    $"alternatives={hasAlternatives} animate={animate} " +
                    $"style={Transition.CoverStyle} duration={Transition.CoverDuration.TotalMilliseconds:0}ms " +
                    $"themeHosted={CoverImageControl.IsHostedByTheme}");
            }

            if (!animate)
            {
                _rotationService.ApplyTo(game, ArtworkKind.Cover);
                if (IsStartupStableCoverMode(mode))
                {
                    _startupStableCoverPrimeDone.Add(game.Id);
                }
                return;
            }

            _coverTransition.Run(
                game.Id,
                () => _rotationService.ApplyTo(game, ArtworkKind.Cover));
        }

        // Arms the per-kind due times and makes sure the timer only runs when
        // there is something to wait for - an idle timer ticking forever for a
        // feature that is off would be pure waste.
        private void ScheduleSlideshow()
        {
            SelectionMode bgMode = Settings != null && _slideshowGame != null
                ? Settings.GetSelectionMode(_slideshowGame.Id, ArtworkKind.Background, Settings.SelectionMode)
                : SelectionMode.Session;
            SelectionMode coverMode = Settings != null && _slideshowGame != null
                ? Settings.GetSelectionMode(_slideshowGame.Id, ArtworkKind.Cover, Settings.CoverSelectionMode)
                : SelectionMode.Session;

            int bg = Settings != null && Settings.EnableRotation && Settings.RotateBackgrounds
                && bgMode == SelectionMode.Slideshow
                ? Settings.BackgroundSlideshowSeconds
                : 0;
            int cover = Settings != null && Settings.EnableRotation && Settings.RotateCovers
                && coverMode == SelectionMode.Slideshow
                ? Settings.CoverSlideshowSeconds
                : 0;

            _backgroundDue = bg >= MinimumSlideshowSeconds && _slideshowGame != null
                ? DateTime.UtcNow.AddSeconds(bg)
                : DateTime.MaxValue;

            if (_backgroundDue == DateTime.MaxValue && _slideshowGame != null)
            {
                _rotationService?.ClearPreparedNext(_slideshowGame.Id, ArtworkKind.Background);
            }

            _coverDue = cover >= MinimumSlideshowSeconds && _slideshowGame != null
                ? DateTime.UtcNow.AddSeconds(cover)
                : DateTime.MaxValue;

            bool wanted = _backgroundDue != DateTime.MaxValue || _coverDue != DateTime.MaxValue;

            // Normal priority, not the default Background. A Background-priority
            // tick waits until the dispatcher has nothing better to do, and a
            // Fullscreen theme animating its backdrop and decoding a video
            // cover always has something better to do - ticks landed late by
            // a different amount each time, which read as the interval
            // wandering.
            if (wanted && _slideshowTimer == null)
            {
                _slideshowTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal);
                _slideshowTimer.Tick += OnSlideshowTick;
            }

            if (_slideshowTimer != null)
            {
                _slideshowTimer.IsEnabled = wanted;
            }

            // A slideshow knows exactly which background will be used next once
            // it reserves the next pick. Warm that still image during the wait
            // instead of making the timer tick pay the first-decode cost.
            PreloadNextSlideshowBackground();

            ArmTimerForNextDue();
        }

        // Wakes the timer when something is actually due, rather than once a
        // second to ask.
        //
        // A fixed one-second tick meant a 10-second interval fired somewhere in
        // [10.0, 11.0) - always late, never early, and visibly so at short
        // intervals. Sleeping until the nearest due time removes that bias and
        // stops the timer waking ~59 times out of 60 with nothing to do.
        //
        // Floored at a short minimum: a due time already in the past (the
        // window was unfocused, so ticks were skipped) must not ask the
        // dispatcher for a zero or negative interval.
        private void ArmTimerForNextDue()
        {
            if (_slideshowTimer == null || !_slideshowTimer.IsEnabled)
            {
                return;
            }

            // Away from the window, due times sit in the past and would ask for
            // the floor over and over. Poll slowly instead, purely to notice
            // that focus came back.
            if (_unfocused || _gameRunningPaused)
            {
                _slideshowTimer.Interval = UnfocusedPollInterval;
                return;
            }

            DateTime next = _backgroundDue < _coverDue ? _backgroundDue : _coverDue;

            if (next == DateTime.MaxValue)
            {
                return;
            }

            TimeSpan wait = next - DateTime.UtcNow;

            _slideshowTimer.Interval = wait > MinimumTimerInterval
                ? wait
                : MinimumTimerInterval;
        }

        // Short enough to be imperceptible, long enough that a due time in the
        // past cannot spin the dispatcher.
        private static readonly TimeSpan MinimumTimerInterval =
            TimeSpan.FromMilliseconds(50);

        // How often to check whether the window came back. Only latency on
        // resuming a slideshow nobody was watching, so a second is generous.
        private static readonly TimeSpan UnfocusedPollInterval =
            TimeSpan.FromSeconds(1);

        // True while the window is minimised or unfocused, so the timer polls
        // rather than chasing due times that are already in the past.
        private bool _unfocused;
        private bool _gameRunningPaused;

        private async void PreloadNextSlideshowBackground()
        {
            try
            {
                Game game = _slideshowGame;
                if (game == null || Settings == null || !Settings.EnableRotation ||
                    !Settings.RotateBackgrounds ||
                    Settings.GetSelectionMode(game.Id, ArtworkKind.Background, Settings.SelectionMode) != SelectionMode.Slideshow ||
                    Settings.BackgroundSlideshowSeconds < MinimumSlideshowSeconds ||
                    _loader == null || _rotationService == null)
                {
                    return;
                }

                string path = _rotationService.PrepareNext(game, ArtworkKind.Background);
                if (string.IsNullOrEmpty(path) || PosterFrame.IsVideo(path) || PosterFrame.IsAnimated(path))
                {
                    return;
                }

                int bucket = WidthBucket.ForWidth(GetPhysicalPrimaryScreenWidth());
                var watch = _fileLogger != null && _fileLogger.IsEnabled
                    ? System.Diagnostics.Stopwatch.StartNew()
                    : null;

                var bitmap = await _loader.LoadAsync(path, bucket);
                if (bitmap != null && watch != null)
                {
                    _fileLogger.Log(
                        $"BG PERF preload-slideshow game=\"{game.Name}\" {watch.ElapsedMilliseconds}ms " +
                        $"bucket={bucket} path={path}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: slideshow background preload failed");
            }
        }

        private void OnSlideshowTick(object sender, EventArgs e)
        {
            try
            {
                Game game = _slideshowGame;
                if (game == null)
                {
                    return;
                }

                // No slideshow for a window nobody is looking at. Rotating
                // while minimised or unfocused is file copies and database
                // writes for an invisible result; due times stay in the past,
                // so the next tick after focus returns rotates immediately.
                //
                // Polls slowly while away rather than re-arming to the due time
                // below: those due times are in the PAST, so the short floor
                // that keeps the timer honest for a real interval would instead
                // spin the dispatcher for as long as the window stayed
                // unfocused.
                Window main = Application.Current?.MainWindow;
                if (main == null || !main.IsActive || main.WindowState == WindowState.Minimized)
                {
                    _unfocused = true;
                    return;
                }

                _unfocused = false;

                if (game.IsRunning)
                {
                    _gameRunningPaused = true;
                    return;
                }

                _gameRunningPaused = false;
                DateTime now = DateTime.UtcNow;

                // Ticks are skipped outright for games with fewer than two
                // images of the kind: ApplyNext would re-pick the same file
                // and the write would be skipped, but the cover fade would
                // still run - a pulse to the same picture every N seconds.
                if (now >= _backgroundDue)
                {
                    if (Settings != null && Settings.EnableRotation && Settings.RotateBackgrounds &&
                        Settings.GetSelectionMode(game.Id, ArtworkKind.Background, Settings.SelectionMode) == SelectionMode.Slideshow &&
                        _imageSource.GetImagePaths(game).Count > 1)
                    {
                        // Playnite's own background element crossfades on
                        // source change, so this swap fades without any work
                        // from us.
                        _rotationService.ApplyNext(game, ArtworkKind.Background);

                        // The game has NOT changed, so no plugin control gets a
                        // context change - they would all keep showing the
                        // previous pick, and a video or GIF would keep playing
                        // it while the file underneath had already moved on.
                        BackgroundImageControl.NotifyBackgroundRotated(game.Id);
                    }

                    _backgroundDue = now.AddSeconds(Settings.BackgroundSlideshowSeconds);
                    PreloadNextSlideshowBackground();
                }

                if (now >= _coverDue)
                {
                    if (Settings != null && Settings.EnableRotation && Settings.RotateCovers &&
                        Settings.GetSelectionMode(game.Id, ArtworkKind.Cover, Settings.CoverSelectionMode) == SelectionMode.Slideshow &&
                        _coverSource.GetImagePaths(game).Count > 1)
                    {
                        // One path for both modes.
                        //
                        // Fullscreen used to write BEFORE the fade, on a
                        // background thread, on the reasoning that its missing
                        // notification meant nothing could snap the tile early.
                        // Once Playnite raises PropertyChanged for
                        // FullscreenListItemCoverObject that reasoning inverts:
                        // the write itself notifies the tile, so doing it first
                        // made the cover change BEFORE the fade started - the
                        // swap was visible and then the fade played over it.
                        //
                        // Writing inside the transition is what both modes
                        // now need: the tile is told to re-read at the moment
                        // the transition hides the change.
                        //
                        // Unless a theme hosts our control: it crossfades its
                        // own picture, and sits opaque over PART_ImageCover -
                        // fading the tile underneath would be invisible work,
                        // and for a video pick would animate something nobody
                        // can see. Then the swap just runs.
                        if (Settings.UseThemeIntegration || CoverImageControl.IsHostedByTheme)
                        {
                            _rotationService.ApplyNext(game, ArtworkKind.Cover);
                            CoverImageControl.NotifyArtworkRotated(game.Id);
                        }
                        else
                        {
                            _coverTransition.Run(
                                game.Id,
                                () =>
                                {
                                    _rotationService.ApplyNext(game, ArtworkKind.Cover);
                                    CoverImageControl.NotifyArtworkRotated(game.Id);
                                });
                        }
                    }

                    _coverDue = now.AddSeconds(Settings.CoverSlideshowSeconds);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: slideshow tick failed");
            }
            finally
            {
                // Re-armed for whichever kind is due next, including after the
                // early returns above - an unfocused window skips its ticks, and
                // without this the timer would keep the interval it was last
                // given and drift.
                ArmTimerForNextDue();
            }
        }

        // Reads the store directly rather than the candidate source, for the
        // same reason HasPluginArtwork does: only the plugin's own folder
        // distinguishes a game the user set up from one they scrolled past.
        private void UpdateHasDataCover(Game game)
        {
            if (Settings == null)
            {
                return;
            }

            try
            {
                Settings.HasDataCover = _store.HasAnyImage(game.Id, ArtworkKind.Cover);
            }
            catch (Exception)
            {
                // An unreadable folder means we cannot promise a cover, so the
                // theme should keep showing its own.
                Settings.HasDataCover = false;
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            // Only ImageRotater's own theme elements arrive here. Keeping
            // ImageRotater and BackgroundChanger on separate element names
            // prevents either plugin from claiming or shadowing the other's
            // controls when a theme supports both.
            //
            // Settings are passed as an accessor, not a value: a settings save
            // replaces the whole object, so handing over the current one would
            // leave the control reading a stale copy forever.
            // Nothing may escape this method.
            //
            // Playnite builds these while laying out the library - once per
            // grid tile in Fullscreen - and an exception there is not caught
            // anywhere above us: the window goes black and the process exits
            // with no crash dialog and nothing in the log. Returning null
            // instead costs the plugin's artwork on that element and leaves
            // the theme's own rendering intact, which is always the better
            // failure.
            try
            {
                if (args.Name == "Background")
                {
                    return new BackgroundImageControl(
                        _imageSource, _selector, _loader, () => Settings, _fileLogger,
                        imageId => PlayniteApi.Database.GetFullFilePath(imageId),
                        () => PlayniteApi.MainView.FilteredGames);
                }

                if (args.Name == "Cover")
                {
                    // No ImageLoader: this control publishes a path and the XAML
                // binds it with IsAsync=True, so decoding never touches the
                // layout thread.
                return new CoverImageControl(_coverSource, _selector, () => Settings, PlayniteApi);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"ImageRotater: could not create the \"{args.Name}\" view control");
            }

            return null;
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            return GameMenuBuilder.Build(args.Games, _menuHandler);
        }

        // Converts every GIF in the plugin's own folders to MP4.
        //
        // Same trade as the download-time conversion, applied to what is
        // already stored: markedly smaller on disk, and playback moves from
        // decoding frames on the UI thread to hardware-assisted H.264. On a
        // real library GIF that was 4.7 MB down to 1.0 MB.
        //
        // Each source is deleted only after its MP4 exists, so an interrupted
        // run costs disk rather than artwork.
        private void ConvertStoredGifs()
        {
            if (!GifConverter.IsAvailable)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    Loc.Get("LOCImageRotaterNeedFfmpeg"),
                    "ImageRotater");
                return;
            }

            List<string> gifs = FindStoredGifs();

            if (gifs.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    Loc.Get("LOCImageRotaterNoGifs"), "ImageRotater");
                return;
            }

            long totalBytes = 0;

            foreach (string gif in gifs)
            {
                try { totalBytes += new System.IO.FileInfo(gif).Length; }
                catch (Exception) { }
            }

            var confirm = PlayniteApi.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterConvertGifsQuestion", gifs.Count, totalBytes / 1024 / 1024),
                "ImageRotater",
                System.Windows.MessageBoxButton.YesNo);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            int converted = 0;
            long saved = 0;

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    progress.ProgressMaxValue = gifs.Count;

                    for (int i = 0; i < gifs.Count; i++)
                    {
                        if (progress.CancelToken.IsCancellationRequested)
                        {
                            break;
                        }

                        string gif = gifs[i];
                        progress.Text = Loc.Format("LOCImageRotaterConvertingFile", System.IO.Path.GetFileName(gif));
                        progress.CurrentProgressValue = i;

                        try
                        {
                            long before = new System.IO.FileInfo(gif).Length;
                            string mp4 = GifConverter.Convert(gif);

                            if (string.IsNullOrEmpty(mp4))
                            {
                                continue;
                            }

                            long after = new System.IO.FileInfo(mp4).Length;

                            // Only now is the source expendable.
                            System.IO.File.Delete(gif);

                            converted++;
                            saved += before - after;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, $"ImageRotater: could not convert {gif}");
                        }
                    }
                },
                new GlobalProgressOptions(Loc.Get("LOCImageRotaterConvertingGifs"), true)
                {
                    IsIndeterminate = false
                });

            // Every converted file changed a game's candidate list, so any
            // remembered pick may name a file that no longer exists.
            _sessionCache.Clear();
            _rotationService.ForgetAll();

            PlayniteApi.Dialogs.ShowMessage(
                converted == 0
                    ? Loc.Get("LOCImageRotaterNoGifsConverted")
                    : Loc.Format("LOCImageRotaterGifsConverted", converted, gifs.Count, saved / 1024 / 1024),
                "ImageRotater");
        }

        // Every GIF the plugin owns, skipping its own caches - a poster frame
        // is a JPEG and a published copy is a duplicate of a real candidate.
        private List<string> FindStoredGifs()
        {
            var found = new List<string>();

            try
            {
                if (!System.IO.Directory.Exists(_store.ImagesRoot))
                {
                    return found;
                }

                foreach (string file in System.IO.Directory.GetFiles(
                    _store.ImagesRoot, "*.gif", System.IO.SearchOption.AllDirectories))
                {
                    if (file.IndexOf(PosterFrame.CacheFolderName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        file.IndexOf(Letterboxer.CacheFolderName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    found.Add(file);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not list stored GIFs");
            }

            return found;
        }

        // Plumbing for whole-library image jobs: progress
        // dialog, off-thread work, and reporting the result back on the UI
        // thread.
        private void RunImageJob(
            string title,
            Func<ImageOptimizer, List<Guid>, Action<int, int>, Func<bool>, ImageOptimizer.Result> job)
        {
            var optimizer = new ImageOptimizer(_store, _fileLogger);

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    List<Guid> ids = PlayniteApi.Database.Games.Select(g => g.Id).ToList();
                    progress.ProgressMaxValue = Math.Max(1, ids.Count);
                    progress.CurrentProgressValue = 0;

                    ImageOptimizer.Result result = job(
                        optimizer,
                        ids,
                        (done, total) =>
                        {
                            progress.ProgressMaxValue = Math.Max(1, total);
                            progress.CurrentProgressValue = done;
                            progress.Text = Loc.Format("LOCImageRotaterProgressItems", done, total);
                        },
                        () => progress.CancelToken.IsCancellationRequested);

                    PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        PlayniteApi.Dialogs.ShowMessage(result.Summary, "ImageRotater"));
                },
                new GlobalProgressOptions(title, true)
                {
                    IsIndeterminate = false
                });
        }

        // Re-encodes stored artwork to smaller files.
        //
        // Confirmed first, and the wording says "cannot be undone": this
        // replaces the stored files rather than copying, and a re-encode is a
        // small permanent quality loss. Preserved originals are affected too,
        // so a user who wants their downloaded art byte-identical should
        // decline.
        public void OptimiseStoredImages()
        {
            var confirm = PlayniteApi.Dialogs.ShowMessage(
                Loc.Get("LOCImageRotaterOptimiseQuestion"),
                "ImageRotater",
                System.Windows.MessageBoxButton.YesNo);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            RunImageJob(
                Loc.Get("LOCImageRotaterOptimising"),
                (optimizer, ids, report, cancelled) => optimizer.OptimiseAll(ids, report, cancelled));
        }

        // One-shot library maintenance action for existing backgrounds. This is
        // deliberately independent from the download resize toggle/preset: using
        // it once must not silently change how future downloads are handled.
        // Preserved Playnite originals are skipped so Restore Original continues
        // to mean the original native artwork, not a resized copy.
        public void ResizeStoredBackgrounds(BackgroundDownloadResizePreset preset)
        {
            var confirm = PlayniteApi.Dialogs.ShowMessage(
                Loc.Get("LOCImageRotaterResizeStoredBackgroundsQuestion"),
                "ImageRotater",
                System.Windows.MessageBoxButton.YesNo);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            List<Guid> ids = PlayniteApi.Database.Games.Select(g => g.Id).ToList();
            int checkedCount = 0;
            int resizedCount = 0;

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    progress.ProgressMaxValue = Math.Max(1, ids.Count);
                    progress.CurrentProgressValue = 0;

                    int done = 0;
                    foreach (Guid gameId in ids)
                    {
                        if (progress.CancelToken.IsCancellationRequested)
                        {
                            break;
                        }

                        IReadOnlyList<string> backgrounds;
                        try
                        {
                            backgrounds = _store.GetImagePathsRaw(gameId, ArtworkKind.Background);
                        }
                        catch
                        {
                            backgrounds = new List<string>();
                        }

                        foreach (string path in backgrounds)
                        {
                            if (progress.CancelToken.IsCancellationRequested)
                            {
                                break;
                            }

                            if (GameImageStore.IsPreservedOriginal(path)
                                || !DownloadedBackgroundResizer.CanResize(path))
                            {
                                continue;
                            }

                            checkedCount++;
                            if (DownloadedBackgroundResizer.ResizeToPreset(path, preset))
                            {
                                resizedCount++;
                                _cache?.Forget(path);
                            }
                        }

                        progress.CurrentProgressValue = ++done;
                        progress.Text = Loc.Format("LOCImageRotaterProgressItems", done, ids.Count);
                    }
                },
                new GlobalProgressOptions(Loc.Get("LOCImageRotaterResizingStoredBackgrounds"), true)
                {
                    IsIndeterminate = false
                });

            _rotationService?.ForgetAll();

            PlayniteApi.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterResizeStoredBackgroundsResult", resizedCount, checkedCount),
                "ImageRotater");
        }

        // Undo for write mode. Write mode changes the user's library data, so
        // there has to be a way back that does not involve editing games by hand.
        public void RestoreOriginalBackgrounds()
        {
            int count = _writer.BackedUpCount;
            if (count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    Loc.Get("LOCImageRotaterNothingRestore"),
                    "ImageRotater");
                return;
            }

            var confirm = PlayniteApi.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterRestoreQuestion", count),
                "ImageRotater",
                System.Windows.MessageBoxButton.YesNo);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            int restored = 0;

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    progress.ProgressMaxValue = Math.Max(1, count);
                    progress.CurrentProgressValue = 0;

                    restored = _writer.RestoreAll((done, total) =>
                    {
                        progress.ProgressMaxValue = Math.Max(1, total);
                        progress.CurrentProgressValue = done;
                        progress.Text = Loc.Format("LOCImageRotaterProgressItems", done, total);
                    });
                },
                new GlobalProgressOptions(Loc.Get("LOCImageRotaterRestoringOriginalsProgress"), false)
                {
                    IsIndeterminate = false
                });

            _rotationService.ForgetAll();

            PlayniteApi.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterRestoredBackgrounds", restored),
                "ImageRotater");
        }

        public void OpenDebugLogFolder()
        {
            try
            {
                string path = System.IO.Path.GetDirectoryName(_fileLogger?.Path_);
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = GetPluginUserDataPath();
                }

                System.IO.Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", path);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not open the debug log folder");
            }
        }

        public void NotifyDebugLoggingSaved(bool wasEnabled)
        {
            if (Settings?.EnableDebugLogging == true && !wasEnabled)
            {
                _fileLogger.StartSession(
                    typeof(ImageRotater).Assembly.GetName().Version.ToString(),
                    PlayniteApi.ApplicationInfo.Mode.ToString(),
                    Settings);
                _fileLogger.Log("Debug logging enabled from Settings.");
            }
        }

        private void ScheduleBackgroundChangerConflictWarning()
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(
                    new Action(CheckBackgroundChangerConflict),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not schedule the BackgroundChanger compatibility check");
            }
        }

        private void CheckBackgroundChangerConflict()
        {
            try
            {
                var addons = PlayniteApi?.Addons;
                if (addons == null)
                {
                    return;
                }

                string addonId = BackgroundChangerImporter.AddonId;
                bool installed = addons.Addons != null
                    && addons.Addons.Any(id => string.Equals(id, addonId, StringComparison.OrdinalIgnoreCase));
                bool disabled = addons.DisabledAddons != null
                    && addons.DisabledAddons.Any(id => string.Equals(id, addonId, StringComparison.OrdinalIgnoreCase));

                if (!installed || disabled)
                {
                    return;
                }

                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _fileLogger.Log("Compatibility warning: BackgroundChanger is installed and enabled.");
                }

                ShowBackgroundChangerConflictDialog();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not check BackgroundChanger status");
            }
        }


        private void ShowBackgroundChangerConflictDialog()
        {
            Window window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
                ShowCloseButton = true
            });

            window.Title = Loc.Get("LOCImageRotaterBackgroundChangerConflictTitle");
            window.Width = 590;
            window.SizeToContent = SizeToContent.Height;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var primaryTextBrush = Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.White;
            var secondaryTextBrush = Application.Current?.TryFindResource("TextBrushSecondary") as Brush ?? primaryTextBrush;

            var root = new Grid
            {
                Margin = new Thickness(24, 22, 24, 20)
            };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var warning = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(Color.FromRgb(120, 82, 22)),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = "!",
                    FontSize = 24,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
            Grid.SetColumn(warning, 0);
            root.Children.Add(warning);

            var content = new StackPanel();
            Grid.SetColumn(content, 1);

            content.Children.Add(new TextBlock
            {
                Text = Loc.Get("LOCImageRotaterBackgroundChangerConflictTitle"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryTextBrush,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            content.Children.Add(new TextBlock
            {
                Text = Loc.Get("LOCImageRotaterBackgroundChangerConflictMessage"),
                FontSize = 13,
                Foreground = primaryTextBrush,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap
            });

            var note = new Border
            {
                Margin = new Thickness(0, 16, 0, 0),
                Padding = new Thickness(12, 9, 12, 9),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                Child = new TextBlock
                {
                    Text = Loc.Get("LOCImageRotaterBackgroundChangerConflictHint"),
                    Foreground = secondaryTextBrush,
                    Opacity = 0.92,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            content.Children.Add(note);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };

            var okButton = new Button
            {
                Content = Loc.Get("LOCImageRotaterBackgroundChangerConflictAcknowledge"),
                MinWidth = 105,
                Padding = new Thickness(18, 7, 18, 7),
                IsDefault = true,
                IsCancel = true
            };
            okButton.Click += (s, e) => window.Close();
            buttons.Children.Add(okButton);
            content.Children.Add(buttons);

            root.Children.Add(content);
            window.Content = root;
            window.ShowDialog();
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new ImageRotaterSettingsView();
        }

        private void HookNavigationDirectionTracking()
        {
            try
            {
                Window window = Application.Current?.MainWindow;
                if (window == null || ReferenceEquals(window, _navigationWindow))
                {
                    return;
                }

                if (_navigationWindow != null)
                {
                    _navigationWindow.PreviewKeyDown -= NavigationWindow_PreviewKeyDown;
                }

                _navigationWindow = window;
                _navigationWindow.PreviewKeyDown += NavigationWindow_PreviewKeyDown;
            }
            catch
            {
                // Direction tracking is cosmetic only. Rotation must never fail
                // because a window was unavailable during startup.
            }
        }

        private void NavigationWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                    Transition.SetNavigationDirection(TransitionDirection.FromLeft);
                    break;
                case Key.Right:
                    Transition.SetNavigationDirection(TransitionDirection.FromRight);
                    break;
                case Key.Up:
                    Transition.SetNavigationDirection(TransitionDirection.FromTop);
                    break;
                case Key.Down:
                    Transition.SetNavigationDirection(TransitionDirection.FromBottom);
                    break;
            }
        }

        private void RequestBackgroundTransitionRebind(string reason)
        {
            int generation = ++_fadeRebindGeneration;

            // Force the normal selection fallback to be allowed to scan again
            // immediately if Playnite rebuilds the control one more time.
            _lastFadeRetime = DateTime.MinValue;

            try
            {
                // First pass: run after the current view-change work has built
                // most of the new visual tree.
                Application.Current?.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (generation != _fadeRebindGeneration)
                        {
                            return;
                        }

                        int patched = FadeImageTuner.Apply();
                        if (_fileLogger != null && _fileLogger.IsEnabled)
                        {
                            _fileLogger.Log(
                                $"background transition rebind reason={reason} pass=immediate patched={patched} " +
                                $"style={Transition.BackgroundStyle}");
                        }
                    }),
                    System.Windows.Threading.DispatcherPriority.Loaded);

                // Second pass: some Fullscreen themes create/replace FadeImage
                // one dispatcher turn later. This one-shot retry catches that
                // final instance. Rapid view changes cancel older generations.
                if (_fadeRebindTimer == null)
                {
                    _fadeRebindTimer = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(300)
                    };

                    _fadeRebindTimer.Tick += (s, e) =>
                    {
                        _fadeRebindTimer.Stop();

                        int current = _fadeRebindGeneration;
                        int patched = FadeImageTuner.Apply();

                        if (_fileLogger != null && _fileLogger.IsEnabled)
                        {
                            _fileLogger.Log(
                                $"background transition rebind reason=fullscreen-view pass=settled generation={current} " +
                                $"patched={patched} style={Transition.BackgroundStyle}");
                        }
                    };
                }

                _fadeRebindTimer.Stop();
                _fadeRebindTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not rebind background transition after view change");
            }
        }

        public override void OnFullscreenViewChanged(OnFullscreenViewChangedArgs args)
        {
            base.OnFullscreenViewChanged(args);

            // Do not rotate anything here. This hook exists only to attach the
            // configured transition to the NEW FadeImage instance created by
            // Fullscreen. It is intentionally cheap and event-driven.
            RequestBackgroundTransitionRebind(
                args != null ? $"fullscreen-{args.NewView}" : "fullscreen-view");
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            var startupTotal = System.Diagnostics.Stopwatch.StartNew();

            // Debug diagnostics are intentionally temporary. Expire forgotten
            // sessions before the file logger decides whether to start.
            _settingsViewModel?.ExpireDebugLoggingIfNeeded();

            _fileLogger.StartSession(
                typeof(ImageRotater).Assembly.GetName().Version.ToString(),
                PlayniteApi.ApplicationInfo.Mode.ToString(),
                Settings);

            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log($"Plugin loaded (mode={PlayniteApi.ApplicationInfo.Mode}).");
                _fileLogger.Log($"Render path={(Settings?.UseThemeIntegration == true ? "ThemeIntegration" : "Compatibility")}");
                _fileLogger.Log($"Session previous-picks loaded={_sessionCache?.PreviousSessionCount ?? 0}");
            }

            ScheduleBackgroundChangerConflictWarning();

            try
            {
                Application.Current?.Dispatcher.BeginInvoke(
                    new Action(HookNavigationDirectionTracking),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch
            {
            }

            // Remove files deferred by the previous clean shutdown before rotation starts.
            try
            {
                var cleanupTimer = System.Diagnostics.Stopwatch.StartNew();
                int removed = _writer.CleanupDeferredFiles();
                Services.PreviewCache.Clear();
                cleanupTimer.Stop();

                // Cleanup timing is debug-only.
                if (_fileLogger != null && _fileLogger.IsEnabled && removed > 0)
                {
                    _fileLogger.Log(
                        $"startup deferred-cleanup files={removed} total={cleanupTimer.ElapsedMilliseconds}ms");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not clean deferred files from the previous session");
            }

            // A previous compatibility-mode session (or an older plugin
            // version) may have left temporary ImageRotater artwork referenced
            // by Playnite. Theme integration promises to leave native metadata
            // untouched, so restore those references once before direct
            // rendering starts. With no saved compatibility writes this is a
            // no-op and costs no per-game rotation work.
            if (Settings?.UseThemeIntegration == true)
            {
                try
                {
                    var restoreTimer = System.Diagnostics.Stopwatch.StartNew();
                    int restored = _writer.RestoreKind(ArtworkKind.Background);
                    restored += _writer.RestoreKind(ArtworkKind.Cover);
                    restoreTimer.Stop();

                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _fileLogger.Log(
                            $"PERF startup-theme-restore total={restoreTimer.ElapsedMilliseconds}ms restored={restored}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ImageRotater: could not restore compatibility artwork for theme integration");
                }
            }

            // Playnite's own crossfade dips a quarter dark at the midpoint of
            // every background change - see FadeImageTuner. Retimed after the
            // window has built its template; Background priority queues this
            // behind that work rather than racing it.
            if (Settings?.UseThemeIntegration != true)
            {
                try
                {
                    Application.Current?.Dispatcher.BeginInvoke(
                        new Action(() => FadeImageTuner.Apply()),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ImageRotater: could not schedule the fade retime");
                }
            }

            // Build the startup artwork index once and share it between the
            // stable-cover prime and the published-file safety seed. Besides
            // avoiding a duplicate filesystem scan, this lets the prime skip
            // games that have no actual ImageRotater cover candidate at all.
            HashSet<string> startupArtworkIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Settings?.UseThemeIntegration != true)
            {
                try
                {
                    var artworkIndexTimer = System.Diagnostics.Stopwatch.StartNew();
                    startupArtworkIndex = _store.GetStartupArtworkIndex();
                    artworkIndexTimer.Stop();

                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _fileLogger.Log(
                            $"PERF startup-artwork-index total={artworkIndexTimer.ElapsedMilliseconds}ms " +
                            $"configuredKinds={startupArtworkIndex.Count}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ImageRotater: could not build shared startup artwork index");
                    startupArtworkIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log("PERF startup-artwork-index skipped=theme-integration");
            }

            // Build the small list of games whose cover is supposed to be
            // stable before the user selects them. Session, Daily and Fixed all
            // have that contract; EverySelection intentionally stays lazy.
            // Do NOT apply these synchronously here: visible tiles are handled
            // first and the rest are deferred in tiny dispatcher batches.
            //
            // Resolve the effective mode per game so artwork-manager overrides
            // are honoured even when the global cover mode is different.
            List<Game> startupStableCoverGames = null;
            if (Settings != null &&
                !Settings.UseThemeIntegration &&
                Settings.EnableRotation &&
                Settings.RotateCovers)
            {
                startupStableCoverGames = new List<Game>();

                foreach (Game game in PlayniteApi.Database.Games)
                {
                    if (game == null ||
                        !startupArtworkIndex.Contains(
                            GameImageStore.StartupIndexKey(game.Id, ArtworkKind.Cover)))
                    {
                        continue;
                    }

                    SelectionMode effectiveMode = Settings.GetSelectionMode(
                        game.Id,
                        ArtworkKind.Cover,
                        Settings.CoverSelectionMode);

                    if (!IsStartupStableCoverMode(effectiveMode))
                    {
                        continue;
                    }

                    startupStableCoverGames.Add(game);
                }
            }

            // Before any tile renders. Themes load these with OnLoad, which
            // throws FileNotFoundException on a missing file - inside
            // FullscreenTilePanel.MeasureOverride, which is fatal. Seeding
            // guarantees the file exists for every game that has artwork, so
            // the throw is impossible rather than merely unlikely.
            //
            // Time the synchronous startup seed for debug diagnostics and slow-start warnings.
            if (Settings?.UseThemeIntegration != true)
            {
                try
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    int seeded = _publisher.SeedEveryGame(PlayniteApi.Database.Games, startupArtworkIndex);
                    timer.Stop();

                    string seedPerf =
                        $"PERF startup-seed total={timer.ElapsedMilliseconds}ms "
                        + $"games={PlayniteApi.Database.Games.Count} wrote={seeded}";

                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _fileLogger.Log(seedPerf);
                    }

                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "ImageRotater: could not seed published artwork files");
                }
            }
            else if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log("PERF startup-seed skipped=theme-integration");
            }

            startupTotal.Stop();
            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log(
                    $"PERF startup-total total={startupTotal.ElapsedMilliseconds}ms "
                    + $"mode={PlayniteApi.ApplicationInfo.Mode} games={PlayniteApi.Database.Games.Count}");
            }

            if (startupStableCoverGames != null && startupStableCoverGames.Count > 0)
            {
                ScheduleVisibleFirstStableCoverPrime(startupStableCoverGames);
            }

            // Resolve Playnite's library grid only once, after startup work and
            // only when the dispatcher is idle. The first real cover
            // transition used to pay this visual-tree search synchronously,
            // which could make the first click feel like a large freeze.
        }

        private static bool IsStartupStableCoverMode(SelectionMode mode)
        {
            return mode == SelectionMode.Session ||
                mode == SelectionMode.Daily ||
                mode == SelectionMode.Fixed;
        }

        private void ScheduleVisibleFirstStableCoverPrime(List<Game> games)
        {
            if (games == null || games.Count == 0 || _coverTransition == null)
            {
                return;
            }

            var allTimer = System.Diagnostics.Stopwatch.StartNew();
            var lookup = games.ToDictionary(g => g.Id, g => g);

            _coverTransition.DiscoverVisibleGameIdsAsync(visibleIds =>
            {
                if (_startupStableCoverPrimeStopping)
                {
                    return;
                }

                var visibleTimer = System.Diagnostics.Stopwatch.StartNew();
                int visibleProcessed = 0;

                _writer.BeginStartupBatch();
                try
                {
                    foreach (Guid id in visibleIds)
                    {
                        Game game;
                        if (!lookup.TryGetValue(id, out game) ||
                            _startupStableCoverPrimeDone.Contains(id))
                        {
                            continue;
                        }

                        _rotationService.ApplyTo(game, ArtworkKind.Cover);
                        _startupStableCoverPrimeDone.Add(id);
                        visibleProcessed++;
                    }
                }
                finally
                {
                    _writer.EndStartupBatch();
                }

                visibleTimer.Stop();

                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _fileLogger.Log(
                        $"PERF startup-cover-stable-visible total={visibleTimer.ElapsedMilliseconds}ms " +
                        $"visible={visibleIds.Count} processed={visibleProcessed} eligible={games.Count}");
                }

                var remaining = new Queue<Game>(
                    games.Where(g => !_startupStableCoverPrimeDone.Contains(g.Id)));

                PrimeDeferredStableCoverBatch(remaining, allTimer, 0, 0, 0);
            });
        }

        private void PrimeDeferredStableCoverBatch(
            Queue<Game> remaining,
            System.Diagnostics.Stopwatch allTimer,
            int processed,
            int batches,
            long maxBatchMilliseconds)
        {
            if (_startupStableCoverPrimeStopping || remaining == null)
            {
                return;
            }

            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ContextIdle,
                    new Action(() =>
                    {
                        if (_startupStableCoverPrimeStopping)
                        {
                            return;
                        }

                        if (remaining.Count == 0)
                        {
                            allTimer.Stop();
                            if (_fileLogger != null && _fileLogger.IsEnabled)
                            {
                                _fileLogger.Log(
                                    $"PERF startup-cover-stable-deferred total={allTimer.ElapsedMilliseconds}ms " +
                                    $"games={processed} batches={batches} maxBatch={maxBatchMilliseconds}ms");
                            }
                            return;
                        }

                        var batchTimer = System.Diagnostics.Stopwatch.StartNew();
                        int batchProcessed = 0;

                        _writer.BeginStartupBatch();
                        try
                        {
                            while (remaining.Count > 0 && batchProcessed < DeferredStableCoverPrimeBatchSize)
                            {
                                Game game = remaining.Dequeue();
                                if (game == null || _startupStableCoverPrimeDone.Contains(game.Id))
                                {
                                    continue;
                                }

                                _rotationService.ApplyTo(game, ArtworkKind.Cover);
                                _startupStableCoverPrimeDone.Add(game.Id);
                                batchProcessed++;
                            }
                        }
                        finally
                        {
                            _writer.EndStartupBatch();
                        }

                        batchTimer.Stop();
                        long nextMax = Math.Max(maxBatchMilliseconds, batchTimer.ElapsedMilliseconds);

                        PrimeDeferredStableCoverBatch(
                            remaining,
                            allTimer,
                            processed + batchProcessed,
                            batches + 1,
                            nextMax);
                    }));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: deferred stable cover prime failed");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            _startupStableCoverPrimeStopping = true;

            if (_navigationWindow != null)
            {
                _navigationWindow.PreviewKeyDown -= NavigationWindow_PreviewKeyDown;
                _navigationWindow = null;
            }

            // Stop the tick, drop the handler, forget the game. The timer dies
            // with the dispatcher anyway, but an armed timer during shutdown
            // can fire into half-disposed state, and leaving it is exactly the
            // "properly stop and dispose timers" class of leak.
            if (_slideshowTimer != null)
            {
                _slideshowTimer.IsEnabled = false;
                _slideshowTimer.Tick -= OnSlideshowTick;
                _slideshowTimer = null;
            }

            if (_settleTimer != null)
            {
                _settleTimer.Stop();
                _settleTimer.Tick -= OnSelectionSettled;
                _settleTimer = null;
            }

            _settling = null;

            _slideshowGame = null;

            // Persist only Session-mode choices once, at clean shutdown. This
            // lets the next Playnite launch avoid reopening on the exact same
            // cover/background without adding disk I/O to normal navigation.
            int savedSessionPicks = _sessionCache != null ? _sessionCache.SaveSessionChoices() : 0;
            int savedDailyPicks = _sessionCache != null ? _sessionCache.SaveDailyChoices() : 0;
            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log($"Session previous-picks saved={savedSessionPicks}");
                _fileLogger.Log($"Daily picks saved={savedDailyPicks}");
            }

            // Defer preview-file cleanup to the next startup.
            Services.PreviewCache.ReleaseForShutdown();

            // Put every game's own artwork back before Playnite closes.
            //
            // Playnite gives an extension no uninstall or disable hook, and a
            // disabled plugin does not load at all - so this is the only moment
            // the plugin is still running and can undo what it wrote. Without
            // it, disabling the plugin left Game.CoverImage and
            // BackgroundImage pointing at plugin artwork, and users had to
            // re-add their own by hand.
            //
            // Rotation re-applies on the next launch, so a user who keeps the
            // plugin enabled sees no difference. What changes is that the
            // database is always left in a state that survives the plugin
            // going away.
            //
            // A crash still skips this, which is why the restore itself was
            // made robust: it re-imports the preserved copy when the recorded
            // id no longer resolves, rather than writing back a dead
            // reference.
            try
            {
                var shutdownTimer = System.Diagnostics.Stopwatch.StartNew();
                int restored = _writer.RestoreAllForShutdown();
                shutdownTimer.Stop();

                if (restored > 0 && _fileLogger != null && _fileLogger.IsEnabled)
                {
                    _fileLogger.Log(
                        $"restored {restored} game(s) to their own artwork on shutdown "
                        + $"without disk cleanup ({shutdownTimer.ElapsedMilliseconds}ms)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not restore artwork on shutdown");
            }
        }

        // Converts every GIF the plugin holds to MP4.
        public void ConvertGifsToMp4()
        {
            if (!GifConverter.IsAvailable)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCImageRotaterNeedFfmpeg"), "ImageRotater");

                return;
            }

            RunBulkJob(
                Loc.Get("LOCImageRotaterConvertAllGifsQuestion"),
                Loc.Get("LOCImageRotaterConvertingArtwork"),
                (report, cancelled) => BulkConverter.GifsToMp4(_store, report, cancelled).Summary);
        }

        // Remuxes every fragmented video the plugin holds.
        public void RepairAllVideos()
        {
            if (!GifConverter.IsAvailable)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCImageRotaterNeedFfmpeg"), "ImageRotater");

                return;
            }

            RunBulkJob(
                Loc.Get("LOCImageRotaterRepairAllVideosQuestion"),
                Loc.Get("LOCImageRotaterRepairingVideosProgress"),
                (report, cancelled) => BulkConverter.RepairVideos(_store, report, cancelled).Summary);
        }

        // Converts every JPEG the plugin holds to PNG.
        public void ConvertJpegsToPng()
        {
            RunBulkJob(
                Loc.Get("LOCImageRotaterConvertJpegsQuestion"),
                Loc.Get("LOCImageRotaterConvertingArtwork"),
                (report, cancelled) => BulkConverter.JpegsToPng(_store).Summary);
        }

        // Copies BackgroundChanger's per-game covers and backgrounds into this
        // plugin's store. Nothing of BackgroundChanger's is moved or deleted.
        public void ImportFromBackgroundChanger()
        {
            string bcRoot = System.IO.Path.Combine(
                PlayniteApi.Paths.ExtensionsDataPath, BackgroundChangerImporter.PluginId);

            // Checked before asking, so a user without BackgroundChanger data
            // gets the answer straight away rather than after a confirmation.
            if (!System.IO.Directory.Exists(System.IO.Path.Combine(bcRoot, "BackgroundChanger")))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    Loc.Format("LOCImageRotaterNoBcArtwork", bcRoot), "ImageRotater");

                return;
            }

            var known = new HashSet<Guid>(PlayniteApi.Database.Games.Select(g => g.Id));

            RunBulkJob(
                Loc.Get("LOCImageRotaterImportBcQuestion"),
                Loc.Get("LOCImageRotaterImportingBc"),
                (report, cancelled) => BackgroundChangerImporter.Import(bcRoot, known, _store, report, cancelled).Summary);
        }

        // Confirm, run under Playnite's progress dialog, forget the rotation
        // caches, report. The job returns its own summary text.
        private void RunBulkJob(
            string question,
            string progressTitle,
            Func<Action<int, int, string>, Func<bool>, string> job)
        {
            if (PlayniteApi.Dialogs.ShowMessage(
                    question,
                    "ImageRotater",
                    System.Windows.MessageBoxButton.YesNo)
                != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            string summary = null;

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    progress.ProgressMaxValue = 1;
                    progress.CurrentProgressValue = 0;

                    summary = job(
                        (done, total, item) =>
                        {
                            progress.ProgressMaxValue = Math.Max(1, total);
                            progress.CurrentProgressValue = Math.Min(done, Math.Max(1, total));

                            string name = string.IsNullOrEmpty(item)
                                ? string.Empty
                                : System.IO.Path.GetFileName(item);

                            progress.Text = string.IsNullOrEmpty(name)
                                ? Loc.Format("LOCImageRotaterProgressItems", done, total)
                                : Loc.Format("LOCImageRotaterProgressFile", done, total, name);
                        },
                        () => progress.CancelToken.IsCancellationRequested);
                },
                new GlobalProgressOptions(progressTitle, true)
                {
                    IsIndeterminate = false
                });

            if (summary == null)
            {
                return;
            }

            _rotationService?.ForgetAll();
            PlayniteApi.Dialogs.ShowMessage(summary, "ImageRotater");
        }

        // Mends games left pointing at plugin artwork that no longer exists.
        //
        // Playnite renders a dead artwork reference as SOLID BLACK rather than
        // as a blank tile, so a library in this state looks like the theme
        // broke. Clearing the reference lets Playnite fall back to its own
        // placeholder, and the game's library plugin can then re-fetch its
        // proper artwork.
        //
        // Separate from the reset, and safe: it deletes nothing and only
        // touches games whose current artwork is already unreachable.
        public bool RepairArtworkReferences()
        {
            int cleared = 0;
            int orphans = 0;
            int videos = 0;
            Exception failure = null;

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    try
                    {
                        progress.ProgressMaxValue = 3;
                        progress.CurrentProgressValue = 0;
                        progress.Text = Loc.Get("LOCImageRotaterRepairReferencesProgress");
                        cleared = _writer.ClearDeadReferences();
                        progress.CurrentProgressValue = 1;

                        progress.Text = Loc.Get("LOCImageRotaterCleaningPublishedProgress");
                        orphans = _store.RemoveOrphanedPublished();
                        progress.CurrentProgressValue = 2;

                        progress.Text = Loc.Get("LOCImageRotaterRepairingVideosProgress");
                        var repaired = BulkConverter.RepairVideos(
                            _store,
                            (done, total, item) =>
                            {
                                progress.ProgressMaxValue = Math.Max(1, total);
                                progress.CurrentProgressValue = done;
                                string name = string.IsNullOrEmpty(item)
                                    ? string.Empty
                                    : System.IO.Path.GetFileName(item);
                                progress.Text = string.IsNullOrEmpty(name)
                                    ? Loc.Get("LOCImageRotaterRepairingVideosProgress")
                                    : Loc.Format("LOCImageRotaterProgressFile", done, total, name);
                            },
                            () => false);
                        videos = repaired.Converted;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                },
                new GlobalProgressOptions(Loc.Get("LOCImageRotaterRepairArtworkProgress"), false)
                {
                    IsIndeterminate = false
                });

            if (failure != null)
            {
                Logger.Error(failure, "ImageRotater: could not repair artwork references");

                PlayniteApi.Dialogs.ShowErrorMessage(
                    Loc.Format("LOCImageRotaterRepairArtworkFailed", failure.Message), "ImageRotater");

                return false;
            }

            if (cleared == 0 && orphans == 0 && videos == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    Loc.Get("LOCImageRotaterNothingRepair"),
                    "ImageRotater");

                return false;
            }

            _rotationService?.ForgetAll();

            PlayniteApi.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterRepairArtworkDone", cleared),
                "ImageRotater");

            return true;
        }

        // Called by the settings view model after every save.
        //
        // Clearing the rotation memory makes toggled settings take effect on
        // the NEXT selection instead of whenever each game happens to rotate
        // again. Concretely: turning letterboxing off used to leave every
        // game on its composite until its pick eventually changed, because
        // the write-skip saw the same final path and did nothing - which read
        // as the toggle being broken.
        public void NotifySettingsSaved()
        {
            if (Settings != null && _writer != null)
            {
                // Switching to theme integration must immediately put the user's
                // native Playnite artwork back. From this point on the plugin
                // controls render ImageRotater files directly and database
                // artwork updates are no longer part of the display path.
                if (Settings.UseThemeIntegration || !Settings.EnableRotation || !Settings.RotateBackgrounds)
                {
                    _writer.RestoreKind(ArtworkKind.Background);
                }

                if (Settings.UseThemeIntegration || !Settings.EnableRotation || !Settings.RotateCovers)
                {
                    _writer.RestoreKind(ArtworkKind.Cover);
                }

                if (Settings.UseThemeIntegration)
                {
                    Settings.CurrentCoverPath = string.Empty;
                    Settings.CurrentCoverGameId = string.Empty;
                    Settings.CurrentCoverIsVideo = false;
                }
            }

            _rotationService?.ForgetAll();

            // Only the compatibility path changes Playnite's native FadeImage.
            // Theme integration animates inside BackgroundImageControl instead.
            if (Settings?.UseThemeIntegration != true)
            {
                FadeImageTuner.Apply();
            }

            // A changed interval - or a slideshow switched on - takes effect
            // now, not at the next selection change.
            ScheduleSlideshow();
        }

        // Removes every image the plugin holds and puts each game's own artwork
        // back. Asks first, and asks for a restart afterwards.
        //
        // The restart matters. Every control in the running session holds
        // decoded images, the rotation service holds picks, and the session
        // cache holds paths - all of which now point at deleted files.
        // Rebuilding that state in place would mean invalidating half a dozen
        // caches in the right order; a restart rebuilds all of it correctly.
        //
        // The restart itself is Playnite's own: its settings window carries an
        // IsRestartRequired flag which, once set, makes Playnite offer the
        // restart when settings are saved. Not in the SDK, so it is reached by
        // reflection - the same route UniPlaySong uses. Doing it Playnite's way
        // rather than relaunching the process ourselves keeps its shutdown, and
        // its database flush, intact.
        //
        // Returns true when the reset actually ran, so the caller knows whether
        // to raise that flag.
        public bool ResetLibrary()
        {
            string warning = Loc.Get("LOCImageRotaterResetWarning");

            if (PlayniteApi.Dialogs.ShowMessage(
                    warning,
                    Loc.Get("LOCImageRotaterResetTitle"),
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning)
                != System.Windows.MessageBoxResult.Yes)
            {
                return false;
            }

            LibraryReset.Result result = null;
            Exception failure = null;

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    try
                    {
                        progress.ProgressMaxValue = 1;
                        progress.CurrentProgressValue = 0;
                        progress.Text = Loc.Get("LOCImageRotaterResetRestoringProgress");

                        result = new LibraryReset(_writer, _store).Run((phase, done, total) =>
                        {
                            progress.ProgressMaxValue = Math.Max(1, total);
                            progress.CurrentProgressValue = done;
                            progress.Text = phase + " " + Loc.Format("LOCImageRotaterProgressItems", done, total);
                        });
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                },
                new GlobalProgressOptions(Loc.Get("LOCImageRotaterResetProgress"), false)
                {
                    IsIndeterminate = false
                });

            if (failure != null)
            {
                Logger.Error(failure, "ImageRotater: library reset failed");

                PlayniteApi.Dialogs.ShowErrorMessage(
                    Loc.Format("LOCImageRotaterResetFailed", failure.Message), "ImageRotater");

                return false;
            }

            if (!result.Success)
            {
                // Restore failed, so nothing was deleted and the session is
                // still consistent. No restart needed.
                PlayniteApi.Dialogs.ShowErrorMessage(result.Error, "ImageRotater");
                return false;
            }

            string summary = Loc.Format(
                "LOCImageRotaterResetDone", result.GamesRestored, result.FoldersDeleted);

            if (result.FoldersFailed > 0)
            {
                summary += Loc.Format(
                    "LOCImageRotaterResetFoldersFailed", result.FoldersFailed);
            }

            // Stops the rotation service handing out picks that point at files
            // this method just deleted.
            _rotationService?.ForgetAll();

            PlayniteApi.Dialogs.ShowMessage(
                summary + Loc.Get("LOCImageRotaterRestartNotice"),
                "ImageRotater");

            return true;
        }
    }
}
