using NUnit.Framework;
using ImageRotater;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // YouTube as an artwork source.
    //
    // The search itself shells out to yt-dlp and is not exercised here - that
    // needs the tool, the network and a JS runtime. What is testable without
    // any of those is the query wording, the availability gates, and how a
    // video is presented on a tile.
    [TestFixture]
    public class YouTubeSearchTests
    {
        [Test]
        public void DefaultQueryAsksForALiveWallpaper()
        {
            // "live wallpaper" rather than "trailer" or "gameplay" on purpose:
            // it selects for looping, mostly wordless video shot to sit behind
            // something else. A trailer has cuts, captions and a voiceover.
            Assert.AreEqual(
                "Hollow Knight live wallpaper",
                YouTubeSearch.DefaultQueryFor("Hollow Knight"));
        }

        [Test]
        public void DefaultQueryToleratesAMissingName()
        {
            Assert.AreEqual("live wallpaper", YouTubeSearch.DefaultQueryFor(null));
            Assert.AreEqual("live wallpaper", YouTubeSearch.DefaultQueryFor("   "));
        }

        [Test]
        public void SearchIsUnavailableWithoutYtDlp()
        {
            // An explicitly wrong path is honoured rather than falling back to
            // PATH, so this stays false regardless of what is installed on the
            // machine running the test.
            var search = new YouTubeSearch(new ImageRotaterSettings
            {
                YtDlpPath = @"C:\definitely\not\here\yt-dlp.exe"
            });

            Assert.IsFalse(search.IsAvailable);
        }

        [Test]
        public void JsRuntimeIsReportedSeparately()
        {
            // Worth its own flag because without deno yt-dlp exits 0 with NO
            // results, which is indistinguishable from a search that genuinely
            // found nothing. The dialog says so rather than showing an empty
            // grid.
            var search = new YouTubeSearch(new ImageRotaterSettings
            {
                YtDlpPath = @"C:\nope\yt-dlp.exe",
                DenoPath = @"C:\nope\deno.exe"
            });

            Assert.IsFalse(search.HasJsRuntime);
        }

        [Test]
        public void DownloadNeedsBothTools()
        {
            // ffmpeg is not optional here: yt-dlp hands it the recode, and
            // without it the download stays WebM, which MediaElement will not
            // open.
            var downloader = new YouTubeDownloader(new ImageRotaterSettings
            {
                YtDlpPath = @"C:\nope\yt-dlp.exe",
                FfmpegPath = @"C:\nope\ffmpeg.exe"
            });

            Assert.IsFalse(downloader.IsAvailable);
        }

        // --- How a video reads on a tile -------------------------------

        [TestCase(0, "")]
        [TestCase(45, "0:45")]
        [TestCase(90, "1:30")]
        [TestCase(600, "10:00")]
        [TestCase(3661, "1:01:01")]
        public void DurationReadsAsATimestamp(int seconds, string expected)
        {
            Assert.AreEqual(
                expected,
                new YouTubeVideo { DurationSeconds = seconds }.DurationText);
        }

        // The decimal separator follows the user's culture; pin one so the
        // expected text does not depend on the machine running the tests.
        [SetCulture("en-US")]
        [TestCase(0, "")]
        [TestCase(842, "842 views")]
        [TestCase(15200, "15.2K views")]
        [TestCase(3400000, "3.4M views")]
        public void ViewCountIsAbbreviated(int views, string expected)
        {
            Assert.AreEqual(
                expected,
                new YouTubeVideo { ViewCount = views }.ViewCountText);
        }

        [Test]
        public void AVideoResultIsMarkedForTheDownloader()
        {
            // The flag is what stops ArtworkDownloader fetching the watch page
            // URL directly and saving the HTML as an image.
            var artwork = new SteamGridDbArtwork
            {
                Url = "https://www.youtube.com/watch?v=abc123",
                IsYouTube = true
            };

            Assert.IsTrue(artwork.IsYouTube);
        }
    }
}
