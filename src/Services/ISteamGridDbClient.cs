using System.Collections.Generic;
using System.Threading.Tasks;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Network seam. Everything downstream - filtering, download-to-store, the
    // dialog - is written against this, so it can be tested with a stub and
    // without a SteamGridDB API key.
    public interface ISteamGridDbClient
    {
        // False when no API key is configured. Callers use this to tell the
        // user to add one rather than showing a failed search.
        bool IsConfigured { get; }

        // Finds candidate games by name.
        Task<SteamGridDbResult<List<SteamGridDbGame>>> SearchGamesAsync(string name);

        // Fetches artwork for a resolved game id.
        Task<SteamGridDbResult<List<SteamGridDbArtwork>>> GetArtworkAsync(
            int gameId, SteamGridDbArtworkType type, bool squareOnly = false, int page = 0, bool includeTagged = false);

        // Downloads one artwork's full image to a local path.
        Task<SteamGridDbResult<byte[]>> DownloadAsync(string url);
    }
}
