using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Playnite.SDK;

namespace ImageRotater.Services
{
    internal static class DownloadedBackgroundResizer
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private const long JpegQuality = 95L;

        public static bool CanResize(string path)
        {
            string extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            return extension == ".jpg" || extension == ".jpeg" || extension == ".png";
        }

        public static bool ResizeToPreset(string path, BackgroundDownloadResizePreset preset)
        {
            int maxWidth;
            int maxHeight;
            GetBounds(preset, out maxWidth, out maxHeight);
            return ResizeIfNeeded(path, maxWidth, maxHeight);
        }

        private static void GetBounds(
            BackgroundDownloadResizePreset preset,
            out int maxWidth,
            out int maxHeight)
        {
            switch (preset)
            {
                case BackgroundDownloadResizePreset.Qhd1440p:
                    maxWidth = 2560;
                    maxHeight = 1440;
                    break;
                case BackgroundDownloadResizePreset.Uhd4K:
                    maxWidth = 3840;
                    maxHeight = 2160;
                    break;
                default:
                    maxWidth = 1920;
                    maxHeight = 1080;
                    break;
            }
        }

        private static bool ResizeIfNeeded(string path, int maxWidth, int maxHeight)
        {
            string temp = null;

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return false;
                }

                string extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
                if (!CanResize(path))
                {
                    return false;
                }

                byte[] bytes = File.ReadAllBytes(path);
                using (var input = new MemoryStream(bytes))
                using (Image source = Image.FromStream(input, true, true))
                {
                    if (source.Width <= 0 || source.Height <= 0)
                    {
                        return false;
                    }

                    double scale = Math.Min(
                        maxWidth / (double)source.Width,
                        maxHeight / (double)source.Height);

                    // Never upscale and avoid a pointless re-encode when the
                    // image already fits completely inside the selected bounds.
                    if (scale >= 1.0)
                    {
                        return false;
                    }

                    int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                    bool alpha = HasAlpha(source.PixelFormat);

                    PixelFormat format = alpha
                        ? PixelFormat.Format32bppArgb
                        : PixelFormat.Format24bppRgb;

                    using (var resized = new Bitmap(width, height, format))
                    {
                        try
                        {
                            if (source.HorizontalResolution > 0 && source.VerticalResolution > 0)
                            {
                                resized.SetResolution(source.HorizontalResolution, source.VerticalResolution);
                            }
                        }
                        catch
                        {
                        }

                        using (Graphics graphics = Graphics.FromImage(resized))
                        {
                            graphics.CompositingMode = CompositingMode.SourceCopy;
                            graphics.CompositingQuality = CompositingQuality.HighQuality;
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.SmoothingMode = SmoothingMode.HighQuality;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            graphics.Clear(alpha ? Color.Transparent : Color.Black);

                            using (var attributes = new ImageAttributes())
                            {
                                attributes.SetWrapMode(WrapMode.TileFlipXY);
                                graphics.DrawImage(
                                    source,
                                    new Rectangle(0, 0, width, height),
                                    0,
                                    0,
                                    source.Width,
                                    source.Height,
                                    GraphicsUnit.Pixel,
                                    attributes);
                            }
                        }

                        temp = path + ".resize";
                        TryDelete(temp);

                        if (extension == ".png")
                        {
                            resized.Save(temp, ImageFormat.Png);
                        }
                        else
                        {
                            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders()
                                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                            if (jpeg == null)
                            {
                                return false;
                            }

                            using (var parameters = new EncoderParameters(1))
                            using (var quality = new EncoderParameter(Encoder.Quality, JpegQuality))
                            {
                                parameters.Param[0] = quality;
                                resized.Save(temp, jpeg, parameters);
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(temp) || !File.Exists(temp))
                {
                    return false;
                }

                File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not resize downloaded background {path}");
                TryDelete(temp);
                return false;
            }
        }

        private static bool HasAlpha(PixelFormat format)
        {
            return (format & PixelFormat.Alpha) != 0
                || (format & PixelFormat.PAlpha) != 0
                || format == PixelFormat.Format32bppArgb
                || format == PixelFormat.Format32bppPArgb
                || format == PixelFormat.Format64bppArgb;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
