using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ImageRotater.Services;
using ImageRotater.Models;
using Newtonsoft.Json;
using Playnite.SDK;

namespace ImageRotater
{
    // When a game has several images, this decides how the shown one is chosen.
    public enum SelectionMode
    {
        Session,        // Pick once per Playnite session; stays put while browsing
        EverySelection, // Re-pick each time the game is selected
        Fixed,          // Always the fixed artwork
        Slideshow,      // Keep rotating while the game remains selected
        Daily,          // One artwork per calendar day
        ViewRefresh     // Re-pick when the library view/filter is rebuilt
    }

    // How the next artwork is chosen whenever a rotation mode needs a new one.
    public enum SelectionOrder
    {
        Random,      // Uniform random, avoiding the immediate previous artwork
        Sequential,  // Follow the order configured in the Artwork Manager
        Shuffle      // Random-looking cycle that uses every artwork before repeating
    }

    public enum VideoStartMode
    {
        Beginning,
        Random
    }

    public enum BackgroundDownloadResizePreset
    {
        FullHd1080p,
        Qhd1440p,
        Uhd4K
    }

    public class GameArtworkOverride
    {
        public SelectionMode? Mode { get; set; }
        public SelectionOrder? Order { get; set; }
    }

    public class ImageRotaterSettings : ObservableObject
    {
        private bool enableRotation = true;     // Master switch for the whole feature
        // Recommended/default rendering path. When enabled, ImageRotater leaves
        // Playnite's native artwork fields untouched and a compatible theme hosts
        // the plugin controls directly (same architecture as BackgroundChanger).
        // Disable it for maximum theme compatibility: the plugin then publishes
        // the selected artwork back through Playnite's database as before.
        private bool useThemeIntegration = true;
        private bool enableDebugLogging = false; // Verbose log to ImageRotater.log
        private DateTime? debugLoggingEnabledAtUtc;
        private SelectionMode selectionMode = SelectionMode.Session;
        private SelectionOrder backgroundSelectionOrder = SelectionOrder.Random;
        private bool rotateBackgrounds = true;
        private bool rotateCovers = false;
        private VideoStartMode coverVideoStartMode = VideoStartMode.Beginning;

        public bool UseThemeIntegration
        {
            get => useThemeIntegration;
            set
            {
                if (useThemeIntegration == value)
                {
                    return;
                }

                useThemeIntegration = value;
                OnPropertyChanged();
            }
        }

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
        private SelectionOrder coverSelectionOrder = SelectionOrder.Random;

        // Off by default. Every animated tile decodes frames continuously on
        // the UI thread, and Playnite is a 32-bit process - a screenful of them
        // is the pressure that took it down when a theme put its own media
        // element in every tile.
        private bool animateUnfocusedCovers = true;

        // Pause invisible work while a launched game owns the screen.
        private bool pauseAnimationsWhenGameRunning = true;
        private bool pauseSlideshowWhenUnfocused = true;

        // Legacy serialized values: older published versions exposed duration
        // sliders. Keep these fields/properties so existing settings deserialize,
        // but the runtime now ignores them and uses fixed per-style timing.
        private int backgroundTransitionDurationMs = 400;
        private int coverTransitionDurationMs = 400;

        // Optional per-game behaviour. Keys are "{gameId:N}|{kind}". Missing
        // values mean "use global settings" and therefore keep old installs small.
        private Dictionary<string, GameArtworkOverride> gameArtworkOverrides =
            new Dictionary<string, GameArtworkOverride>(StringComparer.OrdinalIgnoreCase);

        // Off by default: letterboxing creates an additional processed background.
        // It remains available for users who want unusual aspect ratios fitted
        // instead of cropped, but the default path should stay as lightweight
        // and close to Playnite native behaviour as possible.
        private bool letterboxBackgrounds = false;

        // Off by default: levelling background widths is optional visual polish for
        // Playnite's native blur, and it requires maintaining processed copies.
        // Users who want identical blur strength across mixed-resolution
        // backgrounds can opt in; ordinary rotation should have no preprocessing
        // cost by default.
        private bool normaliseBackgroundSize = false;

        // One-shot settings migration marker. Version 1 changes the historical
        // defaults for the two optional background preprocessing features to
        // OFF for existing installations as well. Once written, we never force
        // them again, so a user can explicitly turn either option back on.
        private int performanceDefaultsMigrationVersion = 0;

        // One-shot migration for the dedicated Slideshow selection mode. Older
        // versions stored slideshow timing separately from the selection mode.
        private int slideshowModeMigrationVersion = 0;

        private string steamGridDbApiKey = string.Empty;

        // Extra words automatically appended to the game name on the generic
        // Web Images tab. These improve relevance without hard-coding one
        // search style: users can change them, or leave them empty to search
        // the plain game title.
        private string webBackgroundSearchTerm = "wallpaper";
        private string webCoverSearchTerm = "cover";

        // Automatic pass for new still artwork, whether downloaded or added
        // manually. This uses the same conservative optimiser as the Library
        // tool: transparent images are preserved and files are only replaced
        // when the saving is meaningful. Enabled by default for new settings.
        private bool optimiseDownloadedImages = true;

        // Download-only background resizing. This is intentionally separate from
        // optimisation: resizing changes pixel dimensions, while optimisation only
        // tries to reduce file size. It is off by default and never affects covers.
        private bool resizeDownloadedBackgrounds = false;
        private BackgroundDownloadResizePreset downloadedBackgroundResizePreset =
            BackgroundDownloadResizePreset.FullHd1080p;


        // Separate preset for the one-shot Library resize action. Keeping it
        // independent from the download preset avoids changing future download
        // behaviour just because the user resized their existing library once.
        private BackgroundDownloadResizePreset storedBackgroundResizePreset =
            BackgroundDownloadResizePreset.FullHd1080p;

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

        public bool OptimiseDownloadedImages
        {
            get => optimiseDownloadedImages;
            set { optimiseDownloadedImages = value; OnPropertyChanged(); }
        }

        public bool ResizeDownloadedBackgrounds
        {
            get => resizeDownloadedBackgrounds;
            set { resizeDownloadedBackgrounds = value; OnPropertyChanged(); }
        }

        public BackgroundDownloadResizePreset DownloadedBackgroundResizePreset
        {
            get => downloadedBackgroundResizePreset;
            set { downloadedBackgroundResizePreset = value; OnPropertyChanged(); }
        }


        public BackgroundDownloadResizePreset StoredBackgroundResizePreset
        {
            get => storedBackgroundResizePreset;
            set { storedBackgroundResizePreset = value; OnPropertyChanged(); }
        }

        public string WebBackgroundSearchTerm
        {
            get => webBackgroundSearchTerm;
            set
            {
                webBackgroundSearchTerm = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string WebCoverSearchTerm
        {
            get => webCoverSearchTerm;
            set
            {
                webCoverSearchTerm = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        // Internal migration state persisted with the normal settings. This is
        // intentionally public for Newtonsoft/Playnite serialization, but it is
        // not exposed in the settings UI.
        public int PerformanceDefaultsMigrationVersion
        {
            get => performanceDefaultsMigrationVersion;
            set => performanceDefaultsMigrationVersion = value;
        }

        public int SlideshowModeMigrationVersion
        {
            get => slideshowModeMigrationVersion;
            set => slideshowModeMigrationVersion = value;
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

        // Interval used by the dedicated Slideshow mode. The mode itself is
        // what enables rotation; keeping the interval separate lets the user
        // switch away and back without losing their preferred timing.
        public int BackgroundSlideshowSeconds
        {
            get => backgroundSlideshowSeconds;
            set { backgroundSlideshowSeconds = value; OnPropertyChanged(); }
        }

        // Same for cover Slideshow mode. Native tiles hard-swap; a
        // theme-hosted ImageRotater_Cover element can animate the transition.
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
            set
            {
                coverSelectionMode = value;
                OnPropertyChanged();

                if (value == SelectionMode.Slideshow && coverSlideshowSeconds < 1)
                {
                    coverSlideshowSeconds = 10;
                    OnPropertyChanged(nameof(CoverSlideshowSeconds));
                }
            }
        }


        public SelectionOrder BackgroundSelectionOrder
        {
            get => backgroundSelectionOrder;
            set { backgroundSelectionOrder = value; OnPropertyChanged(); }
        }

        public SelectionOrder CoverSelectionOrder
        {
            get => coverSelectionOrder;
            set { coverSelectionOrder = value; OnPropertyChanged(); }
        }

        public bool PauseAnimationsWhenGameRunning
        {
            get => pauseAnimationsWhenGameRunning;
            set { pauseAnimationsWhenGameRunning = value; OnPropertyChanged(); }
        }

        public bool PauseSlideshowWhenUnfocused
        {
            get => pauseSlideshowWhenUnfocused;
            set { pauseSlideshowWhenUnfocused = value; OnPropertyChanged(); }
        }

        public int BackgroundTransitionDurationMs
        {
            get => backgroundTransitionDurationMs;
            set { backgroundTransitionDurationMs = Math.Max(100, Math.Min(2000, value)); OnPropertyChanged(); }
        }

        public int CoverTransitionDurationMs
        {
            get => coverTransitionDurationMs;
            set { coverTransitionDurationMs = Math.Max(100, Math.Min(2000, value)); OnPropertyChanged(); }
        }

        public Dictionary<string, GameArtworkOverride> GameArtworkOverrides
        {
            get => gameArtworkOverrides ?? (gameArtworkOverrides = new Dictionary<string, GameArtworkOverride>(StringComparer.OrdinalIgnoreCase));
            set => gameArtworkOverrides = value ?? new Dictionary<string, GameArtworkOverride>(StringComparer.OrdinalIgnoreCase);
        }

        private static string OverrideKey(Guid gameId, ArtworkKind kind)
        {
            return gameId.ToString("N") + "|" + (int)kind;
        }

        public SelectionMode GetSelectionMode(Guid gameId, ArtworkKind kind, SelectionMode fallback)
        {
            GameArtworkOverride value;
            SelectionMode mode = GameArtworkOverrides.TryGetValue(OverrideKey(gameId, kind), out value) && value != null && value.Mode.HasValue
                ? value.Mode.Value
                : fallback;

            // Experimental ViewRefresh was retired. Keep the enum value only so
            // settings written by the test build can still be loaded safely.
            return mode == SelectionMode.ViewRefresh ? SelectionMode.Session : mode;
        }

        public SelectionOrder GetSelectionOrder(Guid gameId, ArtworkKind kind, SelectionOrder fallback)
        {
            GameArtworkOverride value;
            return GameArtworkOverrides.TryGetValue(OverrideKey(gameId, kind), out value) && value != null && value.Order.HasValue
                ? value.Order.Value
                : fallback;
        }

        public SelectionMode? GetSelectionModeOverride(Guid gameId, ArtworkKind kind)
        {
            GameArtworkOverride value;
            return GameArtworkOverrides.TryGetValue(OverrideKey(gameId, kind), out value) && value != null ? value.Mode : null;
        }

        public SelectionOrder? GetSelectionOrderOverride(Guid gameId, ArtworkKind kind)
        {
            GameArtworkOverride value;
            return GameArtworkOverrides.TryGetValue(OverrideKey(gameId, kind), out value) && value != null ? value.Order : null;
        }

        public void SetSelectionModeOverride(Guid gameId, ArtworkKind kind, SelectionMode? value)
        {
            SetGameOverride(gameId, kind, value, GetSelectionOrderOverride(gameId, kind));
        }

        public void SetSelectionOrderOverride(Guid gameId, ArtworkKind kind, SelectionOrder? value)
        {
            SetGameOverride(gameId, kind, GetSelectionModeOverride(gameId, kind), value);
        }

        private void SetGameOverride(Guid gameId, ArtworkKind kind, SelectionMode? mode, SelectionOrder? order)
        {
            string key = OverrideKey(gameId, kind);
            if (!mode.HasValue && !order.HasValue)
            {
                GameArtworkOverrides.Remove(key);
            }
            else
            {
                GameArtworkOverrides[key] = new GameArtworkOverride { Mode = mode, Order = order };
            }
            OnPropertyChanged(nameof(GameArtworkOverrides));
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
            set
            {
                selectionMode = value;
                OnPropertyChanged();

                if (value == SelectionMode.Slideshow && backgroundSlideshowSeconds < 1)
                {
                    backgroundSlideshowSeconds = 10;
                    OnPropertyChanged(nameof(BackgroundSlideshowSeconds));
                }
            }
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

        public DateTime? DebugLoggingEnabledAtUtc
        {
            get => debugLoggingEnabledAtUtc;
            set => debugLoggingEnabledAtUtc = value;
        }

        public bool EnableDebugLogging
        {
            get => enableDebugLogging;
            set
            {
                if (enableDebugLogging == value)
                {
                    return;
                }

                enableDebugLogging = value;
                if (value && !debugLoggingEnabledAtUtc.HasValue)
                {
                    debugLoggingEnabledAtUtc = DateTime.UtcNow;
                }
                else if (!value)
                {
                    debugLoggingEnabledAtUtc = null;
                }

                OnPropertyChanged();
            }
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

            // Tool validation launches the external executables, so never do it
            // synchronously on the UI thread. Paths only live on the Tools page;
            // once that page has been opened, refresh in the background as they
            // change.
            if (e.PropertyName == nameof(ImageRotaterSettings.FfmpegPath)
                || e.PropertyName == nameof(ImageRotaterSettings.YtDlpPath)
                || e.PropertyName == nameof(ImageRotaterSettings.DenoPath))
            {
                if (_toolStatusLoaded)
                {
                    RefreshToolStatusAsync();
                }
            }
        }

        public ImageRotaterSettingsViewModel(ImageRotater plugin)
        {
            this.plugin = plugin;

            var saved = plugin.LoadPluginSettings<ImageRotaterSettings>();
            Settings = saved ?? new ImageRotaterSettings();

            bool settingsChangedByMigration = false;

            // Retire the experimental ViewRefresh mode cleanly. Session is the
            // closest stable behaviour to what the mode was meant to provide.
            if (Settings.SelectionMode == SelectionMode.ViewRefresh)
            {
                Settings.SelectionMode = SelectionMode.Session;
                settingsChangedByMigration = true;
            }

            if (Settings.CoverSelectionMode == SelectionMode.ViewRefresh)
            {
                Settings.CoverSelectionMode = SelectionMode.Session;
                settingsChangedByMigration = true;
            }

            foreach (var entry in Settings.GameArtworkOverrides.Values)
            {
                if (entry != null && entry.Mode == SelectionMode.ViewRefresh)
                {
                    entry.Mode = SelectionMode.Session;
                    settingsChangedByMigration = true;
                }
            }

            // Performance defaults migration v1. Older releases enabled both
            // background preprocessing features by default, so existing users
            // who never touched the settings would otherwise keep paying their
            // cost after updating. Force them OFF once during the update. The
            // marker is persisted immediately; after that, an explicit user
            // choice to turn either feature back ON is respected forever.
            if (Settings.PerformanceDefaultsMigrationVersion < 1)
            {
                Settings.LetterboxBackgrounds = false;
                Settings.NormaliseBackgroundSize = false;
                Settings.PerformanceDefaultsMigrationVersion = 1;
                settingsChangedByMigration = true;
            }

            // Slideshow mode migration v1. Previously the timer was an
            // independent option layered over Session / Every Selection /
            // Fixed. If an existing user had a real slideshow interval active,
            // preserve that intent by moving that artwork kind to the new
            // dedicated Slideshow mode.
            if (Settings.SlideshowModeMigrationVersion < 1)
            {
                if (Settings.BackgroundSlideshowSeconds >= 1)
                {
                    Settings.SelectionMode = SelectionMode.Slideshow;
                }

                if (Settings.CoverSlideshowSeconds >= 1)
                {
                    Settings.CoverSelectionMode = SelectionMode.Slideshow;
                }

                Settings.SlideshowModeMigrationVersion = 1;
                settingsChangedByMigration = true;
            }

            // Older settings files predate the timestamp. Give an already
            // enabled diagnostic session a fresh 24-hour window instead of
            // disabling it immediately during migration.
            if (Settings.EnableDebugLogging && !Settings.DebugLoggingEnabledAtUtc.HasValue)
            {
                Settings.DebugLoggingEnabledAtUtc = DateTime.UtcNow;
                settingsChangedByMigration = true;
            }

            if (settingsChangedByMigration)
            {
                plugin.SavePluginSettings(Settings);
            }
        }

        public void ExpireDebugLoggingIfNeeded()
        {
            if (Settings == null || !Settings.EnableDebugLogging)
            {
                return;
            }

            DateTime enabledAt = Settings.DebugLoggingEnabledAtUtc ?? DateTime.UtcNow;
            if (!Settings.DebugLoggingEnabledAtUtc.HasValue)
            {
                Settings.DebugLoggingEnabledAtUtc = enabledAt;
                plugin.SavePluginSettings(Settings);
                return;
            }

            if (DateTime.UtcNow - enabledAt < TimeSpan.FromHours(24))
            {
                return;
            }

            Settings.EnableDebugLogging = false;
            plugin.SavePluginSettings(Settings);
        }

        // External tool status, following the pattern FullVid and UniPlaySong
        // already use: the view binds these, rather than code-behind reaching
        // into TextBlocks and setting their text and colour by hand.
        private readonly Services.ToolProbe _probe = new Services.ToolProbe();

        private SetupStatus _ffmpegStatus = SetupStatus.Neutral(string.Empty);
        private SetupStatus _ytDlpStatus = SetupStatus.Neutral(string.Empty);
        private SetupStatus _denoStatus = SetupStatus.Neutral(string.Empty);
        private SetupStatus _apiKeyStatus = SetupStatus.Neutral(string.Empty);
        private SetupStatus _desktopThemeStatus = SetupStatus.Neutral(string.Empty);
        private SetupStatus _fullscreenThemeStatus = SetupStatus.Neutral(string.Empty);
        private bool _toolStatusLoaded;
        private bool _themeSupportLoaded;
        private int _themeProbeGeneration;
        private int _toolProbeGeneration;
        private readonly object _toolProbeLock = new object();

        private sealed class ToolProbeSnapshot
        {
            public string FfmpegConfigured;
            public string FfmpegResolved;
            public string FfmpegResult;
            public string YtDlpConfigured;
            public string YtDlpResolved;
            public string YtDlpResult;
            public string DenoConfigured;
            public string DenoResolved;
            public string DenoResult;
        }

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

        public SetupStatus DesktopThemeStatus
        {
            get => _desktopThemeStatus;
            set { _desktopThemeStatus = value; OnPropertyChanged(); }
        }

        public SetupStatus FullscreenThemeStatus
        {
            get => _fullscreenThemeStatus;
            set { _fullscreenThemeStatus = value; OnPropertyChanged(); }
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

        public RelayCommand<object> OptimiseImages => new RelayCommand<object>(a =>
        {
            plugin?.OptimiseStoredImages();
        });

        public RelayCommand<object> ResizeStoredBackgrounds => new RelayCommand<object>(a =>
        {
            plugin?.ResizeStoredBackgrounds(Settings.StoredBackgroundResizePreset);
        });

        public RelayCommand<object> ConvertGifs => new RelayCommand<object>(a =>
        {
            plugin?.ConvertGifsToMp4();
        });

        public RelayCommand<object> RestoreOriginalBackgrounds => new RelayCommand<object>(a =>
        {
            plugin?.RestoreOriginalBackgrounds();
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

        public RelayCommand<object> OpenDebugLogFolder => new RelayCommand<object>(a =>
        {
            plugin?.OpenDebugLogFolder();
        });

        public RelayCommand<object> BrowseFfmpeg => new RelayCommand<object>(a =>
        {
            string path = plugin?.PlayniteApi?.Dialogs?.SelectFile(
                "ffmpeg|ffmpeg.exe|Executable|*.exe");

            if (!string.IsNullOrWhiteSpace(path))
            {
                Settings.FfmpegPath = path;
            }
        });

        public RelayCommand<object> BrowseDeno => new RelayCommand<object>(a =>
        {
            string path = plugin?.PlayniteApi?.Dialogs?.SelectFile(
                "deno|deno.exe|Executable|*.exe");

            if (!string.IsNullOrWhiteSpace(path))
            {
                Settings.DenoPath = path;
            }
        });

        public RelayCommand<object> BrowseYtDlp => new RelayCommand<object>(a =>
        {
            string path = plugin?.PlayniteApi?.Dialogs?.SelectFile(
                "yt-dlp|yt-dlp.exe|Executable|*.exe");

            if (!string.IsNullOrWhiteSpace(path))
            {
                Settings.YtDlpPath = path;
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

        // Theme support is intentionally checked off the UI thread. A theme may
        // contain many XAML files, and opening ImageRotater settings should not
        // become slower just because the user has several themes installed.
        //
        // Playnite exposes the active Desktop and Fullscreen theme IDs through
        // IPlayniteSettingsAPI. We resolve those IDs to their theme.yaml files,
        // then look for ImageRotater's official custom element names in that
        // theme's XAML. This detects actual theme integration rather than merely
        // assuming that an installed plugin means the theme supports it.
        public void EnsureThemeSupportLoaded()
        {
            if (_themeSupportLoaded)
            {
                return;
            }

            _themeSupportLoaded = true;
            RefreshThemeSupportAsync();
        }

        private async void RefreshThemeSupportAsync()
        {
            int generation = ++_themeProbeGeneration;

            DesktopThemeStatus = SetupStatus.Neutral(Loc.Get("LOCImageRotaterCheckingThemeSupport"));
            FullscreenThemeStatus = SetupStatus.Neutral(Loc.Get("LOCImageRotaterCheckingThemeSupport"));

            ThemeProbeResult desktop;
            ThemeProbeResult fullscreen;

            try
            {
                string desktopId = plugin?.PlayniteApi?.ApplicationSettings?.DesktopTheme;
                string fullscreenId = plugin?.PlayniteApi?.ApplicationSettings?.FullscreenTheme;
                string configPath = plugin?.PlayniteApi?.Paths?.ConfigurationPath;
                string appPath = plugin?.PlayniteApi?.Paths?.ApplicationPath;

                var result = await Task.Run(() => new[]
                {
                    ProbeTheme(desktopId, "Desktop", configPath, appPath),
                    ProbeTheme(fullscreenId, "Fullscreen", configPath, appPath)
                });

                desktop = result[0];
                fullscreen = result[1];
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, "ImageRotater: active theme support probe failed");
                return;
            }

            if (generation != _themeProbeGeneration)
            {
                return;
            }

            DesktopThemeStatus = DescribeThemeSupport(desktop);
            FullscreenThemeStatus = DescribeThemeSupport(fullscreen);
        }

        private sealed class ThemeProbeResult
        {
            public string Name;
            public bool Found;
            public bool HasBackground;
            public bool HasCover;
        }

        private static ThemeProbeResult ProbeTheme(string themeId, string modeFolder, string configPath, string appPath)
        {
            var result = new ThemeProbeResult
            {
                Name = string.IsNullOrWhiteSpace(themeId) ? Loc.Get("LOCImageRotaterUnknownTheme") : themeId,
                Found = false
            };

            if (string.IsNullOrWhiteSpace(themeId))
            {
                return result;
            }

            string[] roots =
            {
                string.IsNullOrWhiteSpace(configPath) ? null : Path.Combine(configPath, "Themes", modeFolder),
                string.IsNullOrWhiteSpace(appPath) ? null : Path.Combine(appPath, "Themes", modeFolder)
            };

            foreach (string root in roots.Where(a => !string.IsNullOrWhiteSpace(a) && Directory.Exists(a)))
            {
                IEnumerable<string> manifests;
                try
                {
                    manifests = Directory.EnumerateFiles(root, "theme.yaml", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string manifest in manifests)
                {
                    string id = ReadYamlValue(manifest, "Id");
                    if (!string.Equals(id, themeId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Found = true;
                    string displayName = ReadYamlValue(manifest, "Name");
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        result.Name = displayName;
                    }

                    string themeDir = Path.GetDirectoryName(manifest);
                    if (string.IsNullOrWhiteSpace(themeDir) || !Directory.Exists(themeDir))
                    {
                        return result;
                    }

                    IEnumerable<string> xamlFiles;
                    try
                    {
                        xamlFiles = Directory.EnumerateFiles(themeDir, "*.xaml", SearchOption.AllDirectories);
                    }
                    catch
                    {
                        return result;
                    }

                    foreach (string xaml in xamlFiles)
                    {
                        string text;
                        try
                        {
                            text = File.ReadAllText(xaml);
                        }
                        catch
                        {
                            continue;
                        }

                        if (!result.HasBackground &&
                            text.IndexOf("ImageRotater_Background", StringComparison.Ordinal) >= 0)
                        {
                            result.HasBackground = true;
                        }

                        if (!result.HasCover &&
                            text.IndexOf("ImageRotater_Cover", StringComparison.Ordinal) >= 0)
                        {
                            result.HasCover = true;
                        }

                        if (result.HasBackground && result.HasCover)
                        {
                            return result;
                        }
                    }

                    return result;
                }
            }

            return result;
        }

        private static string ReadYamlValue(string manifestPath, string key)
        {
            try
            {
                string prefix = key + ":";
                foreach (string raw in File.ReadLines(manifestPath))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return line.Substring(prefix.Length).Trim().Trim('"', '\'');
                }
            }
            catch
            {
            }

            return null;
        }

        private static SetupStatus DescribeThemeSupport(ThemeProbeResult result)
        {
            if (result == null || !result.Found)
            {
                string name = result?.Name ?? Loc.Get("LOCImageRotaterUnknownTheme");
                return SetupStatus.Neutral(
                    Loc.Format("LOCImageRotaterThemeSupportUnknown", name));
            }

            if (result.HasBackground && result.HasCover)
            {
                return SetupStatus.Ok(
                    Loc.Format("LOCImageRotaterThemeSupportFull", result.Name));
            }

            if (result.HasBackground)
            {
                return SetupStatus.Warning(
                    Loc.Format("LOCImageRotaterThemeSupportBackgroundOnly", result.Name));
            }

            if (result.HasCover)
            {
                return SetupStatus.Warning(
                    Loc.Format("LOCImageRotaterThemeSupportCoverOnly", result.Name));
            }

            return SetupStatus.Problem(
                Loc.Format("LOCImageRotaterThemeSupportNone", result.Name));
        }

        // Tool probing launches ffmpeg / yt-dlp / deno. Keep it lazy (only
        // when Tools is actually opened) and off the UI thread so opening the
        // settings window stays instant.
        public void EnsureToolStatusLoaded()
        {
            if (_toolStatusLoaded)
            {
                return;
            }

            _toolStatusLoaded = true;
            RefreshToolStatusAsync();
        }

        public async void RefreshToolStatusAsync()
        {
            int generation = ++_toolProbeGeneration;

            string ffmpegConfigured = Settings?.FfmpegPath;
            string ytDlpConfigured = Settings?.YtDlpPath;
            string denoConfigured = Settings?.DenoPath;

            SetupStatus checking = SetupStatus.Neutral(Loc.Get("LOCImageRotaterCheckingTools"));
            FfmpegStatus = checking;
            YtDlpStatus = checking;
            DenoStatus = checking;

            ToolProbeSnapshot snapshot;

            try
            {
                snapshot = await Task.Run(() =>
                {
                    lock (_toolProbeLock)
                    {
                        string ffmpeg = Services.ExternalTool.Resolve(
                            ffmpegConfigured, Services.ExternalTool.FfmpegExe);
                        string ytDlp = Services.ExternalTool.Resolve(
                            ytDlpConfigured, Services.ExternalTool.YtDlpExe);
                        string deno = Services.ExternalTool.Resolve(
                            denoConfigured, Services.ExternalTool.DenoExe);

                        return new ToolProbeSnapshot
                        {
                            FfmpegConfigured = ffmpegConfigured,
                            FfmpegResolved = ffmpeg,
                            FfmpegResult = _probe.Probe(ffmpeg, Services.ToolProbe.FfmpegVersionFlag),
                            YtDlpConfigured = ytDlpConfigured,
                            YtDlpResolved = ytDlp,
                            YtDlpResult = _probe.Probe(ytDlp, Services.ToolProbe.YtDlpVersionFlag),
                            DenoConfigured = denoConfigured,
                            DenoResolved = deno,
                            DenoResult = _probe.Probe(deno, Services.ToolProbe.YtDlpVersionFlag)
                        };
                    }
                });
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, "ImageRotater: external tool status probe failed");
                return;
            }

            // A path may have changed while a previous probe was still running.
            // Only the newest request is allowed to update the UI.
            if (generation != _toolProbeGeneration)
            {
                return;
            }

            FfmpegStatus = DescribeToolResult(
                snapshot.FfmpegResult, snapshot.FfmpegConfigured, "ffmpeg");
            YtDlpStatus = DescribeToolResult(
                snapshot.YtDlpResult, snapshot.YtDlpConfigured, "yt-dlp");
            DenoStatus = DescribeDenoResult(
                snapshot.DenoResult, snapshot.DenoConfigured, snapshot.YtDlpResult);
        }

        private SetupStatus DescribeDenoResult(string result, string configured, string ytDlpResult)
        {
            if (ToolProbeWorked(result))
            {
                bool onPath = string.IsNullOrWhiteSpace(configured);
                return SetupStatus.Ok(onPath ? Loc.Format("LOCImageRotaterOnPath", result) : result);
            }

            if (!string.IsNullOrWhiteSpace(configured))
            {
                return SetupStatus.Problem(
                    Loc.Format("LOCImageRotaterBadToolPath", result, "deno"));
            }

            if (!ToolProbeWorked(ytDlpResult))
            {
                return SetupStatus.Neutral(Loc.Get("LOCImageRotaterDenoOnlyNeeded"));
            }

            return SetupStatus.Neutral(Loc.Get("LOCImageRotaterDenoNotFound"));
        }

        private SetupStatus DescribeToolResult(string result, string configured, string name)
        {
            if (ToolProbeWorked(result))
            {
                bool onPath = string.IsNullOrWhiteSpace(configured);
                return SetupStatus.Ok(onPath ? Loc.Format("LOCImageRotaterOnPath", result) : result);
            }

            if (string.IsNullOrWhiteSpace(configured))
            {
                return SetupStatus.Neutral(Loc.Format("LOCImageRotaterToolNotFound", name));
            }

            return SetupStatus.Problem(Loc.Format("LOCImageRotaterBadToolPath", result, name));
        }

        private static bool ToolProbeWorked(string result)
        {
            return !string.IsNullOrEmpty(result)
                && result.StartsWith("Found", StringComparison.Ordinal);
        }

        // Snapshot for cancel. Deep clone via JSON so every property is covered
        // automatically as settings are added.
        public void BeginEdit()
        {
            // Do not launch external tools here. Most users never open Tools,
            // and blocking BeginEdit was making ImageRotater settings visibly
            // slower to open than other plugins.
            _toolStatusLoaded = false;
            _themeSupportLoaded = false;
            ++_toolProbeGeneration;
            ++_themeProbeGeneration;
            DesktopThemeStatus = SetupStatus.Neutral(string.Empty);
            FullscreenThemeStatus = SetupStatus.Neutral(string.Empty);
            FfmpegStatus = SetupStatus.Neutral(string.Empty);
            YtDlpStatus = SetupStatus.Neutral(string.Empty);
            DenoStatus = SetupStatus.Neutral(string.Empty);

            editingClone = JsonConvert.DeserializeObject<ImageRotaterSettings>(
                JsonConvert.SerializeObject(Settings));
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            bool debugWasEnabled = editingClone?.EnableDebugLogging == true;

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
            plugin?.NotifyDebugLoggingSaved(debugWasEnabled);
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
