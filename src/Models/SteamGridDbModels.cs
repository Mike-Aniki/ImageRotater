using System.Collections.Generic;

namespace ImageRotater.Models
{
    // Which kind of artwork to fetch. SteamGridDB calls backgrounds "heroes".
    public enum SteamGridDbArtworkType
    {
        Hero,   // Wide background art - what ImageRotater displays today
        Grid,   // Vertical/horizontal cover art
        Logo
    }

    // One artwork result. Only the fields ImageRotater actually uses: enough to
    // show a thumbnail, filter the list, and download the full image.
    public class SteamGridDbArtwork : ObservableObject
    {
        private bool isSelected;

        // Ticked in the results list. Selection lives on the model rather than
        // in ListBox.SelectedItems so it survives filtering: applying a filter
        // rebuilds the list, and anything already ticked but filtered out would
        // otherwise be silently dropped from the download.
        //
        // It also makes multi-select discoverable. Ctrl-click worked, but
        // nothing on screen said so, and it is awkward with a controller.
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                {
                    return;
                }

                isSelected = value;
                OnPropertyChanged();
            }
        }

        public int Id { get; set; }
        public string Url { get; set; }
        public string ThumbnailUrl { get; set; }

        // SteamGridDB returns a .webm video in "thumb" for animated WebP
        // artwork. WPF Image cannot show any frame of that video. Keep the
        // regular thumbnail for still images; for videos, show the cached JPEG
        // frame once it has been created asynchronously.
        private string _staticVideoThumbnailUrl;

        public bool NeedsStaticVideoThumbnail
        {
            get
            {
                System.Uri uri;
                return System.Uri.TryCreate(ThumbnailUrl, System.UriKind.Absolute, out uri)
                    && uri.AbsolutePath.EndsWith(".webm", System.StringComparison.OrdinalIgnoreCase);
            }
        }

        public string TileThumbnailUrl
        {
            get { return NeedsStaticVideoThumbnail ? _staticVideoThumbnailUrl : ThumbnailUrl; }
        }

        public void SetStaticVideoThumbnail(string imageUrl)
        {
            if (!NeedsStaticVideoThumbnail || string.IsNullOrEmpty(imageUrl)
                || string.Equals(_staticVideoThumbnailUrl, imageUrl, System.StringComparison.Ordinal))
            {
                return;
            }
            _staticVideoThumbnailUrl = imageUrl;
            OnPropertyChanged(nameof(TileThumbnailUrl));
        }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Style { get; set; }
        public string Mime { get; set; }

        // Content flags, so a user can exclude what they do not want to see.
        public bool Nsfw { get; set; }
        public bool Humor { get; set; }
        public bool Epilepsy { get; set; }

        // True for results that came from a web image search rather than
        // SteamGridDB.
        //
        // Their Id is only a position within one search's results, so it cannot
        // be used to name a file - two searches would both produce "1". The
        // downloader hashes the URL for those instead.
        public bool IsFromWeb { get; set; }

        // Set for YouTube results, where Url is a WATCH PAGE rather than a
        // file. Fetching it directly would save the HTML, so the downloader
        // has to route these through yt-dlp instead.
        //
        // Thumbnail and dimensions still work normally: the tile shows the
        // video's poster frame, which is an ordinary JPEG.
        public bool IsYouTube { get; set; }

        // The YouTube video id, kept because nothing else on this model can
        // stand in for it. Id is an int the search assigns for the tile's sake,
        // and Url is the watch PAGE -- which is HTML, not media, and is exactly
        // what a <video> tag cannot play. The preview needs the id to build a
        // player URL, so it is carried rather than parsed back out of a URL.
        public string YouTubeId { get; set; }

        // Shown on the tile for video results - a background loops, so length
        // is the one thing worth knowing before downloading.
        public string DurationText { get; set; }

        // Views, for the caption. Carried for the same reason the duration is:
        // it is a fact about the video that the artwork model has no field for,
        // and it is the only number on a YouTube result that differs between
        // one result and the next.
        public string ViewCountText { get; set; }

        // "1920x1080" - used as a filter value and shown in the UI.
        public string Dimensions
        {
            get { return Width + "x" + Height; }
        }

        // What the tile prints where a size would go.
        //
        // A YouTube result has no size to print. Width and Height are the
        // POSTER's -- hqdefault.jpg is 480x360 for every video on the site, so
        // every result carried the identical figure, and it described a
        // thumbnail while appearing to describe the video. The real resolution
        // is unknown until the file has been downloaded, and finding it out for
        // two dozen search results means two dozen more yt-dlp calls.
        //
        // Views is the number that is both known and different per result, and
        // it is what anyone actually sorts a soundtrack upload by. Length is on
        // the thumbnail badge, so the two do not repeat each other.
        public string CaptionText
        {
            get
            {
                if (!IsYouTube)
                {
                    return Dimensions;
                }

                return string.IsNullOrEmpty(ViewCountText) ? string.Empty : ViewCountText;
            }
        }

        // True when the file actually moves.
        //
        // The thumbnail extension leads, because the MIME cannot answer this.
        // SteamGridDB reports animated WebP as "image/webp" - identical to a
        // still WebP - so matching the MIME alone badged every static WebP
        // cover as animated. What distinguishes them is the thumbnail: animated
        // entries get a .webm thumb, stills get .jpg or .png.
        //
        // Sampled to be sure rather than assumed: 23 of 23 animated results
        // across two games were image/webp with .webm thumbs, and none were
        // GIF. WPF decodes neither of those - its codec list is BMP, GIF, Icon,
        // JPEG, PNG, TIFF, WMP - which is why these need converting before they
        // can be shown moving at all.
        public bool IsAnimated
        {
            get
            {
                if (IsGif)
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(ThumbnailUrl) &&
                    ThumbnailUrl.IndexOf(".webm", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                return !string.IsNullOrEmpty(Mime)
                    && (Mime.IndexOf("apng", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || Mime.IndexOf("webm", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || Mime.IndexOf("mp4", System.StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        // Playable directly by XamlAnimatedGif, which handles GIF and nothing
        // else.
        public bool IsGif
        {
            get
            {
                return !string.IsNullOrEmpty(Mime)
                    && Mime.IndexOf("gif", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }


        // The actual file format, for the tile and the preview caption.
        //
        // Reads the URL's extension first and falls back to the MIME type.
        // Neither alone is enough: web results carry no MIME at all, and
        // SteamGridDB reports still and animated WebP identically - so the
        // label says PNG or WEBM rather than "web" or "alternate", which told
        // the user nothing about whether the file would even display.
        public string FormatLabel
        {
            get
            {
                string fromUrl = ExtensionOf(Url);

                if (fromUrl != null)
                {
                    return fromUrl;
                }

                if (string.IsNullOrEmpty(Mime))
                {
                    return "?";
                }

                // "image/webp" -> "WEBP"
                int slash = Mime.LastIndexOf('/');

                return (slash >= 0 && slash < Mime.Length - 1
                    ? Mime.Substring(slash + 1)
                    : Mime).ToUpperInvariant();
            }
        }

        private static string ExtensionOf(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            // A YouTube watch page has no extension, and a Steam URL may carry
            // a ?t= cache-buster after one.
            string path = url;

            int query = path.IndexOf('?');
            if (query > 0)
            {
                path = path.Substring(0, query);
            }

            int dot = path.LastIndexOf('.');
            int slash = path.LastIndexOf('/');

            if (dot <= slash || dot == path.Length - 1)
            {
                return null;
            }

            string extension = path.Substring(dot + 1);

            // Guards against a dot in a domain or a hash rather than a real
            // extension - "jpeg" is the longest one that turns up here.
            return extension.Length <= 4 ? extension.ToUpperInvariant() : null;
        }

        // Whether MediaElement can play this straight off the web, with no
        // download and no conversion first.
        //
        // True for MP4 - Steam trailers and YouTube results - and for GIF,
        // which MediaElement also decodes. False for the WebP and VP9 WebM
        // SteamGridDB serves, which need ffmpeg before anything can show them
        // moving.
        //
        // Worth asking as one question because it decides the whole preview
        // path: streaming starts playing in about a second, while the
        // conversion route downloads the file, shells out to ffmpeg and writes
        // a temp copy before the first frame appears.
        public bool CanStreamDirectly
        {
            get
            {
                if (IsGif)
                {
                    return true;
                }

                if (string.IsNullOrEmpty(Mime))
                {
                    return false;
                }

                return Mime.IndexOf("mp4", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        // The URL worth fetching to SHOW motion.
        //
        // For an animated SteamGridDB entry that is the .webm thumbnail rather
        // than the full .webp: it is what actually contains video, and it is
        // far smaller than the full artwork - which matters when the point is
        // a preview rather than the download.
        public string MotionPreviewUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(ThumbnailUrl) &&
                    ThumbnailUrl.IndexOf(".webm", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ThumbnailUrl;
                }

                return Url;
            }
        }

        // For XamlAnimatedGif's SourceUri, which wants a Uri rather than a
        // string. Null when the URL cannot parse - the binding then does
        // nothing rather than throwing inside a template.
        public System.Uri UrlUri
        {
            get
            {
                System.Uri parsed;
                return System.Uri.TryCreate(Url, System.UriKind.Absolute, out parsed) ? parsed : null;
            }
        }
    }

    // A game matched on SteamGridDB, used to resolve a Playnite game to an id
    // before fetching its artwork.
    public class SteamGridDbGame
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    // Result of any call: either data or a reason it failed. Network work fails
    // routinely (no key, rate limit, offline), and callers need to tell the
    // user which - so failure is part of the return value rather than an
    // exception to catch at every call site.
    public class SteamGridDbResult<T>
    {
        public bool Success { get; private set; }
        public T Data { get; private set; }
        public string Error { get; private set; }

        public static SteamGridDbResult<T> Ok(T data)
        {
            return new SteamGridDbResult<T> { Success = true, Data = data };
        }

        public static SteamGridDbResult<T> Fail(string error)
        {
            return new SteamGridDbResult<T> { Success = false, Error = error };
        }
    }
}
