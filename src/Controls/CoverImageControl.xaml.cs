using System;
using System.Collections.Generic;
using System.Windows;
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

        private readonly CoverImageDataContext _data = new CoverImageDataContext();

        // Last path shown for the current game, so a revisit can avoid
        // repeating it.
        private string _previousPick;

        private readonly HashSet<string> _loggedFailures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CoverImageControl(
            IBackgroundImageSource source,
            ImageSelector selector,
            Func<ImageRotaterSettings> settings)
        {
            InitializeComponent();

            _source = source;
            _selector = selector;
            _settings = settings;

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
            ArtworkRotated += OnArtworkRotated;

            // Viewport tracking is attached lazily only for video covers.
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ArtworkRotated -= OnArtworkRotated;
            DetachViewportTracking();

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

        // Playnite calls this when the tile is bound to a different game -
        // which for a virtualised grid is every time it scrolls into reuse.
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            // A recycled tile must release media owned by the previous game before repicking.
            StopVideo();

            // A cut, never a transition: this tile IS a different game now,
            // and Playnite's own tile cuts too. Without this every tile that
            // scrolled into view dissolved - or flashed - from whichever game
            // it last showed.
            ClearPreviousCover();
            LowerVeil();
            _cutNextStage = true;

            _previousPick = null;
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
            get { return GameContext != null && GameContext.Id == _selectedGame; }
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

            bool mustStandDown = !IsSelectedTile
                && settings?.AnimateUnfocusedCovers != true
                && (DisplayImage.Visibility == Visibility.Visible ||
                    DisplayVideo.Visibility == Visibility.Visible);

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
                _previousPick = null;
            }

            Refresh();
        }

        private void Refresh()
        {
            try
            {
                ImageRotaterSettings settings = _settings != null ? _settings() : null;

                if (settings == null || !settings.EnableRotation || !settings.RotateCovers)
                {
                    ShowNothing();
                    return;
                }

                Game game = GameContext;
                if (game == null || _source == null || _selector == null)
                {
                    ShowNothing();
                    return;
                }

                if (!IsSelectedTile && !settings.AnimateUnfocusedCovers)
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

                if (string.Equals(settings.CurrentCoverGameId, game.Id.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    path = settings.CurrentCoverPath;
                }

                // No published pick for this game - a tile scrolled past
                // without ever being selected, so rotation has not run for it.
                // Choosing here is then the only way it shows anything.
                if (string.IsNullOrEmpty(path))
                {
                    path = _selector.Select(
                        CoverSelectionKey(game.Id), candidates, _previousPick, settings.CoverSelectionMode);
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
            }
            catch (Exception ex)
            {
                // Nothing may escape into Playnite's layout pass: an exception
                // there is not caught above us and takes the process down with
                // no dialog and nothing in the log.
                Logger.Error(ex, "ImageRotater cover refresh failed");
            }
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

        // First candidate that still exists, skipping the one already rejected.
        // A single unloadable file should cost one retry, not the whole tile.
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

        private void ShowNothing()
        {
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
            ClearPreviousCover();
            LowerVeil();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
        }

        private void ShowPlaceholder()
        {
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
            ClearPreviousCover();
            LowerVeil();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Visible;
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
                        1.0, new Duration(Transition.Half));

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
                Interval = TimeSpan.FromMilliseconds(600)
            };

            backstop.Tick += (s, e) =>
            {
                backstop.Stop();

                if (generation == _fadeGeneration)
                {
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

                var fade = new System.Windows.Media.Animation.DoubleAnimation(
                    1.0, 0.0, new Duration(Transition.Duration));

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

            // Keep the still visible while the video layer fades out.
            DisplayImage.BeginAnimation(OpacityProperty, null);
            DisplayImage.Opacity = 1.0;
            DisplayImage.Visibility = Visibility.Visible;

            string fadingPath = _videoPath;
            int videoGeneration = _videoGeneration;

            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, 0.0, new Duration(Transition.Duration));

            fade.Completed += (s, e) =>
            {
                if (videoGeneration != _videoGeneration ||
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

        private void ClearPreviousCover()
        {
            PreviousImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.Opacity = 1.0;
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
                0.0, new Duration(Transition.Half));

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

        // Hands a video to the MediaElement and stands the Image down, so
        // exactly one renderer draws.
        private void ShowVideo(string path)
        {
            // Keep the current still while the replacement video opens.
            _videoReplacingImage = DisplayImage.Visibility == Visibility.Visible &&
                DisplayImage.Source != null;

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

            DisplayVideo.BeginAnimation(OpacityProperty, null);
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

                    var reveal = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(700)
                    };

                    reveal.Tick += (s, e) =>
                    {
                        reveal.Stop();

                        if (generation == _videoGeneration &&
                            DisplayVideo.Source != null &&
                            DisplayVideo.Visibility == Visibility.Visible &&
                            DisplayVideo.Opacity < 1.0)
                        {
                            DisplayVideo.BeginAnimation(OpacityProperty, null);
                            DisplayVideo.Opacity = 1.0;
                            CompleteVideoReveal();
                        }
                    };

                    reveal.Start();
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CompleteVideoReveal()
        {
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
            int generation = ++_videoGeneration;
            _videoPath = null;
            _videoReplacingImage = false;
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

            int generation = _videoGeneration;
            var reveal = new System.Windows.Media.Animation.DoubleAnimation(
                0.0, 1.0, new Duration(Transition.Duration));

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
                DisplayVideo.Position = TimeSpan.Zero;
                DisplayVideo.Play();
                _videoPlaying = true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not loop cover video");
            }
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
