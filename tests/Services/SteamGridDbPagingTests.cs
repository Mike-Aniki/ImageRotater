using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using ImageRotater.Controls;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // "Load more" fetches the next API page and appends it. The stub answers
    // page 0 with a full page and page 1 with a short one, which is how the
    // API signals the end.
    [TestFixture]
    public class SteamGridDbPagingTests
    {
        private class PagingClient : ISteamGridDbClient
        {
            public readonly List<int> PagesRequested = new List<int>();

            public bool IsConfigured { get { return true; } }

            public Task<SteamGridDbResult<List<SteamGridDbGame>>> SearchGamesAsync(string name)
            {
                return Task.FromResult(SteamGridDbResult<List<SteamGridDbGame>>.Ok(
                    new List<SteamGridDbGame> { new SteamGridDbGame { Id = 42, Name = name } }));
            }

            public Task<SteamGridDbResult<List<SteamGridDbArtwork>>> GetArtworkAsync(
                int gameId, SteamGridDbArtworkType type, int page = 0)
            {
                PagesRequested.Add(page);

                int count = page == 0 ? SteamGridDbClient.PageSize : 3;
                List<SteamGridDbArtwork> items = Enumerable.Range(0, count)
                    .Select(i => new SteamGridDbArtwork
                    {
                        Id = page * SteamGridDbClient.PageSize + i,
                        Width = 600,
                        Height = 900,
                        Style = "alternate"
                    })
                    .ToList();

                return Task.FromResult(SteamGridDbResult<List<SteamGridDbArtwork>>.Ok(items));
            }
        }

        [Test]
        public async Task LoadMoreAppendsTheNextPageAndStopsAtAShortOne()
        {
            var client = new PagingClient();
            var model = new SteamGridDbSearchViewModel(client);

            Assert.That(await model.SearchAsync("Game", SteamGridDbArtworkType.Grid), Is.True);
            Assert.That(model.HasMore, Is.True);

            Assert.That(await model.LoadMoreAsync(), Is.True);

            CollectionAssert.AreEqual(new[] { 0, 1 }, client.PagesRequested);

            // 53 results at 24 per page, and the new ones start on page 3.
            Assert.That(model.PageCount, Is.EqualTo(3));
            Assert.That(model.CurrentPage, Is.EqualTo(3));
            Assert.That(model.HasMore, Is.False);

            // A short page ended the list, so asking again is a no-op.
            Assert.That(await model.LoadMoreAsync(), Is.False);
            CollectionAssert.AreEqual(new[] { 0, 1 }, client.PagesRequested);
        }
    }
}
