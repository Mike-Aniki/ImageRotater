using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Resizes a background so every candidate for a game is the same width.
    //
    // Why this exists, and it is not about file size:
    //
    // Playnite blurs the window background with a WPF BlurEffect at a FIXED
    // radius (the user's BackgroundImageBlurAmount, commonly 40-60), applied to
    // the container AFTER the image inside has been scaled to fit. It also
    // decodes every background to the screen's working width - so a 3840px
    // source is downscaled 2.7x while a 1440px source is untouched.
    //
    // A fixed-radius blur over those two therefore covers a very different
    // fraction of the actual picture, and rotating between them makes the blur
    // visibly jump even though both fill the same rectangle. Users see it as
    // "the background pops when it changes" - and it disappears entirely when
    // two images happen to share a resolution, which is the clue that gave this
    // away.
    //
    // Normalising the width makes consecutive picks blur identically. Only the
    // published copy is touched; the candidates keep their original resolution.
    public static class BackgroundNormaliser
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Below this, upscaling would visibly soften the image for no gain -
        // better a slightly different blur than a mushy background.
        private const int MinimumWidth = 1280;

        // Above this there is nothing to gain: Playnite decodes to the screen's
        // working width anyway, and on a 4K display that is 3840.
        internal const int MaximumWidth = 3840;

        // Re-encoding a resized 4K background as PNG can take hundreds of
        // milliseconds. JPEG sources therefore stay JPEG. Opaque PNG
        // backgrounds can safely use the same fast cache representation too;
        // PNG is kept only when alpha is actually present. The user's source
        // file is never modified.
        private const long JpegQuality = 92L;

        public static string CacheExtensionFor(string sourcePath)
        {
            string extension = Path.GetExtension(sourcePath);
            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return ".jpg";
            }

            // PNG exposes transparency in its container metadata. Reading the
            // few chunks before IDAT is dramatically cheaper than decoding a
            // 4K bitmap just to decide which cache encoder to use. If the PNG
            // has no alpha channel and no tRNS chunk, JPEG is safe for this
            // derived background cache.
            if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) &&
                !PngMayContainTransparency(sourcePath))
            {
                return ".jpg";
            }

            return ".png";
        }

        private static bool PngMayContainTransparency(string sourcePath)
        {
            try
            {
                using (var stream = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    // PNG signature. If this is not actually a PNG, preserve
                    // it losslessly rather than guessing.
                    byte[] signature = reader.ReadBytes(8);
                    byte[] expected = { 137, 80, 78, 71, 13, 10, 26, 10 };
                    if (signature.Length != expected.Length)
                    {
                        return true;
                    }
                    for (int i = 0; i < expected.Length; i++)
                    {
                        if (signature[i] != expected[i])
                        {
                            return true;
                        }
                    }

                    while (stream.Position + 12 <= stream.Length)
                    {
                        int length = ReadBigEndianInt32(reader);
                        if (length < 0 || stream.Position + 8L + length > stream.Length)
                        {
                            return true;
                        }

                        string type = new string(reader.ReadChars(4));
                        if (type == "IHDR")
                        {
                            if (length < 13)
                            {
                                return true;
                            }

                            reader.ReadBytes(8); // width + height
                            reader.ReadByte();   // bit depth
                            byte colorType = reader.ReadByte();

                            // Colour types 4 and 6 contain an alpha channel.
                            if (colorType == 4 || colorType == 6)
                            {
                                return true;
                            }

                            int remaining = length - 10;
                            if (remaining > 0)
                            {
                                reader.ReadBytes(remaining);
                            }
                        }
                        else if (type == "tRNS")
                        {
                            // Palette/greyscale/true-colour PNG with explicit
                            // transparency. Preserve it as PNG.
                            return true;
                        }
                        else
                        {
                            reader.ReadBytes(length);
                        }

                        reader.ReadBytes(4); // CRC

                        // No transparency metadata can appear after image data
                        // in a valid PNG. Stop before touching the large payload.
                        if (type == "IDAT" || type == "IEND")
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Safe fallback: keep PNG semantics if metadata cannot be read.
                return true;
            }

            return true;
        }

        private static int ReadBigEndianInt32(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(4);
            if (bytes.Length != 4)
            {
                return -1;
            }

            return (bytes[0] << 24) |
                   (bytes[1] << 16) |
                   (bytes[2] << 8) |
                   bytes[3];
        }

        // Reads the pixel width without decoding the whole bitmap for the common
        // background formats. This is important on the selection path: constructing
        // a GDI+ Bitmap for a 4K image can itself take hundreds of milliseconds,
        // even when the image is already exactly the target width and no resize is
        // actually necessary.
        public static bool TryGetPixelWidth(string sourcePath, out int width)
        {
            width = 0;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return false;
            }

            // Do not trust the extension here. Artwork downloaded from web
            // sources can occasionally keep a .png/.jpg suffix that does not
            // match the bytes that were actually returned. The normaliser only
            // needs the dimensions, so sniff the file signature first and then
            // use the matching lightweight metadata parser. This avoids falling
            // back to Image.FromFile() + a full-folder scan just because a JPEG
            // happens to be named .png (or vice versa).
            try
            {
                using (var stream = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 2)
                    {
                        return false;
                    }

                    byte first = reader.ReadByte();
                    byte second = reader.ReadByte();
                    stream.Position = 0;

                    // PNG: 89 50 4E 47 0D 0A 1A 0A, followed by IHDR whose
                    // first four data bytes are the width in big-endian order.
                    if (first == 0x89 && second == 0x50)
                    {
                        byte[] signature = reader.ReadBytes(8);
                        byte[] expected = { 137, 80, 78, 71, 13, 10, 26, 10 };
                        if (signature.Length != expected.Length)
                        {
                            return false;
                        }
                        for (int i = 0; i < expected.Length; i++)
                        {
                            if (signature[i] != expected[i])
                            {
                                return false;
                            }
                        }

                        int length = ReadBigEndianInt32(reader);
                        string type = new string(reader.ReadChars(4));
                        if (length < 8 || type != "IHDR")
                        {
                            return false;
                        }

                        width = ReadBigEndianInt32(reader);
                        return width > 0;
                    }

                    // JPEG: FF D8. Walk marker segments until a SOF marker is
                    // found; this reads only metadata and never decodes pixels.
                    if (first == 0xFF && second == 0xD8)
                    {
                        reader.ReadByte();
                        reader.ReadByte();

                        while (stream.Position + 4 < stream.Length)
                        {
                            byte prefix;
                            do
                            {
                                prefix = reader.ReadByte();
                            }
                            while (prefix != 0xFF && stream.Position < stream.Length);

                            byte marker;
                            do
                            {
                                marker = reader.ReadByte();
                            }
                            while (marker == 0xFF && stream.Position < stream.Length);

                            // Standalone markers have no segment length.
                            if (marker == 0xD8 || marker == 0xD9 ||
                                (marker >= 0xD0 && marker <= 0xD7))
                            {
                                continue;
                            }

                            int segmentLength = ReadBigEndianUInt16(reader);
                            if (segmentLength < 2 || stream.Position + segmentLength - 2 > stream.Length)
                            {
                                return false;
                            }

                            // SOF markers that carry dimensions (excluding
                            // DHT/JPG/DAC which occupy the same marker range).
                            bool isSof = marker >= 0xC0 && marker <= 0xCF &&
                                         marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                            if (isSof)
                            {
                                if (segmentLength < 7)
                                {
                                    return false;
                                }

                                reader.ReadByte(); // precision
                                ReadBigEndianUInt16(reader); // height
                                width = ReadBigEndianUInt16(reader);
                                return width > 0;
                            }

                            stream.Seek(segmentLength - 2, SeekOrigin.Current);
                        }
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static int ReadBigEndianUInt16(BinaryReader reader)
        {
            int high = reader.ReadByte();
            int low = reader.ReadByte();
            return (high << 8) | low;
        }

        // Resizes into targetPath when the source is not already the right
        // width. Returns the path actually written - which is sourcePath itself
        // when no work was needed.
        //
        // Never throws: a background that cannot be resized is published as it
        // is, which is the pre-existing behaviour.
        public static string NormaliseTo(string sourcePath, int targetWidth, string targetPath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return sourcePath;
            }

            if (targetWidth < MinimumWidth || targetWidth > MaximumWidth)
            {
                return sourcePath;
            }

            try
            {
                using (var source = new Bitmap(sourcePath))
                {
                    // Already right, so publishing a re-encoded copy would only
                    // lose quality.
                    if (source.Width == targetWidth)
                    {
                        return sourcePath;
                    }

                    // Aspect ratio preserved - Playnite stretches to fill, and
                    // changing the ratio here would crop differently from the
                    // original.
                    int height = (int)Math.Round(
                        source.Height * (targetWidth / (double)source.Width));

                    if (height < 1)
                    {
                        return sourcePath;
                    }

                    using (var resized = new Bitmap(targetWidth, height))
                    using (var graphics = Graphics.FromImage(resized))
                    {
                        // HighQualityBicubic because this image is then blurred
                        // and stretched across a whole window - resampling
                        // artefacts that would vanish on a thumbnail are
                        // plainly visible at that size.
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;

                        graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, height));

                        if (string.Equals(CacheExtensionFor(sourcePath), ".jpg", StringComparison.OrdinalIgnoreCase))
                        {
                            ImageCodecInfo jpeg = null;
                            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                            {
                                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                                {
                                    jpeg = codec;
                                    break;
                                }
                            }

                            if (jpeg != null)
                            {
                                using (var parameters = new EncoderParameters(1))
                                using (var quality = new EncoderParameter(Encoder.Quality, JpegQuality))
                                {
                                    parameters.Param[0] = quality;
                                    resized.Save(targetPath, jpeg, parameters);
                                }
                            }
                            else
                            {
                                resized.Save(targetPath, ImageFormat.Jpeg);
                            }
                        }
                        else
                        {
                            resized.Save(targetPath, ImageFormat.Png);
                        }
                    }
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not normalise " + Path.GetFileName(sourcePath));
                return sourcePath;
            }
        }

        // The width every background for this game should be published at.
        //
        // The LARGEST candidate wins, capped at the screen width. Upscaling a
        // small image to match a large one would soften it, so the target is
        // whatever the best source can supply - and everything bigger comes
        // down to meet it.
        public static int TargetWidthFor(System.Collections.Generic.IEnumerable<string> candidates, int screenWidth)
        {
            int widest = 0;

            foreach (string path in candidates)
            {
                try
                {
                    using (var image = Image.FromFile(path))
                    {
                        if (image.Width > widest)
                        {
                            widest = image.Width;
                        }
                    }
                }
                catch (Exception)
                {
                    // Not an image, or unreadable - it cannot be a candidate
                    // for the target either.
                }
            }

            if (widest == 0)
            {
                return 0;
            }

            int cap = screenWidth > 0 ? Math.Min(screenWidth, MaximumWidth) : MaximumWidth;

            return Math.Min(widest, cap);
        }
    }
}
