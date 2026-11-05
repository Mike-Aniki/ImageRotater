using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageRotater.Services;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public sealed class MosaicSourceGuardTests
    {
        [Test]
        public void RejectsPreviousAsyncBitmapInsteadOfShowingThirdCover()
        {
            string temp = Path.Combine(Path.GetTempPath(), "IR_Mosaic_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string previous = Path.Combine(temp, "previous.png");
                string current = Path.Combine(temp, "current.png");
                // Small valid PNG. No network, Playnite, or UI host required.
                byte[] png = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/pL8AAAAASUVORK5CYII=");
                File.WriteAllBytes(previous, png);
                File.WriteAllBytes(current, png);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(previous);
                bitmap.EndInit();

                Assert.That(MosaicSourceGuard.MatchesRequestedPath(bitmap, previous), Is.True);
                Assert.That(MosaicSourceGuard.MatchesRequestedPath(bitmap, current), Is.False);
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }

        [Test]
        public void DoesNotGuessForImagesWithoutFileUris()
        {
            ImageSource generated = new DrawingImage();
            Assert.That(MosaicSourceGuard.MatchesRequestedPath(generated, @"C:\\somewhere\\image.png"), Is.True);
            Assert.That(MosaicSourceGuard.MatchesRequestedPath(null, @"C:\\somewhere\\image.png"), Is.False);
        }
    }
}
