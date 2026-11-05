using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageRotater.Models;
using ImageRotater.Services;
using Newtonsoft.Json;
using NUnit.Framework;
using Playnite.SDK.Models;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ArtworkPackTests
    {
        private string root;
        private GameImageStore store;
        private Game game;
        private ImageRotaterSettings settings;
        private ArtworkPackConfiguration config;
        private ArtworkPackService service;
        private readonly ArtworkKind[] kinds = { ArtworkKind.Background, ArtworkKind.Cover, ArtworkKind.Logo };

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "IR_Packs_" + Guid.NewGuid().ToString("N"));
            store = new GameImageStore(root);
            game = new Game { Id = Guid.NewGuid(), Name = "Pack test" };
            config = new ArtworkPackConfiguration { Enabled = true, Mode = SelectionMode.EverySelection, Order = SelectionOrder.Sequential };
            for (int i = 1; i <= 3; i++)
            {
                var pack = new ArtworkPack { Name = "Pack " + i, Background = i + ".png", Cover = i + ".png", Logo = i + ".png" };
                config.Packs.Add(pack);
                foreach (var kind in kinds)
                {
                    string folder = store.GetGameFolder(game.Id, kind);
                    Directory.CreateDirectory(folder);
                    File.WriteAllBytes(Path.Combine(folder, i + ".png"), new[] { (byte)i });
                }
            }
            settings = new ImageRotaterSettings { RotateCovers = false, RotateLogos = false, PackSelectionMode = SelectionMode.EverySelection, PackSelectionOrder = SelectionOrder.Sequential };
            settings.ArtworkPacks[game.Id] = config;
            service = new ArtworkPackService(store, () => settings, (g, k) => null);
        }

        [TearDown]
        public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        private string Pick(ArtworkKind kind)
        {
            IReadOnlyList<string> paths;
            Assert.That(service.TryGetPaths(game, kind, out paths), Is.True);
            return paths.Single();
        }

        [Test]
        public void ReadingAllSourcesDoesNotAdvanceAndNavigationAdvancesExactlyOnce()
        {
            service.SelectGame(game);
            for (int read = 0; read < 4; read++)
                foreach (var kind in kinds) Assert.That(Path.GetFileName(Pick(kind)), Is.EqualTo("1.png"));
            service.SelectGame(game); // Playnite's duplicate selection notification
            Assert.That(Path.GetFileName(Pick(ArtworkKind.Cover)), Is.EqualTo("1.png"));
            service.SelectGame(new Game { Id = Guid.NewGuid() });
            service.SelectGame(game);
            foreach (var kind in kinds) Assert.That(Path.GetFileName(Pick(kind)), Is.EqualTo("2.png"));
        }

        [TestCase(SelectionMode.Session)]
        [TestCase(SelectionMode.Daily)]
        public void StableModesKeepPackAcrossNavigation(SelectionMode mode)
        {
            settings.PackSelectionMode = mode;
            service.SelectGame(game);
            var first = service.Current(game.Id);
            service.SelectGame(null);
            service.SelectGame(game);
            Assert.That(service.Current(game.Id).Id, Is.EqualTo(first.Id));
        }

        [Test]
        public void SlideshowAdvanceChangesAllSourcesTogetherAndSurvivesRendererRefresh()
        {
            settings.PackSelectionMode = SelectionMode.Slideshow;
            service.SelectGame(game);
            service.Advance(game.Id);
            service.SelectGame(game);
            foreach (var kind in kinds) Assert.That(Path.GetFileName(Pick(kind)), Is.EqualTo("2.png"));
            service.Advance(game.Id);
            foreach (var kind in kinds) Assert.That(Path.GetFileName(Pick(kind)), Is.EqualTo("3.png"));
        }

        [Test]
        public void ExcludedAndFixedPacksAreRespected()
        {
            config.Packs[1].Excluded = true;
            service.SelectGame(game);
            service.Advance(game.Id);
            Assert.That(service.Current(game.Id), Is.SameAs(config.Packs[2]));
            settings.PackSelectionMode = SelectionMode.Fixed;
            config.FixedPackId = config.Packs[0].Id;
            Assert.That(service.Current(game.Id), Is.SameAs(config.Packs[0]));
        }

        [TestCase(null)]
        [TestCase("missing.png")]
        [TestCase("../outside.png")]
        public void EmptyMissingAndUnsafeReferencesUseOriginal(string reference)
        {
            string original = Path.Combine(root, "original.png");
            File.WriteAllBytes(original, new byte[] { 1 });
            config.Packs[0].Cover = reference;
            service = new ArtworkPackService(store, () => settings, (g, k) => original);
            Assert.That(Pick(ArtworkKind.Cover), Is.EqualTo(original));
        }

        [Test]
        public void MissingOriginalReturnsEmptyPoolInsteadOfUnrelatedArtwork()
        {
            config.Packs[0].Cover = null;
            IReadOnlyList<string> paths;
            Assert.That(service.TryGetPaths(game, ArtworkKind.Cover, out paths), Is.True);
            Assert.That(paths, Is.Empty);
        }

        [Test]
        public void FolderSourcesExposeTheSamePackEvenWhenIndependentRotationIsDisabled()
        {
            service.SelectGame(game);
            service.Advance(game.Id);
            foreach (var kind in kinds)
            {
                var source = new FolderImageSource(store, kind, settings: () => settings) { Packs = service };
                Assert.That(source.GetImagePaths(game), Has.Count.EqualTo(1));
                Assert.That(Path.GetFileName(source.GetImagePaths(game)[0]), Is.EqualTo("2.png"));
                Assert.That(settings.IsArtworkRotationEnabled(game.Id, kind), Is.True);
            }
            Assert.That(settings.IsArtworkRotationEnabled(Guid.NewGuid(), ArtworkKind.Cover), Is.False);
        }

        [Test]
        public void RotationServiceDoesNotMakeAnIndependentChoiceOrWriteInThemeMode()
        {
            settings.UseThemeIntegration = true;
            service.SelectGame(game);
            service.Advance(game.Id);
            var backgrounds = new FolderImageSource(store, ArtworkKind.Background) { Packs = service };
            var covers = new FolderImageSource(store, ArtworkKind.Cover) { Packs = service };
            var rotation = new BackgroundRotationService(backgrounds, covers,
                new ImageSelector(new ImagePicker(), new SessionSelectionCache()), store, () => settings);
            rotation.ApplyTo(game);
            foreach (var kind in kinds) Assert.That(Path.GetFileName(Pick(kind)), Is.EqualTo("2.png"));
        }

        [Test]
        public void DisabledOrFullyExcludedPacksLeaveIndependentSourcesAvailable()
        {
            settings.EnablePacks = false;
            IReadOnlyList<string> paths;
            Assert.That(service.TryGetPaths(game, ArtworkKind.Cover, out paths), Is.False);
            settings.EnablePacks = true;
            config.Packs.ForEach(p => p.Excluded = true);
            Assert.That(service.TryGetPaths(game, ArtworkKind.Cover, out paths), Is.False);
            Assert.That(settings.IsPackEnabled(game.Id), Is.False);
        }

        [Test]
        public void ShuffleVisitsEveryPackBeforeRepeating()
        {
            settings.PackSelectionMode = SelectionMode.Slideshow;
            settings.PackSelectionOrder = SelectionOrder.Shuffle;
            service.SelectGame(game);
            var seen = new HashSet<string> { service.Current(game.Id).Id };
            seen.Add(service.Advance(game.Id).Id);
            seen.Add(service.Advance(game.Id).Id);
            Assert.That(seen.Count, Is.EqualTo(3));
            Assert.That(seen.Contains(service.Advance(game.Id).Id), Is.True);
        }

        [Test]
        public void DailyChoiceSurvivesRestartWithDedicatedPackCache()
        {
            settings.PackSelectionMode = SelectionMode.Daily;
            string file = Path.Combine(root, "Packs", "session-picks.json");
            var firstCache = new SessionSelectionCache(file);
            var firstService = new ArtworkPackService(store, () => settings, null, firstCache);
            firstService.SelectGame(game);
            string first = firstService.Current(game.Id).Id;
            firstCache.SaveDailyChoices();
            var nextService = new ArtworkPackService(store, () => settings, null, new SessionSelectionCache(file));
            nextService.SelectGame(game);
            Assert.That(nextService.Current(game.Id).Id, Is.EqualTo(first));
        }

        [Test]
        public void GlobalPackOptionsOverrideLegacyPerGameOptions()
        {
            config.Enabled = false;
            config.Mode = SelectionMode.Fixed;
            config.FixedPackId = config.Packs[2].Id;
            config.Order = SelectionOrder.Random;
            settings.PackSelectionMode = SelectionMode.EverySelection;
            settings.PackSelectionOrder = SelectionOrder.Sequential;
            service.SelectGame(game);
            Assert.That(service.Current(game.Id), Is.SameAs(config.Packs[0]));
            service.SelectGame(null);
            service.SelectGame(game);
            Assert.That(service.Current(game.Id), Is.SameAs(config.Packs[1]));
            settings.EnablePacks = false;
            Assert.That(service.IsEnabled(game.Id), Is.False);
            Assert.That(settings.IsArtworkRotationEnabled(game.Id, ArtworkKind.Cover), Is.False);
            settings.EnablePacks = true;
            Assert.That(service.IsEnabled(game.Id), Is.True);
        }

        [Test]
        public void ConfigurationRoundTripsIncludingOrderAndOriginalSlots()
        {
            config.Packs[0].Logo = null;
            config.FixedPackId = config.Packs[1].Id;
            var restored = JsonConvert.DeserializeObject<ImageRotaterSettings>(JsonConvert.SerializeObject(settings));
            var restoredConfig = restored.ArtworkPacks[game.Id];
            Assert.That(restoredConfig.Packs.Select(p => p.Id), Is.EqualTo(config.Packs.Select(p => p.Id)));
            Assert.That(restoredConfig.Packs[0].Logo, Is.Null);
            Assert.That(restoredConfig.FixedPackId, Is.EqualTo(config.FixedPackId));
            Assert.That(restored.PackSelectionMode, Is.EqualTo(settings.PackSelectionMode));
            Assert.That(restored.PackSelectionOrder, Is.EqualTo(settings.PackSelectionOrder));
            Assert.That(restored.PackSlideshowSeconds, Is.EqualTo(settings.PackSlideshowSeconds));
            Assert.That(restored.IsPackEnabled(game.Id), Is.True);
        }
    }
}
