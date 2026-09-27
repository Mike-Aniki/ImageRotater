using System;
// ObservableObject is declared in System.Collections.Generic inside
// Playnite.SDK.dll, not in the Playnite.SDK namespace.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    // A filter option plus whether the user ticked it. Options are rebuilt from
    // each result set rather than kept in a static list, which is what stops
    // them duplicating or offering values no result has.
    public class FilterOption : ObservableObject
    {
        private bool isChecked;

        public string Value { get; set; }
        public string Label { get; set; }

        // Runs after a tick changes, so the filter follows it with no Apply
        // button. Set by the view model once the option is built.
        public Action Changed { get; set; }

        public bool IsChecked
        {
            get => isChecked;
            set => SetChecked(value, true);
        }

        // A group ticking its sizes passes false and fires Changed once
        // itself, rather than once per size.
        internal void SetChecked(bool value, bool raiseChanged)
        {
            if (isChecked == value)
            {
                return;
            }

            isChecked = value;
            OnPropertyChanged(nameof(IsChecked));

            if (raiseChanged)
            {
                Changed?.Invoke();
            }
        }
    }

    // A named aspect ratio with its dimensions nested underneath. Ticking the
    // group ticks all of its dimensions, which is what makes "give me square
    // covers" one click rather than several.
    public class AspectGroupOption : ObservableObject
    {
        private bool isChecked;
        private bool isExpanded;

        public string Label { get; set; }
        public int TotalCount { get; set; }
        public ObservableCollection<FilterOption> Dimensions { get; }
            = new ObservableCollection<FilterOption>();

        // What the header shows. Kept apart from Label, which is the key a
        // remembered tick is matched by - the count changes with every
        // search, the shape does not.
        public string Display => $"{Label} ({TotalCount})";

        // Collapsed until opened: the group tick covers the common case, and
        // the resolutions under it are detail most searches never need.
        public bool IsExpanded
        {
            get => isExpanded;
            set { isExpanded = value; OnPropertyChanged(); }
        }

        public Action Changed { get; set; }

        public bool IsChecked
        {
            get => isChecked;
            set
            {
                isChecked = value;
                OnPropertyChanged();

                foreach (FilterOption dimension in Dimensions)
                {
                    dimension.SetChecked(value, false);
                }

                Changed?.Invoke();
            }
        }
    }

    // Drives the SteamGridDB search dialog. Kept free of WPF types beyond
    // ObservableObject so the search/filter flow is testable without a window.
    public class SteamGridDbSearchViewModel : ObservableObject
    {
        private readonly ISteamGridDbClient _client;

        private List<SteamGridDbArtwork> _allResults = new List<SteamGridDbArtwork>();

        // What the user wants ticked, kept apart from the option objects that
        // carry the ticks on screen.
        //
        // Those objects are rebuilt from every result set, so a tick used to
        // die with them: ticking a resolution and pressing Search cleared it,
        // and switching tabs (which rebuilds against no results) cleared
        // everything. These sets outlive the objects. They are refreshed from
        // the options only while options exist - so an empty rebuild keeps
        // the last real state - and a preset seeds them before the first
        // search, which is all "remember my filters" needs.
        private readonly HashSet<string> _wantGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _wantDims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _wantStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _expandedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Everything matching the current filter, of which Results holds one
        // page.
        //
        // Paging exists to bound cost, not to tidy the layout. Every visible
        // tile fetches and decodes a remote thumbnail, and a web search happily
        // returns a hundred hits - which is a hundred concurrent downloads in a
        // 32-bit process. A page is a ceiling on how much work one search can
        // start.
        private List<SteamGridDbArtwork> _filtered = new List<SteamGridDbArtwork>();

        private int _page;

        // Enough to fill the window without scrolling far, few enough that a
        // page costs little to render.
        private const int PageSize = 24;

        public int PageCount
        {
            get
            {
                return _filtered.Count == 0
                    ? 1
                    : (_filtered.Count + PageSize - 1) / PageSize;
            }
        }

        // 1-based for display; _page is the 0-based index.
        public int CurrentPage
        {
            get { return _page + 1; }
        }

        public bool HasMultiplePages
        {
            get { return PageCount > 1; }
        }

        public bool CanGoBack
        {
            get { return _page > 0; }
        }

        public bool CanGoForward
        {
            get { return _page + 1 < PageCount; }
        }

        public void NextPage()
        {
            if (!CanGoForward)
            {
                return;
            }

            _page++;
            ShowCurrentPage();
        }

        public void PreviousPage()
        {
            if (!CanGoBack)
            {
                return;
            }

            _page--;
            ShowCurrentPage();
        }

        private void ShowCurrentPage()
        {
            Results.Clear();

            foreach (SteamGridDbArtwork item in _filtered.Skip(_page * PageSize).Take(PageSize))
            {
                Results.Add(item);
            }

            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(HasMultiplePages));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(PageLabel));
        }

        public string PageLabel
        {
            get { return Loc.Format("LOCImageRotaterPageOf", CurrentPage, PageCount); }
        }

        private string _status = string.Empty;
        private bool _isBusy;

        public ObservableCollection<SteamGridDbArtwork> Results { get; }
            = new ObservableCollection<SteamGridDbArtwork>();

        public ObservableCollection<FilterOption> DimensionOptions { get; }
            = new ObservableCollection<FilterOption>();

        // Dimensions grouped under a named aspect ratio, so the user picks
        // "2:3 - Steam Vertical" rather than reading raw numbers.
        public ObservableCollection<AspectGroupOption> AspectGroups { get; }
            = new ObservableCollection<AspectGroupOption>();

        public ObservableCollection<FilterOption> StyleOptions { get; }
            = new ObservableCollection<FilterOption>();

        public ArtworkFilterState Filter { get; } = new ArtworkFilterState();

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        public SteamGridDbSearchViewModel(ISteamGridDbClient client)
        {
            _client = client;
        }

        // Resolves a game name to artwork. Returns false when nothing was found
        // or the request failed, with Status carrying the reason.
        // Runs a free-text web image search instead of querying SteamGridDB.
        //
        // Results land in the same list and carry the same shape, so filtering,
        // the checkboxes and the download loop all work unchanged. Web results
        // have no curated style, so the style filter will show a single "web"
        // entry for them - that is honest rather than hiding the difference.
        // YouTube, via yt-dlp.
        //
        // The one source that actually has motion artwork: SteamGridDB's
        // animated entries are WebP and WebM that WPF cannot decode, and Steam
        // publishes no video art at all.
        //
        // Results are mapped into the same model the other tabs use so the
        // existing tile, filter and selection all work unchanged - the poster
        // frame is an ordinary JPEG. Only the download differs, which
        // IsYouTube marks.
        public async Task<bool> SearchYouTubeAsync(
            string query, ImageRotaterSettings settings, CancellationToken cancellationToken)
        {
            var search = new YouTubeSearch(settings);

            if (!search.IsAvailable)
            {
                _allResults = new List<SteamGridDbArtwork>();
                RebuildFilterOptions();
                ApplyFilter();

                Status = Loc.Get("LOCImageRotaterYtDlpNotSetup");
                return false;
            }

            IsBusy = true;
            try
            {
                List<Models.YouTubeVideo> videos =
                    await search.SearchAsync(query, 24, cancellationToken).ConfigureAwait(true);

                var mapped = new List<SteamGridDbArtwork>();
                int id = 0;

                foreach (Models.YouTubeVideo video in videos)
                {
                    mapped.Add(new SteamGridDbArtwork
                    {
                        Id = ++id,
                        Url = video.Url,
                        ThumbnailUrl = video.ThumbnailUrl,

                        // hqdefault.jpg is always 480x360, and the real video is
                        // whatever it is until downloaded. These are here for the
                        // aspect filter, which needs numbers; the tile does NOT
                        // print them, because the same figure on every result
                        // describes the thumbnail while appearing to describe the
                        // video. See SteamGridDbArtwork.CaptionText.
                        Width = 480,
                        Height = 360,

                        Style = video.Channel,
                        Mime = "video/mp4",
                        DurationText = video.DurationText,
                        ViewCountText = video.ViewCountText,
                        IsYouTube = true,
                        YouTubeId = video.Id,

                        // Named from a URL hash like a web result: a YouTube id
                        // is not a SteamGridDB id and cannot name a file.
                        IsFromWeb = true
                    });
                }

                _allResults = mapped;
                RebuildFilterOptions();
                ApplyFilter();

                if (mapped.Count > 0)
                {
                    Status = Loc.Format("LOCImageRotaterFromYouTube", mapped.Count);
                }
                else if (!search.HasJsRuntime)
                {
                    // The likeliest cause by far, and unguessable otherwise:
                    // yt-dlp exits 0 with no results when it has no JS runtime,
                    // which is indistinguishable from finding nothing.
                    Status = Loc.Get("LOCImageRotaterDenoSearchMissing");
                }
                else
                {
                    Status = Loc.Get("LOCImageRotaterNoVideosFound");
                }

                return mapped.Count > 0;
            }
            catch (Exception ex)
            {
                Status = Loc.Format("LOCImageRotaterYouTubeSearchFailed", ex.Message);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Steam's own library artwork for this game.
        //
        // No search term and no API key: the appid is already on the Playnite
        // game, so this is a direct lookup rather than a search. Results are
        // plain JPEG, which WPF decodes natively - the reason this tab leads.
        public async Task<bool> LoadSteamArtworkAsync(
            Playnite.SDK.Models.Game game, ArtworkKind kind)
        {
            IsBusy = true;
            try
            {
                var source = new SteamArtworkSource();

                // Off-thread because each candidate costs a HEAD request, and
                // the first call may also download Steam's app list to match
                // this game by name. The collections updated afterwards are
                // bound to the UI, so this has to come back to this thread.
                string appId = await Task
                    .Run(() => SteamArtworkSource.ResolveAppId(game))
                    .ConfigureAwait(true);

                if (appId == null)
                {
                    _allResults = new List<SteamGridDbArtwork>();
                    RebuildFilterOptions();
                    ApplyFilter();

                    Status = Loc.Get("LOCImageRotaterNoSteamGame");
                    return false;
                }

                // Stills and trailers in one pass. Trailers are the animated
                // content the store page shows - plain MP4, no ffmpeg needed.
                List<SteamGridDbArtwork> found = await Task
                    .Run(() => source.GetArtwork(game, kind)
                        .Concat(source.GetVideo(game, kind))
                        .ToList())
                    .ConfigureAwait(true);

                _allResults = found;
                RebuildFilterOptions();
                ApplyFilter();

                int videos = found.Count(a => a.IsAnimated);

                Status = found.Count == 0
                    ? Loc.Get("LOCImageRotaterSteamNoArtwork")
                    : videos > 0
                        ? Loc.Format("LOCImageRotaterSteamFoundAnimated", found.Count, videos)
                        : Loc.Format("LOCImageRotaterSteamFound", found.Count);

                return found.Count > 0;
            }
            catch (Exception ex)
            {
                Status = Loc.Format("LOCImageRotaterSteamReachFailed", ex.Message);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<bool> SearchWebAsync(string query, WebImageSearch search)
        {
            if (search == null || !search.IsAvailable)
            {
                Status = Loc.Get("LOCImageRotaterWebUnavailable");
                return false;
            }

            IsBusy = true;
            try
            {
                // Only the browsing goes off-thread. Everything below touches
                // ObservableCollections that are bound to the UI, and WPF
                // refuses collection changes from any thread but the
                // dispatcher - running the whole method off-thread threw
                // NotSupportedException from RebuildFilterOptions.
                List<SteamGridDbArtwork> found =
                    await Task.Run(() => search.Search(query).ToList()).ConfigureAwait(true);

                _allResults = found;
                RebuildFilterOptions();
                ApplyFilter();

                if (_allResults.Count == 0)
                {
                    Status = Loc.Format("LOCImageRotaterNoImagesForQuery", query);
                    return false;
                }

                Status = Loc.Format("LOCImageRotaterMatchesForQuery", _filtered.Count, _allResults.Count, query);
                return true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<bool> SearchAsync(string gameName, SteamGridDbArtworkType type)
        {
            if (_client == null || !_client.IsConfigured)
            {
                Status = Loc.Get("LOCImageRotaterNoSgdbKey");
                return false;
            }

            IsBusy = true;
            try
            {
                SteamGridDbResult<List<SteamGridDbGame>> games =
                    await _client.SearchGamesAsync(gameName).ConfigureAwait(true);

                if (!games.Success)
                {
                    Status = games.Error;
                    return false;
                }

                if (games.Data == null || games.Data.Count == 0)
                {
                    Status = Loc.Format("LOCImageRotaterNoSgdbGame", gameName);
                    return false;
                }

                // First match: the autocomplete endpoint returns the closest
                // name match first, which is right for a per-game action.
                SteamGridDbResult<List<SteamGridDbArtwork>> artwork =
                    await _client.GetArtworkAsync(games.Data[0].Id, type).ConfigureAwait(true);

                if (!artwork.Success)
                {
                    Status = artwork.Error;
                    return false;
                }

                _allResults = artwork.Data ?? new List<SteamGridDbArtwork>();
                RebuildFilterOptions();
                ApplyFilter();

                // Both names, deliberately. SteamGridDB resolves the query to
                // its own title, so searching "persona 3" and "Persona 3
                // Reload" can land on the same game and return identical
                // results - which looks like the search did not run at all.
                // Showing what was matched, and what it was matched from,
                // makes that visible instead of mysterious.
                string matched = games.Data[0].Name;
                string via = string.Equals(matched, gameName, StringComparison.OrdinalIgnoreCase)
                    ? Loc.Format("LOCImageRotaterQuoted", matched)
                    : Loc.Format("LOCImageRotaterMatchedFrom", matched, gameName);

                if (_allResults.Count == 0)
                {
                    Status = Loc.Format("LOCImageRotaterNoArtworkFor", via);
                    return false;
                }

                Status = Loc.Format("LOCImageRotaterMatchesFor", _filtered.Count, _allResults.Count, via);
                return true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Seeds the ticks from a remembered preset. Call before the first
        // search; the options that search builds come up already ticked.
        public void SeedTicks(SearchPreset preset)
        {
            if (preset == null)
            {
                return;
            }

            _wantGroups.UnionWith(preset.Groups ?? new List<string>());
            _wantDims.UnionWith(preset.Dimensions ?? new List<string>());
            _wantStyles.UnionWith(preset.Styles ?? new List<string>());
        }

        // Writes the current ticks into a preset, for saving.
        public void SnapshotTicks(SearchPreset preset)
        {
            CollectTicks();

            preset.Groups = _wantGroups.ToList();
            preset.Dimensions = _wantDims.ToList();
            preset.Styles = _wantStyles.ToList();
        }

        // Reads the ticks back off the option objects - only while there are
        // any. An empty option list says nothing about what the user wants.
        //
        // A group counts as wanted only when every dimension under it is
        // ticked. The group box itself is not consulted: its setter cascades
        // down but nothing cascades back up, so after "tick the group, untick
        // one size" the box still reads true and would restore the size.
        private void CollectTicks()
        {
            if (DimensionOptions.Count == 0 && StyleOptions.Count == 0)
            {
                return;
            }

            _wantGroups.Clear();
            _wantDims.Clear();
            _wantStyles.Clear();
            _expandedGroups.Clear();

            foreach (AspectGroupOption group in AspectGroups)
            {
                if (group.IsExpanded)
                {
                    _expandedGroups.Add(group.Label);
                }

                if (group.Dimensions.Count > 0 && group.Dimensions.All(d => d.IsChecked))
                {
                    _wantGroups.Add(group.Label);
                }
            }

            foreach (FilterOption option in DimensionOptions)
            {
                if (option.IsChecked && !string.IsNullOrEmpty(option.Value))
                {
                    _wantDims.Add(option.Value);
                }
            }

            foreach (FilterOption option in StyleOptions)
            {
                if (option.IsChecked && !string.IsNullOrEmpty(option.Value))
                {
                    _wantStyles.Add(option.Value);
                }
            }
        }

        // Options come from the current results, so they cannot duplicate and
        // cannot offer a value that would filter everything away.
        //
        // Only values that exist in the new results come back ticked. A
        // resolution nothing matches any more cannot be re-ticked, and
        // silently keeping it would filter every result away and look like
        // the search returned nothing.
        private void RebuildFilterOptions()
        {
            CollectTicks();

            DimensionOptions.Clear();
            AspectGroups.Clear();

            // Grouped by aspect ratio, with the flat list kept in step so
            // ApplyFilter has one place to read ticked dimensions from.
            foreach (AspectGrouping grouping in AspectGroup.Build(_allResults))
            {
                var group = new AspectGroupOption
                {
                    Label = grouping.Label,
                    TotalCount = grouping.TotalCount,
                    IsExpanded = _expandedGroups.Contains(grouping.Label)
                };

                foreach (DimensionCount dimension in grouping.Dimensions)
                {
                    var option = new FilterOption
                    {
                        Value = dimension.Dimensions,
                        Label = $"{dimension.Dimensions} ({dimension.Count})",
                        IsChecked = _wantDims.Contains(dimension.Dimensions)
                    };

                    group.Dimensions.Add(option);
                    DimensionOptions.Add(option);
                }

                // A wanted group ticks every size under it - including sizes
                // this result set is the first to offer. Cascading true onto
                // sizes already ticked changes nothing.
                if (_wantGroups.Contains(grouping.Label)
                    || (group.Dimensions.Count > 0 && group.Dimensions.All(d => d.IsChecked)))
                {
                    group.IsChecked = true;
                }

                // Wired last: the ticks above are the rebuild restoring state,
                // not the user changing it, and the caller applies once after.
                group.Changed = ApplyFilter;
                foreach (FilterOption option in group.Dimensions)
                {
                    option.Changed = ApplyFilter;
                }

                AspectGroups.Add(group);
            }

            StyleOptions.Clear();
            foreach (string style in ArtworkFilter.AvailableStyles(_allResults))
            {
                StyleOptions.Add(new FilterOption
                {
                    Value = style,
                    Label = style,
                    IsChecked = _wantStyles.Contains(style),
                    Changed = ApplyFilter
                });
            }
        }

        // Everything ticked, across ALL results rather than the visible ones.
        //
        // Filtering rebuilds Results, so reading the visible list would silently
        // drop a tick the user made before narrowing the filter - they would ask
        // for five images and get three with no explanation.
        public IReadOnlyList<SteamGridDbArtwork> SelectedArtwork()
        {
            return _allResults.Where(a => a.IsSelected).ToList();
        }

        // Empties the results, for when the tab changes.
        //
        // Without this the previous tab's results stay on screen under the new
        // tab's header for as long as the next search takes - which reads as
        // "the tab did not refresh", especially when the new search returns
        // fewer results than the old one.
        public void Clear()
        {
            _allResults = new List<SteamGridDbArtwork>();
            RebuildFilterOptions();
            ApplyFilter();
        }

        public void ApplyFilter()
        {
            Filter.Dimensions.Clear();
            foreach (FilterOption option in DimensionOptions.Where(o => o.IsChecked))
            {
                Filter.Dimensions.Add(option.Value);
            }

            Filter.Styles.Clear();
            foreach (FilterOption option in StyleOptions.Where(o => o.IsChecked))
            {
                Filter.Styles.Add(option.Value);
            }

            _filtered = ArtworkFilter.Apply(_allResults, Filter).ToList();

            // Back to page one whenever the filter changes: staying on page 4
            // of a result set that just shrank to two pages shows an empty grid
            // and looks like the filter found nothing.
            _page = 0;

            ShowCurrentPage();

            if (_allResults.Count > 0)
            {
                Status = Loc.Format("LOCImageRotaterMatches", _filtered.Count, _allResults.Count);
            }
        }
    }
}
