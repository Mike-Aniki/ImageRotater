using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Finds artwork by driving Playnite's own offscreen browser at an image
    // search page and reading what the page rendered.
    //
    // Not an API client: there is no key, no quota and no request signing. The
    // SDK exposes IWebViewFactory precisely so plugins can do this, and
    // Playnite itself uses the same approach for its built-in metadata image
    // search. PlayGif does likewise.
    //
    // The trade is that this reads a page layout rather than a contract, so it
    // can stop returning results if that layout changes. Every failure path
    // here returns empty rather than throwing, so a broken parse costs the user
    // this one search and nothing else.
    public class WebImageSearch
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Pairs a thumbnail with the full-size image and its dimensions, as the
        // results page embeds them.
        private static readonly Regex ResultPattern = new Regex(
            @"\[""(https:\/\/encrypted-[^,]+?)"",\d+,\d+\],\[""(http.+?)"",(\d+),(\d+)\]",
            RegexOptions.Compiled);

        private static readonly Regex NewlinePattern = new Regex(@"\r\n?|\n", RegexOptions.Compiled);

        private readonly IPlayniteAPI _api;

        public WebImageSearch(IPlayniteAPI api)
        {
            _api = api;
        }

        public bool IsAvailable
        {
            get { return _api?.WebViews != null; }
        }

        // Artwork matching a free-text query, newest-first as the page returns
        // them. Never throws and never returns null.
        public IReadOnlyList<SteamGridDbArtwork> Search(string query, int maxResults = 60, bool safeSearch = true)
        {
            var found = new List<SteamGridDbArtwork>();

            if (!IsAvailable || string.IsNullOrWhiteSpace(query))
            {
                return found;
            }

            try
            {
                using (IWebView view = _api.WebViews.CreateOffscreenView())
                {
                    string url = BuildSearchUrl(query, safeSearch);
                    view.NavigateAndWait(url);

                    // First visit in a region that requires it lands on a
                    // consent page instead of results. Submitting the form and
                    // retrying is what PlayGif does, and without it the parse
                    // simply finds nothing.
                    if (IsConsentPage(view.GetCurrentAddress()))
                    {
                        view.EvaluateScriptAsync("document.getElementsByTagName('form')[0].submit();").Wait();
                        System.Threading.Thread.Sleep(3000);
                        view.NavigateAndWait(url);
                    }

                    return Parse(view.GetPageSource(), maxResults);
                }
            }
            catch (Exception ex)
            {
                // A failed search must not take anything else down with it.
                Logger.Warn(ex, "ImageRotater: web image search failed");
                return found;
            }
        }

        private static string BuildSearchUrl(string query, bool safeSearch)
        {
            // tbm=isch selects image results; safe=on keeps the default search
            // filtered, matching what Playnite's own image search requests.
            return "https://www.google.com/search?tbm=isch&q="
                + Uri.EscapeDataString(query)
                + (safeSearch ? "&safe=active" : "&safe=off");
        }

        private static bool IsConsentPage(string address)
        {
            return !string.IsNullOrEmpty(address)
                && address.StartsWith("https://consent.google.com", StringComparison.OrdinalIgnoreCase);
        }

        // Public and static so it can be tested against captured page source
        // without standing up a browser. Pure: no state, no I/O.
        public static IReadOnlyList<SteamGridDbArtwork> Parse(string pageSource, int maxResults = 60)
        {
            var found = new List<SteamGridDbArtwork>();

            if (string.IsNullOrEmpty(pageSource))
            {
                return found;
            }

            // The embedded data spans lines; flattening lets one pattern match.
            string flattened = NewlinePattern.Replace(pageSource, string.Empty);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int id = 0;

            foreach (Match match in ResultPattern.Matches(flattened))
            {
                // Deliberately no cap here: the page orders by its own
                // relevance, so trimming before the sort below would throw away
                // large images that happen to appear further down. Parse all,
                // rank, then cap.
                try
                {
                    // Re-parsed as JSON rather than read from capture groups:
                    // the embedded strings carry escapes, and taking them raw
                    // yields URLs that will not resolve.
                    var data = JsonConvert.DeserializeObject<List<List<object>>>("[" + match.Value + "]");
                    if (data == null || data.Count < 2 || data[0].Count < 1 || data[1].Count < 3)
                    {
                        continue;
                    }

                    string thumbnail = Convert.ToString(data[0][0]);
                    string full = Convert.ToString(data[1][0]);

                    if (string.IsNullOrEmpty(full) || !seen.Add(full))
                    {
                        continue;
                    }

                    // Height comes before width in the embedded array.
                    int height = ToInt(data[1][1]);
                    int width = ToInt(data[1][2]);

                    found.Add(new SteamGridDbArtwork
                    {
                        Id = ++id,
                        Url = full,
                        ThumbnailUrl = thumbnail,
                        Width = width,
                        Height = height,
                        Style = "web",
                        Mime = MimeFromUrl(full),
                        IsFromWeb = true
                    });
                }
                catch (Exception)
                {
                    // One malformed entry must not lose the rest of the page.
                }
            }

            // Biggest first. The page returns results by its own relevance,
            // which scatters a 3840-wide image among thumbnails - and for
            // artwork the largest usable image is nearly always the one wanted.
            // Sorting by total pixels rather than width keeps tall box art
            // ranked sensibly against wide banners.
            return found
                .OrderByDescending(a => (long)a.Width * a.Height)
                .Take(maxResults)
                .ToList();
        }

        private static int ToInt(object value)
        {
            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
        }

        // Guessed from the extension so the animated-content filter still has
        // something to work with; the page does not state a content type.
        private static string MimeFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return "image/jpeg";
            }

            string lower = url.ToLowerInvariant();

            if (lower.Contains(".png")) { return "image/png"; }
            if (lower.Contains(".gif")) { return "image/gif"; }
            if (lower.Contains(".webp")) { return "image/webp"; }

            return "image/jpeg";
        }
    }
}
