using System;
using System.Collections.Generic;
using System.Windows;
using ImageRotater.Services;
using Newtonsoft.Json;
using Playnite.SDK;

namespace ImageRotater
{
    // When a game has several images, this decides how the shown one is chosen.
    public enum SelectionMode
    {
        Session,        // Pick once per Playnite session; stays put while browsing
        EverySelection, // Re-pick each time the game is selected
        Fixed           // Always the first image, alphabetically
    }

    public enum VideoStartMode
    {
        Beginning,
        Random
    }

    public class ImageRotaterSettings : ObservableObject
    {
        private bool enableRotation = true;     // Master switch for the whole feature
        private bool enableDebugLogging = false; // Verbose log to ImageRotater.log
        private SelectionMode selectionMode = SelectionMode.Session;
        private bool rotateBackgrounds = true;
        private bool rotateCovers = false;
        private VideoStartMode coverVideoStartMode = VideoStartMode.Beginning;

        public bool RotateBackgrounds
        {
            get => rotateBackgrounds;
            set
            {
                rotateBackgrounds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EnableBackgroundImage));
            }
        }

        // Off by default: covers are usually curated deliberately.
        public bool RotateCovers
        {
            get => rotateCovers;
            set
            {
                rotateCovers = value;
                OnPropertyChanged();

                // EnableCoverImage is derived from this. Themes bind it to
                // decide whether to collapse their native cover, so without
                // this notification the tile would keep hiding (or showing)
                // the wrong element until something else refreshed it.
                OnPropertyChanged(nameof(EnableCoverImage));
            }
        }
        private bool hasDataCover = false;

        private string currentCoverPath = string.Empty;

        private string currentCoverGameId = string.Empty;
        private bool currentCoverIsVideo;

        private string imagesRoot = string.Empty;

        // On by default: it only changes anything for games that HAVE
        // screen-shaped images, and there it prevents a visible flash.
        // Off by default: a slideshow is a taste, and it costs a write (plus a
        // tile refresh for covers) per tick.
        private int backgroundSlideshowSeconds = 0;
        private int coverSlideshowSeconds = 0;

        // Session by default: a new cover per Playnite launch. This is the
        // behaviour users asked for by name, and constant cover reshuffling
        // makes a grid read as noise.
        private SelectionMode coverSelectionMode = SelectionMode.Session;

        // Off by default. Every animated tile decodes frames continuously on
        // the UI thread, and Playnite is a 32-bit process - a screenful of them
        // is the pressure that took it down when a theme put its own media
        // element in every tile.
        private bool animateUnfocusedCovers = false;

        // On by default: it only activates for picks that would otherwise
        // trigger the visible re-fit, and the fill is a soft wash of the image
        // itself rather than bars. Acquiring screen-shaped art in the first
        // place is still the better path - the shape bias steers rotation
        // there whenever a game has any.
        private bool letterboxBackgrounds = true;

        // On by default: it only changes anything for a game whose backgrounds
        // differ in resolution, and there it removes a visible glitch.
        //
        // Playnite blurs the window background with a fixed-radius effect
        // applied after the image is scaled, and decodes every background to
        // the screen width - so sources of different resolutions end up blurred
        // by visibly different amounts. Levelling the width makes consecutive
        // picks blur identically.
        private bool normaliseBackgroundSize = true;

        private string steamGridDbApiKey = string.Empty;

        // Explicit paths to external tools, empty meaning "search PATH".
        //
        // Neither is bundled: ffmpeg and yt-dlp are both GPL and this plugin is
        // MIT, so shipping either binary would relicense the project. A path
        // box is the difference between "install it somewhere the plugin
        // happens to look" and "point the plugin at the copy you already have".
        private string ffmpegPath = string.Empty;
        private string ytDlpPath = string.Empty;

        // deno.exe - not used by this plugin directly, but by yt-dlp.
        //
        // YouTube gates stream URLs behind nsig and PO-token challenges that
        // have to be solved by evaluating JavaScript. yt-dlp delegates that to
        // an external JS runtime rather than carrying an interpreter, so
        // without one it returns nothing for a YouTube search - quietly, with
        // exit code 0 and no error to show the user.
        //
        // Its folder is prepended to the yt-dlp process PATH rather than
        // passed as an argument, which is how yt-dlp expects to find it.
        private string denoPath = string.Empty;

        public bool NormaliseBackgroundSize
        {
            get => normaliseBackgroundSize;
            set { normaliseBackgroundSize = value; OnPropertyChanged(); }
        }

        // Read by themes as {PluginSettings Plugin=ImageRotater, Path=EnableCoverImage}
        // to decide whether to collapse their native cover element in favour of
        // ours. Named to match what BackgroundChanger themes already query, so
        // a theme author adds a branch rather than learning new vocabulary.
        //
        // Simply "are covers rotating". There used to be a separate toggle for
        // whether a theme's element was allowed to render covers, justified by
        // Fullscreen tiles holding a stale cover until rebuilt - Playnite 10.57
        // notifies them properly, so hosting the element is now purely the
        // theme's choice and needs no permission from a setting.
        public bool EnableBackgroundImage
        {
            get => enableRotation && rotateBackgrounds;
        }

        public bool EnableCoverImage
        {
            get => enableRotation && rotateCovers;
        }

        // True when the CURRENTLY SELECTED game has plugin-owned covers.
        //
        // Per-game, despite being a plain property: themes read it through
        // PluginSettings, which binds to the settings object, not to a game. It
        // is updated as the selection changes so a tile only hides its native
        // cover when we actually have something to show in its place -
        // otherwise games the user never set up would render blank.
        public bool HasDataCover
        {
            get => hasDataCover;
            set
            {
                if (hasDataCover == value)
                {
                    return;
                }

                hasDataCover = value;
                OnPropertyChanged();
            }
        }

        // The cover file the current rotation chose, as a full path.
        //
        // This is how Fullscreen grid tiles rotate. Those tiles bind Playnite's
        // native PART_ImageCover, which caches its decoded bitmap and ignores
        // later changes to Game.CoverImage - and hosting a plugin control in
        // every tile instead is not viable: dozens of controls each running an
        // async decode is what took Playnite down.
        //
        // So the theme reads a path instead, exactly as it already does for
        // UniPlaySong's now-playing art. No plugin control is involved, and
        // because each rotation picks a different FILE, the path string
        // changes too - so WPF cannot serve a stale bitmap from its URI cache.
        //
        // Global rather than per game, and that is deliberate: only the
        // selected tile shows it, which is the same scope Playnite's own
        // animated-cover themes use.
        public string CurrentCoverPath
        {
            get => currentCoverPath;
            set
            {
                if (string.Equals(currentCoverPath, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                currentCoverPath = value;
                OnPropertyChanged();
            }
        }

        // True while the selected game's current cover pick is a video.
        //
        // A theme has to know this to decide whether to show its MediaElement,
        // and it cannot work it out from CurrentCoverPath: a WPF trigger
        // compares a binding to a LITERAL, so "does this path end in .mp4" is
        // not expressible in XAML. Publishing the answer as a bool is the one
        // form a DataTrigger can consume.
        //
        // Without it the MediaElement stays visible over PART_ImageCover for as
        // long as a video is published, and the still picks rotate invisibly
        // underneath - the tile appears frozen on one clip while the log shows
        // rotation working perfectly.
        public bool CurrentCoverIsVideo
        {
            get => currentCoverIsVideo;
            set
            {
                if (currentCoverIsVideo == value)
                {
                    return;
                }

                currentCoverIsVideo = value;
                OnPropertyChanged();
            }
        }

        // Id of the game CurrentCoverPath belongs to, as a string.
        //
        // Themes compare this against each tile's own game id so only that
        // tile swaps in the rotated cover. Keyboard focus does not work as the
        // signal - Fullscreen grid tiles never report IsKeyboardFocusWithin
        // (confirmed on a live grid), and GameListItem has no IsSelected at
        // all. Comparing ids is the one signal that is actually present in the
        // tile's own data.
        //
        // A string because theme XAML compares it to {Binding Id} through
        // MultiBinding, and Guid-to-string conversion there is not reliable.
        public string CurrentCoverGameId
        {
            get => currentCoverGameId;
            set
            {
                if (string.Equals(currentCoverGameId, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                currentCoverGameId = value;
                OnPropertyChanged();
            }
        }

        // Root of the plugin's per-game image folders, with no trailing slash.
        //
        // Themes join this with a game's own id in XAML to reach that game's
        // current artwork:
        //   {ImagesRoot}\{Id}\covers\current.tile
        //
        // This is what makes a grid work. The other published values describe
        // only the selected game, so a tile binding them would show that one
        // game's cover across the whole grid; building a path from the tile's
        // own id keeps every tile independent, with no per-tile coordination
        // from the plugin and no converter in the theme.
        //
        // Set once at startup and never changed, so it needs no notification.
        public string ImagesRoot
        {
            get => imagesRoot;
            set { imagesRoot = value; OnPropertyChanged(); }
        }

        // Change the selected game's background every N seconds while it stays
        // selected. 0 = off. Backgrounds crossfade for free: Playnite's own
        // background element animates source changes.
        public int BackgroundSlideshowSeconds
        {
            get => backgroundSlideshowSeconds;
            set { backgroundSlideshowSeconds = value; OnPropertyChanged(); }
        }

        // Same, for the selected game's cover tile. Native tiles hard-swap -
        // only a theme-hosted ImageRotater_Cover element can fade.
        public int CoverSlideshowSeconds
        {
            get => coverSlideshowSeconds;
            set { coverSlideshowSeconds = value; OnPropertyChanged(); }
        }

        // How cover picks are chosen, independently of backgrounds.
        //
        // Split on user request: the common ask is backgrounds changing on
        // every selection while covers change only once per Playnite session
        // ("on startup") - covers are curated box art, and a grid that
        // reshuffles constantly reads as noise. Session mode IS the
        // once-per-startup behaviour.
        public SelectionMode CoverSelectionMode
        {
            get => coverSelectionMode;
            set { coverSelectionMode = value; OnPropertyChanged(); }
        }

        public VideoStartMode CoverVideoStartMode
        {
            get => coverVideoStartMode;
            set { coverVideoStartMode = value; OnPropertyChanged(); }
        }

        // Play animated covers on every tile, not just the selected one.
        //
        // BackgroundChanger does this and people prefer the look, so it is
        // offered - but off by default, because the cost is real. Each
        // animated tile decodes frames continuously on the UI thread, and
        // Playnite is a 32-bit process sharing its address space with
        // Chromium. A library where most games have video covers can exhaust
        // it while scrolling.
        //
        // Only affects tiles that are NOT selected; the selected one always
        // animates.
        public bool AnimateUnfocusedCovers
        {
            get => animateUnfocusedCovers;
            set { animateUnfocusedCovers = value; OnPropertyChanged(); }
        }

        // Letterbox odd-shaped backgrounds over a blurred fill of themselves.
        //
        // The rescue for what PreferScreenShape cannot reach: a game whose
        // images are ALL odd-shaped still needs its stored background to be
        // screen-shaped, or switching to it re-fits the layout visibly. The
        // composite is written to a cache; source files are never modified.
        public bool LetterboxBackgrounds
        {
            get => letterboxBackgrounds;
            set { letterboxBackgrounds = value; OnPropertyChanged(); }
        }

        // Personal SteamGridDB API key, pasted by the user. Stored as plain
        // text in Playnite's settings file: the token is read-only artwork
        // scope, free, and user-revocable, so encrypting it at rest would cost
        // portability (roaming profiles) for little real protection. It is
        // never written to the log.
        public string SteamGridDbApiKey
        {
            get => steamGridDbApiKey;
            set { steamGridDbApiKey = value; OnPropertyChanged(); }
        }

        // Full path to ffmpeg.exe, or empty to search PATH.
        //
        // Converts GIFs to MP4, and is what turns a yt-dlp download into
        // something the plugin can play. Without it those features are simply
        // unavailable and say so.
        public string FfmpegPath
        {
            get => ffmpegPath;
            set { ffmpegPath = value; OnPropertyChanged(); }
        }

        // Full path to yt-dlp.exe, or empty to search PATH.
        //
        // Imports video from YouTube and similar as animated artwork. Needs
        // ffmpeg too - yt-dlp fetches, ffmpeg converts.
        public string YtDlpPath
        {
            get => ytDlpPath;
            set { ytDlpPath = value; OnPropertyChanged(); }
        }

        public string DenoPath
        {
            get => denoPath;
            set { denoPath = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool EnableRotation
        {
            get => enableRotation;
            set
            {
                enableRotation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EnableBackgroundImage));
                OnPropertyChanged(nameof(EnableCoverImage));
            }
        }

        public SelectionMode SelectionMode
        {
            get => selectionMode;
            set { selectionMode = value; OnPropertyChanged(); }
        }

        // How one still replaces another, Desktop and Fullscreen alike. Read
        // by the renderers through Transition. Separate for covers and
        // backgrounds: a flash that is a beat on a tile is a strobe across
        // the whole screen.
        private TransitionStyle coverTransition = TransitionStyle.Crossfade;
        private TransitionStyle backgroundTransition = TransitionStyle.Crossfade;

        public TransitionStyle CoverTransition
        {
            get => coverTransition;
            set { coverTransition = value; OnPropertyChanged(); }
        }

        public TransitionStyle BackgroundTransition
        {
            get => backgroundTransition;
            set { backgroundTransition = value; OnPropertyChanged(); }
        }

        public bool EnableDebugLogging
        {
            get => enableDebugLogging;
            set { enableDebugLogging = value; OnPropertyChanged(); }
        }
    }

    public class ImageRotaterSettingsViewModel : ObservableObject, ISettings
    {
        private readonly ImageRotater plugin;
        private ImageRotaterSettings editingClone;
        private ImageRotaterSettings settings;

        public ImageRotaterSettings Settings
        {
            get => settings;
            set
            {
                // Watch the new object and stop watching the old one. Assigning
                // Settings is how CancelEdit restores the snapshot, so without
                // the swap the status line would keep reporting on a discarded
                // object - and each cancel would leak another subscription.
                if (settings != null)
                {
                    settings.PropertyChanged -= OnSettingChanged;
                }

                settings = value;

                if (settings != null)
                {
                    settings.PropertyChanged += OnSettingChanged;
                }

                OnPropertyChanged();
                UpdateApiKeyStatus();
            }
        }

        private void OnSettingChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImageRotaterSettings.SteamGridDbApiKey))
            {
                UpdateApiKeyStatus();
                return;
            }

            // A path can be typed as well as browsed, and a typed one would
            // otherwise keep the status from whatever was in the box when the
            // page opened. Cheap to re-probe: ToolProbe caches by path and
            // mtime, so this only shells out when the path actually resolves
            // somewhere new.
            if (e.PropertyName == nameof(ImageRotaterSettings.FfmpegPath)
                || e.PropertyName == nameof(ImageRotaterSettings.YtDlpPath)
                || e.PropertyName == nameof(ImageRotaterSettings.DenoPath))
            {
                UpdateToolStatus();
            }
        }

        public ImageRotaterSettingsViewModel(ImageRotater plugin)
        {
            this.plugin = plugin;

            var saved = plugin.LoadPluginSettings<ImageRotaterSettings>();
            Settings = saved ?? new ImageRotaterSettings();
        }

        // External tool status, following the pattern FullVid and UniPlaySong
        // already use: the view binds these, rather than code-behind reaching
        // into TextBlocks and setting their text and colour by hand.
        private readonly Services.ToolProbe _probe = new Services.ToolProbe();

        private SetupStatus _ffmpegStatus = SetupStatus.Neutral(string.Empty);
        private SetupStatus _ytDlpStatus = SetupStatus.Neutral(string.Empty);
        private SetupStatus _denoStatus = SetupStatus.Neutral(string.Empty);
        private SetupStatus _apiKeyStatus = SetupStatus.Neutral(string.Empty);

        public SetupStatus FfmpegStatus
        {
            get => _ffmpegStatus;
            set { _ffmpegStatus = value; OnPropertyChanged(); }
        }

        public SetupStatus YtDlpStatus
        {
            get => _ytDlpStatus;
            set { _ytDlpStatus = value; OnPropertyChanged(); }
        }

        public SetupStatus DenoStatus
        {
            get => _denoStatus;
            set { _denoStatus = value; OnPropertyChanged(); }
        }

        public SetupStatus ApiKeyStatus
        {
            get => _apiKeyStatus;
            set { _apiKeyStatus = value; OnPropertyChanged(); }
        }

        // Reports on the key's SHAPE as it is typed. Whether the key actually
        // works is the server's answer, and the search dialog already relays
        // it - checking here would mean a network call on every keystroke.
        private void UpdateApiKeyStatus()
        {
            string key = Settings?.SteamGridDbApiKey;

            if (string.IsNullOrWhiteSpace(key))
            {
                // No tick and no cross: absent is the default, not a failure.
                ApiKeyStatus = SetupStatus.Neutral(Loc.Get("LOCImageRotaterNoKey"));
                return;
            }

            string problem = Services.SettingsValidator.CheckApiKey(key);

            ApiKeyStatus = problem != null
                ? SetupStatus.Problem(problem)
                : SetupStatus.Ok(Loc.Get("LOCImageRotaterKeyLooksRight"));
        }

        // Clears every image the plugin holds and restores each game's own
        // artwork. Destructive, so ResetLibrary confirms before doing anything.
        //
        // The command parameter is the button, which is how the restart prompt
        // below finds the settings window.
        public RelayCommand<object> ImportBackgroundChanger => new RelayCommand<object>(a =>
        {
            plugin?.ImportFromBackgroundChanger();
        });

        public RelayCommand<object> ConvertGifs => new RelayCommand<object>(a =>
        {
            plugin?.ConvertGifsToMp4();
        });

        public RelayCommand<object> ConvertJpegs => new RelayCommand<object>(a =>
        {
            plugin?.ConvertJpegsToPng();
        });

        public RelayCommand<object> RepairVideos => new RelayCommand<object>(a =>
        {
            plugin?.RepairAllVideos();
        });

        // Clears references to plugin artwork that no longer exists, which
        // Playnite otherwise renders as solid black tiles. Deletes nothing.
        public RelayCommand<object> RepairArtwork => new RelayCommand<object>(a =>
        {
            if (plugin == null || !plugin.RepairArtworkReferences())
            {
                return;
            }

            RequestRestart(a as FrameworkElement);
        });

        public RelayCommand<object> ResetLibrary => new RelayCommand<object>(a =>
        {
            if (plugin == null || !plugin.ResetLibrary())
            {
                return;
            }

            RequestRestart(a as FrameworkElement);
        });

        // Asks Playnite to offer a restart when these settings are saved.
        //
        // Its settings window carries an IsRestartRequired flag that does
        // exactly this. It is not on any SDK interface, so it is reached by
        // reflection off the window's DataContext - the same route UniPlaySong
        // uses. Going through Playnite rather than relaunching the process
        // ourselves keeps its own shutdown, and its database flush, intact.
        private static void RequestRestart(FrameworkElement source)
        {
            try
            {
                Window window = source == null ? null : Window.GetWindow(source);

                object context = window?.DataContext;

                if (context == null)
                {
                    return;
                }

                System.Reflection.PropertyInfo flag =
                    context.GetType().GetProperty("IsRestartRequired");

                if (flag != null && flag.CanWrite)
                {
                    flag.SetValue(context, true);
                }
            }
            catch (Exception ex)
            {
                // Playnite may rename or drop the property. The reset itself
                // has already succeeded either way - the user just has to
                // restart on their own.
                LogManager.GetLogger().Warn(
                    ex, "ImageRotater: could not ask Playnite to restart");
            }
        }

        public RelayCommand<object> BrowseFfmpeg => new RelayCommand<object>(a =>
        {
            string path = plugin?.PlayniteApi?.Dialogs?.SelectFile(
                "ffmpeg|ffmpeg.exe|Executable|*.exe");

            if (!string.IsNullOrWhiteSpace(path))
            {
                Settings.FfmpegPath = path;
                UpdateToolStatus();
            }
        });

        public RelayCommand<object> BrowseDeno => new RelayCommand<object>(a =>
        {
            string path = plugin?.PlayniteApi?.Dialogs?.SelectFile(
                "deno|deno.exe|Executable|*.exe");

            if (!string.IsNullOrWhiteSpace(path))
            {
                Settings.DenoPath = path;
                UpdateToolStatus();
            }
        });

        public RelayCommand<object> BrowseYtDlp => new RelayCommand<object>(a =>
        {
            string path = plugin?.PlayniteApi?.Dialogs?.SelectFile(
                "yt-dlp|yt-dlp.exe|Executable|*.exe");

            if (!string.IsNullOrWhiteSpace(path))
            {
                Settings.YtDlpPath = path;
                UpdateToolStatus();
            }
        });

        // No "detect" button.
        //
        // It would have copied whatever was on PATH into the boxes, which is
        // work the plugin already does on its own: an empty path means "search
        // PATH", and the status line below each box reports what was found.
        // Filling the box with the same answer only makes the setting look
        // explicit when it is not - and a user who later moves the tool would
        // then have a stale path pinned rather than a search that follows it.

        // Re-probes both tools. Cheap to call - ToolProbe caches by path and
        // mtime, so reopening Settings does not re-shell.
        //
        // Probing RUNS each tool rather than checking the file exists: a
        // corrupt download or a wrong-architecture build passes File.Exists and
        // then fails at the moment the user wanted the feature.
        public void UpdateToolStatus()
        {
            // An empty box means "search PATH", so status reflects what would
            // actually be used rather than what was typed.
            string ffmpeg = Services.ExternalTool.Resolve(
                Settings.FfmpegPath, Services.ExternalTool.FfmpegExe);

            string ytDlp = Services.ExternalTool.Resolve(
                Settings.YtDlpPath, Services.ExternalTool.YtDlpExe);

            FfmpegStatus = DescribeTool(
                ffmpeg, Settings?.FfmpegPath, Services.ToolProbe.FfmpegVersionFlag, "ffmpeg");

            YtDlpStatus = DescribeTool(
                ytDlp, Settings?.YtDlpPath, Services.ToolProbe.YtDlpVersionFlag, "yt-dlp");

            string deno = Services.ExternalTool.Resolve(
                Settings?.DenoPath, Services.ExternalTool.DenoExe);

            DenoStatus = DescribeDeno(deno, Settings?.DenoPath, ytDlp);
        }

        // deno gets its own wording because its absence only matters once
        // yt-dlp is present: on its own it is a JS runtime the plugin never
        // calls. Saying "not found" next to an unconfigured yt-dlp would be
        // pointing at the second problem while the first is still open.
        private SetupStatus DescribeDeno(string resolved, string configured, string ytDlp)
        {
            const string Flag = Services.ToolProbe.YtDlpVersionFlag;

            if (_probe.Works(resolved, Flag))
            {
                bool onPath = string.IsNullOrWhiteSpace(configured);
                string version = _probe.Probe(resolved, Flag);

                return SetupStatus.Ok(onPath ? Loc.Format("LOCImageRotaterOnPath", version) : version);
            }

            if (!string.IsNullOrWhiteSpace(configured))
            {
                return SetupStatus.Problem(
                    Loc.Format("LOCImageRotaterBadToolPath", _probe.Probe(resolved, Flag), "deno"));
            }

            if (!_probe.Works(ytDlp, Services.ToolProbe.YtDlpVersionFlag))
            {
                return SetupStatus.Neutral(Loc.Get("LOCImageRotaterDenoOnlyNeeded"));
            }

            return SetupStatus.Neutral(Loc.Get("LOCImageRotaterDenoNotFound"));
        }

        // Turns a probe result into the line under the box.
        //
        // Three outcomes, not two. A tool that is simply absent gets no cross:
        // both are optional, and a red mark against a user who never wanted
        // YouTube import reads as something being broken. The cross is kept
        // for a path that was SET and does not work, which is a real mistake.
        private SetupStatus DescribeTool(
            string resolved, string configured, string versionFlag, string name)
        {
            string result = _probe.Probe(resolved, versionFlag);

            if (_probe.Works(resolved, versionFlag))
            {
                bool onPath = string.IsNullOrWhiteSpace(configured);

                return SetupStatus.Ok(onPath ? Loc.Format("LOCImageRotaterOnPath", result) : result);
            }

            if (string.IsNullOrWhiteSpace(configured))
            {
                return SetupStatus.Neutral(
                    Loc.Format("LOCImageRotaterToolNotFound", name));
            }

            return SetupStatus.Problem(Loc.Format("LOCImageRotaterBadToolPath", result, name));
        }

        // Snapshot for cancel. Deep clone via JSON so every property is covered
        // automatically as settings are added.
        public void BeginEdit()
        {
            // Probed on open, so the page tells the user what is available
            // before they go looking for a feature that is not.
            UpdateToolStatus();

            editingClone = JsonConvert.DeserializeObject<ImageRotaterSettings>(
                JsonConvert.SerializeObject(Settings));
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);

            // The converter reads a static path rather than the settings
            // object, so it has to be told when that path changes - otherwise
            // a user who just pointed the plugin at ffmpeg would have to
            // restart Playnite before anything used it.
            Services.GifConverter.ConfiguredPath = Settings?.FfmpegPath;
            Transition.CoverStyle = Settings?.CoverTransition ?? TransitionStyle.Crossfade;
            Transition.BackgroundStyle = Settings?.BackgroundTransition ?? TransitionStyle.Crossfade;

            // Toggled settings apply on the next selection, not whenever each
            // game happens to rotate again.
            plugin?.NotifySettingsSaved();
        }

        // Playnite calls this on Save. Returning false keeps the window open
        // and shows the errors, so this is the one place a bad value can
        // actually be refused rather than merely reported.
        //
        // The rules live in SettingsValidator, which knows nothing about the
        // UI and can be tested without one.
        public bool VerifySettings(out List<string> errors)
        {
            errors = Services.SettingsValidator.Validate(Settings);
            return errors.Count == 0;
        }
    }
}
