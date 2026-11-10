using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Which downloads are worth converting to MP4.
    //
    // The plugin treats only .gif as animated, so an animated WebP or APNG
    // downloaded and rendered as a still first frame with nothing saying why -
    // the motion was silently lost. BackgroundChanger converts GIF, WebP, WebM
    // and APNG, which is the better list, and this matches it.
    //
    // The trap is APNG: those files are routinely named .png, so extension
    // alone would send every still PNG through ffmpeg and produce a
    // single-frame MP4 - worse than leaving it alone. Frame count decides.
    [TestFixture]
    public class ConvertibleFormatTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageRotaterConv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string WriteImage(string name, ImageFormat format)
        {
            string path = Path.Combine(_dir, name);

            using (var bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.CadetBlue);
                bmp.Save(path, format);
            }

            return path;
        }

        // A single-frame GIF is a still that happens to be a GIF. Converting it
        // produces a one-frame video, which is strictly worse.
        [Test]
        public void ASingleFrameGifIsNotConvertible()
        {
            string still = WriteImage("still.gif", ImageFormat.Gif);

            Assert.IsFalse(GifConverter.IsConvertible(still),
                "a one-frame GIF converted to MP4 is a one-frame video");
        }

        // The APNG trap: extension alone would match every PNG on disk.
        [Test]
        public void AnOrdinaryPngIsNotConvertible()
        {
            string png = WriteImage("cover.png", ImageFormat.Png);

            Assert.IsFalse(GifConverter.IsConvertible(png),
                "APNG files are named .png, so the extension cannot be the test - "
                + "otherwise every still cover would be sent through ffmpeg");
        }

        [TestCase("animated.png")]
        [TestCase("animated.apng")]
        public void TwoFrameApngIsConvertible(string name)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACGFjVEwAAAACAAAAAPONk3AAAAAaZmNUTAAAAAAAAAABAAAAAQAAAAAAAAAAAAEAAgAAVGxhaAAAAA1JREFUeJxj+M/A8B8ABQAB/4mZPR0AAAAaZmNUTAAAAAEAAAABAAAAAQAAAAAAAAAAAAEAAgAAzx+LvAAAABFmZEFUAAAAAnicY2D4z/AfAAQBAf9i5+mcAAAAAElFTkSuQmCC"));

            Assert.IsTrue(GifConverter.IsConvertible(path));
        }

        [Test]
        public void JpegIsNeverConvertible()
        {
            string jpg = WriteImage("cover.jpg", ImageFormat.Jpeg);

            Assert.IsFalse(GifConverter.IsConvertible(jpg));
        }

        // WebP and WebM cannot be opened by GDI+ to count frames, so they are
        // taken on trust. A still WebP converted is a wasted step; a WebM is
        // video by definition and converting it turns "plays if the user has a
        // codec" into "plays".
        [Test]
        public void WebmIsConvertibleOnTrust()
        {
            string webm = Path.Combine(_dir, "clip.webm");
            File.WriteAllBytes(webm, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });

            Assert.IsTrue(GifConverter.IsConvertible(webm),
                "Windows often cannot decode WebM, so converting it is the point");
        }

        // WebP is judged by its header, not trusted by extension: only the
        // VP8X animation flag makes one worth turning into an MP4.
        [TestCase(true)]
        [TestCase(false)]
        public void WebpIsConvertibleOnlyWhenAnimated(bool animated)
        {
            string webp = Path.Combine(_dir, "art.webp");
            byte[] header = System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8X\x0A\0\0\0\0\0");
            if (animated) header[20] = 0x02;
            File.WriteAllBytes(webp, header);

            Assert.AreEqual(animated, GifConverter.IsConvertible(webp));
        }

        [Test]
        public void NothingIsConvertibleWithoutAPath()
        {
            Assert.IsFalse(GifConverter.IsConvertible(null));
            Assert.IsFalse(GifConverter.IsConvertible(string.Empty));
        }

        // A file that does not exist cannot be inspected, and guessing from the
        // name is what this whole check exists to avoid.
        [Test]
        public void AMissingFileIsNotConvertible()
        {
            Assert.IsFalse(GifConverter.IsConvertible(Path.Combine(_dir, "absent.gif")));
        }
    }
}
