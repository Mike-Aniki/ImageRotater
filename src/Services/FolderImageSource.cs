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
    // native Playnite artwork is then added as one logical "Original" candidate,
    // read live from Playnite (or from the safety copy a release before 1.6 left).
    //
    // The source only activates when the user has at least one real
    // ImageRotater image. A native image alone must not opt the game into
    // rotation simply because a theme hosts our control.
    public class FolderImageSource : IBackgroundImageSource
    {
        public ArtworkPackService Packs { get; set; }
        private static readonly IReadOnlyList<string> Empty = new string[0];

        private readonly GameImageStore _store;
        private readonly ArtworkKind _kind;
        private readonly OriginalArtPreserver _preserver;
        private readonly ExtraMetadataLogoSource _extraMetadataLogos;

        public FolderImageSource(
            GameImageStore store,
            ArtworkKind kind,
            OriginalArtPreserver preserver = null,
            Func<ImageRotaterSettings> settings = null,
            ExtraMetadataLogoSource extraMetadataLogos = null)
        {
            _store = store;
            _kind = kind;
            _preserver = preserver;
            _extraMetadataLogos = extraMetadataLogos;
        }

        private static bool IsSupportedLogo(string path)
        {
            string ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp";
        }

        // Passive video->still handoff needs a static image even when the
        // active Artwork Pack contains only a video. Do not add the Original
        // to the pack's rotation pool: doing so would break synchronized packs.
        // Resolve it solely for the unfocused display fallback.
        public string GetOriginalStillPath(Game game)
        {
            if (game == null || _kind != ArtworkKind.Cover || _preserver == null)
                return null;

            string original = _preserver.ResolveOriginalPath(game, _kind);
            if (string.IsNullOrEmpty(original) || !File.Exists(original))
                return null;
            string ext = (Path.GetExtension(original) ?? string.Empty).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" ||
                   ext == ".bmp" || ext == ".gif" || ext == ".tif" || ext == ".tiff"
                ? original : null;
        }

        // Same answer as GetImagePaths(game).Count > 0, cheap enough for every
        // visible tile on every scan. The store's listing is cached against
        // the folder's write time, so no file is re-checked, and the live
        // Original is never resolved: it only joins games that already qualify.
        public bool HasArtwork(Game game)
        {
            if (game == null || _store == null)
            {
                return false;
            }

            IReadOnlyList<string> packPaths;
            if (Packs != null && Packs.TryGetPaths(game, _kind, out packPaths)) return packPaths.Count > 0;

            foreach (string path in _store.GetImagePaths(game.Id, _kind))
            {
                if (!string.IsNullOrEmpty(path) && !GameImageStore.IsPreservedOriginal(path) &&
                    (_kind != ArtworkKind.Logo || IsSupportedLogo(path)))
                {
                    return true;
                }
            }

            return false;
        }

        public IReadOnlyList<string> GetImagePaths(Game game)
        {
            if (game == null || _store == null)
            {
                return Empty;
            }

            IReadOnlyList<string> packPaths;
            if (Packs != null && Packs.TryGetPaths(game, _kind, out packPaths)) return packPaths;

            IReadOnlyList<string> stored = _store.GetImagePaths(game.Id, _kind);

            // original_* files are compatibility safety copies, not proof that
            // the user still has ImageRotater artwork configured for this game.
            var pluginArtwork = stored
                .Where(path => !GameImageStore.IsPreservedOriginal(path))
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Where(path => _kind != ArtworkKind.Logo || IsSupportedLogo(path))
                .ToList();

            if (pluginArtwork.Count == 0)
            {
                return Empty;
            }

            if (_kind == ArtworkKind.Logo)
            {
                // The external original is an optional read-only candidate;
                // at least one ImageRotater logo must still exist to activate rotation.
                string original = _extraMetadataLogos?.GetLogoPath(game.Id);
                if (!string.IsNullOrEmpty(original) &&
                    !_extraMetadataLogos.IsExcluded(_store, game.Id) &&
                    !pluginArtwork.Any(path => ExtraMetadataLogoSource.SameContent(path, original)))
                {
                    pluginArtwork.Insert(0, original);
                }
                return pluginArtwork;
            }

            if (_store.IsNativeArtworkExcluded(game.Id, _kind))
            {
                return pluginArtwork;
            }

            // Native artwork is never copied. Resolve the live Playnite image
            // and insert it as the virtual Original candidate.
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
