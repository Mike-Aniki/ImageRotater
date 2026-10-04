using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Rotation source for one artwork kind.
    //
    // ImageRotater-owned files always come from the game's plugin folder. The
    // native Playnite artwork is then added as one logical "Original" candidate:
    //
    // - Theme Integration: use the live Playnite file directly. No copy.
    // - Compatibility: use the preserved safety copy, because Compatibility
    //   temporarily replaces Playnite's own artwork id during rotation.
    //
    // The source only activates when the user has at least one real
    // ImageRotater image. A native image alone must not opt the game into
    // rotation simply because a theme hosts our control.
    public class FolderImageSource : IBackgroundImageSource
    {
        private static readonly IReadOnlyList<string> Empty = new string[0];

        private readonly GameImageStore _store;
        private readonly ArtworkKind _kind;
        private readonly OriginalArtPreserver _preserver;
        private readonly Func<ImageRotaterSettings> _settings;

        public FolderImageSource(
            GameImageStore store,
            ArtworkKind kind,
            OriginalArtPreserver preserver = null,
            Func<ImageRotaterSettings> settings = null)
        {
            _store = store;
            _kind = kind;
            _preserver = preserver;
            _settings = settings;
        }

        public IReadOnlyList<string> GetImagePaths(Game game)
        {
            if (game == null || _store == null)
            {
                return Empty;
            }

            IReadOnlyList<string> stored = _store.GetImagePaths(game.Id, _kind);

            // original_* files are compatibility safety copies, not proof that
            // the user still has ImageRotater artwork configured for this game.
            var pluginArtwork = stored
                .Where(path => !GameImageStore.IsPreservedOriginal(path))
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .ToList();

            if (pluginArtwork.Count == 0)
            {
                return Empty;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            bool themeIntegration = settings?.UseThemeIntegration == true;

            if (!themeIntegration)
            {
                // Compatibility replaces Playnite's native id, so first make
                // sure the latest user-selected artwork has a stable copy.
                string compatibilityOriginal = _preserver?.Preserve(game, _kind);

                // Re-read after Preserve because it may have created or refreshed
                // original_native.*. Collapse any legacy original_* duplicates
                // to the one authoritative safety copy so old restart bugs cannot
                // weight Original multiple times in the rotation.
                IReadOnlyList<string> refreshed = _store.GetImagePaths(game.Id, _kind);
                var compatibilityPool = refreshed
                    .Where(path => !GameImageStore.IsPreservedOriginal(path))
                    .ToList();

                if (!string.IsNullOrEmpty(compatibilityOriginal) && File.Exists(compatibilityOriginal) &&
                    !_store.IsArtworkExcluded(game.Id, _kind, compatibilityOriginal))
                {
                    compatibilityPool.Insert(0, compatibilityOriginal);
                }

                return compatibilityPool;
            }

            // Theme Integration never copies native artwork. Resolve the live
            // Playnite image and insert it as the virtual Original candidate.
            // If the current id is a leftover Compatibility write, the preserver
            // resolves the logical original from its safety copy instead.
            string themeOriginal = _preserver?.ResolveOriginalPath(game, _kind);
            if (!string.IsNullOrEmpty(themeOriginal) && File.Exists(themeOriginal) &&
                !pluginArtwork.Any(path =>
                    string.Equals(path, themeOriginal, StringComparison.OrdinalIgnoreCase)))
            {
                // Keep Original first, matching Compatibility's original_*
                // ordering and the Artwork Manager. This also preserves the
                // historical Fixed-mode fallback when no explicit fixed marker
                // exists.
                pluginArtwork.Insert(0, themeOriginal);
            }

            return pluginArtwork;
        }
    }
}
