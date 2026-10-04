using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageRotater.Models;
using ImageRotater.Services;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ImageRotater.Controls
{
    public partial class ArtworkManagerView : UserControl
    {
        private readonly IPlayniteAPI _api;
        private readonly GameImageStore _store;
        private readonly SessionSelectionCache _sessionCache;
        private readonly Game _game;
        private readonly ArtworkKind _kind;
        private readonly OriginalArtPreserver _preserver;
        private readonly Action<Guid> _onImagesChanged;
        private readonly Action _automaticDownload;
        private readonly Func<ImageRotaterSettings> _settings;
        private readonly Action _saveSettings;
        private bool _updatingGameBehavior;
        private SteamGridDbSearchView _searchView;
        private readonly Func<SteamGridDbSearchView> _searchViewFactory;
        private bool _managerLoaded;
        private readonly ObservableCollection<ArtworkManagerItem> _items =
            new ObservableCollection<ArtworkManagerItem>();

        public ArtworkManagerView(
            IPlayniteAPI api,
            GameImageStore store,
            SessionSelectionCache sessionCache,
            Game game,
            ArtworkKind kind,
            OriginalArtPreserver preserver,
            Action<Guid> onImagesChanged,
            Func<SteamGridDbSearchView> searchViewFactory,
            Action automaticDownload,
            Func<ImageRotaterSettings> settings,
            Action saveSettings)
        {
            _api = api;
            _store = store;
            _sessionCache = sessionCache;
            _game = game;
            _kind = kind;
            _preserver = preserver;
            _onImagesChanged = onImagesChanged;
            _automaticDownload = automaticDownload;
            _searchViewFactory = searchViewFactory;
            _settings = settings;
            _saveSettings = saveSettings;

            InitializeComponent();
            InitializeGameBehaviorChoices();

            // The visible navigation buttons are separate from the hidden TabControl.
            // IsChecked on LocalNavButton is applied while InitializeComponent is still
            // building the visual tree, so its Checked handler can run before LocalTab
            // exists. Explicitly select the local page once all named controls exist.
            ManagerTabs.SelectedItem = LocalTab;
            LocalNavButton.IsChecked = true;
            SearchNavButton.IsChecked = false;

            ItemsList.ItemsSource = _items;
            // SearchHost stays empty until Search online is opened for the first time.
            // This avoids constructing the heavy search UI during manager startup.

            LocalTab.Header = kind == ArtworkKind.Cover
                ? Loc.Get("LOCImageRotaterManagerMyCovers")
                : Loc.Get("LOCImageRotaterManagerMyBackgrounds");
            LocalNavButton.Content = LocalTab.Header;
            PreviewHintText.Text = Loc.Get("LOCImageRotaterManagerSelectItem");
            PreviewNameText.Text = string.Empty;
            PreviewMetaText.Text = string.Empty;

            Loaded += ArtworkManagerView_Loaded;
            Unloaded += ArtworkManagerView_Unloaded;
        }

        private void ArtworkManagerView_Loaded(object sender, RoutedEventArgs e)
        {
            _managerLoaded = true;

            // Keep startup deterministic even when the active Playnite theme delays
            // template/selection initialization. The manager must always open on the
            // local artwork page.
            if (!ReferenceEquals(ManagerTabs.SelectedItem, LocalTab))
            {
                ManagerTabs.SelectedItem = LocalTab;
            }

            LocalNavButton.IsChecked = true;
            SearchNavButton.IsChecked = false;
            LocalActionsPanel.Visibility = Visibility.Visible;

            // One local scan only. Previously selecting LocalTab during construction and
            // Loaded both triggered ReloadItems(), decoding every thumbnail twice before
            // the user could interact with the manager.
            ReloadItems();
        }

        private void ArtworkManagerView_Unloaded(object sender, RoutedEventArgs e)
        {
            StopPreview();

            if (_searchView != null)
            {
                _searchView.ArtworkChanged -= SearchView_ArtworkChanged;
            }
        }

        private void SearchView_ArtworkChanged(object sender, EventArgs e)
        {
            NotifyImagesChanged();

            if (ReferenceEquals(ManagerTabs.SelectedItem, LocalTab))
            {
                ReloadItems();
            }
        }

        private void ManagerNav_Checked(object sender, RoutedEventArgs e)
        {
            if (ManagerTabs == null || LocalTab == null || SearchTab == null)
            {
                return;
            }

            if (ReferenceEquals(sender, LocalNavButton))
            {
                ManagerTabs.SelectedItem = LocalTab;
            }
            else if (ReferenceEquals(sender, SearchNavButton))
            {
                ManagerTabs.SelectedItem = SearchTab;
            }
        }

        private async void ManagerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, ManagerTabs) ||
                LocalActionsPanel == null || LocalTab == null)
            {
                return;
            }

            bool local = ReferenceEquals(ManagerTabs.SelectedItem, LocalTab);

            // Keep the standalone navigation buttons synchronized with the hidden page host.
            if (LocalNavButton != null && SearchNavButton != null)
            {
                LocalNavButton.IsChecked = local;
                SearchNavButton.IsChecked = !local;
            }

            LocalActionsPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;

            // SelectionChanged can fire while InitializeComponent / constructor setup is
            // still running. Do not perform disk scans or construct the search UI then.
            if (!_managerLoaded)
            {
                return;
            }

            if (local)
            {
                ReloadItems();
            }
            else
            {
                StopPreview();
                SteamGridDbSearchView searchView = EnsureSearchView();
                if (searchView != null)
                {
                    await searchView.EnsureInitialSearchAsync();
                }
            }
        }

        private SteamGridDbSearchView EnsureSearchView()
        {
            if (_searchView != null)
            {
                return _searchView;
            }

            if (_searchViewFactory == null)
            {
                return null;
            }

            _searchView = _searchViewFactory();
            if (_searchView != null)
            {
                _searchView.ArtworkChanged += SearchView_ArtworkChanged;
                SearchHost.Content = _searchView;
            }

            return _searchView;
        }

        private void ReloadItems(string preferredPath = null)
        {
            string currentPath = preferredPath;
            if (currentPath == null)
            {
                var current = ItemsList.SelectedItem as ArtworkManagerItem;
                currentPath = current != null ? current.Path : null;
            }

            StopPreview();
            _items.Clear();

            // Artwork Manager mirrors the selector's Original semantics.
            // Theme Integration keeps Original virtual and points directly at
            // Playnite. Compatibility uses the preserved safety copy once the
            // game has real ImageRotater artwork.
            bool hasPluginArtwork = _store.HasAnyImage(_game.Id, _kind);
            ImageRotaterSettings currentSettings = _settings != null ? _settings() : null;
            bool themeIntegration = currentSettings?.UseThemeIntegration == true;

            string compatibilityOriginalPath = null;
            if (hasPluginArtwork && !themeIntegration)
            {
                compatibilityOriginalPath = _preserver?.Preserve(_game, _kind);
            }

            string fixedArtwork = _store.GetFixedArtworkPath(_game.Id, _kind);
            int artworkIndex = 0;

            // In Theme Integration Original is always the live Playnite image.
            // For an untouched game it is also virtual in Compatibility because
            // there is no reason to create a safety copy until rotation starts.
            if (themeIntegration || !hasPluginArtwork)
            {
                string nativePath = _preserver?.ResolveOriginalPath(_game, _kind)
                    ?? ResolveCurrentPlayniteArtworkPath();

                if (!string.IsNullOrEmpty(nativePath))
                {
                    bool virtualOriginalIsFixed = themeIntegration &&
                        !string.IsNullOrEmpty(fixedArtwork) &&
                        GameImageStore.IsPreservedOriginal(fixedArtwork);

                    _items.Add(new ArtworkManagerItem(
                        nativePath,
                        isFixed: virtualOriginalIsFixed,
                        isExcluded: false,
                        orderIndex: ++artworkIndex,
                        artworkKind: _kind,
                        forceNativeOriginal: true,
                        isVirtualOriginal: true));
                }
            }

            foreach (string path in _store
                .GetImagePathsRaw(_game.Id, _kind)
                .Where(GameImageStore.IsSupported)
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path)))
            {
                // original_* is Compatibility's technical safety copy. In Theme
                // Integration the live virtual Original above replaces it; on an
                // untouched game an old leftover backup must not appear either.
                if (GameImageStore.IsPreservedOriginal(path))
                {
                    if (themeIntegration || !hasPluginArtwork)
                    {
                        continue;
                    }

                    // Old versions could leave several original_* files behind.
                    // Show only the one Compatibility currently treats as the
                    // authoritative Original.
                    if (string.IsNullOrEmpty(compatibilityOriginalPath) ||
                        !string.Equals(path, compatibilityOriginalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                bool isFixed = !string.IsNullOrEmpty(fixedArtwork) &&
                    string.Equals(path, fixedArtwork, StringComparison.OrdinalIgnoreCase);
                bool isExcluded = _store.IsArtworkExcluded(_game.Id, _kind, path);
                _items.Add(new ArtworkManagerItem(path, isFixed, isExcluded, ++artworkIndex, _kind));
            }

            UpdateCounts();

            bool hasItems = _items.Count > 0;
            EmptyListText.Text = _kind == ArtworkKind.Cover
                ? Loc.Get("LOCImageRotaterManagerNoCovers")
                : Loc.Get("LOCImageRotaterManagerNoBackgrounds");
            EmptyStatePanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            ItemsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;

            if (!hasItems)
            {
                ClearPreviewHint();
                ItemsList.SelectedItem = null;
                UpdateDeleteButton();
                UpdateOrderButtons();
                return;
            }

            ArtworkManagerItem target = null;
            if (!string.IsNullOrEmpty(currentPath))
            {
                target = _items.FirstOrDefault(a =>
                    string.Equals(a.Path, currentPath, StringComparison.OrdinalIgnoreCase));
            }

            if (target == null)
            {
                target = _items[0];
            }

            ItemsList.SelectedItem = target;
            ItemsList.ScrollIntoView(target);
            UpdateDeleteButton();
            UpdateFixedArtworkButton();
        }

        private string ResolveCurrentPlayniteArtworkPath()
        {
            try
            {
                string imageId = _kind == ArtworkKind.Cover
                    ? _game.CoverImage
                    : _game.BackgroundImage;

                if (string.IsNullOrEmpty(imageId))
                {
                    return null;
                }

                string path = _api.Database.GetFullFilePath(imageId);
                return !string.IsNullOrEmpty(path) && File.Exists(path)
                    ? path
                    : null;
            }
            catch (Exception)
            {
                // A missing/stale Playnite library file should simply leave the
                // manager empty; it must not prevent the manager from opening.
                return null;
            }
        }

        private void InitializeGameBehaviorChoices()
        {
            GameModeOverrideCombo.ItemsSource = new[]
            {
                new Choice<SelectionMode?>(Loc.Get("LOCImageRotaterManagerUseGlobal"), null),
                new Choice<SelectionMode?>(Loc.Get("LOCImageRotaterPickSession"), SelectionMode.Session),
                new Choice<SelectionMode?>(Loc.Get("LOCImageRotaterPickEverySelection"), SelectionMode.EverySelection),
                new Choice<SelectionMode?>(Loc.Get("LOCImageRotaterPickFixed"), SelectionMode.Fixed),
                new Choice<SelectionMode?>(Loc.Get("LOCImageRotaterPickDaily"), SelectionMode.Daily),
                new Choice<SelectionMode?>(Loc.Get("LOCImageRotaterPickSlideshow"), SelectionMode.Slideshow)
            };

            GameOrderOverrideCombo.ItemsSource = new[]
            {
                new Choice<SelectionOrder?>(Loc.Get("LOCImageRotaterManagerUseGlobal"), null),
                new Choice<SelectionOrder?>(Loc.Get("LOCImageRotaterOrderRandom"), SelectionOrder.Random),
                new Choice<SelectionOrder?>(Loc.Get("LOCImageRotaterOrderSequential"), SelectionOrder.Sequential),
                new Choice<SelectionOrder?>(Loc.Get("LOCImageRotaterOrderShuffle"), SelectionOrder.Shuffle)
            };

            RefreshGameBehaviorChoices();
        }

        private void RefreshGameBehaviorChoices()
        {
            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings == null)
            {
                GameModeOverrideCombo.IsEnabled = false;
                GameOrderOverrideCombo.IsEnabled = false;
                return;
            }

            _updatingGameBehavior = true;
            try
            {
                SelectChoice(GameModeOverrideCombo, settings.GetSelectionModeOverride(_game.Id, _kind));
                SelectChoice(GameOrderOverrideCombo, settings.GetSelectionOrderOverride(_game.Id, _kind));
            }
            finally
            {
                _updatingGameBehavior = false;
            }
        }

        private static void SelectChoice<T>(ComboBox combo, T value)
        {
            foreach (object item in combo.Items)
            {
                var choice = item as Choice<T>;
                if (choice != null && EqualityComparer<T>.Default.Equals(choice.Value, value))
                {
                    combo.SelectedItem = choice;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private void GameModeOverrideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingGameBehavior) return;
            var choice = GameModeOverrideCombo.SelectedItem as Choice<SelectionMode?>;
            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (choice == null || settings == null) return;
            settings.SetSelectionModeOverride(_game.Id, _kind, choice.Value);
            _saveSettings?.Invoke();
            NotifyImagesChanged();
        }

        private void GameOrderOverrideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingGameBehavior) return;
            var choice = GameOrderOverrideCombo.SelectedItem as Choice<SelectionOrder?>;
            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (choice == null || settings == null) return;
            settings.SetSelectionOrderOverride(_game.Id, _kind, choice.Value);
            _saveSettings?.Invoke();
            NotifyImagesChanged();
        }

        private void UpdateCounts()
        {
            string label = _kind == ArtworkKind.Cover
                ? Loc.Get("LOCImageRotaterMenuCovers")
                : Loc.Get("LOCImageRotaterMenuBackgrounds");
            FilesHeaderText.Text = Loc.Format("LOCImageRotaterManagerItemsHeading", label, _items.Count);
            UpdateSelectionSummary();
        }

        private void UpdateDeleteButton()
        {
            var selected = ItemsList.SelectedItems.Cast<ArtworkManagerItem>().ToList();
            int count = selected.Count;
            bool containsNative = selected.Any(item => item.IsNativeOriginal);

            // Preserved Playnite artwork is the game's native/original image.
            // It participates in rotation, but must never be removable from the
            // manager. Disabling the whole action for a mixed selection also
            // avoids a surprising partial delete.
            DeleteButton.IsEnabled = count > 0 && !containsNative;
            DeleteButton.Content = count > 1
                ? Loc.Format("LOCImageRotaterManagerDeleteSelectedCount", count)
                : Loc.Get("LOCImageRotaterManagerDeleteSelected");
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            if (SelectionSummaryText == null || ItemsList == null)
            {
                return;
            }

            int selected = ItemsList.SelectedItems.Count;
            SelectionSummaryText.Text = selected > 0
                ? Loc.Format("LOCImageRotaterManagerSelectedCount", selected)
                : Loc.Format("LOCImageRotaterManagerItemsCount", _items.Count);
        }

        private void UpdateFixedArtworkButton()
        {
            if (SetFixedArtworkButton == null || ItemsList == null)
            {
                return;
            }

            var selected = ItemsList.SelectedItem as ArtworkManagerItem;
            SetFixedArtworkButton.IsEnabled =
                ItemsList.SelectedItems.Count == 1 && selected != null &&
                !selected.IsFixed && !selected.IsVirtualOriginal;
        }

        private void UpdateOrderButtons()
        {
            if (MoveUpButton == null || MoveDownButton == null || ItemsList == null)
            {
                return;
            }

            var selected = ItemsList.SelectedItem as ArtworkManagerItem;
            int index = selected != null ? _items.IndexOf(selected) : -1;
            bool single = ItemsList.SelectedItems.Count == 1 && index >= 0 &&
                selected != null && !selected.IsVirtualOriginal;
            MoveUpButton.IsEnabled = single && index > 0;
            MoveDownButton.IsEnabled = single && index < _items.Count - 1;
        }

        private void MoveArtworkButton_Click(object sender, RoutedEventArgs e)
        {
            if (ItemsList.SelectedItems.Count != 1)
            {
                return;
            }

            var selected = ItemsList.SelectedItem as ArtworkManagerItem;
            if (selected == null || selected.IsVirtualOriginal)
            {
                return;
            }

            int delta = ReferenceEquals(sender, MoveUpButton) ? -1 : 1;
            if (_store.MoveArtwork(_game.Id, _kind, selected.Path, delta))
            {
                NotifyImagesChanged();
                ReloadItems(selected.Path);
            }
        }

        private void SetFixedArtworkButton_Click(object sender, RoutedEventArgs e)
        {
            if (ItemsList.SelectedItems.Count != 1)
            {
                return;
            }

            var selected = ItemsList.SelectedItem as ArtworkManagerItem;
            if (selected == null || selected.IsFixed || selected.IsVirtualOriginal)
            {
                return;
            }

            if (!_store.SetFixedArtwork(_game.Id, _kind, selected.Path))
            {
                return;
            }

            _sessionCache?.Forget(_game.Id);
            NotifyImagesChanged();
            ReloadItems(selected.Path);
        }

        private void OpenSearchButton_Click(object sender, RoutedEventArgs e)
        {
            ManagerTabs.SelectedItem = SearchTab;
        }

        private void NotifyImagesChanged()
        {
            _sessionCache?.Forget(_game.Id);
            _onImagesChanged?.Invoke(_game.Id);
        }

        private ArtworkManagerItem SelectedArtwork()
        {
            return ItemsList.SelectedItem as ArtworkManagerItem;
        }

        private void ItemsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject source = e.OriginalSource as DependencyObject;
            while (source != null && !(source is ListViewItem))
            {
                source = VisualTreeHelper.GetParent(source);
            }
            var item = source as ListViewItem;
            if (item != null)
            {
                ItemsList.SelectedItems.Clear();
                item.IsSelected = true;
                item.Focus();
            }
        }

        private void ArtworkContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ArtworkManagerItem item = SelectedArtwork();
            ToggleExcludeMenuItem.Header = item != null && item.IsExcluded
                ? Loc.Get("LOCImageRotaterManagerIncludeRotation")
                : Loc.Get("LOCImageRotaterManagerExcludeRotation");

            bool canModify = item != null && !item.IsVirtualOriginal;

            if (SetFixedArtworkMenuItem != null)
            {
                SetFixedArtworkMenuItem.IsEnabled = canModify && !item.IsFixed;
            }

            if (ToggleExcludeMenuItem != null)
            {
                ToggleExcludeMenuItem.IsEnabled = canModify;
            }

            if (MoveUpMenuItem != null) MoveUpMenuItem.IsEnabled = canModify;
            if (MoveDownMenuItem != null) MoveDownMenuItem.IsEnabled = canModify;
            if (MoveTopMenuItem != null) MoveTopMenuItem.IsEnabled = canModify;
            if (MoveBottomMenuItem != null) MoveBottomMenuItem.IsEnabled = canModify;

            if (DeleteArtworkMenuItem != null)
            {
                DeleteArtworkMenuItem.IsEnabled = item != null && !item.IsNativeOriginal;
            }
        }

        private void ContextSetFixed_Click(object sender, RoutedEventArgs e)
        {
            SetFixedArtworkButton_Click(sender, e);
        }

        private void ContextToggleExclude_Click(object sender, RoutedEventArgs e)
        {
            ArtworkManagerItem item = SelectedArtwork();
            if (item == null || item.IsVirtualOriginal) return;
            if (_store.SetArtworkExcluded(_game.Id, _kind, item.Path, !item.IsExcluded))
            {
                NotifyImagesChanged();
                ReloadItems(item.Path);
            }
        }

        private void ContextMoveUp_Click(object sender, RoutedEventArgs e) => MoveSelectedBy(-1);
        private void ContextMoveDown_Click(object sender, RoutedEventArgs e) => MoveSelectedBy(1);
        private void ContextMoveTop_Click(object sender, RoutedEventArgs e) => MoveSelectedTo(0);
        private void ContextMoveBottom_Click(object sender, RoutedEventArgs e) => MoveSelectedTo(Math.Max(0, _items.Count - 1));

        private void MoveSelectedBy(int delta)
        {
            ArtworkManagerItem item = SelectedArtwork();
            if (item == null || item.IsVirtualOriginal) return;
            if (_store.MoveArtwork(_game.Id, _kind, item.Path, delta))
            {
                NotifyImagesChanged();
                ReloadItems(item.Path);
            }
        }

        private void MoveSelectedTo(int index)
        {
            ArtworkManagerItem item = SelectedArtwork();
            if (item == null || item.IsVirtualOriginal) return;
            if (_store.MoveArtworkToIndex(_game.Id, _kind, item.Path, index))
            {
                NotifyImagesChanged();
                ReloadItems(item.Path);
            }
        }

        private void ContextOpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            ArtworkManagerItem item = SelectedArtwork();
            if (item == null) return;
            try
            {
                Process.Start("explorer.exe", "/select,\"" + item.Path + "\"");
            }
            catch (Exception)
            {
            }
        }

        private void ContextDelete_Click(object sender, RoutedEventArgs e)
        {
            DeleteButton_Click(sender, e);
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            List<string> selected = _api.Dialogs.SelectFiles(
                Loc.Get("LOCImageRotaterArtworkFilter") + "|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif;*.mp4;*.webm"
                + "|" + Loc.Get("LOCImageRotaterImagesFilter") + "|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif"
                + "|" + Loc.Get("LOCImageRotaterVideoFilter") + "|*.mp4;*.webm");

            if (selected == null || selected.Count == 0)
            {
                return;
            }

            int added = 0;
            int optimised = 0;
            ImageRotaterSettings currentSettings = _settings?.Invoke();
            ImageOptimizer optimizer = currentSettings?.OptimiseDownloadedImages == true
                ? new ImageOptimizer(_store)
                : null;

            foreach (string source in selected)
            {
                string addedPath = _store.AddImage(_game.Id, source, _kind);
                if (addedPath == null)
                {
                    continue;
                }

                added++;

                if (optimizer != null && ImageOptimizer.CanOptimiseAutomatically(addedPath))
                {
                    string finalPath;
                    if (optimizer.Optimise(addedPath, null, out finalPath))
                    {
                        optimised++;
                    }
                }
            }

            if (added <= 0)
            {
                return;
            }

            NotifyImagesChanged();
            ReloadItems();

            string message = Loc.Format("LOCImageRotaterAddedImages", added, 1);
            if (optimised > 0)
            {
                message += "\n" + Loc.Format("LOCImageRotaterAddedImagesOptimised", optimised);
            }

            _api.Dialogs.ShowMessage(message, "ImageRotater");
        }

        private void AutomaticButton_Click(object sender, RoutedEventArgs e)
        {
            _automaticDownload?.Invoke();
            NotifyImagesChanged();
            ReloadItems();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            List<ArtworkManagerItem> selected = ItemsList.SelectedItems
                .Cast<ArtworkManagerItem>()
                .ToList();

            if (selected.Count == 0)
            {
                _api.Dialogs.ShowMessage(
                    Loc.Get("LOCImageRotaterManagerDeleteNone"),
                    "ImageRotater");
                return;
            }

            // Native Playnite artwork is preserved as original_* internally and
            // is deliberately read-only in the manager.
            if (selected.Any(item => item.IsNativeOriginal))
            {
                return;
            }

            MessageBoxResult confirm = _api.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterManagerDeleteQuestion", selected.Count),
                "ImageRotater",
                MessageBoxButton.YesNo);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            int removed = 0;
            foreach (ArtworkManagerItem item in selected)
            {
                if (_store.RemoveImage(item.Path))
                {
                    removed++;
                }
            }

            if (removed <= 0)
            {
                return;
            }

            NotifyImagesChanged();
            ReloadItems();

            _api.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterManagerDeleted", removed),
                "ImageRotater");
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = _store.GetGameFolder(_game.Id, _kind);
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", folder);
            }
            catch (Exception)
            {
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadItems();
        }

        private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDeleteButton();
            UpdateFixedArtworkButton();
            UpdateOrderButtons();

            ArtworkManagerItem item = ItemsList.SelectedItem as ArtworkManagerItem;
            if (item == null)
            {
                ClearPreviewHint();
                return;
            }

            ShowPreview(item);
        }

        private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ArtworkManagerItem item = ItemsList.SelectedItem as ArtworkManagerItem;
            if (item != null)
            {
                ShowPreview(item);
            }
        }

        private void ShowPreview(ArtworkManagerItem item)
        {
            StopPreview();

            PreviewEmptyPanel.Visibility = Visibility.Collapsed;
            PreviewNameText.Text = item.Name;
            PreviewMetaText.Text = item.DetailsLabel + " • " + item.ModifiedLabel;

            try
            {
                if (item.IsVideo)
                {
                    PreviewHintText.Visibility = Visibility.Collapsed;
                    PreviewImage.Visibility = Visibility.Collapsed;
                    PreviewVideo.Visibility = Visibility.Visible;
                    PreviewVideo.Volume = 0;
                    PreviewVideo.Source = new Uri(item.Path, UriKind.Absolute);
                    PreviewVideo.Position = TimeSpan.Zero;
                    PreviewVideo.Play();
                    return;
                }

                PreviewHintText.Visibility = Visibility.Collapsed;
                PreviewVideo.Visibility = Visibility.Collapsed;
                PreviewImage.Visibility = Visibility.Visible;

                if (item.IsGif)
                {
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(
                        PreviewImage,
                        new Uri(item.Path, UriKind.Absolute));
                    return;
                }

                PreviewImage.Source = LoadBitmap(item.Path);
            }
            catch (Exception)
            {
                StopPreview();
                PreviewHintText.Text = Loc.Get("LOCImageRotaterManagerPreviewUnavailable");
                PreviewEmptyPanel.Visibility = Visibility.Visible;
            }
        }

        private void PreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                PreviewVideo.Position = TimeSpan.Zero;
                PreviewVideo.Play();
            }
            catch (Exception)
            {
            }
        }

        private void PreviewVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            StopPreview();
            PreviewHintText.Text = Loc.Get("LOCImageRotaterManagerPreviewUnavailable");
            PreviewEmptyPanel.Visibility = Visibility.Visible;
        }

        private void StopPreview()
        {
            try
            {
                PreviewVideo.Stop();
            }
            catch (Exception)
            {
            }

            PreviewVideo.Source = null;
            PreviewVideo.Visibility = Visibility.Collapsed;

            XamlAnimatedGif.AnimationBehavior.SetSourceUri(PreviewImage, null);
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
        }

        private void ClearPreviewHint()
        {
            StopPreview();
            PreviewHintText.Text = _items.Count == 0
                ? Loc.Get("LOCImageRotaterManagerNoItemsPreview")
                : Loc.Get("LOCImageRotaterManagerSelectItem");
            PreviewEmptyPanel.Visibility = Visibility.Visible;
            PreviewNameText.Text = string.Empty;
            PreviewMetaText.Text = string.Empty;
        }

        private static BitmapImage LoadBitmap(string path)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                unit == 0 ? "{0:0} {1}" : "{0:0.#} {1}",
                value,
                units[unit]);
        }

        private sealed class Choice<T>
        {
            public Choice(string label, T value)
            {
                Label = label;
                Value = value;
            }
            public string Label { get; }
            public T Value { get; }

            public override string ToString()
            {
                return Label ?? string.Empty;
            }
        }

        private sealed class ArtworkManagerItem
        {
            public ArtworkManagerItem(
                string path,
                bool isFixed,
                bool isExcluded,
                int orderIndex,
                ArtworkKind artworkKind,
                bool forceNativeOriginal = false,
                bool isVirtualOriginal = false)
            {
                Path = path;
                IsFixed = isFixed;
                IsExcluded = isExcluded;
                IsNativeOriginal = forceNativeOriginal || GameImageStore.IsPreservedOriginal(path);
                IsVirtualOriginal = isVirtualOriginal;
                OrderLabel = "#" + orderIndex.ToString(CultureInfo.InvariantCulture);
                FixedBadgeVisibility = isFixed ? Visibility.Visible : Visibility.Collapsed;
                ExcludedBadgeVisibility = isExcluded ? Visibility.Visible : Visibility.Collapsed;
                OriginalBadgeVisibility = IsNativeOriginal ? Visibility.Visible : Visibility.Collapsed;

                // Keep the original_* filename on disk for reliable detection, but never
                // expose that technical name in the manager. The badge communicates the
                // role (Original), while the title communicates where the artwork comes from.
                Name = IsNativeOriginal
                    ? (artworkKind == ArtworkKind.Cover
                        ? Loc.Get("LOCImageRotaterManagerPlayniteCover")
                        : Loc.Get("LOCImageRotaterManagerPlayniteBackground"))
                    : System.IO.Path.GetFileName(path);

                string ext = System.IO.Path.GetExtension(path) ?? string.Empty;
                IsVideo = string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".webm", StringComparison.OrdinalIgnoreCase);
                IsGif = string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase);

                if (IsVideo)
                {
                    TypeLabel = Loc.Get("LOCImageRotaterManagerVideo");
                }
                else if (IsGif)
                {
                    TypeLabel = Loc.Get("LOCImageRotaterManagerAnimatedGif");
                }
                else
                {
                    TypeLabel = Loc.Get("LOCImageRotaterManagerStill");
                }

                var info = new FileInfo(path);
                SizeLabel = FormatBytes(info.Exists ? info.Length : 0);
                ModifiedLabel = info.Exists
                    ? info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)
                    : string.Empty;

                string dimensions = TryGetDimensions(path, IsVideo);
                DetailsLabel = string.IsNullOrEmpty(dimensions)
                    ? TypeLabel + " • " + SizeLabel
                    : dimensions + " • " + TypeLabel + " • " + SizeLabel;
                VideoIconVisibility = IsVideo ? Visibility.Visible : Visibility.Collapsed;
                ThumbnailSource = TryLoadThumbnail(path, IsVideo, IsGif);
            }

            public string Path { get; }
            public bool IsFixed { get; }
            public bool IsExcluded { get; }
            public bool IsNativeOriginal { get; }
            public bool IsVirtualOriginal { get; }
            public string OrderLabel { get; }
            public Visibility FixedBadgeVisibility { get; }
            public Visibility ExcludedBadgeVisibility { get; }
            public Visibility OriginalBadgeVisibility { get; }
            public string Name { get; }
            public bool IsVideo { get; }
            public bool IsGif { get; }
            public string TypeLabel { get; }
            public string SizeLabel { get; }
            public string ModifiedLabel { get; }
            public string DetailsLabel { get; }
            public Visibility VideoIconVisibility { get; }
            public ImageSource ThumbnailSource { get; }

            private static ImageSource TryLoadThumbnail(string path, bool isVideo, bool isGif)
            {
                if (isVideo)
                {
                    return null;
                }

                try
                {
                    string source = path;
                    if (string.IsNullOrEmpty(source) || !File.Exists(source))
                    {
                        return null;
                    }

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 220;
                    bitmap.UriSource = new Uri(source, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
                catch
                {
                    return null;
                }
            }

            private static string TryGetDimensions(string path, bool isVideo)
            {
                if (isVideo)
                {
                    return string.Empty;
                }

                try
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var decoder = BitmapDecoder.Create(
                            stream,
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.None);
                        BitmapFrame frame = decoder.Frames.FirstOrDefault();
                        if (frame != null && frame.PixelWidth > 0 && frame.PixelHeight > 0)
                        {
                            return frame.PixelWidth.ToString(CultureInfo.InvariantCulture)
                                + " × "
                                + frame.PixelHeight.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                }
                catch
                {
                }

                return string.Empty;
            }
        }
    }
}
