using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    // Renders MOTION covers - video and GIF - where a theme places it.
    //
    // Still covers never come through here. Rotation writes Game.CoverImage,
    // Playnite notifies its own tile (Desktop always did; Fullscreen since
    // 10.57), and Playnite's PART_ImageCover draws the new picture. No plugin
    // control, no theme support, every theme, both modes.
    //
    // What Playnite's Image can never do is play a file: it renders a
    // BitmapSource, full stop. So a game whose cover is an MP4 or an animated
    // GIF needs a MediaElement, and only a plugin can supply one - which is
    // this control, and the sole reason it exists. Everything in here that
    // looks elaborate - surviving tile recycling, fading video up only once
    // its first frame exists, animating the selected tile only - is the cost
    // of a media pipeline inside a virtualised grid. None of it is about
    // stills.
    //
    // This control decides WHICH cover a game shows and publishes it as a path
    // on its DataContext. It does not decode anything: the XAML binds that path
    // with IsAsync=True, and a theme may instead keep this control hidden and
    // bind Content.ImagePath to render the cover with its own element - which
    // is how Aniki integrates BackgroundChanger.
    //
    // The earlier version assigned DisplayImage.Source in code-behind, which
    // had two consequences. Nothing outside could bind to it, so themes could
    // not reach the value at all. And it decoded during layout, so placing it
    // in a Fullscreen grid template - dozens of tiles realising at once, in a
    // 32-bit process - took Playnite down. That crash was the synchronous
    // decode, not the presence of a plugin control: BackgroundChanger's
    // equivalent survives the same placement precisely because it binds a path
    // asynchronously.
    public partial class CoverImageControl : PluginUserControl
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IBackgroundImageSource _source;
        private readonly ImageSelector _selector;
        private readonly Func<ImageRotaterSettings> _settings;
        private readonly string _playniteConfigPath;
        private readonly bool _nativeInjected;
        private readonly FileLogger _fileLogger;
        internal void LogNativeLayout(Image native)
        {
            if (_fileLogger?.IsEnabled != true) return;
            _fileLogger.Log($"COVER layout game={GameContext?.Id} native={native.ActualWidth:0.##}x{native.ActualHeight:0.##} renderer={ActualWidth:0.##}x{ActualHeight:0.##} media={NativeMediaSize} stretch={native.Stretch} align={native.HorizontalAlignment}/{native.VerticalAlignment}");
        }
        private bool _nativeReleased;
        private bool _nativeSourceTracking;
        private bool _preserveNativeHandoff;
        private bool _nativeDetailsRegion;
        internal event Action NativeFallbackRequested;
        internal event Action NativeMediaReady;
        internal event Action NativeCoverReady;
        private bool _nativeCoverReadyAnnounced;
        // An assigned BitmapImage may still be downloading. Do not hide the
        // native cover or start an entry animation until it can draw pixels.
        private static bool HasReadyStill(Image image) =>
            image.Visibility == Visibility.Visible && image.Source != null && image.Opacity >= .999 &&
            !(image.Source is System.Windows.Media.Imaging.BitmapImage bitmap && bitmap.IsDownloading);

        internal bool CanHideNativeCover => IsCoverDisplayed &&
            (HasReadyStill(DisplayImage) ||
             (DisplayVideo.Visibility == Visibility.Visible && DisplayVideo.Source != null && DisplayVideo.Opacity >= .999) ||
             HasReadyStill(UnfocusedStillImage));

        private void PublishNativeCoverReady()
        {
            if (!_nativeInjected || _nativeReleased || _nativeCoverReadyAnnounced || !CanHideNativeCover) return;
            _nativeCoverReadyAnnounced = true;
            NativeCoverReady?.Invoke();
        }
        internal Size NativeMediaSize { get; private set; } = Size.Empty;
        private FrameworkElement _nativeMeasureMedia;
        private string _nativeLoggedPath;
        private System.Diagnostics.Stopwatch _nativeVideoOpenWatch;

        internal void NotifyNativeMediaReady(FrameworkElement media)
        {
            if (_nativeInjected && _nativeReleased) return;
            SetValue(IsCoverDisplayedPropertyKey, true);
            if (!_nativeInjected) return;
            PublishNativeCoverReady();
            if (_nativeLoggedPath != _previousPick && !string.IsNullOrEmpty(_previousPick))
            {
                _nativeLoggedPath = _previousPick;
                Logger.Info($"ImageRotater native cover ready: game={GameContext?.Id} media={Path.GetFileName(_previousPick)} type={media.GetType().Name}");
            }
            Size size = Size.Empty;
            if (media is Image image && image.Source != null)
                size = new Size(image.Source.Width, image.Source.Height);
            else if (media is MediaElement video && video.NaturalVideoWidth > 0 && video.NaturalVideoHeight > 0)
                size = new Size(video.NaturalVideoWidth, video.NaturalVideoHeight);
            if (size.IsEmpty || size.Width <= 0 || size.Height <= 0 ||
                double.IsNaN(size.Width) || double.IsNaN(size.Height) ||
                double.IsInfinity(size.Width) || double.IsInfinity(size.Height)) return;
            if (_nativeMeasureMedia == media && NativeMediaSize == size) return;
            _nativeMeasureMedia = media;
            NativeMediaSize = size;
            InvalidateMeasure();
            NativeMediaReady?.Invoke();
        }

        protected override Size MeasureOverride(Size constraint)
        {
            Size measured = base.MeasureOverride(constraint);
            // The outgoing layer must not enlarge an auto-width native cover
            // until the transition completes, then make it jump sideways.
            if (_nativeInjected && _nativeMeasureMedia?.Visibility == Visibility.Visible &&
                _nativeMeasureMedia.DesiredSize.Width > 0 && _nativeMeasureMedia.DesiredSize.Height > 0)
                return _nativeMeasureMedia.DesiredSize;
            return measured;
        }

        // The rotation landed on the game's own Playnite cover, so IR draws the
        // same picture as the native Image. A native <-> IR relay would only
        // animate that cover into itself.
        internal bool ShowsNativeOriginal
        {
            get
            {
                string path = _data.ImagePath;
                if (!string.IsNullOrEmpty(_videoPath) || string.IsNullOrEmpty(path)) return false;
                if (GameImageStore.IsPreservedOriginal(path)) return true;
                try
                {
                    string original = (_source as FolderImageSource)?.GetOriginalStillPath(GameContext);
                    return !string.IsNullOrEmpty(original) &&
                        string.Equals(Path.GetFullPath(original), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                    ex is IOException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        internal bool HasNativeMedia => IsCoverDisplayed || !string.IsNullOrEmpty(_data.ImagePath) ||
            !string.IsNullOrEmpty(_videoPath) || _animating;

        internal void SetNativeGameContext(Game game, bool preserveOutgoing)
        {
            _nativeReleased = false;
            _nativeDetailsRegion = preserveOutgoing;
            _preserveNativeHandoff = preserveOutgoing;
            try { GameContext = game; }
            finally { _preserveNativeHandoff = false; }
        }

        private static readonly DependencyPropertyKey IsCoverDisplayedPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(IsCoverDisplayed), typeof(bool),
                typeof(CoverImageControl), new PropertyMetadata(false));
        public static readonly DependencyProperty IsCoverDisplayedProperty = IsCoverDisplayedPropertyKey.DependencyProperty;
        public bool IsCoverDisplayed => (bool)GetValue(IsCoverDisplayedProperty);

        internal void ReleaseNativeRendering()
        {
            CancelStaticVideoPoster();
            _nativeReleased = true;
            ShowNothing();
            DisplayImage.SetCurrentValue(Image.SourceProperty, null);
        }

        // Playnite stores CoverArtStretch in config.json using the same numeric
        // values as WPF Stretch (None=0, Fill=1, Uniform=2, UniformToFill=3).
        // Cache it across every realised cover control so a Fullscreen grid does
        // not make one disk read per tile. The timestamp check also means a
        // setting changed while Playnite is running is picked up automatically.
        private static readonly object CoverStretchSync = new object();
        private static string _coverStretchConfigPath;
        private static DateTime _coverStretchConfigStampUtc = DateTime.MinValue;
        private static Stretch _cachedCoverStretch = Stretch.UniformToFill;

        private readonly CoverImageDataContext _data = new CoverImageDataContext();

        // Last path shown for the current game, so a revisit can avoid
        // repeating it.
        private string _previousPick;
        // A selected game advances EverySelection once per selection event, not
        // each time playback policy/viewport/other tiles trigger Refresh().
        private static int _selectionRevision;
        private int _lastSelectedPickRevision = -1;
        private bool _advancePickRequested;
        // An unfocused video becomes a real still cover (not a newly rotated pick).
        // Retain the last actual still seen for this game, falling back to the
        // original Playnite cover present in FolderImageSource's candidate list.
        private string _lastFixedCoverPath;
        private string _unfocusedVideoPath;
        private string _unfocusedStillPath;
        private int _videoFadeRevision;
        // Async XAML bindings do not guarantee a TargetUpdated callback when a
        // virtualized tile changes selection. Decode passive stills explicitly.
        private static readonly SemaphoreSlim UnfocusedStillDecodeGate = new SemaphoreSlim(2, 2);
        private CancellationTokenSource _unfocusedStillCancellation;
        private DispatcherTimer _unfocusedFadeWatchdog;
        private int _unfocusedSwapRevision;

        private readonly HashSet<string> _loggedFailures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CoverImageControl(
            IBackgroundImageSource source,
            ImageSelector selector,
            Func<ImageRotaterSettings> settings,
            IPlayniteAPI playniteApi,
            bool nativeInjected = false, FileLogger fileLogger = null)
        {
            InitializeComponent();

            _source = source;
            _selector = selector;
            _settings = settings;
            _nativeInjected = nativeInjected;
            _desktopCoverLoop = playniteApi?.ApplicationInfo?.Mode == ApplicationMode.Desktop;
            _fileLogger = fileLogger;
            if (_nativeInjected) DisplayImage.ImageFailed += OnNativeImageFailed;
            _playniteConfigPath = playniteApi?.Paths?.ConfigurationPath == null
                ? null
                : Path.Combine(playniteApi.Paths.ConfigurationPath, "config.json");

            ApplyPlayniteCoverStretch();
            DataContext = _data;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // What a theme binds when it hosts this control hidden and draws the
        // cover itself.
        //
        // Deliberately NOT called "Content": PluginUserControl inherits
        // ContentControl.Content, so that name would shadow an existing
        // dependency property and a theme binding "Content.X" would silently
        // resolve against WPF's property instead of this one. The DataContext
        // is set to the same object, so themes can also bind straight through
        // without naming this at all.
        public CoverImageDataContext CoverData
        {
            get { return _data; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _nativeReleased = false;
            if (_nativeInjected && !_nativeSourceTracking)
            {
                _nativeSourceTracking = true;
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image))
                    .AddValueChanged(DisplayImage, OnNativeImageSourceChanged);
            }
            ArtworkRotated += OnArtworkRotated;
            PlaybackPolicyChanged += OnPlaybackPolicyChanged;

            // Viewport tracking is attached lazily only for video covers.
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelStaticVideoPoster();
            if (_nativeSourceTracking)
            {
                _nativeSourceTracking = false;
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image))
                    .RemoveValueChanged(DisplayImage, OnNativeImageSourceChanged);
            }
            ArtworkRotated -= OnArtworkRotated;
            PlaybackPolicyChanged -= OnPlaybackPolicyChanged;
            DetachViewportTracking();
            if (_nativeInjected)
            {
                ReleaseNativeRendering();
                return;
            }

            // Keep the bound path across Fullscreen unload/reload cycles; release only GIF playback here.
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);

            // Defer video release until layout confirms that the tile was actually removed.
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!IsLoaded)
                    {
                        StopVideo();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnNativeImageSourceChanged(object sender, EventArgs e)
        {
            if (!_nativeReleased && _animating && DisplayImage.Source != null)
                NotifyNativeMediaReady(DisplayImage);
        }

        private void OnNativeImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Guid gameId = GameContext?.Id ?? Guid.Empty;
            string path = _data.ImagePath;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // A separately decoded unfocused still is already valid: a
                // failure in the redundant bound Image must not erase it.
                if (UnfocusedStillImage.Visibility == Visibility.Visible &&
                    UnfocusedStillImage.Source != null) return;
                if (!_nativeReleased && GameContext?.Id == gameId && _data.ImagePath == path) ShowNothing();
            }), DispatcherPriority.Loaded);
        }

        // Playnite calls this when the tile is bound to a different game -
        // which for a virtualised grid is every time it scrolls into reuse.
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            CancelStaticVideoPoster();
            _nativeCoverReadyAnnounced = false;
            if (_nativeInjected && _preserveNativeHandoff && IsCoverDisplayed)
            {
                // Old game's passive still must not obscure the new game's
                // artwork after the virtualized native tile changes context.
                if (oldContext?.Id != newContext?.Id) CancelUnfocusedStillSwap();
                _cutNextStage = false;
                _previousPick = null;
                Refresh();
                return;
            }
            SetValue(IsCoverDisplayedPropertyKey, false);
            if (_nativeInjected)
            {
                _data.ImagePath = string.Empty;
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
                DisplayImage.SetCurrentValue(Image.SourceProperty, null);
            }
            // A recycled tile must release media owned by the previous game before repicking.
            CancelUnfocusedStillSwap();
            StopVideo();

            // A cut, never a transition: this tile IS a different game now,
            // and Playnite's own tile cuts too. Without this every tile that
            // scrolled into view dissolved - or flashed - from whichever game
            // it last showed.
            ClearPreviousCover();
            LowerVeil();
            _cutNextStage = true;

            _previousPick = null;
            _lastSelectedPickRevision = -1;
            _advancePickRequested = false;
            _lastFixedCoverPath = null;
            Refresh();
        }

        // Set by a context change and consumed by the next StagePreviousCover:
        // the picture on screen belongs to another game and must not be the
        // outgoing layer of a transition.
        private bool _cutNextStage;

        // A slideshow tick rotates artwork while the SAME game stays selected,
        // so Playnite raises no context change and nothing above would ever
        // tell this control to re-read. Static because the controls are built
        // by Playnite on demand and there is no reference to hand them: the
        // rotation service announces, whoever is alive listens.
        //
        // Matters most for video and GIFs. A still would merely be stale; a
        // MediaElement keeps PLAYING the previous pick, so the slideshow would
        // appear to do nothing at all while the file underneath it changed.
        public static event Action<Guid> ArtworkRotated;
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

        // Whether any theme is hosting this control right now.
        //
        // A control subscribes on Loaded and unsubscribes on Unloaded, so a
        // live subscriber means a theme has placed ImageRotater_Cover and it
        // will crossfade its own picture. The slideshow uses this to skip
        // fading Playnite's PART_ImageCover underneath - that fade would run
        // beneath an opaque control, invisible, and for a video pick would be
        // animating a tile nobody can see.
        public static bool IsHostedByTheme
        {
            get { return ArtworkRotated != null; }
        }

        public static void NotifyArtworkRotated(Guid gameId)
        {
            Action<Guid> handler = ArtworkRotated;

            if (handler == null)
            {
                return;
            }

            // Marshalled to the UI thread HERE rather than at each call site.
            //
            // The Fullscreen slideshow raises this from inside a Task.Run - the
            // cover write is done off-thread deliberately, so the fade window
            // contains only the binding re-read. Every handler then reads
            // GameContext, which is a WPF DependencyProperty and throws
            // "The calling thread cannot access this object because a different
            // thread owns it" when touched from anywhere else.
            //
            // Fixing it at the announcement means a future caller cannot
            // reintroduce it by forgetting to marshal.
            Application app = Application.Current;

            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => handler(gameId)));
                return;
            }

            handler(gameId);
        }

        // Which game is selected right now, so only that tile animates.
        //
        // A grid realises a screenful of these at once, and every one holding
        // an animated cover decodes frames continuously on the UI thread - in a
        // 32-bit process shared with Chromium. Dozens of simultaneous decoders
        // is the same pressure that took Playnite down during the theme
        // experiments, and it buys nothing: a wall of moving thumbnails is
        // harder to read than one.
        //
        // Static and set by the plugin, because a control has no way to ask
        // whether its own tile is selected - GameListItem has no IsSelected,
        // and the containing ListBoxItem is not reachable from the control's
        // own code without walking the visual tree on every refresh.
        private static Guid _selectedGame;

        public static void NotifySelectionChanged(Guid gameId)
        {
            if (_selectedGame == gameId)
            {
                return;
            }

            _selectedGame = gameId;
            unchecked { ++_selectionRevision; }

            // Both the tile gaining selection and the one losing it need to
            // re-decide, and neither gets a context change for it.
            //
            // Routed through NotifyArtworkRotated rather than raising the event
            // directly, so this cannot bypass the UI-thread marshalling that
            // lives there.
            NotifyArtworkRotated(gameId);
        }

        private bool IsSelectedTile
        {
            get { return GameContext != null && ((_nativeInjected && _nativeDetailsRegion) || GameContext.Id == _selectedGame); }
        }

        // True while this tile is actually decoding something. Lets a tile that
        // loses selection recognise it has work to stop, since the announcement
        // names the arriving game rather than this one.
        private bool _animating;
        private string _videoPath;
        private int _videoGeneration;
        private bool _videoPlaying;
        private bool _pausedByViewport;
        private bool _inViewport = true;
        private bool _viewportCheckPending;
        private System.Windows.Controls.ScrollViewer _scrollViewer;
        private System.Windows.Controls.ScrollContentPresenter _viewportHost;

        private void EnsureViewportTracking()
        {
            if (_scrollViewer != null && _viewportHost != null)
            {
                return;
            }

            AttachViewportTracking();
        }

        private void AttachViewportTracking()
        {
            DetachViewportTracking();

            DependencyObject current = this;
            while (current != null)
            {
                if (_viewportHost == null)
                {
                    _viewportHost = current as System.Windows.Controls.ScrollContentPresenter;
                }

                if (_scrollViewer == null)
                {
                    _scrollViewer = current as System.Windows.Controls.ScrollViewer;
                }

                if (_viewportHost != null && _scrollViewer != null)
                {
                    break;
                }

                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged += OnViewportChanged;
                _scrollViewer.SizeChanged += OnViewportSizeChanged;
            }
        }

        private void DetachViewportTracking()
        {
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged -= OnViewportChanged;
                _scrollViewer.SizeChanged -= OnViewportSizeChanged;
            }

            _scrollViewer = null;
            _viewportHost = null;
            _viewportCheckPending = false;
        }

        private void OnViewportChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        {
            QueueViewportCheck();
        }

        private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
        {
            QueueViewportCheck();
        }

        private void QueueViewportCheck()
        {
            if (_viewportCheckPending)
            {
                return;
            }

            _viewportCheckPending = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _viewportCheckPending = false;
                    UpdateViewportState();
                }),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        private bool IsActuallyInViewport()
        {
            if (!IsLoaded || Visibility != Visibility.Visible)
            {
                return false;
            }

            if (_viewportHost == null || ActualWidth <= 0 || ActualHeight <= 0 ||
                _viewportHost.ActualWidth <= 0 || _viewportHost.ActualHeight <= 0)
            {
                return true;
            }

            try
            {
                Rect bounds = TransformToAncestor(_viewportHost).TransformBounds(
                    new Rect(0, 0, ActualWidth, ActualHeight));
                Rect viewport = new Rect(0, 0, _viewportHost.ActualWidth, _viewportHost.ActualHeight);
                Rect visible = Rect.Intersect(bounds, viewport);
                return !visible.IsEmpty && visible.Width > 1 && visible.Height > 1;
            }
            catch
            {
                return true;
            }
        }

        private void UpdateViewportState()
        {
            bool inViewport = IsActuallyInViewport();
            if (_inViewport == inViewport)
            {
                return;
            }

            _inViewport = inViewport;

            // An unfocused video is being held as the outgoing frame for an
            // image handoff. A viewport notification must not resume playback.
            if (_unfocusedVideoPath != null) return;

            if (!inViewport)
            {
                if (DisplayVideo.Source != null && _videoPlaying)
                {
                    try
                    {
                        DisplayVideo.Pause();
                        _videoPlaying = false;
                        _pausedByViewport = true;
                    }
                    catch
                    {
                    }
                }

                return;
            }

            if (DisplayVideo.Source != null && _pausedByViewport)
            {
                try
                {
                    DisplayVideo.Play();
                    _videoPlaying = true;
                    _pausedByViewport = false;
                }
                catch
                {
                }

                return;
            }

            if (DisplayVideo.Source == null && !string.IsNullOrEmpty(_videoPath))
            {
                string path = _videoPath;
                ShowVideo(path);
            }
        }

        private void OnArtworkRotated(Guid gameId)
        {
            // Normally only this tile's own game - a grid raises this for one
            // game while dozens of controls listen.
            //
            // A tile that loses selection also needs to release its plugin layer.
            bool mine = GameContext != null && GameContext.Id == gameId;

            ImageRotaterSettings settings = _settings != null ? _settings() : null;

            // The host fades this layer back to native when selection is lost.
            // Do not clear it first from the global selection notification.
            if (_nativeInjected && !mine && settings?.ShowUnfocusedCovers != true) return;

            bool mustStandDown = !IsSelectedTile
                && settings?.ShouldAnimateCover(false) != true
                && (settings?.UsesDirectRendering(ArtworkKind.Cover) == true
                    ? (_animating || DisplayVideo.Visibility == Visibility.Visible)
                    : (DisplayImage.Visibility == Visibility.Visible ||
                       DisplayVideo.Visibility == Visibility.Visible));

            if (!mine && !mustStandDown)
            {
                return;
            }

            // Only a tile whose PICK actually changed forgets its history.
            //
            // Clearing this unconditionally was a regression: every wake -
            // including the selection announcement, which fires on every move -
            // wiped the avoid-previous memory, so in EverySelection mode tiles
            // re-rolled their artwork constantly. Tiles that were merely told
            // to re-read now keep their memory, and only the tile the rotation
            // actually re-picked for starts fresh.
            //
            // "mine" is the test because the announcement names the game whose
            // artwork moved on; a stand-down tile is being told to stop, not
            // that its pick changed.
            if (mine)
            {
                if (settings?.UsesDirectRendering(ArtworkKind.Cover) != true)
                    _previousPick = null;
                else
                    _advancePickRequested = true;
            }

            Refresh();
        }

        private void OnPlaybackPolicyChanged()
        {
            Refresh();
        }


        private void ApplyPlayniteCoverStretch()
        {
            if (_nativeInjected) return; // Native host supplies the theme's own stretch bindings.
            // Default/fallback: behave like Playnite's native cover renderer.
            // A theme can override the renderer per ImageRotater_Cover host by
            // setting the standard ContentControl.Tag property, for example:
            //
            //   Tag="ImageRotater:Stretch=Uniform;StretchDirection=Both"
            //
            // This keeps the Playnite integration contract as a plain
            // ContentControl (no plugin XML namespace required in themes) while
            // still letting Grid View, Details View and Fullscreen choose their
            // own layout independently. Missing values always fall back to the
            // native Playnite behaviour.
            Stretch stretch = GetPlayniteCoverStretch();
            StretchDirection direction = StretchDirection.Both;

            TryGetThemeCoverLayoutOverride(ref stretch, ref direction);

            DisplayImage.Stretch = stretch;
            DisplayImage.StretchDirection = direction;
            PreviousImage.Stretch = stretch;
            PreviousImage.StretchDirection = direction;
            DisplayVideo.Stretch = stretch;
            UnfocusedStillImage.Stretch = stretch;
            UnfocusedStillImage.StretchDirection = direction;
        }

        private void TryGetThemeCoverLayoutOverride(ref Stretch stretch, ref StretchDirection direction)
        {
            FrameworkElement current = this;

            while (current != null)
            {
                var host = current as System.Windows.Controls.ContentControl;
                if (host != null && string.Equals(host.Name, "ImageRotater_Cover", StringComparison.Ordinal))
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
                        if (pair.Length != 2)
                        {
                            continue;
                        }

                        string key = pair[0].Trim();
                        string rawValue = pair[1].Trim();

                        if (key.Equals("Stretch", StringComparison.OrdinalIgnoreCase))
                        {
                            Stretch parsedStretch;
                            if (Enum.TryParse(rawValue, true, out parsedStretch))
                            {
                                stretch = parsedStretch;
                            }
                        }
                        else if (key.Equals("StretchDirection", StringComparison.OrdinalIgnoreCase))
                        {
                            StretchDirection parsedDirection;
                            if (Enum.TryParse(rawValue, true, out parsedDirection))
                            {
                                direction = parsedDirection;
                            }
                        }
                    }

                    return;
                }

                DependencyObject parent = null;
                try
                {
                    parent = System.Windows.Media.VisualTreeHelper.GetParent(current);
                }
                catch
                {
                }

                if (parent == null)
                {
                    parent = LogicalTreeHelper.GetParent(current);
                }

                current = parent as FrameworkElement;
            }
        }

        private Stretch GetPlayniteCoverStretch()
        {
            if (string.IsNullOrEmpty(_playniteConfigPath))
            {
                return Stretch.UniformToFill;
            }

            try
            {
                DateTime stamp = File.Exists(_playniteConfigPath)
                    ? File.GetLastWriteTimeUtc(_playniteConfigPath)
                    : DateTime.MinValue;

                lock (CoverStretchSync)
                {
                    if (string.Equals(_coverStretchConfigPath, _playniteConfigPath, StringComparison.OrdinalIgnoreCase) &&
                        stamp == _coverStretchConfigStampUtc)
                    {
                        return _cachedCoverStretch;
                    }

                    Stretch resolved = Stretch.UniformToFill;
                    if (stamp != DateTime.MinValue)
                    {
                        JObject root = JObject.Parse(File.ReadAllText(_playniteConfigPath));
                        JToken token = root["CoverArtStretch"];
                        if (token != null)
                        {
                            int numeric;
                            if (token.Type == JTokenType.Integer && int.TryParse(token.ToString(), out numeric) &&
                                numeric >= (int)Stretch.None && numeric <= (int)Stretch.UniformToFill)
                            {
                                resolved = (Stretch)numeric;
                            }
                            else
                            {
                                Stretch parsed;
                                if (Enum.TryParse(token.ToString(), true, out parsed))
                                {
                                    resolved = parsed;
                                }
                            }
                        }
                    }

                    _coverStretchConfigPath = _playniteConfigPath;
                    _coverStretchConfigStampUtc = stamp;
                    _cachedCoverStretch = resolved;
                    return resolved;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not read Playnite CoverArtStretch; using UniformToFill");
                return Stretch.UniformToFill;
            }
        }

        private void Refresh()
        {
            try
            {
                Window mainWindow = Application.Current?.MainWindow;
                if (mainWindow != null && (mainWindow.WindowState == WindowState.Minimized || !mainWindow.IsVisible))
                {
                    CancelStaticVideoPoster();
                    ShowNothing();
                    DisplayImage.SetCurrentValue(Image.SourceProperty, null);
                    return;
                }
                // Match Playnite's native cover renderer by default, while
                // allowing the hosting theme to override Stretch and
                // StretchDirection on its ImageRotater_Cover placeholder.
                ApplyPlayniteCoverStretch();

                ImageRotaterSettings settings = _settings != null ? _settings() : null;

                if ((_nativeInjected && (_nativeReleased || settings?.UseExperimentalNativeCovers != true)) ||
                    (!_nativeInjected && settings?.UseExperimentalNativeCovers == true))
                {
                    ShowNothing();
                    return;
                }

                if (settings == null || !settings.EnableRotation || !settings.IsArtworkRotationEnabled(GameContext?.Id ?? Guid.Empty, ArtworkKind.Cover))
                {
                    CancelStaticVideoPoster();
                    ShowNothing();
                    return;
                }

                Game game = GameContext;
                if (game == null || _source == null || _selector == null)
                {
                    ShowNothing();
                    return;
                }

                if (game.IsRunning)
                {
                    ShowNothing();
                    return;
                }

                bool selectedTile = IsSelectedTile;
                if (selectedTile || settings.ShouldAnimateCover(selectedTile)) CancelUnfocusedStillSwap();
                if (selectedTile || !settings.ShowUnfocusedCovers || settings.ShouldAnimateCover(selectedTile))
                    CancelStaticVideoPoster();
                if (!selectedTile && !settings.ShowUnfocusedCovers)
                {
                    ShowNothing();
                    return;
                }

                IReadOnlyList<string> candidates = _source.GetImagePaths(game);
                if (candidates == null || candidates.Count == 0)
                {
                    // The common case: this game has no plugin cover. Render
                    // nothing so the theme's own artwork shows through, and do
                    // not log - it is not an error.
                    ShowNothing();
                    return;
                }

                // The pick the ROTATION made, when there is one for this game.
                //
                // Choosing again here meant two independent rolls for the same
                // tile: the rotation writes Game.CoverImage, Playnite's own
                // PART_ImageCover picks that up, and this control - drawing on
                // top of it - had selected something else. Two different covers
                // for one game, updating at different moments, which is the
                // image seen flipping back and forth. One source of truth for
                // the pick is the only fix, and it is not about how the tile
                // underneath gets notified.
                //
                // The published value names the pick and the game it belongs
                // to, set together by the publisher precisely so the two cannot
                // drift apart.
                string path = null;

                if (!settings.UsesDirectRendering(ArtworkKind.Cover) &&
                    string.Equals(settings.CurrentCoverGameId, game.Id.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    path = settings.CurrentCoverPath;
                }

                // No published pick for this game - a tile scrolled past
                // without ever being selected, so rotation has not run for it.
                // Choosing here is then the only way it shows anything.
                if (string.IsNullOrEmpty(path))
                {
                    SelectionMode effectiveMode = settings.GetSelectionMode(
                        game.Id, ArtworkKind.Cover, settings.CoverSelectionMode);
                    SelectionOrder effectiveOrder = settings.GetSelectionOrder(
                        game.Id, ArtworkKind.Cover, settings.CoverSelectionOrder);

                    // Losing selection is NOT a new selection. In EverySelection
                    // mode the picker must retain the artwork already displayed
                    // on this tile. The old code re-rolled whenever Refresh() ran,
                    // then ShowStaticVideoPoster exposed native while decoding.
                    // The revision also covers tiles unloaded during navigation:
                    // they advance once when they next become selected.
                    bool advance = selectedTile &&
                        (_lastSelectedPickRevision != _selectionRevision || _advancePickRequested);
                    bool retain = effectiveMode == SelectionMode.EverySelection &&
                        !advance && !string.IsNullOrEmpty(_previousPick) &&
                        IsUsable(_previousPick) && candidates.Any(candidate =>
                            string.Equals(candidate, _previousPick, StringComparison.OrdinalIgnoreCase));
                    path = retain ? _previousPick : _selector.Select(
                        CoverSelectionKey(game.Id), candidates, _previousPick,
                        effectiveMode, effectiveOrder);
                    if (effectiveMode == SelectionMode.EverySelection && _fileLogger?.IsEnabled == true &&
                        (advance || (!selectedTile && !retain)))
                        _fileLogger.Log($"COVER selection-pick game={game.Id} selected={selectedTile} advance={advance} retained={retain} file={Path.GetFileName(path)}");
                    if (effectiveMode == SelectionMode.EverySelection && selectedTile)
                    {
                        _lastSelectedPickRevision = _selectionRevision;
                        _advancePickRequested = false;
                    }
                }

                // Recorded before use, so a pick that turns out to be unusable
                // still counts as tried and rotation moves past it.
                _previousPick = path;

                if (!IsUsable(path))
                {
                    path = FirstUsable(candidates, path);
                }

                if (string.IsNullOrEmpty(path))
                {
                    // Every candidate is missing. Worth saying once per path.
                    if (_previousPick != null && _loggedFailures.Add(_previousPick))
                    {
                        Logger.Warn($"ImageRotater: no usable cover image for \"{game.Name}\"");
                    }

                    ShowPlaceholder();
                    return;
                }

                if (!settings.ShouldAnimateCover(selectedTile))
                {
                    if (PosterFrame.IsAnimated(path))
                    {
                        path = PosterFrame.For(path);
                        if (string.IsNullOrEmpty(path)) path = FirstStillCandidate(candidates);
                    }
                    else if (PosterFrame.IsVideo(path))
                    {
                        // Losing focus must not start a new rotation and must
                        // not expose the native image for FFmpeg's decode delay.
                        // Switch directly to an already existing fixed candidate
                        // while the paused video remains visible underneath.
                        if (!selectedTile)
                            ShowUnfocusedVideoStill(path, candidates);
                        else
                            ShowStaticVideoPoster(path, FirstStillCandidate(candidates));
                        return;
                    }
                    if (string.IsNullOrEmpty(path))
                    {
                        ShowNothing();
                        return;
                    }
                }
                CancelStaticVideoPoster();
                if (selectedTile && !PosterFrame.IsMotion(path))
                    _lastFixedCoverPath = path;
                PresentCover(path);

            }
            catch (Exception ex)
            {
                // Nothing may escape into Playnite's layout pass: an exception
                // there is not caught above us and takes the process down with
                // no dialog and nothing in the log.
                Logger.Error(ex, "ImageRotater cover refresh failed");
                if (_nativeInjected) NativeFallbackRequested?.Invoke();
            }
        }

        private void PresentCover(string path)
        {
            // Video is a third channel, and a different renderer: WPF's
            // imaging stack cannot decode a container, so this cannot be a
            // mode of the Image.
            if (PosterFrame.IsVideo(path))
            {
                ShowVideo(path);
                return;
            }

            // Exactly one channel drives the Image at a time. Static picks
            // go through the DataContext path binding; GIFs go through
            // XamlAnimatedGif's attached property, which owns Image.Source
            // while active. Setting both would race - the one-channel rule
            // that already bit this control once (the Content shadowing).
            //
            // Noted before the teardown: when a video WAS here, the still
            // replacing it has no previous image to crossfade from, so
            // TargetUpdated fades the incoming still itself instead.
            _replacingVideo = DisplayVideo.Visibility == Visibility.Visible &&
                DisplayVideo.Source != null;

            // Video -> image is a real media handoff, not a teardown.
            // Keep the video fully opaque until the incoming image has
            // actually decoded; otherwise Playnite's native cover behind
            // this control flashes through for a frame or two.
            if (!_replacingVideo)
            {
                StopVideo();
            }

            if (PosterFrame.IsAnimated(path))
            {
                // Order matters. The attached property takes Image.Source
                // synchronously, so it goes FIRST - clearing the binding
                // first would blank the tile until the animation loaded.
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, new Uri(path));
                _data.ImagePath = string.Empty;
                _animating = true;
                if (DisplayImage.Source != null) NotifyNativeMediaReady(DisplayImage);
                LowerVeil();

                if (_replacingVideo)
                {
                    Dispatcher.BeginInvoke(
                        new Action(() => FadeOutVideoOverReadyImage()),
                        System.Windows.Threading.DispatcherPriority.Render);
                }
            }
            else
            {
                // And the other way round here, for the opposite reason.
                //
                // The still arrives through a binding marked IsAsync=True,
                // so it lands some time AFTER this returns. Releasing the
                // animation first left Image.Source empty for that whole
                // gap - a visible blank on every animated-to-still
                // rotation. Handing over the path first means the old frame
                // stays up until the new image is decoded and ready.
                //
                // IsAsync is not negotiable: a synchronous decode inside a
                // Fullscreen tile's layout pass is what took Playnite down
                // before, so the fix has to work with the delay rather than
                // remove it.
                StagePreviousCover(path);
                _data.ImagePath = path;
                _animating = false;

                // The animation is released by the same TargetUpdated
                // handler that runs the crossfade, so there is no separate
                // deferral to get right.
            }

            DisplayImage.Visibility = Visibility.Visible;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
            PublishNativeCoverReady();
        }

        // BackgroundRotationService keeps cover Session selections under a
        // derived key so they cannot collide with a background selection for
        // the same game. The renderer must use the exact same key whenever it
        // has to consult the selector directly; using game.Id here created a
        // second independent Session roll and caused a cover to change on focus.
        private static Guid CoverSelectionKey(Guid gameId)
        {
            byte[] bytes = gameId.ToByteArray();
            bytes[0] ^= 0xC0;
            return new Guid(bytes);
        }

        private static bool IsUsable(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A VIDEO losing selection is a display-policy change, not a new
        // EverySelection pick. Do not restart the selector or reveal native
        // art while a still is loading. The playing video is our outgoing layer.
        private void ShowUnfocusedVideoStill(string videoPath, IReadOnlyList<string> candidates)
        {
            string still = ChooseUnfocusedStill(candidates, _lastFixedCoverPath);
            if (string.IsNullOrEmpty(still))
            {
                // An enabled synchronized Artwork Pack replaces the normal
                // candidate pool with the pack's single cover. When that cover
                // is a video, no still is present in 'candidates' even though
                // Playnite has a perfectly valid native cover. Resolve it
                // independently, without changing the actual rotation pick.
                var folderSource = _source as FolderImageSource;
                still = folderSource?.GetOriginalStillPath(GameContext);
                if (!string.IsNullOrEmpty(still) &&
                    _fileLogger?.IsEnabled == true)
                    _fileLogger.Log($"COVER unfocused-native-fallback game={GameContext.Id} still={Path.GetFileName(still)}");
            }
            if (string.IsNullOrEmpty(still))
            {
                // No usable fixed artwork: preserve the existing poster
                // fallback, and log why the normal video->still fade was not used.
                if (_fileLogger?.IsEnabled == true)
                    _fileLogger.Log($"COVER unfocused-still-unavailable game={GameContext.Id} candidates={candidates?.Count ?? 0} fallback=poster");
                ShowStaticVideoPoster(videoPath, null);
                return;
            }

            if (string.Equals(videoPath, _unfocusedVideoPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(still, _unfocusedStillPath, StringComparison.OrdinalIgnoreCase))
                return;

            CancelUnfocusedStillSwap();
            _unfocusedVideoPath = videoPath;
            _unfocusedStillPath = still;
            int revision = ++_unfocusedSwapRevision;
            Guid gameId = GameContext.Id;
            var cancellation = _unfocusedStillCancellation = new CancellationTokenSource();

            // Keep playback visible while decoding the still. Pausing here
            // caused the old 'stuck frame' even when loading took 100+ ms.
            // Pause only after the new image is actually ready.

            if (_fileLogger?.IsEnabled == true)
                _fileLogger.Log($"COVER unfocused-still-load game={gameId} video={Path.GetFileName(videoPath)} still={Path.GetFileName(still)}");

            // Decode to tile size; otherwise a 4K native cover in each visible
            // tile would allocate a full-resolution bitmap for a 200px slot.
            // Stretch.None must retain native pixel dimensions instead.
            int decodeWidth = DisplayImage.Stretch == Stretch.None ? 0 :
                (int)Math.Min(1920, Math.Max(512,
                    Math.Ceiling(Math.Max(ActualWidth, DisplayImage.ActualWidth) * 2.0)));
            LoadUnfocusedStillAsync(still, gameId, revision, decodeWidth, cancellation.Token);
        }

        private static BitmapImage DecodeUnfocusedStill(string path, int decodeWidth)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.EndInit();
            bitmap.Freeze(); // Safe to hand to the WPF UI dispatcher.
            return bitmap;
        }

        private async void LoadUnfocusedStillAsync(string still, Guid gameId,
            int revision, int decodeWidth, CancellationToken token)
        {
            try
            {
                BitmapImage bitmap = await Task.Run(async () =>
                {
                    await UnfocusedStillDecodeGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        BitmapImage decoded = DecodeUnfocusedStill(still, decodeWidth);
                        token.ThrowIfCancellationRequested();
                        return decoded;
                    }
                    finally { UnfocusedStillDecodeGate.Release(); }
                }, token);

                // The continuation is normally on the WPF dispatcher, but
                // explicitly marshal to it for safety after a tile unload.
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _nativeReleased ||
                        revision != _unfocusedSwapRevision || GameContext?.Id != gameId ||
                        IsSelectedTile ||
                        !string.Equals(still, _unfocusedStillPath, StringComparison.OrdinalIgnoreCase))
                        return;

                    // Dedicated ready image below the video: its visibility
                    // never depends on DisplayImage.TargetUpdated firing.
                    UnfocusedStillImage.Source = bitmap;
                    UnfocusedStillImage.BeginAnimation(OpacityProperty, null);
                    UnfocusedStillImage.Opacity = 1.0;
                    UnfocusedStillImage.Visibility = Visibility.Visible;
                    SetValue(IsCoverDisplayedPropertyKey, true);
                    PublishNativeCoverReady();

                    // Keep the ordinary data path available to theme hosts.
                    // The independent image remains above that bound image.
                    _data.ImagePath = still;
                    DisplayImage.Visibility = Visibility.Visible;
                    _replacingVideo = false;
                    if (_fileLogger?.IsEnabled == true)
                        _fileLogger.Log($"COVER unfocused-still-ready game={gameId} still={Path.GetFileName(still)} size={bitmap.PixelWidth}x{bitmap.PixelHeight}");
                    if (DisplayVideo.Source != null && DisplayVideo.Visibility == Visibility.Visible)
                    {
                        try { DisplayVideo.Pause(); _videoPlaying = false; }
                        catch (Exception ex) { Logger.Warn(ex, "ImageRotater: unable to pause ready cover handoff"); }
                    }
                    FadeUnfocusedVideoToReadyStill(revision, gameId);
                }, DispatcherPriority.Loaded);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not decode unfocused cover still");
                if (_fileLogger?.IsEnabled == true)
                    _fileLogger.Log($"COVER unfocused-still-failed game={gameId} still={Path.GetFileName(still)} reason={ex.GetType().Name}");
                // Preserve the existing video instead of exposing the native
                // layer through a black/empty image. Never fade on failure.
            }
        }

        private void FadeUnfocusedVideoToReadyStill(int revision, Guid gameId)
        {
            if (DisplayVideo.Source == null || DisplayVideo.Visibility != Visibility.Visible)
                return; // Already released (e.g. virtualization during decode).

            int videoGeneration = _videoGeneration;
            int fadeRevision = ++_videoFadeRevision;
            bool finished = false;
            DispatcherTimer watchdog = null;
            Action complete = () =>
            {
                if (finished) return;
                finished = true;
                // A stale Completed from an old animation must not stop the
                // watchdog belonging to a newer game/selection.
                watchdog?.Stop();
                if (ReferenceEquals(_unfocusedFadeWatchdog, watchdog))
                    _unfocusedFadeWatchdog = null;
                if (revision != _unfocusedSwapRevision ||
                    fadeRevision != _videoFadeRevision ||
                    videoGeneration != _videoGeneration ||
                    GameContext?.Id != gameId || IsSelectedTile ||
                    UnfocusedStillImage.Source == null ||
                    UnfocusedStillImage.Visibility != Visibility.Visible)
                    return;

                StopVideo();
                if (_fileLogger?.IsEnabled == true)
                    _fileLogger.Log($"COVER unfocused-still-complete game={gameId}");
            };

            // A MediaElement's animation Completed is not guaranteed during a
            // Fullscreen tile unload/reload. Timer backstop finalizes the swap.
            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(250)));
            fade.Completed += (s, e) => complete();
            HideLoopStandby();
            DisplayVideo.BeginAnimation(OpacityProperty, fade);

            _unfocusedFadeWatchdog?.Stop();
            watchdog = new DispatcherTimer(DispatcherPriority.Loaded, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _unfocusedFadeWatchdog = watchdog;
            watchdog.Tick += (s, e) => { watchdog.Stop(); complete(); };
            watchdog.Start();
        }

        private static string ChooseUnfocusedStill(IReadOnlyList<string> candidates, string lastStill)
        {
            if (!string.IsNullOrEmpty(lastStill) && IsUsable(lastStill) &&
                !PosterFrame.IsMotion(lastStill) && candidates != null &&
                candidates.Any(c => string.Equals(c, lastStill, StringComparison.OrdinalIgnoreCase)))
                return lastStill;
            if (candidates != null)
                foreach (string candidate in candidates)
                    if (IsUsable(candidate) && !PosterFrame.IsMotion(candidate))
                        return candidate;
            return null;
        }

        // A new selection, policy change, or recycled tile invalidates all
        // pending decode/fade callbacks before the overlay is removed.
        private void CancelUnfocusedStillSwap()
        {
            if (_unfocusedVideoPath == null &&
                UnfocusedStillImage.Source == null && _unfocusedStillCancellation == null)
                return;
            ++_unfocusedSwapRevision;
            ++_videoFadeRevision;
            ++_fadeGeneration;
            _unfocusedStillCancellation?.Cancel();
            _unfocusedStillCancellation?.Dispose();
            _unfocusedStillCancellation = null;
            _unfocusedFadeWatchdog?.Stop();
            _unfocusedFadeWatchdog = null;
            _unfocusedVideoPath = null;
            _unfocusedStillPath = null;
            _replacingVideo = false;
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.Opacity = 1.0;
            UnfocusedStillImage.BeginAnimation(OpacityProperty, null);
            UnfocusedStillImage.Visibility = Visibility.Collapsed;
            UnfocusedStillImage.Source = null;
        }

        private static string FirstStillCandidate(IReadOnlyList<string> candidates)
        {
            if (candidates == null)
            {
                return null;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                if (!PosterFrame.IsMotion(candidate) && IsUsable(candidate))
                {
                    return candidate;
                }

                if (PosterFrame.IsAnimated(candidate))
                {
                    string poster = PosterFrame.For(candidate);
                    if (!string.IsNullOrEmpty(poster) && IsUsable(poster))
                    {
                        return poster;
                    }
                }
            }

            return null;
        }

        private static string FirstUsable(IReadOnlyList<string> candidates, string skip)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUsable(candidates[i]))
                {
                    return candidates[i];
                }
            }

            return null;
        }

        private void ShowNothing(bool preservePosterRequest = false)
        {
            CancelUnfocusedStillSwap();
            if (!preservePosterRequest) CancelStaticVideoPoster();
            _nativeCoverReadyAnnounced = false;
            _nativeMeasureMedia = null;
            NativeMediaSize = Size.Empty;
            SetValue(IsCoverDisplayedPropertyKey, false);
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
            ClearPreviousCover();
            LowerVeil();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
            if (_nativeInjected) NativeFallbackRequested?.Invoke();
        }

        private void ShowPlaceholder()
        {
            CancelUnfocusedStillSwap();
            CancelStaticVideoPoster();
            _nativeCoverReadyAnnounced = false;
            _nativeMeasureMedia = null;
            NativeMediaSize = Size.Empty;
            SetValue(IsCoverDisplayedPropertyKey, false);
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
            ClearPreviousCover();
            LowerVeil();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Visible;
            if (_nativeInjected) NativeFallbackRequested?.Invoke();
        }

        // Parks the cover currently on screen on the layer underneath, so the
        // incoming one has something to dissolve FROM.
        //
        // Called before the bound path changes. A Fullscreen tile's own cover
        // is a plain Image rather than a FadeImage, so unlike backgrounds there
        // is no theme transition to defer to - the plugin has to do this or the
        // switch is a hard cut.
        //
        // Nothing is staged when the path is not actually changing. Refresh
        // runs on every selection announcement and usually resolves the same
        // published pick; the data context drops an unchanged path without a
        // notification, so no picture arrives and no TargetUpdated fires to
        // finish what was started here. A staged copy of the same cover was
        // invisible; a veil raised for it stayed up - the tile went black on
        // selection and only recovered at the next slideshow tick.
        private void StagePreviousCover(string incomingPath)
        {
            try
            {
                // Cancel visual state left by a transition interrupted by fast navigation.
                DisplayImage.BeginAnimation(OpacityProperty, null);
                DisplayImage.BeginAnimation(Image.SourceProperty, null);
                PreviousImage.BeginAnimation(Image.SourceProperty, null);
                RestoreCoverBitmapScaling();
                DisplayImage.Opacity = 1.0;
                DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
                PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;
                DisplayImage.Effect = null;
                PreviousImage.Effect = null;
                if (_cutNextStage)
                {
                    _cutNextStage = false;
                    ClearPreviousCover();
                    return;
                }

                if (string.Equals(incomingPath, _data.ImagePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Invalidate the outgoing Mosaic (and any async ready callback)
                // NOW, not only after the new bitmap finishes loading. Otherwise
                // an older Completed may unmask the departing cover while the
                // next selection is still decoding, exposing a third image.
                ++_fadeGeneration;
                PreviousImage.BeginAnimation(OpacityProperty, null);
                PreviousImage.OpacityMask = null;
                DisplayImage.OpacityMask = null;

                if (Transition.CoverStyle == TransitionStyle.Cut ||
                    DisplayImage.Source == null || DisplayImage.Visibility != Visibility.Visible)
                {
                    ClearPreviousCover();
                    return;
                }

                PreviousImage.Source = DisplayImage.Source;
                PreviousImage.Opacity = 1.0;
                PreviousImage.Visibility = Visibility.Visible;

                // A flash goes up NOW, over the old picture, so it is full by
                // the time the new one lands underneath it. The old layer is
                // still staged: the picture can arrive before the veil is
                // opaque, and must not show through early.
                //
                // And it usually does arrive first - a cached cover decodes in
                // a few milliseconds. The lowering therefore waits for the
                // raise to finish (see CrossfadePreviousCover), or the veil
                // would turn round at a tenth of its height and no flash
                // would ever be seen.
                if (Transition.IsFlash(Transition.CoverStyle))
                {
                    Veil.Fill = new System.Windows.Media.SolidColorBrush(Transition.FlashColor(Transition.CoverStyle));
                    Veil.Visibility = Visibility.Visible;

                    int raise = ++_veilRaiseGeneration;
                    _veilRising = true;
                    _veilLowerPending = false;

                    var up = new System.Windows.Media.Animation.DoubleAnimation(
                        1.0, new Duration(Transition.CoverHalf));

                    // Completed fires for a replaced animation too, so a raise
                    // superseded by a newer one must not report that one done.
                    up.Completed += (s, e) =>
                    {
                        if (raise != _veilRaiseGeneration)
                        {
                            return;
                        }

                        _veilRising = false;

                        if (_veilLowerPending)
                        {
                            _veilLowerPending = false;
                            DropVeil();
                        }
                    };

                    Veil.BeginAnimation(OpacityProperty, up);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not stage the previous cover");
            }
        }

        // Runs when the async binding actually delivers the new cover.
        //
        // This is the only moment the crossfade can start. The binding is
        // asynchronous, so at the point the path was set the picture did not
        // exist yet - an earlier version queued a dispatcher callback and hoped
        // it landed afterwards, which is guesswork this event replaces.
        private void DisplayImage_TargetUpdated(
            object sender, System.Windows.Data.DataTransferEventArgs e)
        {
            if (_nativeInjected && _nativeReleased)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_nativeReleased) DisplayImage.SetCurrentValue(Image.SourceProperty, null);
                }), DispatcherPriority.Loaded);
                return;
            }
            // A null delivery - the path was cleared - is not a picture
            // arriving, and must not finish a transition that is waiting for
            // one. The paths that clear the image tear their own layers down.
            if (DisplayImage.Source == null)
            {
                return;
            }

            // The GIF behaviour owns Image.Source while attached, and a still
            // arriving means it is time to let go.
            if (!string.IsNullOrEmpty(_data.ImagePath))
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            }

            // A late IsAsync result may belong to a selection made before
            // the current one. Mosaic must NEVER unveil such an old bitmap.
            // The staged departing image remains fully opaque until the
            // requested Source arrives and triggers TargetUpdated again.
            if (Transition.CoverStyle == TransitionStyle.Mosaic &&
                !string.IsNullOrEmpty(_data.ImagePath) &&
                !MosaicSourceGuard.MatchesRequestedPath(DisplayImage.Source, _data.ImagePath))
            {
                Transition.DebugLog?.Invoke("COVER mosaic-stale-binding ignored requested=" + _data.ImagePath);
                return;
            }

            // Wait for the picture to be READY, not merely delivered.
            //
            // TargetUpdated says the binding produced a BitmapImage, which is
            // not the same as that image having pixels: with IsAsync the decode
            // can still be in flight. Fading the old layer out at this point
            // uncovers an image that has not drawn yet, which is the flash of
            // whatever sits behind the control.
            CrossfadeWhenReady(DisplayImage.Source as System.Windows.Media.Imaging.BitmapImage);
        }

        // Starts the crossfade once the incoming image can actually be drawn.
        //
        // IsDownloading covers the case that matters here - a large still, or a
        // file on a slow disk. A BitmapImage that is already decoded reports
        // false and the fade starts immediately, so the common case costs
        // nothing.
        private void CrossfadeWhenReady(System.Windows.Media.Imaging.BitmapImage bitmap)
        {
            if (bitmap == null || !bitmap.IsDownloading)
            {
                if (Transition.CoverStyle == TransitionStyle.Pixelate)
                {
                    PixelateFrames.QueuePrewarm(DisplayImage.Source);
                }
                CrossfadePreviousCover();
                return;
            }

            // Guarded by the same generation counter as the fade itself: a
            // slow image that finishes after the user has moved on twice must
            // not start a transition for a cover no longer on screen.
            int generation = _fadeGeneration;

            // A backstop, because waiting on an event that may never arrive is
            // how a tile gets stuck showing its OLD cover forever.
            //
            // IsDownloading is true for a bitmap still being fetched, but the
            // completion events are not guaranteed to fire for every source -
            // a cached or already-decoded local file can report downloading and
            // then simply never raise either one. Before this, that left the
            // outgoing layer opaque over the new cover with nothing to clear
            // it: the tile looked like it had not rotated at all.
            var backstop = new System.Windows.Threading.DispatcherTimer
            {
                // Large local covers can occasionally need longer than 600 ms
                // to finish their asynchronous decode. Releasing the old layer
                // too early exposes an unready image and looks like a hard
                // flash instead of a transition. This remains only a safety
                // fallback; normally DownloadCompleted fires first.
                Interval = TimeSpan.FromMilliseconds(1500)
            };

            backstop.Tick += (s, e) =>
            {
                backstop.Stop();

                if (generation == _fadeGeneration)
                {
                    if (Transition.CoverStyle == TransitionStyle.Pixelate)
                    {
                        PixelateFrames.QueuePrewarm(DisplayImage.Source);
                    }
                    CrossfadePreviousCover();
                }
            };

            backstop.Start();

            System.EventHandler onReady = null;
            System.EventHandler<System.Windows.Media.ExceptionEventArgs> onFailed = null;

            onReady = (s, e) =>
            {
                backstop.Stop();
                bitmap.DownloadCompleted -= onReady;
                bitmap.DownloadFailed -= onFailed;

                if (generation == _fadeGeneration)
                {
                    if (Transition.CoverStyle == TransitionStyle.Pixelate)
                    {
                        PixelateFrames.QueuePrewarm(DisplayImage.Source);
                    }
                    CrossfadePreviousCover();
                }
            };

            // A failed decode still has to release the old layer, or it stays
            // frozen on screen forever.
            onFailed = (s, e) =>
            {
                backstop.Stop();
                bitmap.DownloadCompleted -= onReady;
                bitmap.DownloadFailed -= onFailed;

                if (generation == _fadeGeneration)
                {
                    CrossfadePreviousCover();
                }
            };

            bitmap.DownloadCompleted += onReady;
            bitmap.DownloadFailed += onFailed;
        }

        // Dissolves the outgoing layer away, revealing the cover already opaque
        // beneath it.
        //
        // The OLD layer fades, not the new one: the incoming cover is fully
        // drawn underneath from the first frame, so nothing behind the control
        // is ever visible through the transition. Fading the new one up would
        // show the tile's own artwork through the gap.
        private void CrossfadePreviousCover()
        {
            if (DisplayImage.Source != null) NotifyNativeMediaReady(DisplayImage);
            try
            {
                // Under a flash there is nothing to dissolve: the new picture
                // is already whole beneath the veil, so the old layer goes at
                // once and the veil comes down over it - once it is fully up.
                if (Veil.Visibility == Visibility.Visible)
                {
                    // For video-to-image, dissolve the video after the still reports ready.
                    if (_replacingVideo)
                    {
                        LowerVeil();
                        FadeOutVideoOverReadyImage();
                        return;
                    }

                    if (_veilRising)
                    {
                        _veilLowerPending = true;
                    }
                    else
                    {
                        DropVeil();
                    }

                    return;
                }

                if (PreviousImage.Source == null ||
                    PreviousImage.Visibility != Visibility.Visible)
                {
                    // For video-to-image, fade the video only after the still is ready.
                    if (_replacingVideo)
                    {
                        FadeOutVideoOverReadyImage();
                    }

                    return;
                }

                _replacingVideo = false;

                TransitionStyle style = Transition.CoverStyle;

                if (style == TransitionStyle.SlideFromRight)
                {
                    StartSlideCoverTransition(style);
                    return;
                }

                if (style == TransitionStyle.SideReveal)
                {
                    StartSideRevealCoverTransition(style);
                    return;
                }

                if (style == TransitionStyle.DiagonalReveal)
                {
                    StartDiagonalRevealCoverTransition(style);
                    return;
                }

                if (style == TransitionStyle.DepthShift)
                {
                    StartDepthShiftCoverTransition(style);
                    return;
                }

                if (style == TransitionStyle.Mosaic)
                {
                    StartMosaicCoverTransition(style);
                    return;
                }

                if (style == TransitionStyle.Pixelate)
                {
                    StartPixelateCoverTransition(style);
                    return;
                }

                if (style == TransitionStyle.Zoom)
                {
                    StartZoomCoverTransition(style);
                    return;
                }

                if (style == TransitionStyle.Focus)
                {
                    StartFocusCoverTransition(style);
                    return;
                }

                var fade = new System.Windows.Media.Animation.DoubleAnimation(
                    1.0, 0.0, new Duration(Transition.CoverDuration));

                // Completed fires even for a REPLACED animation, so without a
                // generation an older fade tears down the layer a newer one is
                // still using.
                int generation = ++_fadeGeneration;

                fade.Completed += (s, e) =>
                {
                    if (generation == _fadeGeneration)
                    {
                        ClearPreviousCover();
                    }
                };

                PreviousImage.BeginAnimation(OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                ClearPreviousCover();
                Logger.Warn(ex, "ImageRotater: could not crossfade the cover");
            }
        }

        private void StartSideRevealCoverTransition(TransitionStyle style)
        {
            StartFeatheredCoverRevealTransition(style);
        }

        private void StartDiagonalRevealCoverTransition(TransitionStyle style)
        {
            StartFeatheredCoverRevealTransition(style);
        }

        private void StartFeatheredCoverRevealTransition(TransitionStyle style)
        {
            // IR -> IR has its outgoing still ABOVE the incoming still.
            // Mask the old image inversely to the incoming-mask used by the
            // native relays. Both now use the same edge, softness and timing.
            int generation = ++_fadeGeneration;
            DisplayImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 1.0;
            PreviousImage.Opacity = 1.0;
            DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
            PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;
            DisplayImage.Effect = null;
            PreviousImage.Effect = null;
            DisplayImage.OpacityMask = null;
            PreviousImage.OpacityMask = null;

            double width = PreviousImage.ActualWidth > 0 ? PreviousImage.ActualWidth : RootGrid.ActualWidth;
            double height = PreviousImage.ActualHeight > 0 ? PreviousImage.ActualHeight : RootGrid.ActualHeight;
            var bounds = new Rect(0, 0, Math.Max(1.0, width), Math.Max(1.0, height));
            var mask = CoverRevealMask.Create(style, bounds, 0.0, false);
            PreviousImage.OpacityMask = mask;

            CoverRevealMask.Animate(mask, style, bounds, Transition.CoverDuration, () =>
            {
                if (generation != _fadeGeneration) return;
                // Finish only once the final all-revealed frame was painted;
                // previous mask stays fully transparent until then.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (generation != _fadeGeneration) return;
                    if (ReferenceEquals(PreviousImage.OpacityMask, mask))
                    {
                        // ClearPreviousCover first: removing the old full image
                        // before its soft mask would flash the former cover.
                        ClearPreviousCover();
                    }
                }));
            });
        }

        private Rect GetRevealBounds(FrameworkElement element)
        {
            double width = 0.0;
            double height = 0.0;

            if (element != null)
            {
                width = element.ActualWidth;
                height = element.ActualHeight;
            }

            if (width <= 0) width = DisplayImage.ActualWidth;
            if (height <= 0) height = DisplayImage.ActualHeight;
            if (width <= 0) width = RootGrid.ActualWidth;
            if (height <= 0) height = RootGrid.ActualHeight;

            return new Rect(0, 0, Math.Max(1.0, width), Math.Max(1.0, height));
        }

        private bool IsFeatheredRevealStyle(TransitionStyle style)
        {
            return style == TransitionStyle.SideReveal ||
                style == TransitionStyle.DiagonalReveal;
        }

        // A native PART_ImageCover entry and the media control must never
        // animate the same handoff in sequence. A video replacing an existing
        // IR image/video is different: the control owns that internal swap.
        internal static bool NativeOwnsInitialVideoEntry(bool nativeInjected,
            bool replacingImage, bool replacingVideo) =>
            nativeInjected && !replacingImage && !replacingVideo;

        private void StartVideoIncomingReveal(TransitionStyle style, int generation)
        {
            if (!_videoReplacingImage)
            {
                CompleteVideoReveal();
                return;
            }

            // MediaElement is ABOVE DisplayImage in RootGrid. Masking the
            // outgoing still (the previous implementation) has no visible
            // effect once the video becomes opaque, and looks like a fade
            // followed by a second wipe. Reveal the incoming video itself.
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.OpacityMask = null;
            Rect bounds = GetRevealBounds(DisplayVideo);
            var mask = CoverRevealMask.Create(style, bounds, 0.0, true);
            DisplayVideo.OpacityMask = mask;
            DisplayVideo.Opacity = 1.0;
            DisplayVideo.Visibility = Visibility.Visible;

            DisplayImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 1.0;
            DisplayImage.Visibility = Visibility.Visible;
            DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
            DisplayImage.Effect = null;
            DisplayImage.OpacityMask = null;

            CoverRevealMask.Animate(mask, style, bounds, Transition.CoverDuration, () =>
            {
                if (generation != _videoGeneration)
                {
                    return;
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (generation != _videoGeneration)
                    {
                        return;
                    }

                    if (ReferenceEquals(DisplayVideo.OpacityMask, mask))
                    {
                        DisplayVideo.OpacityMask = null;
                    }

                    CompleteVideoReveal();
                }));
            });
        }

        private void StartVideoOutgoingReveal(TransitionStyle style, TimeSpan duration,
            int fadeRevision, int videoGeneration, string fadingPath)
        {
            if (DisplayVideo.Source == null ||
                DisplayVideo.Visibility != Visibility.Visible)
            {
                StopVideo();
                return;
            }

            HideLoopStandby();
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.Opacity = 1.0;
            DisplayVideo.RenderTransform = System.Windows.Media.Transform.Identity;
            DisplayVideo.Effect = null;
            DisplayVideo.OpacityMask = null;

            Rect bounds = GetRevealBounds(DisplayVideo);
            var mask = CoverRevealMask.Create(style, bounds, 0.0, false);
            DisplayVideo.OpacityMask = mask;

            CoverRevealMask.Animate(mask, style, bounds, duration, () =>
            {
                if (fadeRevision != _videoFadeRevision ||
                    videoGeneration != _videoGeneration ||
                    !string.Equals(fadingPath, _videoPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (fadeRevision != _videoFadeRevision ||
                        videoGeneration != _videoGeneration ||
                        !string.Equals(fadingPath, _videoPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (ReferenceEquals(DisplayVideo.OpacityMask, mask))
                    {
                        DisplayVideo.OpacityMask = null;
                    }

                    StopVideo();
                    DisplayVideo.BeginAnimation(OpacityProperty, null);
                    DisplayVideo.Opacity = 1.0;
                }));
            });
        }

        private void StartDepthShiftCoverTransition(TransitionStyle style)
        {
            int generation = ++_fadeGeneration;

            DisplayImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 0.0;
            PreviousImage.Opacity = 1.0;

            DisplayImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            PreviousImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            var incomingScale = new System.Windows.Media.ScaleTransform(1.05, 1.05);
            var outgoingScale = new System.Windows.Media.ScaleTransform(1.0, 1.0);
            DisplayImage.RenderTransform = incomingScale;
            PreviousImage.RenderTransform = outgoingScale;

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
            DisplayImage.Effect = incomingBlur;
            PreviousImage.Effect = outgoingBlur;

            var easeOut = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            };
            var easeIn = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
            };

            var incomingFade = new System.Windows.Media.Animation.DoubleAnimation(
                0.0, 1.0, new Duration(Transition.CoverDuration))
            {
                EasingFunction = easeOut
            };
            incomingFade.Completed += (s, e) =>
            {
                if (generation != _fadeGeneration) return;
                DisplayImage.BeginAnimation(OpacityProperty, null);
                DisplayImage.Opacity = 1.0;
                incomingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                outgoingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                if (ReferenceEquals(DisplayImage.Effect, incomingBlur))
                {
                    DisplayImage.Effect = null;
                }
                DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
                PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;
                ClearPreviousCover();
            };

            var outgoingFade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.CoverDuration)
            };
            outgoingFade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                1.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.20)));
            outgoingFade.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
                0.0,
                System.Windows.Media.Animation.KeyTime.FromPercent(0.84),
                easeIn));

            var sharpen = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.CoverDuration)
            };
            sharpen.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                12.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.18)));
            sharpen.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
                0.0,
                System.Windows.Media.Animation.KeyTime.FromPercent(1.0),
                easeOut));

            var defocus = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.CoverDuration)
            };
            defocus.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                0.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.0)));
            defocus.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
                9.0,
                System.Windows.Media.Animation.KeyTime.FromPercent(0.52),
                easeIn));
            defocus.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                9.0, System.Windows.Media.Animation.KeyTime.FromPercent(1.0)));

            var inScaleX = new System.Windows.Media.Animation.DoubleAnimation(1.05, 1.0, new Duration(Transition.CoverDuration))
            {
                EasingFunction = easeOut,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            var inScaleY = new System.Windows.Media.Animation.DoubleAnimation(1.05, 1.0, new Duration(Transition.CoverDuration))
            {
                EasingFunction = easeOut,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            var outScaleX = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.965, new Duration(Transition.CoverDuration))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            var outScaleY = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.965, new Duration(Transition.CoverDuration))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };

            DisplayImage.BeginAnimation(OpacityProperty, incomingFade);
            PreviousImage.BeginAnimation(OpacityProperty, outgoingFade);
            incomingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, sharpen);
            outgoingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, defocus);
            incomingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, inScaleX);
            incomingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, inScaleY);
            outgoingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, outScaleX);
            outgoingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, outScaleY);
        }

        private void StartMosaicCoverTransition(TransitionStyle style)
        {
            int generation = ++_fadeGeneration;

            DisplayImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 1.0;
            PreviousImage.Opacity = 1.0;
            DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
            PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;
            DisplayImage.Effect = null;
            PreviousImage.Effect = null;
            DisplayImage.OpacityMask = null;
            PreviousImage.OpacityMask = null;

            MosaicMaskState mask = MosaicMask.Create();
            MosaicMask.SetVisible(mask, true);
            PreviousImage.OpacityMask = mask.Brush;

            var reveal = MosaicMask.BuildStoryboard(
                PreviousImage,
                mask,
                false,
                Transition.CoverDuration);

            reveal.Completed += (s, e) =>
            {
                if (generation != _fadeGeneration)
                {
                    // Replaced animations may still deliver Completed. Never
                    // tear down or expose the next selection's outgoing still.
                    return;
                }

                // Do not call MosaicMask.Stop or remove the mask while the
                // outgoing image is still visible: Stop makes EVERY old tile
                // opaque again, flashing an unrelated cover for one frame.
                PreviousImage.Visibility = Visibility.Collapsed;
                try { reveal.Remove(PreviousImage); } catch { }
                ClearPreviousCover();
                MosaicMask.Stop(mask);
            };

            reveal.Begin(PreviousImage, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace, true);
        }


        private async void StartPixelateCoverTransition(TransitionStyle style)
        {
            int generation = ++_fadeGeneration;

            ImageSource oldSource = PreviousImage.Source;
            ImageSource newSource = DisplayImage.Source;
            if (oldSource == null || newSource == null)
            {
                return;
            }

            BitmapScalingMode oldScaling = RenderOptions.GetBitmapScalingMode(PreviousImage);
            BitmapScalingMode newScaling = RenderOptions.GetBitmapScalingMode(DisplayImage);

            IReadOnlyList<ImageSource> oldLevels = PixelateFrames.GetCachedOrSource(oldSource);
            Task<IReadOnlyList<ImageSource>> oldWarm = PixelateFrames.GetLevelsAsync(oldSource);
            Task<IReadOnlyList<ImageSource>> newWarm = PixelateFrames.GetLevelsAsync(newSource);

            TimeSpan total = Transition.CoverDuration;
            TimeSpan oldPhase = TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.48);
            TimeSpan newPhase = TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.52);
            TimeSpan blend = TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.06);
            double incomingHold = blend.TotalMilliseconds / Math.Max(1.0, newPhase.TotalMilliseconds);

            DisplayImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
            PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;
            DisplayImage.Effect = null;
            PreviousImage.Effect = null;
            DisplayImage.OpacityMask = null;
            PreviousImage.OpacityMask = null;

            PreviousImage.Visibility = Visibility.Visible;
            PreviousImage.Opacity = 1.0;
            PreviousImage.BeginAnimation(Image.SourceProperty, null);
            PreviousImage.Source = oldSource;
            PreviousImage.SetCurrentValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.NearestNeighbor);

            DisplayImage.Visibility = Visibility.Visible;
            DisplayImage.Opacity = 0.0;
            DisplayImage.BeginAnimation(Image.SourceProperty, null);
            DisplayImage.Source = newSource;
            DisplayImage.SetCurrentValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.NearestNeighbor);

            if (oldLevels.Count > 1)
            {
                PreviousImage.BeginAnimation(
                    Image.SourceProperty,
                    PixelateFrames.BuildOutgoingPhase(oldLevels, oldPhase),
                    System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                _ = UpgradeOutgoingPixelateAsync();
            }

            async Task UpgradeOutgoingPixelateAsync()
            {
                IReadOnlyList<ImageSource> warmed = await oldWarm;
                if (generation != _fadeGeneration || warmed == null || warmed.Count <= 1)
                {
                    return;
                }

                PreviousImage.BeginAnimation(
                    Image.SourceProperty,
                    PixelateFrames.BuildOutgoingPhase(warmed, oldPhase),
                    System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
            }

            try
            {
                await Task.Delay(oldPhase);
                if (generation != _fadeGeneration)
                {
                    return;
                }

                IReadOnlyList<ImageSource> newLevels = await newWarm;
                if (generation != _fadeGeneration)
                {
                    return;
                }

                if (newLevels == null || newLevels.Count == 0)
                {
                    newLevels = new[] { newSource };
                }

                DisplayImage.BeginAnimation(Image.SourceProperty, null);
                DisplayImage.Source = newLevels[newLevels.Count - 1];
                DisplayImage.BeginAnimation(
                    Image.SourceProperty,
                    PixelateFrames.BuildIncomingPhase(newLevels, newPhase, incomingHold),
                    System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);

                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(
                    0.0, 1.0, new Duration(blend))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                    }
                };
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(
                    1.0, 0.0, new Duration(blend))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                    }
                };

                DisplayImage.BeginAnimation(OpacityProperty, fadeIn);
                PreviousImage.BeginAnimation(OpacityProperty, fadeOut);

                await Task.Delay(newPhase);
                if (generation != _fadeGeneration)
                {
                    return;
                }

                PreviousImage.BeginAnimation(Image.SourceProperty, null);
                DisplayImage.BeginAnimation(Image.SourceProperty, null);
                DisplayImage.Source = newSource;
                PreviousImage.SetCurrentValue(RenderOptions.BitmapScalingModeProperty, oldScaling);
                DisplayImage.SetCurrentValue(RenderOptions.BitmapScalingModeProperty, newScaling);
                DisplayImage.BeginAnimation(OpacityProperty, null);
                DisplayImage.Opacity = 1.0;
                ClearPreviousCover();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: Pixelate cover transition failed");
                if (generation == _fadeGeneration)
                {
                    PreviousImage.BeginAnimation(Image.SourceProperty, null);
                    DisplayImage.BeginAnimation(Image.SourceProperty, null);
                    DisplayImage.Source = newSource;
                    PreviousImage.SetCurrentValue(RenderOptions.BitmapScalingModeProperty, oldScaling);
                    DisplayImage.SetCurrentValue(RenderOptions.BitmapScalingModeProperty, newScaling);
                    DisplayImage.Opacity = 1.0;
                    ClearPreviousCover();
                }
            }
        }

        private void StartZoomCoverTransition(TransitionStyle style)
        {
            int generation = ++_fadeGeneration;

            DisplayImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 0.0;
            PreviousImage.Opacity = 1.0;

            DisplayImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            var incomingScale = new System.Windows.Media.ScaleTransform(1.08, 1.08);
            DisplayImage.RenderTransform = incomingScale;
            PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;

            var easeOut = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            };
            var easeIn = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
            };

            var incomingFade = new System.Windows.Media.Animation.DoubleAnimation(
                0.0, 1.0, new Duration(Transition.CoverDuration))
            {
                EasingFunction = easeOut
            };
            incomingFade.Completed += (s, e) =>
            {
                if (generation != _fadeGeneration) return;
                DisplayImage.BeginAnimation(OpacityProperty, null);
                DisplayImage.Opacity = 1.0;
                DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
                PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;
                ClearPreviousCover();
            };

            var outgoingFade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.CoverDuration)
            };
            outgoingFade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                1.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.34)));
            outgoingFade.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
                0.0,
                System.Windows.Media.Animation.KeyTime.FromPercent(0.92),
                easeIn));

            var zoomX = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, new Duration(Transition.CoverDuration))
            {
                EasingFunction = easeOut,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            var zoomY = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, new Duration(Transition.CoverDuration))
            {
                EasingFunction = easeOut,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };

            DisplayImage.BeginAnimation(OpacityProperty, incomingFade);
            PreviousImage.BeginAnimation(OpacityProperty, outgoingFade);
            incomingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, zoomX);
            incomingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, zoomY);
        }

        private void StartFocusCoverTransition(TransitionStyle style)
        {
            int generation = ++_fadeGeneration;

            DisplayImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 0.0;
            PreviousImage.Opacity = 1.0;
            DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
            PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;

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
            DisplayImage.Effect = incomingBlur;
            PreviousImage.Effect = outgoingBlur;

            var easeOut = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            };
            var easeIn = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
            };

            var incomingFade = new System.Windows.Media.Animation.DoubleAnimation(
                0.0, 1.0, new Duration(Transition.CoverDuration))
            {
                EasingFunction = easeOut
            };
            incomingFade.Completed += (s, e) =>
            {
                if (generation != _fadeGeneration) return;
                DisplayImage.BeginAnimation(OpacityProperty, null);
                DisplayImage.Opacity = 1.0;
                incomingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                outgoingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                if (ReferenceEquals(DisplayImage.Effect, incomingBlur))
                {
                    DisplayImage.Effect = null;
                }
                ClearPreviousCover();
            };

            var outgoingFade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.CoverDuration)
            };
            outgoingFade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                1.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.22)));
            outgoingFade.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
                0.0,
                System.Windows.Media.Animation.KeyTime.FromPercent(0.86),
                easeIn));

            var sharpen = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.CoverDuration)
            };
            sharpen.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                26.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.24)));
            sharpen.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
                0.0,
                System.Windows.Media.Animation.KeyTime.FromPercent(1.0),
                easeOut));

            var defocus = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Transition.CoverDuration)
            };
            defocus.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                0.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.08)));
            defocus.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
                20.0,
                System.Windows.Media.Animation.KeyTime.FromPercent(0.48),
                easeIn));
            defocus.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                20.0, System.Windows.Media.Animation.KeyTime.FromPercent(1.0)));

            DisplayImage.BeginAnimation(OpacityProperty, incomingFade);
            PreviousImage.BeginAnimation(OpacityProperty, outgoingFade);
            incomingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, sharpen);
            outgoingBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, defocus);
        }

        private void StartSlideCoverTransition(TransitionStyle style)
        {
            int generation = ++_fadeGeneration;
            TransitionDirection dir = TransitionDirection.FromRight;

            double distance = 40.0;
            double inX = 0.0, inY = 0.0, outX = 0.0, outY = 0.0;
            switch (dir)
            {
                case TransitionDirection.FromLeft:
                    inX = -distance; outX = distance * 0.5; break;
                case TransitionDirection.FromRight:
                    inX = distance; outX = -distance * 0.5; break;
                case TransitionDirection.FromTop:
                    inY = -distance; outY = distance * 0.5; break;
                case TransitionDirection.FromBottom:
                    inY = distance; outY = -distance * 0.5; break;
            }

            var incomingT = new System.Windows.Media.TranslateTransform(inX, inY);
            var outgoingT = new System.Windows.Media.TranslateTransform();
            DisplayImage.RenderTransform = incomingT;
            PreviousImage.RenderTransform = outgoingT;
            DisplayImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 0.15;
            PreviousImage.Opacity = 1.0;

            var ease = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            };
            var incomingFade = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, new Duration(Transition.CoverDuration)) { EasingFunction = ease };
            incomingFade.Completed += (s, e) =>
            {
                if (generation != _fadeGeneration) return;
                DisplayImage.BeginAnimation(OpacityProperty, null);
                DisplayImage.Opacity = 1.0;
                DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
                PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;
                ClearPreviousCover();
            };

            DisplayImage.BeginAnimation(OpacityProperty, incomingFade);
            PreviousImage.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.0, new Duration(Transition.CoverDuration)) { EasingFunction = ease });

            if (inX != 0.0) incomingT.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(0.0, new Duration(Transition.CoverDuration)) { EasingFunction = ease });
            if (inY != 0.0) incomingT.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0.0, new Duration(Transition.CoverDuration)) { EasingFunction = ease });
            if (outX != 0.0) outgoingT.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(outX, new Duration(Transition.CoverDuration)) { EasingFunction = ease });
            if (outY != 0.0) outgoingT.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(outY, new Duration(Transition.CoverDuration)) { EasingFunction = ease });
        }

        private void FadeOutVideoOverReadyImage()
        {
            if (!_replacingVideo)
            {
                return;
            }

            _replacingVideo = false;

            if (DisplayVideo.Source == null ||
                DisplayVideo.Visibility != Visibility.Visible)
            {
                StopVideo();
                return;
            }

            // Keep the still visible while the video layer fades out. The
            // loop standby must not show through the fading video.
            HideLoopStandby();
            DisplayImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 1.0;
            DisplayImage.Visibility = Visibility.Visible;

            string fadingPath = _videoPath;
            int videoGeneration = _videoGeneration;
            int fadeRevision = ++_videoFadeRevision;
            bool unfocusedSwap = _unfocusedVideoPath != null;
            TransitionStyle style = Transition.CoverStyle;

            // A passive video->still handoff is always a simple 250 ms
            // dissolve, not the user's active cover-transition effect.
            var fadeDuration = unfocusedSwap
                ? TimeSpan.FromMilliseconds(250)
                : Transition.CoverDuration;

            if (!unfocusedSwap && IsFeatheredRevealStyle(style))
            {
                StartVideoOutgoingReveal(style, fadeDuration, fadeRevision, videoGeneration, fadingPath);
                return;
            }

            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, 0.0, new Duration(fadeDuration));

            fade.Completed += (s, e) =>
            {
                if (fadeRevision != _videoFadeRevision ||
                    videoGeneration != _videoGeneration ||
                    !string.Equals(fadingPath, _videoPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                StopVideo();
                DisplayVideo.BeginAnimation(OpacityProperty, null);
                DisplayVideo.Opacity = 1.0;
            };

            DisplayVideo.BeginAnimation(OpacityProperty, fade);
        }

        private void RestoreCoverBitmapScaling()
        {
            foreach (Image image in new[] { DisplayImage, PreviousImage })
            {
                var binding = System.Windows.Data.BindingOperations.GetBindingExpression(
                    image, RenderOptions.BitmapScalingModeProperty);
                if (binding != null) binding.UpdateTarget();
                else RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
            }
        }

        private void ClearPreviousCover()
        {
            PreviousImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.BeginAnimation(Image.SourceProperty, null);
            DisplayImage.BeginAnimation(Image.SourceProperty, null);
            RestoreCoverBitmapScaling();
            PreviousImage.Opacity = 1.0;
            PreviousImage.RenderTransform = System.Windows.Media.Transform.Identity;
            DisplayImage.RenderTransform = System.Windows.Media.Transform.Identity;
            PreviousImage.Effect = null;
            DisplayImage.Effect = null;
            PreviousImage.OpacityMask = null;
            DisplayImage.OpacityMask = null;
            PreviousImage.Visibility = Visibility.Collapsed;
            PreviousImage.Source = null;
        }

        // The veil is opaque and the new picture is whole beneath it: swap
        // the layers out and bring the veil down over the result.
        private void DropVeil()
        {
            ClearPreviousCover();

            int veilGeneration = ++_fadeGeneration;
            var lower = new System.Windows.Media.Animation.DoubleAnimation(
                0.0, new Duration(Transition.CoverHalf));

            lower.Completed += (s, e) =>
            {
                if (veilGeneration == _fadeGeneration)
                {
                    LowerVeil();
                }
            };

            Veil.BeginAnimation(OpacityProperty, lower);
        }

        // Hard reset: no animation, no pending work.
        private void LowerVeil()
        {
            _veilRising = false;
            _veilLowerPending = false;
            Veil.BeginAnimation(OpacityProperty, null);
            Veil.Opacity = 0.0;
            Veil.Visibility = Visibility.Collapsed;
        }

        private int _veilRaiseGeneration;
        private bool _veilRising;
        private bool _veilLowerPending;

        private int _fadeGeneration;

        // True while the still now arriving is replacing a video, so the fade
        // runs on the incoming image - there is no outgoing layer to dissolve.
        private bool _replacingVideo;

        // Tracks whether a video is replacing an already visible still.
        private bool _videoReplacingImage;
        // A new video replacing an earlier video has no retained still: it
        // keeps the legacy opacity transition rather than being mistaken for
        // a first native -> IR entry.
        private bool _videoReplacingVideo;
        private int _videoHandoffStartedGeneration = -1;

        // Hands a video to the MediaElement and stands the Image down, so
        // exactly one renderer draws.
        private void ShowVideo(string path)
        {
            // Refresh/selection can ask for the SAME media again while
            // MediaOpened or the mask animation is still in flight. Do not
            // turn the video opaque or remove its outgoing still mid-reveal.
            if (!string.IsNullOrEmpty(_videoPath) &&
                string.Equals(_videoPath, path, StringComparison.OrdinalIgnoreCase) &&
                DisplayVideo.Source != null &&
                (_videoHandoffStartedGeneration != _videoGeneration ||
                 DisplayVideo.OpacityMask != null || DisplayVideo.Opacity < .999))
            {
                return;
            }

            // Any previous video fade is obsolete once playback becomes the
            // selected render target again (including the SAME video URI).
            ++_videoFadeRevision;
            _replacingVideo = false;
            // Keep the current still while the replacement video opens.
            _videoReplacingImage = DisplayImage.Visibility == Visibility.Visible &&
                DisplayImage.Source != null;
            _videoReplacingVideo = !_videoReplacingImage &&
                DisplayVideo.Visibility == Visibility.Visible && DisplayVideo.Source != null;

            if (!_videoReplacingImage)
            {
                _data.ImagePath = string.Empty;
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
                DisplayImage.Visibility = Visibility.Collapsed;
            }

            LowerVeil();
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
            ClearPreviousCover();

            // Only video covers need viewport tracking.
            EnsureViewportTracking();
            QueueViewportCheck();

            bool inViewport = IsActuallyInViewport();
            _inViewport = inViewport;

            bool sameVideo =
                !string.IsNullOrEmpty(_videoPath) &&
                string.Equals(_videoPath, path, StringComparison.OrdinalIgnoreCase) &&
                DisplayVideo.Source != null;

            if (sameVideo)
            {
                DisplayVideo.BeginAnimation(OpacityProperty, null);
                DisplayVideo.Opacity = 1.0;
                DisplayVideo.Visibility = Visibility.Visible;
                _animating = true;

                if (_videoReplacingImage)
                {
                    CompleteVideoReveal();
                }

                if (!inViewport)
                {
                    if (_videoPlaying)
                    {
                        try
                        {
                            DisplayVideo.Pause();
                            _videoPlaying = false;
                        }
                        catch
                        {
                        }
                    }

                    _pausedByViewport = true;
                    return;
                }

                if (!_videoPlaying)
                {
                    try
                    {
                        DisplayVideo.Play();
                        _videoPlaying = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "ImageRotater: could not resume cover video");
                    }
                }

                _pausedByViewport = false;
                return;
            }

            if (!inViewport)
            {
                StopVideo();
                _videoPath = path;
                _inViewport = false;
                EnsureViewportTracking();
                QueueViewportCheck();
                return;
            }

            _videoPath = path;
            int generation = ++_videoGeneration;
            _videoHandoffStartedGeneration = -1;

            StopCoverVideoLoop();
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.OpacityMask = null;
            DisplayVideo.Opacity = 0.0;
            DisplayVideo.Visibility = Visibility.Collapsed;

            if (DisplayVideo.Source != null)
            {
                try
                {
                    DisplayVideo.Stop();
                }
                catch
                {
                }

                _videoPlaying = false;
            }

            _animating = true;
            _pausedByViewport = false;

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (generation != _videoGeneration ||
                        !string.Equals(_videoPath, path, StringComparison.OrdinalIgnoreCase) ||
                        !IsLoaded || !IsActuallyInViewport())
                    {
                        return;
                    }

                    try
                    {
                        if (_nativeInjected) _nativeVideoOpenWatch = System.Diagnostics.Stopwatch.StartNew();
                        DisplayVideo.Source = new Uri(path);
                        DisplayVideo.Visibility = Visibility.Visible;
                        DisplayVideo.Play();
                        _videoPlaying = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "ImageRotater: could not start cover video");
                        return;
                    }

                    // Do not force Opacity=1 after 700 ms. MediaOpened is the
                    // only start signal for the handoff, and a reveal may
                    // legitimately be longer than 700 ms. The old still or
                    // native cover stays visible until the decoder is ready;
                    // MediaFailed handles a real decoder failure.
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CompleteVideoReveal()
        {
            PublishNativeCoverReady();
            if (!_videoReplacingImage)
            {
                return;
            }

            _videoReplacingImage = false;

            // Release the old still after the video reveal completes.
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            DisplayImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 1.0;
            DisplayImage.Visibility = Visibility.Collapsed;
            ClearPreviousCover();
        }

        // Restarts playback after the element is re-inserted into the tree.
        //
        // Selecting a Fullscreen tile calls Focus() then BringIntoView(),
        // which re-measures the tile panel; the panel removes and re-inserts
        // its containers, so this element is unloaded and loaded again. With
        // UnloadedBehavior=Manual the media survives that, but playback does
        // not resume on its own - and a Play() issued while the element was
        // detached is silently swallowed, which is exactly why the video
        // stopped the moment a tile became selected.
        private void DisplayVideo_Loaded(object sender, RoutedEventArgs e)
        {
            if (_unfocusedVideoPath != null) return;
            if (DisplayVideo.Source == null ||
                DisplayVideo.Visibility != Visibility.Visible ||
                !IsActuallyInViewport())
            {
                return;
            }

            if (_videoPlaying)
            {
                return;
            }

            try
            {
                DisplayVideo.Play();
                _videoPlaying = true;
                _pausedByViewport = false;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not resume video after a reload");
            }
        }

        private void StopVideo()
        {
            ++_videoFadeRevision;
            StopCoverVideoLoop();
            ReleaseLoopStandby();
            _nativeVideoOpenWatch = null;
            int generation = ++_videoGeneration;
            _videoPath = null;
            _videoReplacingImage = false;
            _videoReplacingVideo = false;
            _videoHandoffStartedGeneration = -1;
            _videoPlaying = false;
            _pausedByViewport = false;

            // Stop viewport tracking when no video is active.
            DetachViewportTracking();

            if (DisplayVideo.Source == null && DisplayVideo.Visibility == Visibility.Collapsed)
            {
                _animating = false;
                return;
            }

            try
            {
                DisplayVideo.Stop();
            }
            catch
            {
            }

            DisplayVideo.Source = null;
            DisplayVideo.Visibility = Visibility.Collapsed;
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.Opacity = 1.0;
            DisplayVideo.OpacityMask = null;
            DisplayVideo.RenderTransform = System.Windows.Media.Transform.Identity;
            DisplayVideo.Effect = null;
            _animating = false;

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (generation == _videoGeneration && DisplayVideo.Source == null)
                    {
                        try
                        {
                            DisplayVideo.Close();
                        }
                        catch
                        {
                        }
                    }
                }),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private static readonly Random VideoStartRandom = new Random();

        private void DisplayVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (_nativeInjected && (_nativeReleased || DisplayVideo.Source == null)) return;
            if (_nativeVideoOpenWatch != null)
            {
                Logger.Info($"ImageRotater native cover video open: game={GameContext?.Id} elapsed={_nativeVideoOpenWatch.ElapsedMilliseconds}ms");
                _nativeVideoOpenWatch = null;
            }
            if (!IsActuallyInViewport())
            {
                try
                {
                    DisplayVideo.Pause();
                    _videoPlaying = false;
                    _pausedByViewport = true;
                }
                catch
                {
                }

                return;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings != null &&
                settings.CoverVideoStartMode == VideoStartMode.Random &&
                DisplayVideo.NaturalDuration.HasTimeSpan)
            {
                double seconds = DisplayVideo.NaturalDuration.TimeSpan.TotalSeconds;
                if (seconds >= 2.0)
                {
                    double offset;
                    lock (VideoStartRandom)
                    {
                        offset = VideoStartRandom.NextDouble() * (seconds * 0.75);
                    }

                    try
                    {
                        DisplayVideo.Position = TimeSpan.FromSeconds(offset);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "ImageRotater: could not set cover video start point");
                    }
                }
            }

            ScheduleCoverVideoLoop();
            int generation = _videoGeneration;
            if (_videoHandoffStartedGeneration == generation)
                return; // duplicate MediaOpened must not restart animation
            _videoHandoffStartedGeneration = generation;

            // For an injected PART_ImageCover with no preceding IR still, the
            // native entry layer owns native -> video. If we fade the video
            // here, CanHideNativeCover becomes true only when that fade ends;
            // NativeCoverEntryTransition then starts a SECOND Side/Diagonal
            // reveal. Hand the fully ready live video to the native relay in
            // this same dispatcher turn, with no internal opacity animation.
            if (NativeOwnsInitialVideoEntry(_nativeInjected,
                _videoReplacingImage, _videoReplacingVideo))
            {
                DisplayVideo.BeginAnimation(OpacityProperty, null);
                DisplayVideo.OpacityMask = null;
                DisplayVideo.Opacity = 1.0;
                NotifyNativeMediaReady(DisplayVideo);
                CompleteVideoReveal();
                return;
            }

            NotifyNativeMediaReady(DisplayVideo);

            if (_videoReplacingImage && IsFeatheredRevealStyle(Transition.CoverStyle))
            {
                StartVideoIncomingReveal(Transition.CoverStyle, generation);
                return;
            }

            var reveal = new System.Windows.Media.Animation.DoubleAnimation(
                0.0, 1.0, new Duration(Transition.CoverDuration));

            reveal.Completed += (s, completedArgs) =>
            {
                if (generation == _videoGeneration)
                {
                    CompleteVideoReveal();
                }
            };

            DisplayVideo.BeginAnimation(OpacityProperty, reveal);
        }

        private void DisplayVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (!IsActuallyInViewport())
            {
                _videoPlaying = false;
                _pausedByViewport = true;
                return;
            }

            try
            {
                TraceCoverVideoLoop("media-ended");
                DisplayVideo.Position = TimeSpan.Zero;
                DisplayVideo.Play();
                _videoPlaying = true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not loop cover video");
            }
            ScheduleCoverVideoLoop();
        }

        // Usually a missing codec - Windows ships no .webm filter. Fall back to
        // the theme's own artwork rather than a black rectangle.
        private void DisplayVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Logger.Warn(
                $"ImageRotater: cover video failed for {GameContext?.Name} - "
                + (e.ErrorException == null ? "no detail" : e.ErrorException.Message));

            string path = DisplayVideo.Source?.LocalPath;

            if (!string.IsNullOrEmpty(path) && _loggedFailures.Add(path))
            {
                Logger.Warn($"ImageRotater: could not play cover video (missing codec?): {path}");
            }

            bool keepImageFallback = _videoReplacingImage &&
                DisplayImage.Visibility == Visibility.Visible &&
                DisplayImage.Source != null;

            StopVideo();

            if (keepImageFallback)
            {
                DisplayImage.BeginAnimation(OpacityProperty, null);
                DisplayImage.Opacity = 1.0;
                DisplayImage.Visibility = Visibility.Visible;
                return;
            }

            ShowNothing();
        }
    }
}
