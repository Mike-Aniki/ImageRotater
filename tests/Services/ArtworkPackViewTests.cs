using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ImageRotater.Controls;
using ImageRotater.Models;
using ImageRotater.Services;
using NUnit.Framework;
using Playnite.SDK.Models;
using Playnite.SDK;
using Moq;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class ArtworkPackViewTests
    {
        [TestCase(1000)]
        [TestCase(1900)]
        public void ArtworkCardTitlesUseTheCardWidthAndStayCentered(double width)
        {
            var game = new Game { Id = Guid.NewGuid(), Name = "Layout game" };
            var settings = new ImageRotaterSettings();
            settings.ArtworkPacks[game.Id] = new ArtworkPackConfiguration
            {
                Packs = new System.Collections.Generic.List<ArtworkPack> { new ArtworkPack { Name = "Pack" } }
            };
            var view = new ArtworkPacksView(null, new GameImageStore(Path.GetTempPath()), game, null, () => settings, null);
            foreach (string kind in new[] { "Cover", "Background", "Logo" })
                ((TextBlock)view.FindName(kind + "SlotText")).Text = "An illustration name of a different length";
            view.Measure(new Size(width, 1000));
            view.Arrange(new Rect(0, 0, width, 1000));
            view.UpdateLayout();
            foreach (string kind in new[] { "Cover", "Background", "Logo" })
            {
                var button = (Button)view.FindName(kind + "Slot");
                var title = (TextBlock)view.FindName(kind + "SlotTitle");
                double left = title.TransformToAncestor(button).Transform(new Point()).X;
                Assert.That(title.ActualWidth, Is.GreaterThan(button.ActualWidth * 0.8), kind);
                Assert.That(left + title.ActualWidth / 2, Is.EqualTo(button.ActualWidth / 2).Within(1), kind);
                Assert.That(title.TextAlignment, Is.EqualTo(TextAlignment.Center));
                foreach (string suffix in new[] { "SlotText", "SlotChoose" })
                {
                    var text = (TextBlock)view.FindName(kind + suffix);
                    double textLeft = text.TransformToAncestor(button).Transform(new Point()).X;
                    Assert.That(textLeft + text.ActualWidth / 2, Is.EqualTo(button.ActualWidth / 2).Within(1), kind + suffix);
                    Assert.That(text.TextAlignment, Is.EqualTo(TextAlignment.Center), kind + suffix);
                }
            }
        }

        [Test]
        public void VisualPickerAssignsAnExistingImageAndCanReturnToOriginal()
        {
            string root = Path.Combine(Path.GetTempPath(), "IR_PackPicker_" + Guid.NewGuid().ToString("N"));
            var store = new GameImageStore(root);
            var game = new Game { Id = Guid.NewGuid(), Name = "Picker game" };
            var settings = new ImageRotaterSettings();
            string folder = store.GetGameFolder(game.Id, ArtworkKind.Cover);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "cover.png"), new byte[] { 1 });
            try
            {
                var view = new ArtworkPacksView(null, store, game, null, () => settings, null);
                ((Button)view.FindName("NewPackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)view.FindName("CoverSlot")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var choices = (ListView)view.FindName("ArtworkChoices");
                Assert.That(choices.Items.Count, Is.EqualTo(2));
                Assert.That(((FrameworkElement)view.FindName("GalleryOverlay")).Visibility, Is.EqualTo(Visibility.Visible));
                choices.SelectedIndex = 1;
                ((Button)view.FindName("ChooseArtworkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)view.FindName("SavePacksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.That(settings.ArtworkPacks[game.Id].Packs[0].Cover, Is.EqualTo("cover.png"));
                ((Button)view.FindName("CoverSlot")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                choices.SelectedIndex = 0;
                ((Button)view.FindName("ChooseArtworkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)view.FindName("SavePacksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.That(settings.ArtworkPacks[game.Id].Packs[0].Cover, Is.Null);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestCase(MessageBoxResult.Cancel, false, 0)]
        [TestCase(MessageBoxResult.No, true, 0)]
        [TestCase(MessageBoxResult.Yes, true, 1)]
        public void ClosingOffersToSaveDiscardOrKeepEditing(MessageBoxResult answer, bool closes, int expectedSaves)
        {
            var api = new Mock<IPlayniteAPI>();
            api.Setup(a => a.Dialogs.ShowMessage(It.IsAny<string>(), It.IsAny<string>(), MessageBoxButton.YesNoCancel)).Returns(answer);
            var settings = new ImageRotaterSettings();
            var game = new Game { Id = Guid.NewGuid() };
            int saves = 0;
            var view = new ArtworkPacksView(api.Object, new GameImageStore(Path.GetTempPath()), game, null, () => settings, () => saves++);
            Assert.That(view.TryClose(), Is.True);
            ((Button)view.FindName("NewPackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(view.TryClose(), Is.EqualTo(closes));
            Assert.That(saves, Is.EqualTo(expectedSaves));
            Assert.That(settings.ArtworkPacks.ContainsKey(game.Id), Is.EqualTo(expectedSaves > 0));
        }

        [Test]
        public void PackOptionsAreInPluginSettingsAndNotInTheCreationWindow()
        {
            var settings = new ImageRotaterSettings();
            var game = new Game { Id = Guid.NewGuid(), Name = "Game" };
            var view = new ArtworkPacksView(null, new GameImageStore(Path.GetTempPath()), game, null, () => settings, null);
            Assert.That(view.FindName("ModeBox"), Is.Null);
            Assert.That(view.FindName("IntervalBox"), Is.Null);
            Assert.That(view.FindName("OrderBox"), Is.Null);
            Assert.That(view.FindName("EnabledBox"), Is.Null);
            var artwork = new ArtworkManagerView(null, new GameImageStore(Path.GetTempPath()),
                new SessionSelectionCache(), game, ArtworkKind.Cover, null, null, null, null, () => settings, null);
            Assert.That(artwork.FindName("PacksTab"), Is.Null);
            Assert.That(artwork.TryFindResource("ManagerPrimaryButtonStyle"), Is.Not.Null);
            var options = new ImageRotaterSettingsView();
            var tab = (TabItem)options.FindName("PacksSettingsTab");
            Assert.That(tab, Is.Not.Null);
            var navigation = (TabControl)options.FindName("MainSettingsTabs");
            navigation.SelectedItem = tab;
            Assert.That(navigation.SelectedItem, Is.SameAs(tab));
            ((Button)options.FindName("PackBackgroundsDisplayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(navigation.SelectedItem, Is.SameAs(options.FindName("BackgroundsTab")));
            Assert.That(((TabControl)((TabItem)navigation.SelectedItem).Content).SelectedIndex, Is.EqualTo(1));
        }

        [Test]
        public void EditorCanCreateRenameAndSaveAPackWithoutMutatingSettingsBeforeSave()
        {
            string root = Path.Combine(Path.GetTempPath(), "IR_PackView_" + Guid.NewGuid().ToString("N"));
            var game = new Game { Id = Guid.NewGuid() };
            var settings = new ImageRotaterSettings();
            int saves = 0;
            var view = new ArtworkPacksView(null, new GameImageStore(root), game, null, () => settings, () => saves++);
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            var list = (ListView)view.FindName("PackList");
            // The editor's New button is wired through its routed Click event.
            ((Button)view.FindName("NewPackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(list.Items.Count, Is.EqualTo(1));
            ((TextBox)view.FindName("NameBox")).Text = "Retro";
            Assert.That(settings.ArtworkPacks, Is.Empty);
            ((Button)view.FindName("SavePacksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(saves, Is.EqualTo(1));
            Assert.That(settings.ArtworkPacks[game.Id].Packs[0].Name, Is.EqualTo("Retro"));
            Assert.That(settings.ArtworkPacks[game.Id].Packs[0].Cover, Is.Null);
            Assert.That(settings.IsPackEnabled(game.Id), Is.True);
            ((TextBox)view.FindName("NameBox")).Text = "Unsaved edit";
            Assert.That(settings.ArtworkPacks[game.Id].Packs[0].Name, Is.EqualTo("Retro"));
        }
    }
}
