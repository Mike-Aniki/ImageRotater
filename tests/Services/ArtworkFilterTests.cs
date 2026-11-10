using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ArtworkFilterTests
    {
        private static SteamGridDbArtwork Art(
            int id,
            int width = 1920,
            int height = 1080,
            string style = "alternate",
            string mime = "image/jpeg",
            bool nsfw = false,
            bool humor = false,
            bool epilepsy = false)
        {
            return new SteamGridDbArtwork
            {
                Id = id,
                Url = "https://example.invalid/" + id,
                Width = width,
                Height = height,
                Style = style,
                Mime = mime,
                Nsfw = nsfw,
                Humor = humor,
                Epilepsy = epilepsy
            };
        }

        // The reported bug: filter options appeared twice. Deriving them from
        // the results makes duplication impossible, so this pins that property.
        [Test]
        public void AvailableDimensions_AreDistinct()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 1920, 1080),
                Art(2, 1920, 1080),
                Art(3, 3840, 2160),
                Art(4, 1920, 1080)
            };

            var dimensions = ArtworkFilter.AvailableDimensions(artwork);

            Assert.AreEqual(2, dimensions.Count);
            CollectionAssert.AllItemsAreUnique(dimensions);
        }

        [Test]
        public void AvailableDimensions_LargestFirst()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 1280, 720),
                Art(2, 3840, 2160),
                Art(3, 1920, 1080)
            };

            var dimensions = ArtworkFilter.AvailableDimensions(artwork);

            CollectionAssert.AreEqual(new[] { "3840x2160", "1920x1080", "1280x720" }, dimensions);
        }

        [Test]
        public void AvailableStyles_AreDistinctAndSorted()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, style: "material"),
                Art(2, style: "alternate"),
                Art(3, style: "material"),
                Art(4, style: null)
            };

            var styles = ArtworkFilter.AvailableStyles(artwork);

            CollectionAssert.AreEqual(new[] { "alternate", "material" }, styles);
        }

        [Test]
        public void AvailableOptions_HandleNullInput()
        {
            Assert.AreEqual(0, ArtworkFilter.AvailableDimensions(null).Count);
            Assert.AreEqual(0, ArtworkFilter.AvailableStyles(null).Count);
        }

        // An untouched filter must not hide everything - that would look like a
        // failed search.
        [Test]
        public void Apply_EmptySelections_ImposeNoConstraint()
        {
            var artwork = new List<SteamGridDbArtwork> { Art(1), Art(2, 3840, 2160, "material") };

            var result = ArtworkFilter.Apply(artwork, new ArtworkFilterState());

            Assert.AreEqual(2, result.Count);
        }

        [Test]
        public void Apply_FiltersByDimension()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 1920, 1080),
                Art(2, 3840, 2160)
            };

            var filter = new ArtworkFilterState();
            filter.Dimensions.Add("3840x2160");

            var result = ArtworkFilter.Apply(artwork, filter);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(2, result[0].Id);
        }

        [Test]
        public void Apply_FiltersByStyle()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, style: "alternate"),
                Art(2, style: "material")
            };

            var filter = new ArtworkFilterState();
            filter.Styles.Add("material");

            var result = ArtworkFilter.Apply(artwork, filter);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(2, result[0].Id);
        }

        // Content flags are opt-in: hidden by default, shown only on request.
        [Test]
        public void Apply_HidesFlaggedContentByDefault()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1),
                Art(2, nsfw: true),
                Art(3, humor: true),
                Art(4, epilepsy: true)
            };

            var result = ArtworkFilter.Apply(artwork, new ArtworkFilterState());

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(1, result[0].Id);
        }

        [Test]
        public void Apply_ShowsFlaggedContentWhenRequested()
        {
            var artwork = new List<SteamGridDbArtwork> { Art(1, nsfw: true) };

            var filter = new ArtworkFilterState { ShowNsfw = true };

            Assert.AreEqual(1, ArtworkFilter.Apply(artwork, filter).Count);
        }

        [TestCase(false, new[] { 2 })]
        [TestCase(true, new[] { 1, 2 })]
        public void Apply_UntaggedCanBeToggledIndependently(bool showUntagged, int[] expected)
        {
            var artwork = new[] { Art(1), Art(2, humor: true), Art(3, nsfw: true),
                Art(4, nsfw: true, humor: true) };
            var filter = new ArtworkFilterState { ShowUntagged = showUntagged, ShowHumor = true };

            Assert.That(ArtworkFilter.Apply(artwork, filter).Select(item => item.Id), Is.EqualTo(expected));
        }

        // Animated results are SHOWN by default. They render and preview
        // correctly now, and animated artwork is the reason most people open
        // this dialog - hiding it meant the best results were invisible unless
        // the user knew to go and find the checkbox.
        [Test]
        public void Apply_ShowsAnimatedByDefault()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, mime: "image/jpeg"),

                // Animated WebP is identified by its .webm THUMBNAIL, not its
                // MIME: SteamGridDB reports animated and still WebP alike as
                // "image/webp", so matching the MIME badged every static WebP
                // cover as animated and hid it from an unfiltered search.
                new SteamGridDbArtwork
                {
                    Id = 2,
                    Mime = "image/webp",
                    ThumbnailUrl = "https://cdn.steamgriddb.com/mb/abc.webm",
                    Width = 1920,
                    Height = 1080
                },

                Art(3, mime: "image/apng")
            };

            Assert.AreEqual(3, ArtworkFilter.Apply(artwork, new ArtworkFilterState()).Count);

            // Unticking the box still hides them, which is the whole point of
            // it still being there.
            var hidden = ArtworkFilter.Apply(
                artwork, new ArtworkFilterState { ShowAnimated = false });

            Assert.AreEqual(1, hidden.Count);
            Assert.AreEqual(1, hidden[0].Id);

            var animatedOnly = ArtworkFilter.Apply(
                artwork, new ArtworkFilterState { AnimatedOnly = true });
            CollectionAssert.AreEquivalent(new[] { artwork[1], artwork[2] }, animatedOnly);

            var staticOnly = ArtworkFilter.Apply(
                artwork, new ArtworkFilterState { ShowAnimated = false, AnimatedOnly = true });
            CollectionAssert.AreEqual(new[] { artwork[0] }, staticOnly);
        }

        // The other half of that rule: a STILL WebP must survive the default
        // filter. Treating every image/webp as animated hid perfectly ordinary
        // covers from a search that had not asked for animation.
        [Test]
        public void Apply_KeepsStillWebpByDefault()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                new SteamGridDbArtwork
                {
                    Id = 1,
                    Mime = "image/webp",
                    ThumbnailUrl = "https://cdn.steamgriddb.com/thumb/abc.jpg",
                    Width = 600,
                    Height = 900
                }
            };

            var result = ArtworkFilter.Apply(artwork, new ArtworkFilterState());

            Assert.AreEqual(1, result.Count,
                "a still WebP is not animated and must not be filtered out as if it were");
        }

        [Test]
        public void Apply_CombinesFiltersAsAnd()
        {
            var artwork = new List<SteamGridDbArtwork>
            {
                Art(1, 3840, 2160, "material"),
                Art(2, 3840, 2160, "alternate"),
                Art(3, 1920, 1080, "material")
            };

            var filter = new ArtworkFilterState();
            filter.Dimensions.Add("3840x2160");
            filter.Styles.Add("material");

            var result = ArtworkFilter.Apply(artwork, filter);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(1, result[0].Id);
        }

        [Test]
        public void Apply_NullInputsAreTolerated()
        {
            Assert.AreEqual(0, ArtworkFilter.Apply(null, new ArtworkFilterState()).Count);

            var artwork = new List<SteamGridDbArtwork> { Art(1) };
            Assert.AreEqual(1, ArtworkFilter.Apply(artwork, null).Count);
        }

        [Test]
        public void Dimensions_FormatAsWidthByHeight()
        {
            Assert.AreEqual("1920x1080", Art(1, 1920, 1080).Dimensions);
        }
    }
}
