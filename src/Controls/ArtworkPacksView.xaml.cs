using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImageRotater.Models;
using ImageRotater.Services;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ImageRotater.Controls
{
    public partial class ArtworkPacksView : UserControl
    {
        private readonly IPlayniteAPI api;
        private readonly GameImageStore store;
        private readonly Game game;
        private readonly OriginalArtPreserver preserver;
        private readonly ExtraMetadataLogoSource logos;
        private readonly Func<ImageRotaterSettings> settings;
        private readonly Action save;
        private readonly ArtworkPackConfiguration config;
        private readonly ObservableCollection<PackCard> cards = new ObservableCollection<PackCard>();
        private bool updating;
        private bool dirty;
        private int previewGeneration;
        private int galleryGeneration;
        private int cardGeneration;
        private ArtworkKind galleryKind;
        private PackCard SelectedCard => PackList.SelectedItem as PackCard;
        private ArtworkPack Selected => SelectedCard?.Pack;

        public ArtworkPacksView(IPlayniteAPI api, GameImageStore store, Game game,
            OriginalArtPreserver preserver, Func<ImageRotaterSettings> settings, Action save)
        {
            this.api = api;
            this.store = store;
            this.game = game;
            this.preserver = preserver;
            this.settings = settings;
            this.save = save;
            logos = new ExtraMetadataLogoSource(api?.Paths?.ConfigurationPath);
            ArtworkPackConfiguration existing;
            config = settings()?.ArtworkPacks?.TryGetValue(game.Id, out existing) == true && existing != null
                ? JsonConvert.DeserializeObject<ArtworkPackConfiguration>(JsonConvert.SerializeObject(existing))
                : new ArtworkPackConfiguration();
            foreach (var pack in config.Packs ?? new List<ArtworkPack>())
                if (pack != null) cards.Add(new PackCard(pack));
            InitializeComponent();
            GameNameText.Text = game.Name;
            PackList.ItemsSource = cards;
            UpdateSummaries();
            PackList.SelectedIndex = cards.Count > 0 ? 0 : -1;
            UpdateActions();
            SavePacksButton.IsEnabled = false;
            StatusText.Text = Loc.Get("LOCImageRotaterPackSettingsHint");
            Loaded += async (s, e) => await LoadCardThumbnailsAsync();
            Unloaded += (s, e) => { previewGeneration++; galleryGeneration++; cardGeneration++; };
        }

        private void MarkDirty()
        {
            dirty = true;
            SavePacksButton.IsEnabled = true;
            StatusText.Text = Loc.Get("LOCImageRotaterPackUnsaved");
        }

        private void UpdateSummaries()
        {
            foreach (var card in cards)
            {
                card.RefreshName();
                card.Summary = Loc.Get(card.Pack.Excluded ? "LOCImageRotaterPackExcluded" :
                    card.Pack.Id == config.FixedPackId ? "LOCImageRotaterPackPinned" : "LOCImageRotaterPackIncluded");
            }
            UpdateActions();
        }

        private void UpdateActions()
        {
            if (UpPackButton == null) return;
            int index = PackList.SelectedIndex;
            UpPackButton.IsEnabled = index > 0;
            DownPackButton.IsEnabled = index >= 0 && index < cards.Count - 1;
            PinPackButton.IsEnabled = Selected != null && (Selected.Id != config.FixedPackId || Selected.Excluded);
        }

        private async void PackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Editor == null) return;
            updating = true;
            Editor.Visibility = Selected == null ? Visibility.Collapsed : Visibility.Visible;
            EmptyPanel.Visibility = Selected == null ? Visibility.Visible : Visibility.Collapsed;
            NameBox.Text = Selected?.Name ?? string.Empty;
            IncludedBox.IsChecked = Selected != null && !Selected.Excluded;
            UpdateActions();
            updating = false;
            await PreviewAsync();
        }

        private void Name_Changed(object sender, TextChangedEventArgs e)
        {
            if (updating || Selected == null) return;
            Selected.Name = NameBox.Text;
            SelectedCard.RefreshName();
            MarkDirty();
        }

        private void Included_Changed(object sender, RoutedEventArgs e)
        {
            if (updating || Selected == null) return;
            Selected.Excluded = IncludedBox.IsChecked != true;
            UpdateSummaries();
            MarkDirty();
        }

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null) return;
            config.FixedPackId = Selected.Id;
            Selected.Excluded = false;
            IncludedBox.IsChecked = true;
            UpdateSummaries();
            MarkDirty();
        }

        private string Original(ArtworkKind kind) => kind == ArtworkKind.Logo
            ? logos.GetLogoPath(game.Id) : preserver?.ResolveOriginalPath(game, kind);

        private string Resolve(ArtworkPack pack, ArtworkKind kind) =>
            new ArtworkPackService(store, settings, (g, k) => Original(k)).Resolve(game, pack, kind);

        private async Task PreviewAsync()
        {
            int generation = ++previewGeneration;
            var card = SelectedCard;
            var pack = card?.Pack;
            BackgroundPreview.Source = CoverPreview.Source = LogoPreview.Source = null;
            BackgroundSlotPreview.Source = CoverSlotPreview.Source = LogoSlotPreview.Source = null;
            WarningText.Text = string.Empty;
            if (pack == null) return;
            CoverSlotText.Text = SlotLabel(pack.Cover);
            BackgroundSlotText.Text = SlotLabel(pack.Background);
            LogoSlotText.Text = SlotLabel(pack.Logo);
            bool missing = new[] { Tuple.Create(ArtworkKind.Cover, pack.Cover), Tuple.Create(ArtworkKind.Background, pack.Background), Tuple.Create(ArtworkKind.Logo, pack.Logo) }
                .Any(p => !string.IsNullOrEmpty(p.Item2) &&
                    (Path.GetFileName(p.Item2) != p.Item2 || !File.Exists(Path.Combine(store.GetGameFolder(game.Id, p.Item1), p.Item2))));
            if (missing) WarningText.Text = Loc.Get("LOCImageRotaterPacksMissingHint");
            var entries = new[] {
                Tuple.Create(ArtworkKind.Background, BackgroundPreview, BackgroundSlotPreview),
                Tuple.Create(ArtworkKind.Cover, CoverPreview, CoverSlotPreview),
                Tuple.Create(ArtworkKind.Logo, LogoPreview, LogoSlotPreview) };
            foreach (var entry in entries)
            {
                string path = Resolve(pack, entry.Item1);
                if (string.IsNullOrEmpty(path)) continue;
                bool video = PosterFrame.IsVideo(path);
                var bitmap = await Task.Run(() => Thumbnail(path, 760));
                await Dispatcher.InvokeAsync(() =>
                {
                    if (generation != previewGeneration) return;
                    entry.Item2.Source = entry.Item3.Source = bitmap;
                    if (entry.Item1 == ArtworkKind.Background) card.Thumbnail = bitmap;
                    if (video && bitmap == null) WarningText.Text = Loc.Get("LOCImageRotaterPacksVideoPreviewUnavailable");
                });
                if (generation != previewGeneration) return;
            }
        }

        private string SlotLabel(string file) => string.IsNullOrEmpty(file)
            ? Loc.Get("LOCImageRotaterPacksOriginal") : Path.GetFileNameWithoutExtension(file);

        private async Task LoadCardThumbnailsAsync()
        {
            int generation = ++cardGeneration;
            foreach (var card in cards.ToList())
            {
                if (generation != cardGeneration) return;
                if (card == SelectedCard) continue;
                string path = Resolve(card.Pack, ArtworkKind.Background) ?? Resolve(card.Pack, ArtworkKind.Cover);
                if (string.IsNullOrEmpty(path)) continue;
                var image = await Task.Run(() => Thumbnail(path, 260));
                await Dispatcher.InvokeAsync(() =>
                {
                    if (generation == cardGeneration && card != SelectedCard) card.Thumbnail = image;
                });
            }
        }

        private static BitmapSource Thumbnail(string path, int width)
        {
            try
            {
                if (PosterFrame.IsVideo(path)) path = PosterFrame.ForVideo(path);
                else if (PosterFrame.IsAnimated(path)) path = PosterFrame.For(path);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = width;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }

        private async void ChooseArtwork_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null) return;
            galleryKind = (ArtworkKind)Enum.Parse(typeof(ArtworkKind), (string)((Button)sender).Tag);
            await ShowGalleryAsync();
        }

        private async Task ShowGalleryAsync(string selectFile = null)
        {
            int generation = ++galleryGeneration;
            GalleryTitle.Text = Loc.Get(galleryKind == ArtworkKind.Cover ? "LOCImageRotaterPackChooseCover" :
                galleryKind == ArtworkKind.Logo ? "LOCImageRotaterPackChooseLogo" : "LOCImageRotaterPackChooseBackground");
            GalleryOverlay.Visibility = Visibility.Visible;
            var choices = new List<ArtworkChoice> { new ArtworkChoice(null, Loc.Get("LOCImageRotaterPacksOriginal"), Original(galleryKind)) };
            choices.AddRange(store.GetImagePathsRaw(game.Id, galleryKind)
                .Where(p => !GameImageStore.IsPreservedOriginal(p))
                .Where(p => galleryKind != ArtworkKind.Logo || new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .Select(p => new ArtworkChoice(Path.GetFileName(p), Path.GetFileNameWithoutExtension(p), p)));
            ArtworkChoices.ItemsSource = choices;
            string currentFile = selectFile ?? (galleryKind == ArtworkKind.Cover ? Selected.Cover : galleryKind == ArtworkKind.Logo ? Selected.Logo : Selected.Background);
            ArtworkChoices.SelectedItem = choices.FirstOrDefault(c => c.FileName == currentFile) ?? choices[0];
            foreach (var choice in choices)
            {
                if (generation != galleryGeneration) return;
                if (string.IsNullOrEmpty(choice.Path)) continue;
                var thumbnail = await Task.Run(() => Thumbnail(choice.Path, 200));
                await Dispatcher.InvokeAsync(() => { if (generation == galleryGeneration) choice.Thumbnail = thumbnail; });
            }
        }

        private async void UseArtwork_Click(object sender, RoutedEventArgs e)
        {
            var choice = ArtworkChoices.SelectedItem as ArtworkChoice;
            if (choice == null || Selected == null) return;
            if (galleryKind == ArtworkKind.Cover) Selected.Cover = choice.FileName;
            else if (galleryKind == ArtworkKind.Logo) Selected.Logo = choice.FileName;
            else Selected.Background = choice.FileName;
            HideGallery();
            MarkDirty();
            await PreviewAsync();
        }

        private void ArtworkChoices_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(ArtworkChoices, e.OriginalSource as DependencyObject) != null)
                UseArtwork_Click(sender, e);
        }

        private async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            if (api == null) return;
            string filter = galleryKind == ArtworkKind.Logo ? "*.png;*.jpg;*.jpeg;*.bmp" : "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.webm";
            var files = api.Dialogs.SelectFiles(Loc.Get("LOCImageRotaterArtworkFilter") + "|" + filter);
            if (files == null || files.Count == 0) return;
            int generation = galleryGeneration;
            ArtworkKind kind = galleryKind;
            GalleryOverlay.IsEnabled = false;
            string last;
            try
            {
                last = await Task.Run(() =>
                {
                    string result = null;
                    foreach (string path in files)
                    {
                        string added = store.AddImage(game.Id, path, kind);
                        if (added != null) result = Path.GetFileName(added);
                    }
                    return result;
                });
            }
            finally { GalleryOverlay.IsEnabled = true; }
            if (generation == galleryGeneration) await ShowGalleryAsync(last);
        }

        private void HideGallery()
        {
            galleryGeneration++;
            GalleryOverlay.Visibility = Visibility.Collapsed;
            ArtworkChoices.ItemsSource = null;
        }
        private void CancelGallery_Click(object sender, RoutedEventArgs e) => HideGallery();

        private void New_Click(object sender, RoutedEventArgs e)
        {
            var card = new PackCard(new ArtworkPack { Name = Loc.Get("LOCImageRotaterPacksNew") + " " + (cards.Count + 1) });
            cards.Add(card);
            UpdateSummaries();
            PackList.SelectedItem = card;
            MarkDirty();
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var card = SelectedCard;
            if (card == null) return;
            int index = PackList.SelectedIndex;
            cards.Remove(card);
            if (config.FixedPackId == card.Pack.Id) config.FixedPackId = null;
            if (cards.Count > 0) PackList.SelectedIndex = Math.Min(index, cards.Count - 1);
            UpdateSummaries();
            MarkDirty();
        }

        private void Move(int delta)
        {
            int index = PackList.SelectedIndex;
            if (index < 0 || index + delta < 0 || index + delta >= cards.Count) return;
            cards.Move(index, index + delta);
            UpdateActions();
            MarkDirty();
        }
        private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
        private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
        private void Save_Click(object sender, RoutedEventArgs e) => SavePacks();

        private bool SavePacks()
        {
            if (cards.Any(c => string.IsNullOrWhiteSpace(c.Pack.Name)))
            {
                StatusText.Text = Loc.Get("LOCImageRotaterPackNameRequired");
                return false;
            }
            config.Packs = cards.Select(c => c.Pack).ToList();
            config.Enabled = config.Packs.Any(p => !p.Excluded);
            if (!config.Packs.Any(p => p.Id == config.FixedPackId && !p.Excluded))
                config.FixedPackId = config.Packs.FirstOrDefault(p => !p.Excluded)?.Id;
            var value = settings();
            if (value.ArtworkPacks == null) value.ArtworkPacks = new Dictionary<Guid, ArtworkPackConfiguration>();
            ArtworkPackConfiguration previous;
            bool hadPrevious = value.ArtworkPacks.TryGetValue(game.Id, out previous);
            try
            {
                value.ArtworkPacks[game.Id] = JsonConvert.DeserializeObject<ArtworkPackConfiguration>(JsonConvert.SerializeObject(config));
                value.NotifyPacksChanged();
                save?.Invoke();
                dirty = false;
                SavePacksButton.IsEnabled = false;
                UpdateSummaries();
                StatusText.Text = Loc.Get("LOCImageRotaterPacksSaved");
                return true;
            }
            catch (Exception ex)
            {
                if (hadPrevious) value.ArtworkPacks[game.Id] = previous;
                else value.ArtworkPacks.Remove(game.Id);
                value.NotifyPacksChanged();
                LogManager.GetLogger().Error(ex, "ImageRotater: could not save artwork packs");
                StatusText.Text = Loc.Get("LOCImageRotaterPackSaveFailed");
                return false;
            }
        }

        public bool TryClose()
        {
            if (!dirty) return true;
            if (api == null) return false;
            var answer = api.Dialogs.ShowMessage(Loc.Get("LOCImageRotaterPackCloseUnsaved"),
                Loc.Get("LOCImageRotaterPackManager"), MessageBoxButton.YesNoCancel);
            return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && SavePacks());
        }
        private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

        private sealed class PackCard : ObservableObject
        {
            public ArtworkPack Pack { get; }
            public string Name => Pack.Name;
            private BitmapSource thumbnail;
            public BitmapSource Thumbnail { get => thumbnail; set { thumbnail = value; OnPropertyChanged(); } }
            private string summary;
            public string Summary { get => summary; set { summary = value; OnPropertyChanged(); } }
            public PackCard(ArtworkPack pack) { Pack = pack; }
            public void RefreshName() => OnPropertyChanged(nameof(Name));
        }
        private sealed class ArtworkChoice : ObservableObject
        {
            public string FileName { get; }
            public string Label { get; }
            public string Path { get; }
            private BitmapSource thumbnail;
            public BitmapSource Thumbnail { get => thumbnail; set { thumbnail = value; OnPropertyChanged(); } }
            public ArtworkChoice(string name, string label, string path) { FileName = name; Label = label; Path = path; }
        }
    }
}
