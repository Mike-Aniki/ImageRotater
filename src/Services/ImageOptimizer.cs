using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Re-encodes stored artwork to smaller files.
    //
    // Nearly all the size in a rotation folder is photographic backgrounds
    // saved as PNG - lossless, and the wrong format for a photograph. Re-saving
    // those as high-quality JPEG routinely cuts them by 80% or more with no
    // difference visible at display size.
    //
    // Uses System.Drawing, which ships with .NET Framework. No new dependency,
    // no external tool for the user to install.
    public class ImageOptimizer
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // High enough that re-encoding is not visible on a background, low
        // enough to be worth doing. Below about 80 the sky and gradients common
        // in game art start to band.
        private const long JpegQuality = 88L;

        // Anything smaller is not worth touching: the saving is negligible and
        // every re-encode is a small quality loss.
        private const long MinimumBytes = 200 * 1024;

        // Never grow a file. Some images are already well compressed, and a
        // JPEG of an already-small PNG can come out larger.
        private const double MustSaveFraction = 0.10;

        private readonly GameImageStore _store;
        private readonly FileLogger _fileLogger;

        public ImageOptimizer(GameImageStore store, FileLogger fileLogger = null)
        {
            _store = store;
            _fileLogger = fileLogger;
        }

        public class Result
        {
            public int FilesConsidered { get; set; }
            public int FilesOptimised { get; set; }
            public long BytesBefore { get; set; }
            public long BytesAfter { get; set; }

            public long BytesSaved
            {
                get { return BytesBefore - BytesAfter; }
            }

            public bool ReportSavings { get; set; } = true;

            public string Summary
            {
                get
                {
                    if (FilesOptimised == 0)
                    {
                        return ReportSavings
                            ? Loc.Format("LOCImageRotaterCheckedNothingSmaller", FilesConsidered)
                            : Loc.Format("LOCImageRotaterCheckedMatchingSize", FilesConsidered);
                    }

                    string counts = Loc.Format("LOCImageRotaterOptimisedCount", FilesOptimised, FilesConsidered);

                    return ReportSavings
                        ? counts + Loc.Format("LOCImageRotaterSavingMb", Megabytes(BytesSaved))
                        : counts + ".";
                }
            }

            private static string Megabytes(long bytes)
            {
                return (bytes / 1048576.0).ToString("0.0");
            }
        }

        // Optimises every stored image for the given games.
        //
        // Runs off the UI thread by the caller: this decodes and re-encodes
        // whole images, which on a large library takes seconds.
        public Result OptimiseAll(IEnumerable<Guid> gameIds, Action<int, int> onProgress = null)
        {
            var result = new Result();

            if (_store == null || gameIds == null)
            {
                return result;
            }

            List<Guid> ids = gameIds.ToList();
            int done = 0;

            foreach (Guid gameId in ids)
            {
                foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
                {
                    foreach (string path in SafeList(gameId, kind))
                    {
                        Optimise(path, result);
                    }
                }

                onProgress?.Invoke(++done, ids.Count);
            }

            _fileLogger?.Log($"optimise: {result.Summary}");

            return result;
        }

        private IReadOnlyList<string> SafeList(Guid gameId, ArtworkKind kind)
        {
            try
            {
                return _store.GetImagePaths(gameId, kind);
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        // Re-encodes one file when that makes it meaningfully smaller.
        //
        // The original is replaced only after the new file is written and
        // measured, so a failure at any point leaves the original untouched.
        public bool Optimise(string path, Result result = null)
        {
            result = result ?? new Result();

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return false;
                }

                var original = new FileInfo(path);

                result.FilesConsidered++;
                result.BytesBefore += original.Length;
                result.BytesAfter += original.Length;

                if (original.Length < MinimumBytes)
                {
                    return false;
                }

                string temp = path + ".opt";

                if (!TryReencode(path, temp))
                {
                    TryDelete(temp);
                    return false;
                }

                var candidate = new FileInfo(temp);

                // Only worth it if the saving is real. A marginal gain is not
                // worth a generation of lossy re-encoding.
                if (candidate.Length >= original.Length * (1.0 - MustSaveFraction))
                {
                    TryDelete(temp);
                    return false;
                }

                // The bytes are now JPEG, so the name has to say so. Leaving a
                // re-encoded file called ".png" would make the extension lie
                // about its content - GetImagePaths filters on extension, and
                // anything reading the name rather than the header would be
                // misled.
                string finalPath = Path.ChangeExtension(path, ".jpg");

                File.Delete(path);

                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(temp, finalPath);

                result.FilesOptimised++;
                result.BytesAfter -= original.Length - candidate.Length;

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not optimise {path}");
                return false;
            }
        }

        // Decodes and re-saves as JPEG. Returns false if the file is not an
        // image this can handle, leaving the caller to keep the original.
        private static bool TryReencode(string source, string target)
        {
            try
            {
                // Copied to memory first so the source file is not locked while
                // the encode runs - Image.FromFile holds the file open for the
                // lifetime of the object.
                byte[] bytes = File.ReadAllBytes(source);

                using (var input = new MemoryStream(bytes))
                using (Image image = Image.FromStream(input))
                {
                    // Transparency cannot survive a JPEG. Converting it to a
                    // black background would visibly wreck a logo or an icon
                    // with a cut-out edge, so those are left alone.
                    if (HasAlpha(image.PixelFormat))
                    {
                        return false;
                    }

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
                        image.Save(target, jpeg, parameters);
                    }
                }

                return File.Exists(target);
            }
            catch (Exception)
            {
                // Not a decodable image, or the encoder refused it. Either way
                // the original stays.
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
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
