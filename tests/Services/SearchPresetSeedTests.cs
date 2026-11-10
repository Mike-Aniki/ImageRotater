using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using ImageRotater.Controls;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Remembered filters: ticks seeded before the first search survive into
    // the options that search builds, and a tab switch (Clear) no longer
    // drops them.
    // Labels are compared through the same lookup the plugin uses: the test
    // host has no Playnite resources, so the English text is not available.
    [TestFixture]
    public class SearchPresetSeedTests
    {
        private class StubClient : ISteamGridDbClient
        {
            public bool IsConfigured { get { return true; } }

            public Task<SteamGridDbResult<List<SteamGridDbGame>>> SearchGamesAsync(string name)
            {
                return Task.FromResult(SteamGridDbResult<List<SteamGridDbGame>>.Ok(
                    new List<SteamGridDbGame> { new SteamGridDbGame { Id = 1, Name = name } }));
            }

            public Task<SteamGridDbResult<List<SteamGridDbArtwork>>> GetArtworkAsync(
                int gameId, SteamGridDbArtworkType type, int page = 0)
            {
                return Task.FromResult(SteamGridDbResult<List<SteamGridDbArtwork>>.Ok(
                    new List<SteamGridDbArtwork>
                    {
                        new SteamGridDbArtwork { Id = 1, Width = 512, Height = 512, Style = "alternate" },
                        new SteamGridDbArtwork { Id = 2, Width = 1024, Height = 1024, Style = "alternate" },
                        new SteamGridDbArtwork { Id = 3, Width = 600, Height = 900, Style = "blurred" }
                    }));
            }

            public Task<SteamGridDbResult<byte[]>> DownloadAsync(string url)
            {
                return Task.FromResult(SteamGridDbResult<byte[]>.Ok(new byte[0]));
            }
        }

        private static AspectGroupOption Group(SteamGridDbSearchViewModel vm, string label)
        {
            return vm.AspectGroups.Single(g => g.Label == label);
        }

        [Test]
        public async Task Seeded_group_and_style_are_ticked_once_results_arrive()
        {
            var vm = new SteamGridDbSearchViewModel(new StubClient());
            vm.SeedTicks(new SearchPreset
            {
                Groups = new List<string> { Loc.Get("LOCImageRotaterAspectSquare") },
                Styles = new List<string> { "alternate" }
            });

            Assert.IsTrue(await vm.SearchAsync("game", SteamGridDbArtworkType.Grid));

            AspectGroupOption square = Group(vm, Loc.Get("LOCImageRotaterAspectSquare"));
            Assert.IsTrue(square.IsChecked);
            Assert.IsTrue(square.Dimensions.All(d => d.IsChecked));
            Assert.IsFalse(Group(vm, Loc.Get("LOCImageRotaterAspectSteamVertical")).IsChecked);
            Assert.IsFalse(Group(vm, Loc.Get("LOCImageRotaterAspectSteamVertical")).Dimensions.Any(d => d.IsChecked));

            Assert.IsTrue(vm.StyleOptions.Single(s => s.Value == "alternate").IsChecked);
            Assert.IsFalse(vm.StyleOptions.Single(s => s.Value == "blurred").IsChecked);

            // The filter was applied with the seed, not after it.
            Assert.AreEqual(2, vm.Results.Count);
        }

        [Test]
        public async Task Snapshot_reports_a_group_only_when_all_its_dimensions_are_ticked()
        {
            var vm = new SteamGridDbSearchViewModel(new StubClient());
            Assert.IsTrue(await vm.SearchAsync("game", SteamGridDbArtworkType.Grid));

            Group(vm, Loc.Get("LOCImageRotaterAspectSquare")).IsChecked = true;
            Group(vm, Loc.Get("LOCImageRotaterAspectSquare")).Dimensions.Single(d => d.Value == "512x512").IsChecked = false;
            vm.StyleOptions.Single(s => s.Value == "blurred").IsChecked = true;

            var preset = new SearchPreset();
            vm.SnapshotTicks(preset);

            CollectionAssert.IsEmpty(preset.Groups);
            CollectionAssert.AreEqual(new[] { "1024x1024" }, preset.Dimensions);
            CollectionAssert.AreEqual(new[] { "blurred" }, preset.Styles);
        }

        [Test]
        public async Task Ticks_survive_a_clear_and_a_new_search()
        {
            var vm = new SteamGridDbSearchViewModel(new StubClient());
            Assert.IsTrue(await vm.SearchAsync("game", SteamGridDbArtworkType.Grid));
            Group(vm, Loc.Get("LOCImageRotaterAspectSquare")).IsChecked = true;

            vm.Clear();
            Assert.IsTrue(await vm.SearchAsync("game", SteamGridDbArtworkType.Grid));

            Assert.IsTrue(Group(vm, Loc.Get("LOCImageRotaterAspectSquare")).IsChecked);
            Assert.AreEqual(2, vm.Results.Count);
        }

        [Test]
        public async Task Ticks_apply_on_their_own()
        {
            var vm = new SteamGridDbSearchViewModel(new StubClient());
            Assert.IsTrue(await vm.SearchAsync("game", SteamGridDbArtworkType.Grid));
            Assert.AreEqual(3, vm.Results.Count);

            Group(vm, Loc.Get("LOCImageRotaterAspectSquare")).IsChecked = true;
            Assert.AreEqual(2, vm.Results.Count, "group tick");

            Group(vm, Loc.Get("LOCImageRotaterAspectSquare")).Dimensions.Single(d => d.Value == "512x512").IsChecked = false;
            Assert.AreEqual(1, vm.Results.Count, "single size untick");

            vm.StyleOptions.Single(s => s.Value == "blurred").IsChecked = true;
            Assert.AreEqual(0, vm.Results.Count, "style tick");
        }

        [Test]
        public async Task Group_tick_is_restored_after_a_rebuild()
        {
            var vm = new SteamGridDbSearchViewModel(new StubClient());
            Assert.IsTrue(await vm.SearchAsync("game", SteamGridDbArtworkType.Grid));
            Group(vm, Loc.Get("LOCImageRotaterAspectSquare")).IsChecked = true;

            Assert.IsTrue(await vm.SearchAsync("game", SteamGridDbArtworkType.Grid));

            Assert.IsTrue(Group(vm, Loc.Get("LOCImageRotaterAspectSquare")).IsChecked);
        }
    }
}
