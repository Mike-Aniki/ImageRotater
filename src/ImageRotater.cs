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
        private NativeFadeImageHost _nativeFadeImageHost;
        private NativeCoverHost _nativeCoverHost;
        private NativeLogoHost _nativeLogoHost;
        private Game _nativeSelectedGame;
        private bool _nativeHostUpdateQueued;
        private bool _nativeHostsStopping;

        private void SyncNativeFadeImageHost(Game selected)
        {
            _nativeSelectedGame = selected;
            if (_nativeHostsStopping || _nativeHostUpdateQueued) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            _nativeHostUpdateQueued = true;
            dispatcher.BeginInvoke(new Action(() =>
            {
                _nativeHostUpdateQueued = false;
                if (!_nativeHostsStopping) ApplyNativeHosts(_nativeSelectedGame);
            }), System.Windows.Threading.DispatcherPriority.DataBind);
        }

        // Desktop only: Fullscreen has no such setting and keeps its behaviour.
        private Func<bool> WindowBackgroundSetting()
        {
            if (PlayniteApi.ApplicationInfo.Mode != ApplicationMode.Desktop) return () => true;
            PlayniteWindowBackgroundSetting.SetConfigurationDirectory(PlayniteApi.Paths.ConfigurationPath);
            return PlayniteWindowBackgroundSetting.IsEnabled;
        }

        private void ApplyNativeHosts(Game selected)
        {
            try
            {
                if (_nativeFadeImageHost == null && Settings?.UseExperimentalFadeImage == true)
                    _nativeFadeImageHost = new NativeFadeImageHost(() => Settings,
                        () => new BackgroundImageControl(_imageSource, _selector, _loader,
                            () => Settings, _fileLogger,
                            imageId => PlayniteApi.Database.GetFullFilePath(imageId),
                            () => PlayniteApi.MainView.FilteredGames, nativeInjected: true),
                        () => Application.Current?.MainWindow,
                        WindowBackgroundSetting());
                _nativeFadeImageHost?.Update(selected);
            }
            catch (Exception ex) { Logger.Warn(ex, "ImageRotater: native background selection update failed"); }
            try
            {
                if (_nativeCoverHost == null && Settings?.UseExperimentalNativeCovers == true)
                    _nativeCoverHost = new NativeCoverHost(() => Settings,
                        () => new CoverImageControl(_coverSource, _selector, () => Settings, PlayniteApi, nativeInjected: true, fileLogger: _fileLogger),
                        id => PlayniteApi.Database.Games.Get(id),
                        game => (_coverSource as FolderImageSource)?.HasArtwork(game) ?? _coverSource.GetImagePaths(game).Count > 0,
                        () => Application.Current?.MainWindow);
                _nativeCoverHost?.Update(selected);
            }
            catch (Exception ex) { Logger.Warn(ex, "ImageRotater: native cover selection update failed"); }
            try
            {
                if (_nativeLogoHost == null && Settings?.UseExperimentalNativeLogos == true)
                    _nativeLogoHost = new NativeLogoHost(() => Settings,
                        () => new LogoImageControl(_logoSource, _selector, _loader, () => Settings, nativeInjected: true, fileLogger: _fileLogger),
                        id => PlayniteApi.Database.Games.Get(id), () => Application.Current?.MainWindow,
                        game => _packs.IsEnabled(game.Id) || _store.HasAnyImage(game.Id, ArtworkKind.Logo));
                _nativeLogoHost?.Update(selected);
            }
            catch (Exception ex) { Logger.Warn(ex, "ImageRotater: native logo selection update failed"); }
        }

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
        private readonly ArtworkPackService _packs;
        private readonly SessionSelectionCache _packCache;
        private readonly GameImageStore _store;
        private readonly ImageMenuHandler _menuHandler;
        private readonly ISteamGridDbClient _steamGridDb;
        private readonly ArtworkDownloader _downloader;
        private readonly IBackgroundImageSource _imageSource;
        private readonly IBackgroundImageSource _coverSource;
        private readonly IBackgroundImageSource _logoSource;
        private readonly ExtraMetadataLogoSource _extraMetadataLogos;
        private readonly PlayniteBackgroundWriter _writer;
        private readonly OriginalArtPreserver _preserver;
        private readonly BackgroundRotationService _rotationService;
        private readonly FileLogger _fileLogger;

        // Slideshow state: the game currently selected, and when each kind is
        // next due. One coarse timer serves both kinds - sub-second precision
        // is meaningless for something that ticks in tens of seconds.
        private System.Windows.Threading.DispatcherTimer _slideshowTimer;
        private Game _slideshowGame;

        private DateTime _backgroundDue = DateTime.MaxValue;
        private DateTime _coverDue = DateTime.MaxValue;

        private const int MinimumSlideshowSeconds = 1;

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
            Transition.Enabled = Settings?.EnableTransitions != false;
            Transition.CoverStyle = Transition.CoverStyleForSettings(
                Settings?.CoverTransition ?? TransitionStyle.Crossfade, Settings?.ShowUnfocusedCovers == true);
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

            // The writer is handed over so the preserver can recognise artwork
            // this plugin wrote and leave it alone.
            _preserver = new OriginalArtPreserver(api, _store, _writer);
            _extraMetadataLogos = new ExtraMetadataLogoSource(api.Paths.ConfigurationPath);
            _packCache = new SessionSelectionCache(System.IO.Path.Combine(GetPluginUserDataPath(), "Packs", "session-picks.json"));
            _packs = new ArtworkPackService(_store, () => Settings,
                (game, kind) => kind == ArtworkKind.Logo ? _extraMetadataLogos.GetLogoPath(game.Id) :
                    _preserver.ResolveOriginalPath(game, kind),
                _packCache);

            // Candidate sources expose one coherent pool to every selection mode:
            // plugin files + live virtual Original in Theme Integration, or
            // plugin files + preserved Original in Compatibility.
            _imageSource = new FolderImageSource(
                _store, ArtworkKind.Background, _preserver, () => Settings) { Packs = _packs };
            _coverSource = new FolderImageSource(
                _store, ArtworkKind.Cover, _preserver, () => Settings) { Packs = _packs };
            _logoSource = new FolderImageSource(
                _store, ArtworkKind.Logo, null, () => Settings, _extraMetadataLogos) { Packs = _packs };

            _rotationService = new BackgroundRotationService(
                _imageSource, _coverSource, _selector, _store, () => Settings, _fileLogger);

            // Adding or removing images changes what a game can rotate to, so
            // the rotation service must drop its once-per-game guard for it -
            // otherwise new artwork would not appear on the game currently
            // selected until the user navigated away and back.
            _menuHandler = new ImageMenuHandler(
                api, _store, _sessionCache, _steamGridDb, _downloader, _preserver,
                () => Settings,
                gameId =>
                {
                    _rotationService.Forget(gameId);
                    LogoImageControl.NotifyArtworkChanged(gameId);
                    if (_slideshowGame != null && _slideshowGame.Id == gameId)
                    {
                        UpdateHasDataLogo(_slideshowGame);
                    }
                },
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
                ElementList = new List<string> { "Background", "Cover", "Logo" }
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


        private void HandleGameSelected(OnGameSelectedEventArgs args)
        {
            Game selected = args?.NewValue?.FirstOrDefault();

            _packs.SelectGame(selected);
            if (selected != null && _packs.IsEnabled(selected.Id)) ApplyCurrentPack(selected);

            if (selected != null)
            {
                // Tell themes whether this game has a plugin cover to show, so
                // a tile only collapses its native cover when we can replace
                // it. Without this, games the user never set up would render
                // blank.
                UpdateHasDataCover(selected);
                UpdateHasDataLogo(selected);

                // Avoid formatting selection diagnostics unless debug logging is enabled.
                if (_fileLogger.IsEnabled)
                {
                    _fileLogger.Log(
                        $"selected \"{selected.Name}\" ({selected.Id}) hasDataCover={Settings?.HasDataCover}");
                }

            }

            // Notify the injected covers after the selected game has changed.
            CoverImageControl.NotifySelectionChanged(selected?.Id ?? Guid.Empty);

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
            SyncNativeFadeImageHost(selected);

            if (changed || _slideshowTimer == null)
            {
                ScheduleSlideshow();
            }
        }

        private void ApplyCurrentPack(Game game)
        {
            _rotationService.Forget(game.Id);
            _rotationService.ApplyTo(game);
            UpdateHasDataCover(game);
            UpdateHasDataLogo(game);
            BackgroundImageControl.NotifyBackgroundRotated(game.Id);
            CoverImageControl.NotifyArtworkRotated(game.Id);
            LogoImageControl.NotifyArtworkChanged(game.Id);
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

            if (_slideshowGame != null && _packs.IsEnabled(_slideshowGame.Id))
            {
                var config = _packs.Configuration(_slideshowGame.Id);
                bg = Settings.PackSelectionMode == SelectionMode.Slideshow && config.Packs.Count(p => p != null && !p.Excluded) > 1
                    ? Settings.PackSlideshowSeconds : 0;
                cover = 0;
            }

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
                if (game != null && _packs.IsEnabled(game.Id)) return;
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

                if (_packs.IsEnabled(game.Id))
                {
                    if (now >= _backgroundDue)
                    {
                        _packs.Advance(game.Id);
                        ApplyCurrentPack(game);
                        _backgroundDue = now.AddSeconds(Math.Max(MinimumSlideshowSeconds,
                            Settings.PackSlideshowSeconds));
                    }
                    return;
                }

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
                        _rotationService.ApplyNext(game, ArtworkKind.Cover);
                        CoverImageControl.NotifyArtworkRotated(game.Id);
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

        // Legacy theme hosts must stand down; injected adapters own these slots.
        // No directory scan is needed merely to update an old visibility flag.
        private void UpdateHasDataCover(Game game) { if (Settings != null) Settings.HasDataCover = false; }
        private void UpdateHasDataLogo(Game game) { if (Settings != null) Settings.HasDataLogo = false; }
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

                if (args.Name == "Logo")
                {
                    return new LogoImageControl(_logoSource, _selector, _loader, () => Settings, fileLogger: _fileLogger);
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
            // Use Playnite's own themed message box in Fullscreen. The active
            // theme (or Playnite's built-in Fullscreen theme) supplies the
            // MessageBoxBorder, caption, body and gamepad-friendly buttons.
            // Keep our existing custom dialog untouched in Desktop mode.
            if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
            {
                string message = Loc.Get("LOCImageRotaterBackgroundChangerConflictMessage")
                    + Environment.NewLine + Environment.NewLine
                    + Loc.Get("LOCImageRotaterBackgroundChangerConflictHint");

                PlayniteApi.Dialogs.ShowMessage(
                    message,
                    Loc.Get("LOCImageRotaterBackgroundChangerConflictTitle"),
                    MessageBoxImage.Warning,
                    new List<MessageBoxOption>
                    {
                        new MessageBoxOption(
                            Loc.Get("LOCImageRotaterBackgroundChangerConflictAcknowledge"),
                            isDefault: true,
                            isCancel: true)
                    });
                return;
            }

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
                    _navigationWindow.StateChanged -= NavigationWindow_StateChanged;
                    _navigationWindow.IsVisibleChanged -= NavigationWindow_IsVisibleChanged;
                }

                _navigationWindow = window;
                _navigationWindow.PreviewKeyDown += NavigationWindow_PreviewKeyDown;
                _navigationWindow.StateChanged += NavigationWindow_StateChanged;
                _navigationWindow.IsVisibleChanged += NavigationWindow_IsVisibleChanged;
                NavigationWindow_StateChanged(window, EventArgs.Empty);
            }
            catch
            {
                // Direction tracking is cosmetic only. Rotation must never fail
                // because a window was unavailable during startup.
            }
        }

        private void NavigationWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            NavigationWindow_StateChanged(sender, EventArgs.Empty);
        }

        private void NavigationWindow_StateChanged(object sender, EventArgs e)
        {
            Window window = sender as Window;
            if (window == null || _loader == null) return;
            bool minimized = window.WindowState == WindowState.Minimized || !window.IsVisible;
            if (minimized == _loader.IsSuspended) return;

            long cachedBytes = _cache.CurrentBytes;
            if (minimized) _loader.Suspend();
            else _loader.Resume();

            // Release hosted media too: clearing only the LRU would leave
            // active WPF images and decoders holding the same resources.
            BackgroundImageControl.NotifyPlaybackPolicyChanged();
            CoverImageControl.NotifyPlaybackPolicyChanged();
            LogoImageControl.NotifySettingsChanged();
            _nativeCoverHost?.Update(_slideshowGame);
            _nativeLogoHost?.Update(_slideshowGame);
            if (_fileLogger?.IsEnabled == true)
                _fileLogger.Log($"MEM window minimized={minimized} cache-before={cachedBytes} cache-after={_cache.CurrentBytes}");
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

        public override void OnFullscreenViewChanged(OnFullscreenViewChangedArgs args)
        {
            base.OnFullscreenViewChanged(args);
            SyncNativeFadeImageHost(PlayniteApi.MainView.SelectedGames?.FirstOrDefault());
        }
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            SyncNativeFadeImageHost(PlayniteApi.MainView.SelectedGames?.FirstOrDefault());
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
                _fileLogger.Log("Render path=NativeInjection");
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
            if (Settings?.UsesDirectRendering(ArtworkKind.Background) == true ||
                Settings?.UsesDirectRendering(ArtworkKind.Cover) == true)
            {
                try
                {
                    var restoreTimer = System.Diagnostics.Stopwatch.StartNew();
                    int restored = Settings.UsesDirectRendering(ArtworkKind.Background) ? _writer.RestoreKind(ArtworkKind.Background) : 0;
                    if (Settings.UsesDirectRendering(ArtworkKind.Cover)) restored += _writer.RestoreKind(ArtworkKind.Cover);
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

            startupTotal.Stop();
            if (_fileLogger != null && _fileLogger.IsEnabled)
            {
                _fileLogger.Log(
                    $"PERF startup-total total={startupTotal.ElapsedMilliseconds}ms "
                    + $"mode={PlayniteApi.ApplicationInfo.Mode} games={PlayniteApi.Database.Games.Count}");
            }

        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            _nativeHostsStopping = true;
            _nativeSelectedGame = null;
            _nativeFadeImageHost?.Dispose();
            _nativeFadeImageHost = null;
            _nativeCoverHost?.Dispose();
            _nativeLogoHost?.Dispose();
            _nativeLogoHost = null;
            _nativeCoverHost = null;

            if (_navigationWindow != null)
            {
                _navigationWindow.PreviewKeyDown -= NavigationWindow_PreviewKeyDown;
                _navigationWindow.StateChanged -= NavigationWindow_StateChanged;
                _navigationWindow.IsVisibleChanged -= NavigationWindow_IsVisibleChanged;
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

            _slideshowGame = null;

            // Persist only Session-mode choices once, at clean shutdown. This
            // lets the next Playnite launch avoid reopening on the exact same
            // cover/background without adding disk I/O to normal navigation.
            int savedSessionPicks = _sessionCache != null ? _sessionCache.SaveSessionChoices() : 0;
            _packCache.SaveSessionChoices();
            _packCache.SaveDailyChoices();
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
            Settings?.NotifyPacksChanged();
            BackgroundImageControl.NotifyPlaybackPolicyChanged();
            CoverImageControl.NotifyPlaybackPolicyChanged();
            if (Settings != null && _writer != null)
            {
                // Switching to theme integration must immediately put the user's
                // native Playnite artwork back. From this point on the plugin
                // controls render ImageRotater files directly and database
                // artwork updates are no longer part of the display path.
                if (Settings.UsesDirectRendering(ArtworkKind.Background) || !Settings.EnableRotation || !Settings.RotateBackgrounds)
                {
                    _writer.RestoreKind(ArtworkKind.Background);
                }

                if (Settings.UsesDirectRendering(ArtworkKind.Cover) || !Settings.EnableRotation || !Settings.RotateCovers)
                {
                    _writer.RestoreKind(ArtworkKind.Cover);
                }

                if (Settings.UsesDirectRendering(ArtworkKind.Cover))
                {
                    Settings.CurrentCoverPath = string.Empty;
                    Settings.CurrentCoverGameId = string.Empty;
                    Settings.CurrentCoverIsVideo = false;
                }
            }

            _rotationService?.ForgetAll();
            if (_slideshowGame != null)
            {
                _packs.Forget(_slideshowGame.Id);
                if (_packs.IsEnabled(_slideshowGame.Id)) ApplyCurrentPack(_slideshowGame);
                else
                {
                    UpdateHasDataCover(_slideshowGame);
                    BackgroundImageControl.NotifyBackgroundRotated(_slideshowGame.Id);
                    CoverImageControl.NotifyArtworkRotated(_slideshowGame.Id);
                }
            }
            LogoImageControl.NotifySettingsChanged();
            if (_slideshowGame != null)
            {
                UpdateHasDataLogo(_slideshowGame);
            }

            SyncNativeFadeImageHost(_slideshowGame ?? PlayniteApi.MainView.SelectedGames?.FirstOrDefault());

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
            if (result.FoldersFailed == 0 && Settings?.ArtworkPacks != null)
            {
                foreach (Guid id in Settings.ArtworkPacks.Keys.ToList()) _packs.Forget(id);
                Settings.ArtworkPacks.Clear();
                Settings.NotifyPacksChanged();
                SavePluginSettings(Settings);
            }

            PlayniteApi.Dialogs.ShowMessage(
                summary + Loc.Get("LOCImageRotaterRestartNotice"),
                "ImageRotater");

            return true;
        }
    }
}
