using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    // Browse SteamGridDB artwork for a game, filter it, and download the ones
    // you pick. The auto-download menu item takes the highest-resolution match
    // without asking; this is for when that guess is not what you wanted.
    public partial class SteamGridDbSearchView : UserControl
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly SteamGridDbSearchViewModel _model;
        private readonly ArtworkDownloader _downloader;
        private readonly IPlayniteAPI _api;
        private readonly Guid _gameId;
        private readonly Playnite.SDK.Models.Game _game;
        private readonly ImageRotaterSettings _settings;
        private readonly string _pluginUserDataPath;
        private readonly ArtworkKind _kind;
        private readonly bool _embedded;

        public event EventHandler ArtworkChanged;

        // Peer source to SteamGridDB, used when the checkbox is ticked.
        private readonly WebImageSearch _webSearch;

        // Result tile geometry, read by the tile template through a
        // RelativeSource binding - the DataContext there is one search result,
        // so these cannot live on the view model.
        //
        // Dependency properties rather than plain ones because the template
        // binds them; a CLR property with no change notification would work
        // here only by accident of being set before the first measure.
        public static readonly DependencyProperty TileWidthProperty =
            DependencyProperty.Register(
                nameof(TileWidth), typeof(double), typeof(SteamGridDbSearchView),
                new PropertyMetadata(240.0));

        public static readonly DependencyProperty ThumbnailHeightProperty =
            DependencyProperty.Register(
                nameof(ThumbnailHeight), typeof(double), typeof(SteamGridDbSearchView),
                new PropertyMetadata(112.0));

        public double TileWidth
        {
            get { return (double)GetValue(TileWidthProperty); }
            set { SetValue(TileWidthProperty, value); }
        }

        public double ThumbnailHeight
        {
            get { return (double)GetValue(ThumbnailHeightProperty); }
            set { SetValue(ThumbnailHeightProperty, value); }
        }

        public SteamGridDbSearchView(
            IPlayniteAPI api,
            ISteamGridDbClient client,
            ArtworkDownloader downloader,
            Playnite.SDK.Models.Game game,
            ArtworkKind kind = ArtworkKind.Background,
            ImageRotaterSettings settings = null,
            string pluginUserDataPath = null,
            bool embedded = false)
        {
            InitializeComponent();

            _api = api;
            _downloader = downloader;

            // The whole Game, not just its id: the Steam tab needs GameId and
            // PluginId to find the appid, and the name is on it anyway.
            _game = game;
            _gameId = game?.Id ?? Guid.Empty;
            _settings = settings ?? new ImageRotaterSettings();

            // Where the web view keeps its own data folder.
            _pluginUserDataPath = pluginUserDataPath;
            _kind = kind;
            _embedded = embedded;
            _webSearch = new WebImageSearch(api);

            if (_embedded)
            {
                CloseDialogButton.Visibility = Visibility.Collapsed;
            }

            _model = new SteamGridDbSearchViewModel(client);
            DataContext = _model;

            SearchBox.Text = game?.Name ?? string.Empty;

            // Deliberately NOT restricted to games Steam imported. The artwork
            // is keyed by Steam's appid, which exists for a game regardless of
            // where this user's copy came from - so a GOG or Xbox install
            // reaches the same art, once the appid is worked out from the name.

            // Fullscreen is read from across a room, so the tiles grow and the
            // filter column gets out of the way. Desktop keeps the sizes that
            // suit a monitor at arm's length.
            if (api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen)
            {
                TileWidth = 340;
                ThumbnailHeight = 160;
                FiltersColumn.Width = new GridLength(0);
                FilterToggleRow.Visibility = Visibility.Visible;
            }

            // Covers get a square box, backgrounds keep the banner.
            //
            // Cover results are 1:1 and 2:3; backgrounds are 16:9 and wider.
            // One box cannot suit both: a square cover in the banner box is a
            // 112px postage stamp, a hero in a square box wastes half of it.
            // 16 is the frame's padding and border, so the box matches the
            // tile's inner width exactly.
            if (kind == ArtworkKind.Cover)
            {
                ThumbnailHeight = TileWidth - 16;
            }

            // Conversion needs ffmpeg, which the plugin cannot bundle - it is
            // GPL and this project is MIT. Say so on the control rather than
            // leaving a ticked box that quietly does nothing.
            if (!GifConverter.IsAvailable)
            {
                ConvertGifsBox.IsChecked = false;
                ConvertGifsBox.IsEnabled = false;
                ConvertGifsNote.Text =
                    "Needs ffmpeg on your PATH. Without it, GIFs download as GIFs.";
            }

            RestorePreset();

            // Search straight away - the user opened this from a specific game,
            // so making them press Search first is a pointless extra step.
            //
            // Focus goes to the RESULTS, not the search box. The box is already
            // filled with the game's name and the search runs on its own, so a
            // controller user wants to be browsing results immediately - and
            // landing in a text box with a controller means an on-screen
            // keyboard nobody asked for.
            Loaded += async (s, e) =>
            {
                _open = this;
                await RunSearch();
                FocusFirstResult();
            };

            // Cleared on unload, so a controller press after the dialog has
            // gone cannot reach a dead window.
            Unloaded += (s, e) =>
            {
                if (ReferenceEquals(_open, this))
                {
                    _open = null;
                }

                SavePreset();

                // The web view owns a browser process; leaving it running
                // behind a closed dialog would leak one per search.
                if (_previewRenderer != null)
                {
                    _previewRenderer.Dispose();
                    _previewRenderer = null;
                }
            };
        }

        // Opens the dialog the way it was last closed for this artwork kind.
        //
        // No "save preset" button: what the user last did is the preset. The
        // same shape and style get picked for every game in a session, and
        // re-ticking them for each was the whole complaint.
        private void RestorePreset()
        {
            SearchPreset preset = SearchPresets.Load(_pluginUserDataPath, _kind);
            if (preset == null)
            {
                return;
            }

            _model.SeedTicks(preset);

            // Both the box and the filter flag: the box's Checked handler is
            // guarded by IsLoaded and does not fire this early.
            ShowAnimatedBox.IsChecked = preset.ShowAnimated;
            ShowNsfwBox.IsChecked = preset.ShowNsfw;
            ShowHumorBox.IsChecked = preset.ShowHumor;
            ShowEpilepsyBox.IsChecked = preset.ShowEpilepsy;
            _model.Filter.ShowAnimated = preset.ShowAnimated;
            _model.Filter.ShowNsfw = preset.ShowNsfw;
            _model.Filter.ShowHumor = preset.ShowHumor;
            _model.Filter.ShowEpilepsy = preset.ShowEpilepsy;

            // Only while ffmpeg is there; the box was just disabled otherwise,
            // and re-ticking it would request a conversion that cannot run.
            if (GifConverter.IsAvailable)
            {
                ConvertGifsBox.IsChecked = preset.ConvertGifs;
            }

            // Selecting a tab here is safe: SourceTabs_SelectionChanged
            // ignores changes before Loaded, and Loaded runs the one search.
            TabItem tab = SourceTabs.Items.OfType<TabItem>()
                .FirstOrDefault(t => t.Name == preset.Tab);

            if (tab != null)
            {
                SourceTabs.SelectedItem = tab;
                SyncSearchTermToTab();
            }
        }

        private void SavePreset()
        {
            if (string.IsNullOrEmpty(_pluginUserDataPath))
            {
                return;
            }

            // Start from what was loaded, so a dialog that never got results
            // (no key, network down) keeps the remembered ticks rather than
            // saving none - and so ConvertGifs survives a session without
            // ffmpeg, when the box is disabled and reads false.
            SearchPreset preset = SearchPresets.Load(_pluginUserDataPath, _kind) ?? new SearchPreset();

            _model.SnapshotTicks(preset);

            preset.ShowAnimated = ShowAnimatedBox.IsChecked == true;
            preset.ShowNsfw = ShowNsfwBox.IsChecked == true;
            preset.ShowHumor = ShowHumorBox.IsChecked == true;
            preset.ShowEpilepsy = ShowEpilepsyBox.IsChecked == true;

            if (GifConverter.IsAvailable)
            {
                preset.ConvertGifs = ConvertGifsBox.IsChecked == true;
            }

            preset.Tab = (SourceTabs.SelectedItem as TabItem)?.Name ?? preset.Tab;

            SearchPresets.Save(_pluginUserDataPath, _kind, preset);
        }

        // Puts keyboard focus on the first result tile, so D-pad navigation has
        // somewhere to start. WPF will not move focus from nowhere, so without
        // this the first press of a direction does nothing at all.
        private void FocusFirstResult()
        {
            try
            {
                ResultsList.UpdateLayout();

                var first = FindFirstFocusable(ResultsList);

                if (first != null)
                {
                    first.Focus();
                    return;
                }

                // No results yet - the search may have found nothing. Put focus
                // somewhere reachable rather than leaving the window with none.
                SearchButton.Focus();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not set initial focus in the search dialog");
            }
        }

        private static System.Windows.Controls.Primitives.ToggleButton FindFirstFocusable(
            DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

                var toggle = child as System.Windows.Controls.Primitives.ToggleButton;
                if (toggle != null && toggle.Focusable && toggle.IsVisible)
                {
                    return toggle;
                }

                var found = FindFirstFocusable(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private async Task RunSearch()
        {
            SearchButton.IsEnabled = false;
            try
            {
                if (ReferenceEquals(SourceTabs.SelectedItem, SteamTab))
                {
                    await _model.LoadSteamArtworkAsync(_game, _kind);
                    return;
                }

                if (ReferenceEquals(SourceTabs.SelectedItem, YouTubeTab))
                {
                    await _model.SearchYouTubeAsync(
                        SearchBox.Text, _settings, System.Threading.CancellationToken.None);
                    return;
                }

                if (ReferenceEquals(SourceTabs.SelectedItem, WebTab))
                {
                    // NOT wrapped in Task.Run: the method puts only the
                    // browsing off-thread itself, because the collections it
                    // updates afterwards are bound to the UI and WPF rejects
                    // collection changes from any other thread.
                    await _model.SearchWebAsync(SearchBox.Text, _webSearch);
                    return;
                }

                // Covers come from grids, backgrounds from heroes.
                SteamGridDbArtworkType type = _kind == ArtworkKind.Cover
                    ? SteamGridDbArtworkType.Grid
                    : SteamGridDbArtworkType.Hero;

                await _model.SearchAsync(SearchBox.Text, type);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: SteamGridDB search failed");
                _model.Status = Loc.Get("LOCImageRotaterSearchFailed");
            }
            finally
            {
                SearchButton.IsEnabled = true;
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSearch();
        }

        // Re-run on tab change so the results always match the visible tab.
        // Leaving the previous source's results on screen under a different
        // heading is worse than a moment's wait.
        private async void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Fires once while the tabs are being built, before the rest of the
            // dialog exists. The Loaded handler runs the first search.
            if (!IsLoaded)
            {
                return;
            }

            // Compared against SENDER, not OriginalSource.
            //
            // SelectionChanged bubbles, and for a TabControl the OriginalSource
            // is frequently the inner TabItem rather than the TabControl - so
            // the obvious-looking OriginalSource check dropped real tab changes
            // and the results kept belonging to the previous tab. sender is the
            // element the handler is attached to, which is exactly the question
            // being asked.
            if (!ReferenceEquals(sender, SourceTabs))
            {
                return;
            }

            // The old tab's results must not sit under the new tab's header
            // while the next search runs.
            _model.Clear();

            SyncSearchTermToTab();

            await RunSearch();
        }

        // The YouTube tab searches for "<game> live wallpaper", the others for
        // the game's name.
        //
        // Only rewritten while the box still holds a term this method itself
        // put there - the moment a user types their own words, switching tabs
        // stops overwriting them.
        private void SyncSearchTermToTab()
        {
            string plain = (_game?.Name ?? string.Empty).Trim();
            string forYouTube = Services.YouTubeSearch.DefaultQueryFor(plain);

            string current = (SearchBox.Text ?? string.Empty).Trim();

            bool onYouTube = ReferenceEquals(SourceTabs.SelectedItem, YouTubeTab);

            if (onYouTube && current == plain)
            {
                SearchBox.Text = forYouTube;
            }
            else if (!onYouTube && current == forYouTube)
            {
                SearchBox.Text = plain;
            }
        }

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await RunSearch();
            }
        }

        // Shows or hides the filter column.
        //
        // Only reachable in Fullscreen, where the column starts collapsed
        // because it would otherwise take a third of a TV for controls a
        // controller user touches rarely.
        private void FilterToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool show = FilterToggleRow.IsChecked == true;

            FiltersColumn.Width = show ? new GridLength(260) : new GridLength(0);
        }

        // Escape closes, and so does the controller's B.
        //
        // Playnite synthesises real key messages from the pad - D-pad becomes
        // the arrow keys and A becomes Enter, which is why navigation and
        // activation need no code at all. B is the exception: Playnite maps it
        // to nothing (a comment in its own source admits nobody remembers why),
        // so a dialog that does not handle it strands a controller user with no
        // way out.
        //
        // OnPreviewKeyDown rather than the KeyDown event, so this still fires
        // when focus is inside a child that handles keys itself - the search
        // box swallows Escape otherwise.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseWindow();
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        // Closes whichever search dialog is currently open.
        //
        // Static because the controller event arrives at the PLUGIN, which has
        // no reference to a dialog it did not construct. B is the one button
        // Playnite does not synthesise a key for, so this is the only route
        // from "user pressed B" to "the dialog closes".
        public static void CloseOpenDialog()
        {
            SteamGridDbSearchView open = _open;

            if (open == null)
            {
                return;
            }

            try
            {
                open.Dispatcher.Invoke(() => open.CloseWindow());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not close the search dialog from the controller");
            }
        }

        // The dialog is modal and there is only ever one, so a single reference
        // is enough - and it is cleared on unload so a closed dialog cannot be
        // told to close again.
        private static SteamGridDbSearchView _open;

        private void CloseWindow()
        {
            try
            {
                Window.GetWindow(this)?.Close();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not close the search dialog");
            }
        }

        // Starts (or clears) GIF playback for one result tile.
        //
        // This lives in code-behind rather than a DataTrigger on purpose. A
        // gif: xmlns in the XAML makes BAML resolve XamlAnimatedGif through
        // Assembly.Load, which probes only Playnite's own folder and never the
        // extension folder our DLL sits in - InitializeComponent then throws
        // XamlParseException and the dialog never opens, animated results or
        // not. Reached from code, the same reference resolves in the LoadFrom
        // context that Playnite loaded us with, which does know where we live.
        //
        // The results panel virtualizes, so this Image may have been showing a
        // different result a moment ago. Both branches must therefore assign:
        // clearing on the static branch is what stops a recycled tile from
        // playing the previous item's GIF underneath a new thumbnail. Exactly
        // one channel drives the Image at a time - the same rule the cover
        // control follows.
        private void ResultImage_Loaded(object sender, RoutedEventArgs e)
        {
            var image = sender as System.Windows.Controls.Image;
            if (image == null)
            {
                return;
            }

            var item = image.DataContext as SteamGridDbArtwork;

            // GIF results do not auto-play. They play on HOVER.
            //
            // Auto-playing handed XamlAnimatedGif the FULL-SIZE remote URL for
            // every visible result, so a search returning a dozen animated hits
            // downloaded and decoded a dozen whole GIFs at once, on the UI
            // thread, in a 32-bit process. Playnite froze or died - worst on a
            // web search, where results are arbitrary files with no size
            // ceiling.
            //
            // Hover keeps the preview but bounds it at one file: the mouse can
            // only be over a single tile. The static thumbnail, a separate and
            // much smaller URL, is what the grid shows at rest.
            if (item != null && item.IsGif)
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(image, null);
            }
        }

        // Starts a GIF playing while the pointer is over its tile.
        //
        // One at a time by construction - the mouse is over a single tile - so
        // this restores the animated preview without the cost that made
        // auto-play crash Playnite. Stops again on MouseLeave, so a grid the
        // user has scrolled through is not left with a trail of decoders.
        // Hovering a tile no longer plays it.
        //
        // It used to start XamlAnimatedGif on the tile's remote URI, which
        // downloaded the whole GIF and decoded every frame on the UI thread -
        // on mouse-over, for a file the user had not asked for. Skimming a grid
        // of results meant fetching megabytes nobody chose to fetch.
        //
        // The preview button covers the same need without the ambush: it
        // streams through MediaElement and starts in about a second. The
        // handlers stay as no-ops because the tile template binds them.
        private void ResultImage_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
        }

        private void ResultImage_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            var item = button?.Tag as SteamGridDbArtwork;

            if (item == null)
            {
                return;
            }

            try
            {
                ShowPreview(item);
            }
            catch (Exception ex)
            {
                // A preview that will not open must not take the search dialog
                // with it - the user can still download without looking.
                Logger.Warn(ex, "ImageRotater: could not open the artwork preview");

                _api.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCImageRotaterPreviewOpenFailed"), "ImageRotater");
            }
        }

        // Shows one result in the panel beside the grid.
        //
        // A panel rather than a modal window: a modal has to be dismissed
        // before the next result can be looked at, which turns comparing two
        // candidates into a chore. This stays open while the grid is browsed,
        // and each preview click just replaces its contents.
        private async void ShowPreview(SteamGridDbArtwork item)
        {
            StopPreview();

            PreviewColumn.Width = new GridLength(320);
            PreviewPanel.Visibility = Visibility.Visible;

            PreviewTitle.Text = string.IsNullOrEmpty(item.Style) ? Loc.Get("LOCImageRotaterPreview") : item.Style;

            // A YouTube result's Width and Height describe its THUMBNAIL -- the
            // 480x360 poster frame -- because the video's real size is unknown
            // until it has been downloaded. Printing that as the dimensions
            // states a measurement of the wrong thing; the length is both known
            // and the number that actually matters for a looping background.
            PreviewStatus.Text = item.IsYouTube
                ? (string.IsNullOrEmpty(item.DurationText)
                    ? "YouTube" : "YouTube   " + item.DurationText)
                : item.Dimensions + "   " + item.FormatLabel;

            if (!item.IsAnimated)
            {
                ShowStill(item);
                return;
            }

            // Animated content goes through the web view.
            //
            // Not MediaElement: setting its Source succeeds and it then dies
            // with a NullReferenceException inside MediaPlayerState while
            // rendering, taking the window with it. Reproduced outside Playnite
            // for both a remote URL and a local file, so it is the control
            // rather than the media.
            //
            // The web view also plays WebM and animated WebP, which MediaElement
            // never could - so ffmpeg conversion is now an optimisation rather
            // than the only route.
            int generation = ++_previewGeneration;

            bool ready = await EnsurePreviewRendererAsync();

            if (generation != _previewGeneration)
            {
                return;
            }

            if (!ready)
            {
                // No runtime. The still frame is the honest fallback, and the
                // caption says why there is no motion.
                ShowStill(item);

                PreviewStatus.Text = Loc.Format(
                    "LOCImageRotaterAnimatedPreviewNeedsWebView",
                    item.Dimensions,
                    item.FormatLabel);

                return;
            }

            PreviewImage.Visibility = Visibility.Collapsed;

            /* YouTube is played by YouTube's own player and leaves here.

               Its Url is a watch PAGE, and MotionPreviewUrl falls back to that
               same page because there is no .webm thumbnail to prefer. Sending
               it down the line below put an HTML document into a <video> tag,
               which decodes nothing and draws an empty box -- the preview did
               not fail, it succeeded at showing nothing. */
            if (item.IsYouTube && !string.IsNullOrEmpty(item.YouTubeId))
            {
                _previewRenderer.ShowYouTube(item.YouTubeId);
                return;
            }

            // GIF and animated WebP are IMAGES to a browser; only real video
            // goes in a <video> tag.
            bool isVideo = item.FormatLabel == "MP4"
                || item.FormatLabel == "WEBM";

            _previewRenderer.Show(item.MotionPreviewUrl ?? item.Url, isVideo);
        }

        private void ShowStill(SteamGridDbArtwork item)
        {
            if (_previewRenderer != null)
            {
                _previewRenderer.Clear();
            }

            PreviewImage.Visibility = Visibility.Visible;

            try
            {
                PreviewImage.Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri(item.Url, UriKind.Absolute));
            }
            catch (Exception)
            {
                // WPF has no WebP or AVIF decoder, so those throw here. The
                // caption still says what the format is.
                PreviewImage.Source = null;

                PreviewStatus.Text = Loc.Format(
                    "LOCImageRotaterWindowsCannotDisplay",
                    item.Dimensions,
                    item.FormatLabel);
            }
        }

        // Built on first use rather than at construction: creating the
        // environment costs a folder and a process, and most previews are of
        // still images that never need it.
        private async Task<bool> EnsurePreviewRendererAsync()
        {
            if (_previewRenderer == null)
            {
                _previewRenderer = new PreviewRenderer(_pluginUserDataPath);

                // Into the tree BEFORE initialising: the core cannot be created
                // for a control that has no window behind it.
                PreviewVideoHost.Content = _previewRenderer.CreateControl();
                PreviewVideoHost.UpdateLayout();
            }

            return await _previewRenderer.InitialiseAsync();
        }

        private PreviewRenderer _previewRenderer;

        // Bumped whenever a new preview starts, so a slow web view startup that
        // finishes late cannot play over a different result.
        private int _previewGeneration;

        private void PreviewClose_Click(object sender, RoutedEventArgs e)
        {
            StopPreview();

            PreviewPanel.Visibility = Visibility.Collapsed;
            PreviewColumn.Width = new GridLength(0);
        }

        // Releases the preview. Not merely hidden: a hidden video keeps
        // decoding, which is the cost being avoided.
        private void StopPreview()
        {
            _previewGeneration++;

            if (_previewRenderer != null)
            {
                _previewRenderer.Clear();
            }

            PreviewImage.Source = null;
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            _model.NextPage();
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            _model.PreviousPage();
        }

        // The content boxes apply as they are ticked, like every other filter.
        // The shape and style ticks do the same through the view model.
        //
        // They filter what is ALREADY on screen - no request is involved - so
        // making the user press a second button to see the effect was pure
        // ceremony, and made the boxes look broken.
        //
        // Not before Loaded: the constructor sets these boxes from the
        // remembered preset and writes the flags itself.
        private void ContentFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            _model.Filter.ShowAnimated = ShowAnimatedBox.IsChecked == true;
            _model.Filter.ShowNsfw = ShowNsfwBox.IsChecked == true;
            _model.Filter.ShowHumor = ShowHumorBox.IsChecked == true;
            _model.Filter.ShowEpilepsy = ShowEpilepsyBox.IsChecked == true;

            _model.ApplyFilter();
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            // Read from the model, not ListBox.SelectedItems: a filter rebuilds
            // the visible list, and anything ticked but currently filtered out
            // would otherwise be dropped from the download without saying so.
            var chosen = _model.SelectedArtwork();
            if (chosen.Count == 0)
            {
                _model.Status = Loc.Get("LOCImageRotaterSelectImagesFirst");
                return;
            }

            DownloadButton.IsEnabled = false;
            _model.Status = Loc.Format("LOCImageRotaterDownloadingImages", chosen.Count);

            // Read at download time, not construction: the user may have
            // changed their mind about conversion since the dialog opened.
            _downloader.ConvertGifsToMp4 = ConvertGifsBox.IsChecked == true;

            int saved = 0;
            try
            {
                foreach (SteamGridDbArtwork artwork in chosen)
                {
                    if (await _downloader.DownloadAsync(_gameId, artwork, _kind) != null)
                    {
                        saved++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: SteamGridDB download failed");
            }
            finally
            {
                DownloadButton.IsEnabled = true;
            }

            // Say what was actually fetched. A video download runs yt-dlp and
            // a remux behind this button, and reporting it as "images" made a
            // user reasonably conclude the conversion step never happened.
            int videos = chosen.Count(c => c.IsYouTube || c.CanStreamDirectly && c.IsAnimated);

            string what = videos == chosen.Count
                ? Loc.Get("LOCImageRotaterVideoFiles")
                : videos > 0
                    ? Loc.Get("LOCImageRotaterFiles")
                    : Loc.Get("LOCImageRotaterImages");

            _model.Status = saved == chosen.Count
                ? Loc.Format("LOCImageRotaterDownloadedConverted", saved, what)
                : Loc.Format("LOCImageRotaterDownloadedPartial", saved, chosen.Count);

            // Rotation needs something to rotate TO. One image is a static
            // replacement, and a user who picked one and saw no transition
            // reasonably concludes the plugin is broken - say so here, while
            // the results to add a second one from are still on screen.
            if (saved > 0 && CandidateCount() < 2)
            {
                _model.Status += Loc.Get("LOCImageRotaterDownloadTip");
            }

            if (saved > 0)
            {
                ArtworkChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private int CandidateCount()
        {
            if (string.IsNullOrEmpty(_pluginUserDataPath) || _game == null)
            {
                return int.MaxValue;
            }

            return new GameImageStore(_pluginUserDataPath).RotationPoolSize(
                _gameId, _kind, _kind == ArtworkKind.Cover ? _game.CoverImage : _game.BackgroundImage);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }
    }
}
