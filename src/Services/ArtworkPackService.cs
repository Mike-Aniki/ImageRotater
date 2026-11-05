using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageRotater.Models;
using Playnite.SDK.Models;

namespace ImageRotater.Services
{
    // One choice per game, shared by all renderers. Reading a source never
    // advances a pack; navigation and the single slideshow clock own that step.
    public sealed class ArtworkPackService
    {
        private readonly GameImageStore store;
        private readonly Func<ImageRotaterSettings> settings;
        private readonly Func<Game, ArtworkKind, string> original;
        private readonly ImageSelector selector;
        private readonly SessionSelectionCache cache;
        private readonly Dictionary<Guid, string> current = new Dictionary<Guid, string>();
        private Guid selected;

        public ArtworkPackService(GameImageStore store, Func<ImageRotaterSettings> settings,
            Func<Game, ArtworkKind, string> original, SessionSelectionCache cache = null)
        {
            this.store = store;
            this.settings = settings;
            this.original = original;
            this.cache = cache ?? new SessionSelectionCache();
            selector = new ImageSelector(new ImagePicker(), this.cache);
        }

        public ArtworkPackConfiguration Configuration(Guid id)
        {
            ArtworkPackConfiguration value;
            return settings()?.ArtworkPacks?.TryGetValue(id, out value) == true ? value : null;
        }

        public bool IsEnabled(Guid id) => settings()?.EnableRotation == true &&
            settings().IsPackEnabled(id);

        public void SelectGame(Game game)
        {
            bool changed = selected != (game?.Id ?? Guid.Empty);
            selected = game?.Id ?? Guid.Empty;
            if (game == null || !IsEnabled(game.Id)) return;
            if (changed || settings().PackSelectionMode == SelectionMode.ViewRefresh || !current.ContainsKey(game.Id))
                Choose(game.Id, false);
        }

        public ArtworkPack Current(Guid id)
        {
            if (!IsEnabled(id)) return null;
            var config = Configuration(id);
            string key;
            current.TryGetValue(id, out key);
            var pack = config.Packs.FirstOrDefault(p => p != null && !p.Excluded && p.Id == key);
            if (pack == null || settings().PackSelectionMode == SelectionMode.Daily || settings().PackSelectionMode == SelectionMode.Fixed)
                pack = Choose(id, false);
            return pack;
        }

        public ArtworkPack Advance(Guid id) => Choose(id, true);

        private ArtworkPack Choose(Guid id, bool advance)
        {
            if (!IsEnabled(id)) return null;
            var config = Configuration(id);
            var packs = config.Packs.Where(p => p != null && !p.Excluded && !string.IsNullOrEmpty(p.Id)).ToList();
            if (packs.Count == 0) return null;
            string previous;
            current.TryGetValue(id, out previous);
            var mode = advance ? SelectionMode.EverySelection : settings().PackSelectionMode;
            string choice = mode == SelectionMode.Fixed
                ? packs.FirstOrDefault(p => p.Id == config.FixedPackId)?.Id ?? packs[0].Id
                : selector.Select(id, packs.Select(p => p.Id).ToList(), previous, mode, settings().PackSelectionOrder);
            current[id] = choice;
            if (advance && settings().PackSelectionMode == SelectionMode.Slideshow) selector.Remember(id, choice);
            return packs.FirstOrDefault(p => p.Id == choice);
        }

        public bool TryGetPaths(Game game, ArtworkKind kind, out IReadOnlyList<string> paths)
        {
            paths = null;
            var pack = game == null ? null : Current(game.Id);
            if (pack == null) return false;
            string path = Resolve(game, pack, kind);
            paths = string.IsNullOrEmpty(path) ? new string[0] : new[] { path };
            return true;
        }

        public string Resolve(Game game, ArtworkPack pack, ArtworkKind kind)
        {
            string name = kind == ArtworkKind.Cover ? pack.Cover : kind == ArtworkKind.Logo ? pack.Logo : pack.Background;
            // Metadata must never escape the game's artwork folder.
            if (!string.IsNullOrEmpty(name) && name == Path.GetFileName(name) && !Path.IsPathRooted(name))
            {
                string path = Path.Combine(store.GetGameFolder(game.Id, kind), name);
                if (File.Exists(path) && GameImageStore.IsSupported(path)) return path;
            }
            string fallback = original?.Invoke(game, kind);
            return File.Exists(fallback) ? fallback : null;
        }

        public void Forget(Guid id)
        {
            current.Remove(id);
            cache.Forget(id);
        }
    }
}
