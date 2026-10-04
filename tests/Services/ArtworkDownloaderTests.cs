using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ArtworkDownloaderTests
    {
        // Stands in for the network so the save path can be tested without an
        // API key or a live SteamGridDB.
        private class StubClient : ISteamGridDbClient
        {
            public byte[] Payload = new byte[] { 1, 2, 3, 4 };
            public bool ShouldFail;
            public int DownloadCalls;

            public bool IsConfigured { get { return true; } }

            public Task<SteamGridDbResult<List<SteamGridDbGame>>> SearchGamesAsync(string name)
            {
                return Task.FromResult(SteamGridDbResult<List<SteamGridDbGame>>.Ok(
                    new List<SteamGridDbGame> { new SteamGridDbGame { Id = 1, Name = name } }));
            }

            public Task<SteamGridDbResult<List<SteamGridDbArtwork>>> GetArtworkAsync(
                int gameId, SteamGridDbArtworkType type)
            {
                return Task.FromResult(SteamGridDbResult<List<SteamGridDbArtwork>>.Ok(
                    new List<SteamGridDbArtwork>()));
            }

            public Task<SteamGridDbResult<byte[]>> DownloadAsync(string url)
            {
                DownloadCalls++;
                return Task.FromResult(ShouldFail
                    ? SteamGridDbResult<byte[]>.Fail("network down")
                    : SteamGridDbResult<byte[]>.Ok(Payload));
            }
        }

        private string _root;
        private GameImageStore _store;
        private SessionSelectionCache _cache;
        private StubClient _client;
        private ArtworkDownloader _downloader;
        private Guid _gameId;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageRotaterDl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _store = new GameImageStore(_root);
            _cache = new SessionSelectionCache();
            _client = new StubClient();
            _downloader = new ArtworkDownloader(_client, _store, _cache);
            _gameId = Guid.NewGuid();
        }

        [TearDown]
        public void TearDown()
        {
            ArtworkDownloader.SettingsSource = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static SteamGridDbArtwork Art(int id = 7, string mime = "image/jpeg")
        {
            return new SteamGridDbArtwork
            {
                Id = id,
                Url = "https://example.invalid/art.jpg",
                Mime = mime,
                Width = 1920,
                Height = 1080
            };
        }

        [Test]
        public async Task Download_SavesIntoTheGameFolder()
        {
            string saved = await _downloader.DownloadAsync(_gameId, Art());

            Assert.IsNotNull(saved);
            Assert.IsTrue(File.Exists(saved));
            Assert.AreEqual(_store.GetGameFolder(_gameId, ArtworkKind.Background), Path.GetDirectoryName(saved));
            Assert.AreEqual(1, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        [Test]
        public async Task Download_UsesExtensionFromMimeType()
        {
            string png = await _downloader.DownloadAsync(_gameId, Art(1, "image/png"));
            Assert.IsTrue(png.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

            string jpg = await _downloader.DownloadAsync(_gameId, Art(2, "image/jpeg"));
            Assert.IsTrue(jpg.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        }

        // Named by artwork id, so fetching the same art twice replaces it
        // instead of piling up near-duplicates.
        [Test]
        public async Task Download_SameArtworkTwice_DoesNotDuplicate()
        {
            await _downloader.DownloadAsync(_gameId, Art(42));
            await _downloader.DownloadAsync(_gameId, Art(42));

            Assert.AreEqual(1, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        [Test]
        public async Task Download_Failure_ReturnsNullAndWritesNothing()
        {
            _client.ShouldFail = true;

            string saved = await _downloader.DownloadAsync(_gameId, Art());

            Assert.IsNull(saved);
            Assert.AreEqual(0, _store.GetImagePaths(_gameId, ArtworkKind.Background).Count);
        }

        [Test]
        public async Task Download_LeavesNoTempFileBehind()
        {
            await _downloader.DownloadAsync(_gameId, Art());

            string[] leftovers = Directory.GetFiles(_store.GetGameFolder(_gameId, ArtworkKind.Background), "*.tmp");
            Assert.AreEqual(0, leftovers.Length, "a .tmp file survived the download");
        }

        // A new image changes the candidate list, so any remembered choice for
        // that game is stale and must be dropped.
        [Test]
        public async Task Download_ForgetsTheRememberedChoice()
        {
            var candidates = new List<string> { "a.jpg" };
            _cache.Remember(_gameId, "a.jpg");
            Assert.AreEqual(1, _cache.Count);

            await _downloader.DownloadAsync(_gameId, Art());

            Assert.IsNull(_cache.GetRemembered(_gameId, candidates));
        }

        [Test]
        public async Task Download_WhenAutoOptimiseEnabled_ReportsOptimisationStage()
        {
            ArtworkDownloader.SettingsSource = () => new ImageRotaterSettings
            {
                OptimiseDownloadedImages = true
            };

            bool sawOptimising = false;
            bool sawFinished = false;

            string saved = await _downloader.DownloadAsync(
                _gameId,
                Art(),
                ArtworkKind.Background,
                progress =>
                {
                    if (progress.Stage == ArtworkDownloadStage.Optimising)
                    {
                        sawOptimising = true;
                    }
                    else if (progress.Stage == ArtworkDownloadStage.OptimisationFinished)
                    {
                        sawFinished = true;
                    }
                });

            Assert.IsNotNull(saved);
            Assert.IsTrue(sawOptimising);
            Assert.IsTrue(sawFinished);
        }

        [Test]
        public async Task Download_NullOrUrllessArtwork_ReturnsNullWithoutCallingNetwork()
        {
            Assert.IsNull(await _downloader.DownloadAsync(_gameId, null));
            Assert.IsNull(await _downloader.DownloadAsync(_gameId, new SteamGridDbArtwork { Id = 1 }));
            Assert.AreEqual(0, _client.DownloadCalls);
        }
    }
}
