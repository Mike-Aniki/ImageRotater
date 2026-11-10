using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Talks to the SteamGridDB v2 API.
    //
    // Auth is a per-user API key sent as a bearer token. The key is a
    // credential: it is never logged, and never included in an error message
    // shown to the user, because those end up in bug reports.
    public class SteamGridDbClient : ISteamGridDbClient
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const string BaseUrl = "https://www.steamgriddb.com/api/v2";

        // One shared HttpClient. A new instance per request exhausts sockets
        // under repeated searching (each leaves a TIME_WAIT connection behind).
        private static readonly HttpClient Http = CreateHttpClient();

        private readonly Func<string> _apiKey;

        public SteamGridDbClient(Func<string> apiKey)
        {
            _apiKey = apiKey;
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(GetKey()); }
        }

        private static HttpClient CreateHttpClient()
        {
            // .NET Framework defaults to SSL3/TLS1.0, which the API rejects.
            // Without this every request fails with an opaque connection error.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                // Very old platform; requests will fail and surface normally.
            }

            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        private string GetKey()
        {
            return _apiKey != null ? _apiKey() : null;
        }

        public async Task<SteamGridDbResult<List<SteamGridDbGame>>> SearchGamesAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return SteamGridDbResult<List<SteamGridDbGame>>.Fail("No game name to search for.");
            }

            string url = BaseUrl + "/search/autocomplete/" + Uri.EscapeDataString(name);
            SteamGridDbResult<JObject> response = await GetJsonAsync(url).ConfigureAwait(false);

            if (!response.Success)
            {
                return SteamGridDbResult<List<SteamGridDbGame>>.Fail(response.Error);
            }

            var games = new List<SteamGridDbGame>();

            try
            {
                JToken data = response.Data["data"];
                if (data != null)
                {
                    foreach (JToken entry in data)
                    {
                        games.Add(new SteamGridDbGame
                        {
                            Id = (int?)entry["id"] ?? 0,
                            Name = (string)entry["name"]
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not parse SteamGridDB search response");
                return SteamGridDbResult<List<SteamGridDbGame>>.Fail("Unexpected response from SteamGridDB.");
            }

            return SteamGridDbResult<List<SteamGridDbGame>>.Ok(games);
        }

        public async Task<SteamGridDbResult<List<SteamGridDbArtwork>>> GetArtworkAsync(
            int gameId, SteamGridDbArtworkType type, bool squareOnly = false, int page = 0, bool includeTagged = false)
        {
            string segment = ToUrlSegment(type);
            string url = BaseUrl + "/" + segment + "/game/" + gameId;
            if (squareOnly && type == SteamGridDbArtworkType.Grid)
            {
                url += "?dimensions=512x512,1024x1024";
            }

            if (page > 0)
            {
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + "page=" + page;
            }

            // SteamGridDB may otherwise return only static artwork. Request
            // both types for covers and backgrounds, then let the existing
            // Show Animated checkbox filter the received results locally.
            // Logos remain unchanged because ImageRotater does not support
            // animated logos in the Artwork Manager.
            if (type != SteamGridDbArtworkType.Logo)
            {
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + "types=static,animated";
            }

            if (includeTagged)
            {
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + "nsfw=any&humor=any&epilepsy=any";
            }

            SteamGridDbResult<JObject> response = await GetJsonAsync(url).ConfigureAwait(false);

            if (!response.Success)
            {
                return SteamGridDbResult<List<SteamGridDbArtwork>>.Fail(response.Error);
            }

            var artwork = new List<SteamGridDbArtwork>();

            try
            {
                JToken data = response.Data["data"];
                if (data != null)
                {
                    foreach (JToken entry in data)
                    {
                        artwork.Add(new SteamGridDbArtwork
                        {
                            Id = (int?)entry["id"] ?? 0,
                            Url = (string)entry["url"],
                            ThumbnailUrl = (string)entry["thumb"],
                            Width = (int?)entry["width"] ?? 0,
                            Height = (int?)entry["height"] ?? 0,
                            Style = (string)entry["style"],
                            Mime = (string)entry["mime"],
                            // Read the API's content flags and accept the tags
                            // array representation as well.
                            Nsfw = HasTag(entry, "nsfw"),
                            Humor = HasTag(entry, "humor"),
                            Epilepsy = HasTag(entry, "epilepsy")
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not parse SteamGridDB artwork response");
                return SteamGridDbResult<List<SteamGridDbArtwork>>.Fail("Unexpected response from SteamGridDB.");
            }

            return SteamGridDbResult<List<SteamGridDbArtwork>>.Ok(artwork);
        }

        public async Task<SteamGridDbResult<byte[]>> DownloadAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return SteamGridDbResult<byte[]>.Fail("No image URL.");
            }

            try
            {
                // Image URLs are public CDN links and take no auth header.
                using (HttpResponseMessage response = await Http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return SteamGridDbResult<byte[]>.Fail(
                            "Download failed (" + (int)response.StatusCode + ").");
                    }

                    byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return SteamGridDbResult<byte[]>.Ok(bytes);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: SteamGridDB image download failed");
                return SteamGridDbResult<byte[]>.Fail("Could not download the image.");
            }
        }

        private async Task<SteamGridDbResult<JObject>> GetJsonAsync(string url)
        {
            string key = GetKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                return SteamGridDbResult<JObject>.Fail(
                    "No SteamGridDB API key configured. Add one in ImageRotater settings.");
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

                    using (HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized ||
                            response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            return SteamGridDbResult<JObject>.Fail(
                                "SteamGridDB rejected the API key. Check it in ImageRotater settings.");
                        }

                        if ((int)response.StatusCode == 429)
                        {
                            return SteamGridDbResult<JObject>.Fail(
                                "SteamGridDB rate limit reached. Try again shortly.");
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            return SteamGridDbResult<JObject>.Fail(
                                "SteamGridDB returned an error (" + (int)response.StatusCode + ").");
                        }

                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return SteamGridDbResult<JObject>.Ok(JObject.Parse(body));
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return SteamGridDbResult<JObject>.Fail("SteamGridDB request timed out.");
            }
            catch (Exception ex)
            {
                // Deliberately logs the exception but not the URL-with-key or
                // the key itself.
                Logger.Warn(ex, "ImageRotater: SteamGridDB request failed");
                return SteamGridDbResult<JObject>.Fail("Could not reach SteamGridDB.");
            }
        }

        // The grids endpoint serves several aspect ratios under one category.
        // 600x900 and 1000x1500 are true 2:3 box art; 920x430, 460x215 and
        // similar are Steam's old wide capsule format, which really are crops
        // of larger key art. Without this filter those wide banners come back
        // mixed in with box art and can win the selection.
        //
        // Heroes are left unfiltered: backgrounds want whatever the largest
        // available image is, and hero dimensions are already consistent.
        // The grids category mixes true box art with Steam's old wide capsule
        // banners, which are crops of larger key art. Asking only for the
        // portrait sizes keeps those out.
        //
        // These values are exact members of SteamGridDB's documented dimensions
        // enum. The API validates the whole comma-list and rejects the entire
        // request with 400 if any single value is unrecognised, so nothing here
        // may be guessed at. The full grid enum is 460x215, 920x430, 600x900,
        // 342x482, 660x930, 512x512 and 1024x1024; the three below are its
        // portrait members. Squares (512x512, 1024x1024) are excluded because
        // they are icons rather than box art.
        //
        // Heroes have a separate, disjoint enum (1920x620, 3840x1240,
        // 1600x650) and are left unfiltered: backgrounds want whatever the
        // largest available image is.
        // Content warnings arrive as entries in a tags array rather than as
        // boolean fields on the result object.
        private static bool HasTag(JToken entry, string tag)
        {
            JToken flag = entry[tag];
            if (flag?.Type == JTokenType.Boolean && flag.Value<bool>()) return true;

            try
            {
                JToken tags = entry["tags"];
                if (tags == null)
                {
                    return false;
                }

                foreach (JToken value in tags)
                {
                    if (string.Equals((string)value, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // A malformed tags array is not worth failing the whole parse
                // over - treat it as untagged.
            }

            return false;
        }

        // No dimension filter is sent.
        //
        // An earlier version requested only portrait grid sizes to keep Steam's
        // wide capsule banners out of cover results. That also excluded square
        // art (1024x1024, 512x512), which users legitimately want as box art.
        // Every shape is fetched instead, and the browse dialog groups them by
        // aspect ratio so the user chooses. Automatic download still picks by
        // fit to the configured grid cell shape.
        private static string ToUrlSegment(SteamGridDbArtworkType type)
        {
            switch (type)
            {
                case SteamGridDbArtworkType.Grid:
                    return "grids";
                case SteamGridDbArtworkType.Logo:
                    return "logos";
                default:
                    return "heroes";
            }
        }
    }
}
