using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The parser reads a rendered page rather than an API contract, so it can
    // stop matching if that page changes. These tests pin the shape it expects
    // and, more importantly, that every failure path yields an empty list
    // rather than throwing - a broken parse must cost one search, not the
    // dialog.
    [TestFixture]
    public class WebImageSearchTests
    {
        // One result as the page embeds it: thumbnail first, then the full
        // image with its height and width.
        private static string Entry(string thumb, string full, int height, int width)
        {
            return $"[\"{thumb}\",200,300],[\"{full}\",{height},{width}]";
        }

        private static IReadOnlyList<SteamGridDbArtwork> Parse(string source)
        {
            return WebImageSearch.Parse(source);
        }

        [Test]
        public void ParsesThumbnailFullUrlAndDimensions()
        {
            var found = Parse(Entry(
                "https://encrypted-tbn0.example.invalid/thumb1",
                "https://example.invalid/full1.jpg", 1080, 1920));

            Assert.AreEqual(1, found.Count);
            Assert.AreEqual("https://example.invalid/full1.jpg", found[0].Url);
            Assert.AreEqual("https://encrypted-tbn0.example.invalid/thumb1", found[0].ThumbnailUrl);

            // Height precedes width in the embedded array - swapping them would
            // break the aspect filters that decide box art from banners.
            Assert.AreEqual(1920, found[0].Width);
            Assert.AreEqual(1080, found[0].Height);
        }

        [Test]
        public void MarksResultsAsComingFromTheWeb()
        {
            var found = Parse(Entry(
                "https://encrypted-tbn0.example.invalid/t",
                "https://example.invalid/a.jpg", 900, 600));

            Assert.IsTrue(found[0].IsFromWeb,
                "the downloader names web files by URL hash; without this they collide on Id");
        }

        // The same image appearing twice on a page must not become two
        // near-identical entries to pick between.
        [Test]
        public void DuplicateUrlsAreCollapsed()
        {
            string one = Entry("https://encrypted-tbn0.example.invalid/t1", "https://example.invalid/same.jpg", 100, 100);
            string two = Entry("https://encrypted-tbn0.example.invalid/t2", "https://example.invalid/same.jpg", 100, 100);

            Assert.AreEqual(1, Parse(one + "," + two).Count);
        }

        [Test]
        public void EachResultGetsADistinctId()
        {
            string source = string.Join(",",
                Entry("https://encrypted-tbn0.example.invalid/t1", "https://example.invalid/a.jpg", 100, 100),
                Entry("https://encrypted-tbn0.example.invalid/t2", "https://example.invalid/b.jpg", 100, 100));

            var found = Parse(source);
            Assert.AreEqual(2, found.Count);
            Assert.AreEqual(2, found.Select(a => a.Id).Distinct().Count());
        }

        // The embedded data spans lines; the parser flattens before matching.
        [Test]
        public void MatchesAcrossLineBreaks()
        {
            string source = "junk\r\n" + Entry(
                "https://encrypted-tbn0.example.invalid/t",
                "https://example.invalid/a.png", 900, 600) + "\nmore junk";

            Assert.AreEqual(1, Parse(source).Count);
        }

        [Test]
        public void MimeIsGuessedFromTheUrl()
        {
            Assert.AreEqual("image/png", Parse(Entry(
                "https://encrypted-tbn0.example.invalid/t", "https://example.invalid/a.png", 1, 1))[0].Mime);
            Assert.AreEqual("image/gif", Parse(Entry(
                "https://encrypted-tbn0.example.invalid/t", "https://example.invalid/a.gif", 1, 1))[0].Mime);

            // Unknown extensions fall back to jpeg rather than empty, so the
            // animated-content filter always has something to test.
            Assert.AreEqual("image/jpeg", Parse(Entry(
                "https://encrypted-tbn0.example.invalid/t", "https://example.invalid/a", 1, 1))[0].Mime);
        }

        // Every one of these previously had the potential to throw inside a
        // dialog handler.
        [Test]
        public void MalformedInputYieldsEmptyRatherThanThrowing()
        {
            Assert.IsEmpty(Parse(null));
            Assert.IsEmpty(Parse(string.Empty));
            Assert.IsEmpty(Parse("<html><body>no results here</body></html>"));
            Assert.IsEmpty(Parse("[\"https://encrypted-tbn0.example.invalid/t\",1,2]"));
        }

        // Surrounding junk must not stop well-formed entries being found, and
        // must never throw.
        //
        // Note the limit this does NOT claim: the full-URL group is lazy but
        // unanchored, so a malformed entry sitting immediately before a good
        // one can swallow it and yield a single bogus match. That comes with
        // the pattern Playnite itself uses, and the cost is a missed result
        // rather than a crash - which is why the guarantee here is "no throw,
        // clean entries parse" rather than "every entry is recovered".
        [Test]
        public void JunkAroundEntriesIsIgnored()
        {
            string source = "<div>unrelated markup</div>"
                + Entry("https://encrypted-tbn0.example.invalid/t", "https://example.invalid/good.jpg", 900, 600)
                + "<script>trailing junk</script>";

            var found = Parse(source);
            Assert.IsTrue(found.Any(a => a.Url == "https://example.invalid/good.jpg"));
        }

        [Test]
        public void EntriesWithNonNumericDimensionsAreSkipped()
        {
            Assert.DoesNotThrow(() => Parse(
                "[\"https://encrypted-tbn0.example.invalid/t\",1,2],[\"http://x\",notanumber,2]"));
        }

        [Test]
        public void ResultCountIsCapped()
        {
            var entries = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                entries.Add(Entry(
                    "https://encrypted-tbn0.example.invalid/t" + i,
                    "https://example.invalid/" + i + ".jpg", 100, 100));
            }

            Assert.AreEqual(5, WebImageSearch.Parse(string.Join(",", entries), 5).Count);
        }

        [Test]
        public void DefaultSearchKeepsSmallSquaresAfterSixtyLargerImages()
        {
            var entries = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                entries.Add(Entry("https://encrypted-tbn0.example.invalid/t" + i,
                    "https://example.invalid/" + i + ".jpg", 1080, 1920));
            }
            entries.Add(Entry("https://encrypted-tbn0.example.invalid/square",
                "https://example.invalid/square.jpg", 512, 512));

            var found = Parse(string.Join(",", entries));
            Assert.AreEqual(61, found.Count);
            Assert.AreEqual("https://example.invalid/square.jpg", found[60].Url);
        }

        // Largest first: the page orders by its own relevance, which scatters a
        // 4K image among thumbnails.
        [Test]
        public void ResultsAreSortedLargestFirst()
        {
            string source = string.Join(",",
                Entry("https://encrypted-tbn0.example.invalid/a", "https://example.invalid/small.jpg", 100, 100),
                Entry("https://encrypted-tbn0.example.invalid/b", "https://example.invalid/huge.jpg", 2160, 3840),
                Entry("https://encrypted-tbn0.example.invalid/c", "https://example.invalid/mid.jpg", 720, 1280));

            var found = Parse(source);

            Assert.AreEqual("https://example.invalid/huge.jpg", found[0].Url);
            Assert.AreEqual("https://example.invalid/mid.jpg", found[1].Url);
            Assert.AreEqual("https://example.invalid/small.jpg", found[2].Url);
        }

        // The cap must apply AFTER ranking. Trimming during the parse would
        // discard large images that appear further down the page - exactly the
        // ones the sort exists to surface.
        [Test]
        public void CapKeepsTheLargestNotTheFirstFound()
        {
            var entries = new List<string>();

            // Ten small images first, one large one last.
            for (int i = 0; i < 10; i++)
            {
                entries.Add(Entry(
                    "https://encrypted-tbn0.example.invalid/t" + i,
                    "https://example.invalid/small" + i + ".jpg", 100, 100));
            }

            entries.Add(Entry(
                "https://encrypted-tbn0.example.invalid/big",
                "https://example.invalid/big.jpg", 2160, 3840));

            var found = WebImageSearch.Parse(string.Join(",", entries), 3);

            Assert.AreEqual(3, found.Count);
            Assert.AreEqual("https://example.invalid/big.jpg", found[0].Url,
                "a large image late in the page must survive the cap");
        }
    }
}
