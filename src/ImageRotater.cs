using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        // Slideshow state: the game currently selected, and when each kind is
        // next due. One coarse timer serves both kinds - sub-second precision
        // is meaningless for something that ticks in tens of seconds.
        private System.Windows.Threading.DispatcherTimer _slideshowTimer;
        private Game _slideshowGame;

        // Bumped on every selection. A queued background write compares the
        // value it captured against this one to tell whether the user has moved
        // on again since - see PlayniteBackgroundWriter.SelectionGeneration.
        private int _selectionGeneration;

        // Session cover choices are primed once at startup.
        private bool _sessionCoversPrimed;

        private DateTime _backgroundDue = DateTime.MaxValue;
        private DateTime _lastFadeRetime = DateTime.MinValue;
        private DateTime _coverDue = DateTime.MaxValue;

        // Below this, slideshow ticks become write churn - every tick imports
        // a file, updates the database and refreshes a tile.
        private const int MinimumSlideshowSeconds = 5;

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
            _sessionCache = new SessionSelectionCache();
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

            // The key is read through an accessor: a settings save replaces the
            // whole settings object, so a captured string would go stale and the
            // user's newly entered key would appear not to work.
            _steamGridDb = new SteamGridDbClient(() => Settings?.SteamGridDbApiKey);
            _downloader = new ArtworkDownloader(_steamGridDb, _store, _sessionCache);

            // Plugin-owned images first, then Playnite's own background as a
            // fallback, deduplicated. Game.BackgroundImage is a database ID, so
            // GetFullFilePath turns it into a real path.
            //
            // Must be built before the rotation service, which captures it.
            // Candidates come from the plugin's own folder and NOWHERE else.
            //
            // Playnite's store must never be a source. In write mode it is our
            // output: each rotation imports a new copy and deletes the previous
            // one. Offering that copy back as a candidate meant a rotation could
            // pick the very file the next write was about to delete - valid when
            // chosen, gone by the time it was used. That is the blank/stretched
            // artwork, and no amount of File.Exists checking can fix it, because
            // the race is between our own read and our own delete.
            //
            // The user's pre-existing art is not lost by this: OriginalArtPreserver
            // copies it into our folder on first touch, so it rotates as an
            // ordinary candidate that we own and never delete.
            _imageSource = new FolderImageSource(_store, ArtworkKind.Background);
            _coverSource = new FolderImageSource(_store, ArtworkKind.Cover);

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

            _writer.ScreenWidth = () =>
            {
                try
                {
                    return (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                }
                catch (Exception)
                {
                    return 0;
                }
            };
            // The writer is handed over so the preserver can recognise artwork
            // this plugin wrote and leave it alone.
            _preserver = new OriginalArtPreserver(api, _store, _writer);
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
                api, _store, _sessionCache, _steamGridDb, _downloader,
                () => Settings,
                gameId => _rotationService.Forget(gameId));

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

            // Also answer to the element names BackgroundChanger themes
            // already use, so such a theme works here unmodified.
            //
            // Unconditional. Playnite routes a name to whichever plugin claimed
            // it, so with both plugins enabled the winner depends on load
            // order - but the two cannot run together anyway, and the
            // documented requirement is to disable BackgroundChanger first. A
            // toggle for this only ever added a restart to the setup.
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "BackgroundChanger",
                ElementList = new List<string> { "PluginBackgroundImage", "PluginCoverImage" }
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

                if (timer.ElapsedMilliseconds > 100)
                {
                    _fileLogger.Log(
                        $"PERF selection total={timer.ElapsedMilliseconds}ms "
                        + $"fade={_selectionPhases[0]}ms background={_selectionPhases[1]}ms");
                }
            }
        }

        // Populated only while verbose diagnostics are enabled.
        private readonly long[] _selectionPhases = new long[2];

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
            if ((DateTime.UtcNow - _lastFadeRetime).TotalSeconds > 10)
            {
                _lastFadeRetime = DateTime.UtcNow;
                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _selectionPhases[0] = Timed(() => FadeImageTuner.Apply());
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

                // Session mode uses its startup-primed choice; other modes rotate on arrival.
                bool rotateCoverNow = Settings == null
                    || Settings.CoverSelectionMode != SelectionMode.Session
                    || !_sessionCoversPrimed;

                if (rotateCoverNow)
                {
                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        long coverMs = Timed(() => _rotationService.ApplyTo(selected, ArtworkKind.Cover));
                        if (coverMs > 100)
                        {
                            _fileLogger.Log(
                                $"PERF cover-rotation game=\"{selected.Name}\" total={coverMs}ms");
                        }
                    }
                    else
                    {
                        _rotationService.ApplyTo(selected, ArtworkKind.Cover);
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
            if (left != null && left.Id == _settledGameId)
            {
                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _selectionPhases[1] = Timed(() => _rotationService.ApplyTo(left, ArtworkKind.Background));
                }
                else
                {
                    _rotationService.ApplyTo(left, ArtworkKind.Background);
                }
            }

            _settledGameId = Guid.Empty;
            _settling = selected;

            if (selected != null)
            {
                SettleTimer.Stop();
                SettleTimer.Start();
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

        // Arms the per-kind due times and makes sure the timer only runs when
        // there is something to wait for - an idle timer ticking forever for a
        // feature that is off would be pure waste.
        private void ScheduleSlideshow()
        {
            int bg = Settings != null && Settings.EnableRotation && Settings.RotateBackgrounds
                ? Settings.BackgroundSlideshowSeconds
                : 0;
            int cover = Settings != null && Settings.EnableRotation && Settings.RotateCovers
                ? Settings.CoverSlideshowSeconds
                : 0;

            _backgroundDue = bg >= MinimumSlideshowSeconds && _slideshowGame != null
                ? DateTime.UtcNow.AddSeconds(bg)
                : DateTime.MaxValue;

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
            if (_unfocused)
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
                if (main == null || !main.IsActive ||
                    main.WindowState == WindowState.Minimized)
                {
                    _unfocused = true;
                    return;
                }

                _unfocused = false;

                DateTime now = DateTime.UtcNow;

                // Ticks are skipped outright for games with fewer than two
                // images of the kind: ApplyNext would re-pick the same file
                // and the write would be skipped, but the cover fade would
                // still run - a pulse to the same picture every N seconds.
                if (now >= _backgroundDue)
                {
                    if (Settings != null && Settings.EnableRotation && Settings.RotateBackgrounds &&
                        _store.GetImagePaths(game.Id, ArtworkKind.Background).Count > 1)
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
                }

                if (now >= _coverDue)
                {
                    if (Settings != null && Settings.EnableRotation && Settings.RotateCovers &&
                        _store.GetImagePaths(game.Id, ArtworkKind.Cover).Count > 1)
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
                        if (CoverImageControl.IsHostedByTheme)
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
            // Both plugins' element names arrive here. Playnite strips the
            // SourceName prefix, so a theme built for BackgroundChanger asks
            // for "PluginCoverImage" and one built for us asks for "Cover" -
            // the same control answers either.
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
                if (args.Name == "Background" || args.Name == "PluginBackgroundImage")
                {
                    return new BackgroundImageControl(
                        _imageSource, _selector, _loader, () => Settings, _fileLogger,
                        imageId => PlayniteApi.Database.GetFullFilePath(imageId));
                }

                if (args.Name == "Cover" || args.Name == "PluginCoverImage")
                {
                    // No ImageLoader: this control publishes a path and the XAML
                // binds it with IsAsync=True, so decoding never touches the
                // layout thread.
                return new CoverImageControl(_coverSource, _selector, () => Settings);
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

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new List<MainMenuItem>
            {
                new MainMenuItem
                {
                    MenuSection = "@ImageRotater",
                    Description = Loc.Get("LOCImageRotaterMenuOptimise"),
                    Action = a => OptimiseStoredImages()
                },
                new MainMenuItem
                {
                    MenuSection = "@ImageRotater",
                    Description = Loc.Get("LOCImageRotaterMenuConvertGifs"),
                    Action = a => ConvertStoredGifs()
                },
                new MainMenuItem
                {
                    MenuSection = "@ImageRotater",
                    Description = Loc.Get("LOCImageRotaterMenuRestoreOriginals"),
                    Action = a => RestoreOriginalBackgrounds()
                }
            };
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
            Func<ImageOptimizer, List<Guid>, Action<int, int>, ImageOptimizer.Result> job)
        {
            var optimizer = new ImageOptimizer(_store, _fileLogger);

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    List<Guid> ids = PlayniteApi.Database.Games.Select(g => g.Id).ToList();
                    progress.ProgressMaxValue = ids.Count;

                    ImageOptimizer.Result result = job(
                        optimizer,
                        ids,
                        (done, total) =>
                        {
                            if (!progress.CancelToken.IsCancellationRequested)
                            {
                                progress.CurrentProgressValue = done;
                            }
                        });

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
        private void OptimiseStoredImages()
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
                (optimizer, ids, report) => optimizer.OptimiseAll(ids, report));
        }

        // Undo for write mode. Write mode changes the user's library data, so
        // there has to be a way back that does not involve editing games by hand.
        private void RestoreOriginalBackgrounds()
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

            int restored = _writer.RestoreAll();
            _rotationService.ForgetAll();

            PlayniteApi.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterRestoredBackgrounds", restored),
                "ImageRotater");
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new ImageRotaterSettingsView();
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            Logger.Info($"ImageRotater loaded (mode: {PlayniteApi.ApplicationInfo.Mode})");

            _fileLogger.StartSession(
                typeof(ImageRotater).Assembly.GetName().Version.ToString(),
                PlayniteApi.ApplicationInfo.Mode.ToString(),
                Settings);

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

            // Playnite's own crossfade dips a quarter dark at the midpoint of
            // every background change - see FadeImageTuner. Retimed after the
            // window has built its template; Background priority queues this
            // behind that work rather than racing it.
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

            // Before any tile renders. Themes load these with OnLoad, which
            // throws FileNotFoundException on a missing file - inside
            // FullscreenTilePanel.MeasureOverride, which is fatal. Seeding
            // guarantees the file exists for every game that has artwork, so
            // the throw is impossible rather than merely unlikely.
            //
            // Time the synchronous startup seed for debug diagnostics and slow-start warnings.
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                int seeded = _publisher.SeedEveryGame(PlayniteApi.Database.Games);
                timer.Stop();

                string seedPerf =
                    $"PERF startup-seed total={timer.ElapsedMilliseconds}ms "
                    + $"games={PlayniteApi.Database.Games.Count} wrote={seeded}";

                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    _fileLogger.Log(seedPerf);
                }

                // Only report unusually slow startup work to the normal Playnite log.
                if (timer.ElapsedMilliseconds >= 1500)
                {
                    Logger.Warn("ImageRotater: slow " + seedPerf);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not seed published artwork files");
            }

            // Prime Session cover choices before the library is browsed.
            _sessionCoversPrimed = false;
            if (Settings != null && Settings.EnableRotation && Settings.RotateCovers
                && Settings.CoverSelectionMode == SelectionMode.Session)
            {
                try
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    int rememberedBefore = _sessionCache != null ? _sessionCache.Count : 0;

                    foreach (Game game in PlayniteApi.Database.Games)
                    {
                        try
                        {
                            // ApplyTo performs the plugin-artwork check internally.
                            _rotationService.ApplyTo(game, ArtworkKind.Cover);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, $"ImageRotater: could not prime session cover for \"{game.Name}\"");
                        }
                    }

                    timer.Stop();
                    _sessionCoversPrimed = true;

                    int primed = _sessionCache != null
                        ? Math.Max(0, _sessionCache.Count - rememberedBefore)
                        : 0;

                    string primePerf =
                        $"PERF session-cover-prime total={timer.ElapsedMilliseconds}ms "
                        + $"primed={primed} games={PlayniteApi.Database.Games.Count}";

                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _fileLogger.Log(primePerf);
                    }

                    if (timer.ElapsedMilliseconds >= 1500)
                    {
                        Logger.Warn("ImageRotater: slow " + primePerf);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "ImageRotater: could not prime session covers");
                }
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
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
                () => BulkConverter.GifsToMp4(_store).Summary);
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
                Loc.Get("LOCImageRotaterConvertingArtwork"),
                () => BulkConverter.RepairVideos(_store).Summary);
        }

        // Converts every JPEG the plugin holds to PNG.
        public void ConvertJpegsToPng()
        {
            RunBulkJob(
                Loc.Get("LOCImageRotaterConvertJpegsQuestion"),
                Loc.Get("LOCImageRotaterConvertingArtwork"),
                () => BulkConverter.JpegsToPng(_store).Summary);
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
                () => BackgroundChangerImporter.Import(bcRoot, known, _store).Summary);
        }

        // Confirm, run under Playnite's progress dialog, forget the rotation
        // caches, report. The job returns its own summary text.
        private void RunBulkJob(string question, string progressTitle, Func<string> job)
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

            // Through Playnite's progress dialog: a large library is minutes of
            // ffmpeg, and a frozen settings window looks like a hang.
            PlayniteApi.Dialogs.ActivateGlobalProgress(
                args =>
                {
                    args.ProgressMaxValue = 0;
                    summary = job();
                },
                new GlobalProgressOptions(progressTitle, false));

            if (summary == null)
            {
                return;
            }

            // The candidate lists have changed under every cached pick - files
            // renamed by a conversion, or new ones from an import - so anything
            // remembered from before is stale.
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
            int cleared;
            int orphans;
            int videos = 0;

            try
            {
                cleared = _writer.ClearDeadReferences();

                // Published copies for games whose artwork is gone. These
                // outlive their source, and a theme binding the leftover
                // 1x1 placeholder renders it as a black thumbnail.
                orphans = _store.RemoveOrphanedPublished();

                // Videos downloaded as DASH fragments. Windows renders those as
                // solid black while reporting them as playing, so they need
                // rewriting into a normal container.
                videos = BulkConverter.RepairVideos(_store).Converted;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not repair artwork references");

                PlayniteApi.Dialogs.ShowErrorMessage(
                    Loc.Format("LOCImageRotaterRepairArtworkFailed", ex.Message), "ImageRotater");

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
                if (!Settings.EnableRotation || !Settings.RotateBackgrounds)
                {
                    _writer.RestoreKind(ArtworkKind.Background);
                }

                if (!Settings.EnableRotation || !Settings.RotateCovers)
                {
                    _writer.RestoreKind(ArtworkKind.Cover);
                }
            }

            _rotationService?.ForgetAll();

            // Playnite's background control is tuned per instance to the
            // transition in force; a new choice has to reach the instances
            // already on screen.
            FadeImageTuner.Apply();

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

            LibraryReset.Result result;

            try
            {
                result = new LibraryReset(_writer, _store).Run();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: library reset failed");

                PlayniteApi.Dialogs.ShowErrorMessage(
                    Loc.Format("LOCImageRotaterResetFailed", ex.Message), "ImageRotater");

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
