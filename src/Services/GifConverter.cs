using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Converts animated GIFs to MP4 using ffmpeg, when the user has ffmpeg.
    //
    // Worth doing for two reasons, both measured against a real 4.7 MB library
    // GIF: the MP4 came out at 1.0 MB, a 78% saving, and it plays through
    // MediaElement - hardware-assisted H.264 - rather than decoding frames on
    // the UI thread the way XamlAnimatedGif must. In a 32-bit process hosting a
    // grid of tiles, that difference is the one that decides whether Playnite
    // stays up.
    //
    // ffmpeg is NOT bundled and never will be: it is GPL and this plugin is
    // MIT, so shipping the binary would force a licence change on the whole
    // project. The plugin uses it when the user already has it and says so
    // plainly when they do not, rather than silently doing nothing.
    public static class GifConverter
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Where the user said ffmpeg lives, empty meaning "search PATH".
        //
        // Set from settings rather than read from them, so this class stays
        // usable without the whole plugin standing behind it - the bulk
        // conversion, the download path and the tests all reach it.
        public static string ConfiguredPath { get; set; }

        public static bool IsAvailable
        {
            get { return FindFfmpeg() != null; }
        }

        // Full path to ffmpeg, or null when it cannot be found.
        public static string FindFfmpeg()
        {
            return ExternalTool.Resolve(ConfiguredPath, ExternalTool.FfmpegExe);
        }

        // Formats worth converting to MP4.
        //
        // GIF is the obvious one, but an animated WebP or APNG downloads and
        // renders as a still first frame with nothing saying why - the plugin
        // treats only .gif as animated, so the motion is silently lost.
        // BackgroundChanger converts the same four, which is the better list.
        //
        // WebM is here because it is video Windows often cannot decode: the
        // plugin plays MP4 everywhere and WebM only where the user has a codec,
        // so converting it turns a maybe into a yes.
        private static readonly string[] ConvertibleExtensions =
            { ".gif", ".webp", ".apng", ".png", ".webm" };

        // True for a file worth handing to ffmpeg.
        //
        // .png is in the list above only because APNG files are routinely named
        // .png - so a still PNG would match too. ffmpeg produces a
        // single-frame MP4 from one, which is worse than leaving it alone, so
        // the frame count decides rather than the extension.
        public static bool IsConvertible(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string ext = Path.GetExtension(path);

            foreach (string candidate in ConvertibleExtensions)
            {
                if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return HasMultipleFrames(path);
                }
            }

            return false;
        }

        // The WebP header records whether animation is present. Checking its
        // VP8X animation flag avoids converting ordinary static WebPs into MP4.
        // This reads only the first 21 bytes, never decodes the artwork.
        public static bool IsAnimatedWebp(string path)
        {
            if (string.IsNullOrEmpty(path) ||
                !string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    byte[] header = new byte[21];
                    if (stream.Read(header, 0, header.Length) != header.Length)
                    {
                        return false;
                    }

                    // RIFF size WEBP VP8X chunk-size flags
                    return header[0] == 'R' && header[1] == 'I' &&
                        header[2] == 'F' && header[3] == 'F' &&
                        header[8] == 'W' && header[9] == 'E' &&
                        header[10] == 'B' && header[11] == 'P' &&
                        header[12] == 'V' && header[13] == 'P' &&
                        header[14] == '8' && header[15] == 'X' &&
                        (header[20] & 0x02) != 0;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static bool IsAnimatedPng(string path)
        {
            string extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".apng", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadUInt64() != 0x0A1A0A0D474E5089UL) return false;
                    var stream = reader.BaseStream;
                    while (stream.Length - stream.Position >= 12)
                    {
                        uint length = ReadPngUInt32(reader);
                        uint type = reader.ReadUInt32();
                        if (length > stream.Length - stream.Position - 4) return false;
                        // acTL must precede the first IDAT chunk.
                        if (type == 0x4C546361) return length == 8 && ReadPngUInt32(reader) > 1;
                        if (type == 0x54414449 || type == 0x444E4549) return false;
                        stream.Seek(length + 4L, SeekOrigin.Current);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        private static uint ReadPngUInt32(BinaryReader reader) =>
            ((uint)reader.ReadByte() << 24) | ((uint)reader.ReadByte() << 16) |
            ((uint)reader.ReadByte() << 8) | reader.ReadByte();

        // GDI+ counts GIF frames but only sees the default image of an APNG.
        private static bool HasMultipleFrames(string path)
        {
            string ext = Path.GetExtension(path) ?? string.Empty;

            if (ext.Equals(".webm", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                return IsAnimatedWebp(path);
            }
            if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".apng", StringComparison.OrdinalIgnoreCase))
            {
                return IsAnimatedPng(path);
            }

            try
            {
                using (var image = System.Drawing.Image.FromFile(path))
                {
                    var dimension = new System.Drawing.Imaging.FrameDimension(
                        image.FrameDimensionsList[0]);

                    return image.GetFrameCount(dimension) > 1;
                }
            }
            catch (Exception)
            {
                // Unreadable by GDI+ - leave it alone rather than guess.
                return false;
            }
        }

        // Converts one GIF, returning the MP4 path or null on failure.
        //
        // The source is left alone. Callers that want it gone delete it after
        // confirming the result, so a failed conversion can never lose the only
        // copy of a user's artwork.
        // True for a FRAGMENTED mp4 - one built from moof/mdat fragments rather
        // than a single progressive stream.
        //
        // YouTube serves DASH, so a download taken straight from it is exactly
        // this. ffmpeg reads them without complaint, so a decode check passes
        // and the file looks healthy - but Windows Media Foundation does not,
        // and neither do most desktop players. MediaElement reports such a file
        // as open and playing, with the position advancing, while rendering
        // solid black.
        //
        // Detected from the ftyp brand, which is the first atom in the file.
        public static bool IsFragmented(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var header = new byte[12];

                    if (stream.Read(header, 0, header.Length) < header.Length)
                    {
                        return false;
                    }

                    if (header[4] != 'f' || header[5] != 't' ||
                        header[6] != 'y' || header[7] != 'p')
                    {
                        return false;
                    }

                    string brand = System.Text.Encoding.ASCII.GetString(header, 8, 4);

                    return brand == "dash" || brand == "iso5" || brand == "iso6";
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Rewrites a fragmented mp4 as a normal progressive one.
        //
        // A stream copy, not a re-encode: the H.264 inside is fine, it is only
        // the container that nothing but ffmpeg will read. Costs a second and
        // loses no quality.
        public static bool Remux(string path)
        {
            string ffmpeg = FindFfmpeg();

            if (ffmpeg == null || !File.Exists(path))
            {
                return false;
            }

            string temp = path + ".remux.mp4";

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments =
                        $"-y -i \"{path}\" -c copy -movflags +faststart -an \"{temp}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0 || !File.Exists(temp))
                    {
                        TryDelete(temp);
                        return false;
                    }
                }

                File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not remux {Path.GetFileName(path)}");
                TryDelete(temp);
                return false;
            }
        }

        // Pulls a single still frame out of a video.
        //
        // Needed because a theme's tile binding is a still image path, so a
        // game whose cover is a video has nothing for it to show. Publishing
        // only the video left the tile on its 1x1 transparent placeholder,
        // which Playnite stretches across the tile and renders as solid black -
        // a downloaded video looked like broken artwork.
        //
        // PosterFrame cannot do this: it is GDI+, which decodes no video
        // container at all. ffmpeg is already here for conversion.
        //
        // Returns null when ffmpeg is missing or the frame cannot be read.
        public static string ExtractPoster(string videoPath, string outputPath)
        {
            string ffmpeg = FindFfmpeg();

            if (ffmpeg == null || string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                return null;
            }

            string temp = outputPath + ".tmp.png";

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = ffmpeg,

                    // One second in rather than frame zero: videos routinely
                    // open on a black or a fade-in frame, which is exactly the
                    // useless poster this is meant to avoid.
                    //
                    // PNG so the tile keeps the lossless quality the plugin
                    // prefers everywhere else.
                    Arguments =
                        $"-y -ss 00:00:01 -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{temp}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    // Drained rather than ignored: a full pipe deadlocks the
                    // process being waited on.
                    process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();

                    process.WaitForExit();

                    if (process.ExitCode != 0 || !File.Exists(temp))
                    {
                        // A video shorter than the seek point lands here.
                        TryDelete(temp);
                        return null;
                    }
                }

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                File.Move(temp, outputPath);
                return outputPath;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not take a poster frame from {Path.GetFileName(videoPath)}");
                TryDelete(temp);
                return null;
            }
        }

        public static string Convert(string gifPath, string outputPath = null)
        {
            string ffmpeg = FindFfmpeg();

            if (ffmpeg == null || string.IsNullOrEmpty(gifPath) || !File.Exists(gifPath))
            {
                return null;
            }

            string target = outputPath ?? Path.ChangeExtension(gifPath, ".mp4");
            string temp = target + ".tmp.mp4";

            try
            {
                // yuv420p and the even-dimension scale are both required, not
                // stylistic. H.264 in an MP4 container will not play in
                // MediaElement without 4:2:0 chroma, and libx264 refuses odd
                // dimensions outright - GIFs are routinely odd-sized.
                //
                // faststart moves the index to the front so playback can begin
                // before the whole file is read. crf 23 is ffmpeg's default
                // quality: visually transparent for artwork at this size.
                var start = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments =
                        $"-y -i \"{gifPath}\" -movflags faststart -pix_fmt yuv420p "
                        + "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" "
                        + $"-c:v libx264 -crf 23 -an \"{temp}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    // Drained rather than ignored: ffmpeg writes progress to
                    // stderr continuously, and a full pipe buffer deadlocks the
                    // process we are waiting on.
                    string conversionLog = process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();

                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Logger.Warn(
                            $"ImageRotater: ffmpeg exited {process.ExitCode} converting {Path.GetFileName(gifPath)}");
                        TryDelete(temp);
                        return null;
                    }

                    // Some older FFmpeg versions decode only the first WebP
                    // frame and still exit successfully. Never replace an
                    // animated source with a one-frame "video". FFmpeg prints
                    // its output frame count in the final progress line.
                    if (IsAnimatedWebp(gifPath))
                    {
                        MatchCollection frames = Regex.Matches(conversionLog ?? string.Empty,
                            @"frame=\s*(\d+)");
                        int encodedFrames;
                        if (frames.Count == 0 ||
                            !int.TryParse(frames[frames.Count - 1].Groups[1].Value, out encodedFrames) ||
                            encodedFrames < 2)
                        {
                            Logger.Warn($"ImageRotater: FFmpeg did not encode an animated WebP: {Path.GetFileName(gifPath)}");
                            TryDelete(temp);
                            return null;
                        }
                    }
                }

                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                {
                    TryDelete(temp);
                    return null;
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);
                return target;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not convert {gifPath} to MP4");
                TryDelete(temp);
                return null;
            }
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
