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

        // Fetches one API page of artwork for a resolved game id. Pages are
        // 0-based and hold at most SteamGridDbClient.PageSize items; callers
        // that want more ask for the next page themselves.
        Task<SteamGridDbResult<List<SteamGridDbArtwork>>> GetArtworkAsync(
            int gameId, SteamGridDbArtworkType type, int page = 0);

        // Downloads one artwork's full image to a local path.
        Task<SteamGridDbResult<byte[]>> DownloadAsync(string url);
    }
}
