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
        private readonly Action<Guid> _onImagesChanged;
        private readonly Action _automaticDownload;
        private readonly SteamGridDbSearchView _searchView;
        private readonly ObservableCollection<ArtworkManagerItem> _items =
            new ObservableCollection<ArtworkManagerItem>();

        public ArtworkManagerView(
            IPlayniteAPI api,
            GameImageStore store,
            SessionSelectionCache sessionCache,
            Game game,
            ArtworkKind kind,
            Action<Guid> onImagesChanged,
            SteamGridDbSearchView searchView,
            Action automaticDownload)
        {
            _api = api;
            _store = store;
            _sessionCache = sessionCache;
            _game = game;
            _kind = kind;
            _onImagesChanged = onImagesChanged;
            _automaticDownload = automaticDownload;
            _searchView = searchView;

            InitializeComponent();

            ItemsList.ItemsSource = _items;
            SearchHost.Content = _searchView;

            if (_searchView != null)
            {
                _searchView.ArtworkChanged += SearchView_ArtworkChanged;
            }

            LocalTab.Header = kind == ArtworkKind.Cover
                ? Loc.Get("LOCImageRotaterManagerMyCovers")
                : Loc.Get("LOCImageRotaterManagerMyBackgrounds");
            PreviewHintText.Text = Loc.Get("LOCImageRotaterManagerSelectItem");
            PreviewNameText.Text = string.Empty;
            PreviewMetaText.Text = string.Empty;

            Loaded += ArtworkManagerView_Loaded;
            Unloaded += ArtworkManagerView_Unloaded;
        }

        private void ArtworkManagerView_Loaded(object sender, RoutedEventArgs e)
        {
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

        private void ManagerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, ManagerTabs) ||
                LocalActionsPanel == null || LocalTab == null)
            {
                return;
            }

            bool local = ReferenceEquals(ManagerTabs.SelectedItem, LocalTab);
            LocalActionsPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;

            if (local)
            {
                ReloadItems();
            }
            else
            {
                StopPreview();
            }
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

            foreach (string path in _store
                .GetImagePathsRaw(_game.Id, _kind)
                .Where(GameImageStore.IsSupported)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                _items.Add(new ArtworkManagerItem(path));
            }

            UpdateCounts();

            bool hasItems = _items.Count > 0;
            EmptyListText.Text = _kind == ArtworkKind.Cover
                ? Loc.Get("LOCImageRotaterManagerNoCovers")
                : Loc.Get("LOCImageRotaterManagerNoBackgrounds");
            EmptyListText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

            if (!hasItems)
            {
                ClearPreviewHint();
                ItemsList.SelectedItem = null;
                UpdateDeleteButton();
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
        }

        private void UpdateCounts()
        {
            string label = _kind == ArtworkKind.Cover
                ? Loc.Get("LOCImageRotaterMenuCovers")
                : Loc.Get("LOCImageRotaterMenuBackgrounds");
            FilesHeaderText.Text = Loc.Format("LOCImageRotaterManagerItemsHeading", label, _items.Count);
        }

        private void UpdateDeleteButton()
        {
            int count = ItemsList.SelectedItems.Count;
            DeleteButton.IsEnabled = count > 0;
            DeleteButton.Content = count > 1
                ? Loc.Format("LOCImageRotaterManagerDeleteSelectedCount", count)
                : Loc.Get("LOCImageRotaterManagerDeleteSelected");
        }

        private void NotifyImagesChanged()
        {
            _sessionCache?.Forget(_game.Id);
            _onImagesChanged?.Invoke(_game.Id);
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
            foreach (string source in selected)
            {
                if (_store.AddImage(_game.Id, source, _kind) != null)
                {
                    added++;
                }
            }

            if (added <= 0)
            {
                return;
            }

            NotifyImagesChanged();
            ReloadItems();

            _api.Dialogs.ShowMessage(
                Loc.Format("LOCImageRotaterAddedImages", added, 1),
                "ImageRotater");
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

            PreviewNameText.Text = item.Name;
            PreviewMetaText.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0} • {1} • {2}",
                item.TypeLabel,
                item.SizeLabel,
                item.ModifiedLabel);

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
                PreviewHintText.Visibility = Visibility.Visible;
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
            PreviewHintText.Visibility = Visibility.Visible;
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
            PreviewHintText.Visibility = Visibility.Visible;
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

        private sealed class ArtworkManagerItem
        {
            public ArtworkManagerItem(string path)
            {
                Path = path;
                Name = System.IO.Path.GetFileName(path);

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
            }

            public string Path { get; }
            public string Name { get; }
            public bool IsVideo { get; }
            public bool IsGif { get; }
            public string TypeLabel { get; }
            public string SizeLabel { get; }
            public string ModifiedLabel { get; }
        }
    }
}
