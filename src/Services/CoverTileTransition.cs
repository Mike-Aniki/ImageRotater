using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Transitions Playnite's own cover tile through a slideshow swap.
    //
    // A slideshow tick changes Game.CoverImage while the same game stays
    // selected. Playnite notifies the tile and it snaps to the new picture -
    // correct, but a hard cut every few seconds is not what a slideshow
    // should look like. This finds the Image showing the game's cover -
    // grid tile or details pane, either mode - and runs the swap behind the
    // configured transition (see Transition), drawn over it in the adorner
    // layer, so nothing is injected into the tile's tree and no theme
    // support is needed.
    public class CoverTileTransition
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly FileLogger _fileLogger;

        // Found once, remembered. The grid does not change identity within a
        // session, and walking the visual tree on every switch would be waste.
        private ListBox _grid;
        private Image _windowCover;
        private bool _searched;
        private bool _lazySearchInProgress;
        private Queue<DependencyObject> _lazySearchQueue;
        private Image _lazyBestWindowCover;
        private double _lazyBestWindowCoverArea;
        private Action<HashSet<Guid>> _visibleDiscoveryCallback;

        public CoverTileTransition(FileLogger fileLogger = null)
        {
            _fileLogger = fileLogger;
        }

        // Pay the one-time visual-tree lookup when Playnite is idle instead of
        // on the user's first cover click. This does not decode any artwork or
        // touch the database; it only caches the library ListBox reference.
        // Kept for source compatibility with older callers. Grid discovery is
        // now deliberately lazy: startup does zero visual-tree work.
        public void WarmUp()
        {
        }

        // Finds the currently REALIZED library items without forcing Playnite to
        // create containers for the rest of the library. This is used by Session
        // startup priming: visible covers get first priority, while off-screen
        // games can be filled in later without blocking Playnite startup.
        public void DiscoverVisibleGameIdsAsync(Action<HashSet<Guid>> callback)
        {
            if (callback == null)
            {
                return;
            }

            try
            {
                ListBox grid = GetCachedGrid();
                if (grid != null)
                {
                    Application.Current?.Dispatcher?.BeginInvoke(
                        DispatcherPriority.Loaded,
                        new Action(() => callback(GetVisibleGameIds(grid))));
                    return;
                }

                // Only one startup discovery is needed. If another caller ever
                // arrives before completion, chain it instead of starting a
                // second visual-tree walk.
                if (_visibleDiscoveryCallback == null)
                {
                    _visibleDiscoveryCallback = callback;
                }
                else
                {
                    Action<HashSet<Guid>> previous = _visibleDiscoveryCallback;
                    _visibleDiscoveryCallback = ids =>
                    {
                        previous(ids);
                        callback(ids);
                    };
                }

                EnsureLazySearch();
            }
            catch (Exception)
            {
                callback(new HashSet<Guid>());
            }
        }

        // Start a chunked visual-tree search after the first cover request.
        // Each dispatcher slice does only a tiny amount of work, so locating
        // Playnite's library grid never creates the large first-click hitch
        // that a full recursive scan caused. The first request simply swaps
        // without a transition; subsequent requests use the cached controls.
        private void EnsureLazySearch()
        {
            if (_lazySearchInProgress)
            {
                return;
            }

            try
            {
                Window window = Application.Current?.MainWindow;
                Dispatcher dispatcher = Application.Current?.Dispatcher;
                if (window == null || dispatcher == null)
                {
                    return;
                }

                _lazySearchInProgress = true;
                _searched = false;
                _lazyBestWindowCover = null;
                _lazyBestWindowCoverArea = 0;
                _lazySearchQueue = new Queue<DependencyObject>();
                _lazySearchQueue.Enqueue(window);

                dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(ProcessLazySearchChunk));
            }
            catch (Exception)
            {
                _lazySearchInProgress = false;
                _lazySearchQueue = null;
            }
        }

        private void ProcessLazySearchChunk()
        {
            const int MaxNodesPerSlice = 120;
            const int MaxMillisecondsPerSlice = 2;

            try
            {
                var sw = Stopwatch.StartNew();
                int processed = 0;

                while (_lazySearchQueue != null && _lazySearchQueue.Count > 0 &&
                       processed < MaxNodesPerSlice && sw.ElapsedMilliseconds < MaxMillisecondsPerSlice)
                {
                    DependencyObject node = _lazySearchQueue.Dequeue();
                    processed++;

                    var listBox = node as ListBox;
                    if (listBox?.Items != null && listBox.Items.Count > 0 &&
                        string.Equals(
                            listBox.Items[0]?.GetType().Name,
                            "GamesCollectionViewEntry",
                            StringComparison.Ordinal))
                    {
                        _grid = listBox;
                    }

                    var image = node as Image;
                    if (image != null && image.Name == "PART_ImageCover" && image.IsVisible)
                    {
                        double area = image.ActualWidth * image.ActualHeight;
                        if (area > _lazyBestWindowCoverArea)
                        {
                            _lazyBestWindowCover = image;
                            _lazyBestWindowCoverArea = area;
                        }
                    }

                    int count = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < count; i++)
                    {
                        _lazySearchQueue.Enqueue(VisualTreeHelper.GetChild(node, i));
                    }
                }

                if (_grid != null || _lazySearchQueue == null || _lazySearchQueue.Count == 0)
                {
                    _windowCover = _lazyBestWindowCover;
                    _searched = true;
                    _lazySearchInProgress = false;
                    _lazySearchQueue = null;

                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _fileLogger.Log(
                            $"cover transition lazy-cache grid={(_grid != null)} " +
                            $"windowCover={(_windowCover != null)}");
                    }

                    Action<HashSet<Guid>> callback = _visibleDiscoveryCallback;
                    _visibleDiscoveryCallback = null;
                    if (callback != null)
                    {
                        callback(_grid != null ? GetVisibleGameIds(_grid) : new HashSet<Guid>());
                    }
                    return;
                }

                Application.Current?.Dispatcher?.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(ProcessLazySearchChunk));
            }
            catch (Exception)
            {
                _lazySearchInProgress = false;
                _lazySearchQueue = null;
            }
        }

        // The swap runs BETWEEN the fades, not before them. Playnite's change
        // notification updates the tile the instant the database write lands,
        // so a fade started after the write would animate a tile that has
        // already snapped. Writing behind opacity 0 is the only ordering that
        // actually dissolves.
        //
        // The animation is explicitly detached at the end (BeginAnimation with
        // null) so a lingering clock cannot pin a recycled container's opacity
        // for the session, and every failure path restores opacity 1 - a tile
        // must never stay invisible.
        public void Run(Guid gameId, Action swap)
        {
            if (swap == null)
            {
                return;
            }

            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => RunNow(gameId, swap)));
            }
            catch (Exception)
            {
                // No dispatcher, no UI - but the rotation itself must still
                // happen.
                RunSwapSafely(swap);
            }
        }

        private void RunNow(Guid gameId, Action swap)
        {
            Image cover = null;
            var resolveTimer = Stopwatch.StartNew();

            try
            {
                ListBox grid = GetCachedGrid();

                // Do not synchronously walk Playnite's full visual tree on the
                // first request. That was the source of the one-time UI hitch.
                // Swap immediately and build the transition cache in tiny
                // dispatcher slices for the next request.
                if (grid == null && GetCachedWindowCover() == null)
                {
                    resolveTimer.Stop();
                    if (_fileLogger != null && _fileLogger.IsEnabled)
                    {
                        _fileLogger.Log(
                            $"cover transition lazy miss game={gameId} " +
                            $"resolve={resolveTimer.ElapsedMilliseconds}ms - swap now, cache deferred");
                    }

                    RunSwapSafely(swap);
                    EnsureLazySearch();
                    return;
                }

                object item = grid?.ItemsSource != null ? FindItem(grid, gameId) : null;
                var container = item != null
                    ? grid.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement
                    : null;

                cover = container != null ? FindCoverImage(container) : null;

                // A tiny Image is not a cover. Desktop's DETAILS layout put a
                // 26px PART_ImageIcon inside the list item and the fade ran on
                // that - invisible from the couch - while the real cover sat
                // in the details pane, outside the list entirely.
                if (cover != null && cover.ActualWidth * cover.ActualHeight < 64 * 64)
                {
                    cover = null;
                }

                // The visible cover can live outside the items container:
                // Desktop's details pane hosts its own PART_ImageCover
                // (GameOverview names it exactly that). Largest visible match
                // in the window wins.
                if (cover == null)
                {
                    cover = GetCachedWindowCover();
                }

                // Which link broke - or which element was chosen - decides
                // where a fix goes. A fade running on the WRONG Image (some
                // layer that is not the visible cover) looks identical to "no
                // fade" from the couch, so the success path logs its choice
                // too.
                resolveTimer.Stop();
                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    if (cover == null)
                    {
                        _fileLogger.Log(
                            $"cover transition fallback: grid={(grid != null)} item={(item != null)} "
                            + $"container={(container != null)} cover=False resolve={resolveTimer.ElapsedMilliseconds}ms");
                    }
                    else
                    {
                        _fileLogger.Log(
                            $"cover transition target: name='{cover.Name}' "
                            + $"{(int)cover.ActualWidth}x{(int)cover.ActualHeight} "
                            + $"visible={cover.IsVisible} opacity={cover.Opacity:0.##} "
                            + $"resolve={resolveTimer.ElapsedMilliseconds}ms");
                    }
                }
            }
            catch (Exception)
            {
                cover = null;
            }

            // Tile not on screen: nothing to animate, just rotate.
            if (cover == null)
            {
                RunSwapSafely(swap);
                return;
            }

            // The configured transition, drawn in the adorner layer over
            // Playnite's Image. A theme whose window template dropped its
            // AdornerDecorator leaves no layer to draw in; then the tile dips
            // through its own backdrop instead, which needs nothing.
            try
            {
                if (Transition.Run(cover, () => RunSwapSafely(swap)))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: cover transition failed, dipping instead");
            }

            DipSwap(cover, swap);
        }

        // Fades the tile out, swaps behind opacity 0, fades it back in.
        private static void DipSwap(Image cover, Action swap)
        {
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, 0.0, new Duration(Transition.CoverHalf));

            fadeOut.Completed += (s, e) =>
            {
                try
                {
                    // Playnite's own notification swaps the source while the
                    // tile is invisible - both modes, since 10.57. The fade
                    // back up waits for that source to land, since the
                    // binding is asynchronous.
                    Transition.SwapThen(cover, () => RunSwapSafely(swap), () =>
                    {
                        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(
                            0.0, 1.0, new Duration(Transition.CoverHalf));

                        fadeIn.Completed += (s2, e2) =>
                        {
                            cover.BeginAnimation(UIElement.OpacityProperty, null);
                            cover.Opacity = 1.0;
                        };

                        cover.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    });
                }
                catch (Exception)
                {
                    cover.BeginAnimation(UIElement.OpacityProperty, null);
                    cover.Opacity = 1.0;
                }
            };

            cover.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private static void RunSwapSafely(Action swap)
        {
            try
            {
                swap();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: slideshow swap failed");
            }
        }

        private static System.Reflection.PropertyInfo _idProperty;

        private static HashSet<Guid> GetVisibleGameIds(ListBox grid)
        {
            var result = new HashSet<Guid>();
            if (grid == null)
            {
                return result;
            }

            try
            {
                foreach (object item in grid.Items)
                {
                    var container = grid.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                    if (container == null || !container.IsVisible ||
                        container.ActualWidth <= 0 || container.ActualHeight <= 0)
                    {
                        continue;
                    }

                    Guid id;
                    if (TryGetItemId(item, out id))
                    {
                        result.Add(id);
                    }
                }
            }
            catch (Exception)
            {
            }

            return result;
        }

        private static bool TryGetItemId(object item, out Guid id)
        {
            id = Guid.Empty;
            if (item == null)
            {
                return false;
            }

            try
            {
                if (_idProperty == null || _idProperty.ReflectedType != item.GetType())
                {
                    _idProperty = item.GetType().GetProperty("Id");
                }

                object value = _idProperty?.GetValue(item);
                if (value is Guid)
                {
                    id = (Guid)value;
                    return id != Guid.Empty;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        // The entry whose game id matches, via reflection: the item type is
        // Playnite-internal and not referencable from a plugin.
        private static object FindItem(ListBox grid, Guid gameId)
        {
            foreach (object item in grid.Items)
            {
                try
                {
                    if (item == null)
                    {
                        continue;
                    }

                    Guid id;
                    if (TryGetItemId(item, out id) && id == gameId)
                    {
                        return item;
                    }
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        // The tile's cover element. PART_ImageCover by name when the theme
        // kept Playnite's naming; otherwise the largest rendered Image in the
        // tile - which in a grid tile is the cover by construction. Matching
        // on name alone broke on Desktop themes that rename the part: every
        // other link resolved and the fade silently fell back to a snap.
        private static Image FindCoverImage(DependencyObject root)
        {
            Image named = null;
            Image largest = null;
            double largestArea = 0;

            CollectImages(root, ref named, ref largest, ref largestArea);

            return named ?? largest;
        }

        private static void CollectImages(
            DependencyObject root, ref Image named, ref Image largest, ref double largestArea)
        {
            if (root == null || named != null)
            {
                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                var image = child as Image;
                if (image != null)
                {
                    if (image.Name == "PART_ImageCover")
                    {
                        named = image;
                        return;
                    }

                    double area = image.ActualWidth * image.ActualHeight;
                    if (area > largestArea)
                    {
                        largest = image;
                        largestArea = area;
                    }
                }

                CollectImages(child, ref named, ref largest, ref largestArea);
            }
        }

        private ListBox GetCachedGrid()
        {
            if (_grid != null && PresentationSource.FromVisual(_grid) != null)
            {
                return _grid;
            }

            if (_grid != null)
            {
                _grid = null;
                _windowCover = null;
                _searched = false;
                EnsureLazySearch();
            }

            return null;
        }

        private Image GetCachedWindowCover()
        {
            if (_windowCover != null && PresentationSource.FromVisual(_windowCover) != null)
            {
                return _windowCover;
            }

            _windowCover = null;
            return null;
        }


    }
}
