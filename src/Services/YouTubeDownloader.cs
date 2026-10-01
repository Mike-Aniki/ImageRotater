using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Fetches one YouTube video as an MP4 the plugin can play.
    //
    // yt-dlp does the download and hands the result to ffmpeg for the container
    // work in the same pass - there is no second conversion step here, because
    // --recode-video already runs ffmpeg with the settings that matter.
    public class YouTubeDownloader
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly string _ytDlpPath;
        private readonly string _ffmpegPath;
        private readonly string _denoPath;

        public YouTubeDownloader(ImageRotaterSettings settings)
        {
            _ytDlpPath = ExternalTool.Resolve(settings?.YtDlpPath, ExternalTool.YtDlpExe);
            _ffmpegPath = ExternalTool.Resolve(settings?.FfmpegPath, ExternalTool.FfmpegExe);
            _denoPath = ExternalTool.Resolve(settings?.DenoPath, ExternalTool.DenoExe);
        }

        public bool IsAvailable
        {
            get { return _ytDlpPath != null && _ffmpegPath != null; }
        }

        // Downloads to targetPath (an .mp4). Returns false if anything failed;
        // the reason is logged rather than thrown, because the caller shows a
        // status line rather than a stack trace.
        public async Task<bool> DownloadAsync(
            string url, string targetPath, CancellationToken cancellationToken)
        {
            if (!IsAvailable)
            {
                Logger.Warn("ImageRotater: YouTube download needs both yt-dlp and ffmpeg");
                return false;
            }

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            return await Task
                .Run(() => Run(url, targetPath, cancellationToken))
                .ConfigureAwait(true);
        }

        private bool Run(string url, string targetPath, CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(targetPath);

            try
            {
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not create " + directory);
                return false;
            }

            // yt-dlp appends its own extension, so it is given the path without
            // one and the result is found afterwards.
            string stem = Path.Combine(
                directory ?? string.Empty,
                Path.GetFileNameWithoutExtension(targetPath) + ".ytdl");

            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,

                // Height capped at 1080: this is wallpaper behind a UI, and a
                // 4K download costs minutes and hundreds of megabytes to be
                // scaled straight back down.
                //
                // Audio is dropped by the recode - a background that starts
                // playing sound when a game is selected is a bug, not a
                // feature.
                //
                // --recode-video mp4 is what makes the result playable: yt-dlp
                // otherwise hands back WebM or VP9, neither of which
                // MediaElement will open.
                // --postprocessor-args, not --recode-video alone.
                //
                // YouTube serves DASH, so what arrives is a FRAGMENTED mp4:
                // ftyp brand "dash", then moof/mdat fragments. --recode-video
                // mp4 sees a file already called mp4 and does nothing, so those
                // fragments were kept verbatim. ffmpeg decodes them happily,
                // which is why the file looked fine - but Windows Media
                // Foundation does not, and neither do most desktop players.
                // MediaElement then reported the video as OPEN and PLAYING with
                // the position advancing, while rendering solid black.
                //
                // faststart rewrites it as a normal progressive mp4 with the
                // index at the front, which is what everything can read.
                //
                // Frame rate is deliberately NOT capped. Measured on this
                // machine, a progressive High-profile 60 fps file renders
                // perfectly - the container was the whole problem, not the
                // frame rate - and 60 fps is what makes a live wallpaper look
                // like one.
                //
                // yuv420p is not optional: H.264 in MP4 will not render through
                // MediaElement without 4:2:0 chroma, and YouTube occasionally
                // serves 4:4:4.
                Arguments =
                    "-f \"bestvideo[height<=1080][ext=mp4]/bestvideo[height<=1080]/best\" "
                    + "--recode-video mp4 "
                    + "--postprocessor-args \"VideoConvertor:-movflags +faststart -pix_fmt yuv420p\" "
                    + "--no-playlist --no-warnings --no-part "
                    + $"--ffmpeg-location \"{_ffmpegPath}\" "
                    + $"-o \"{stem}.%(ext)s\" \"{url}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            ExternalTool.PrependToolDirToPath(psi, _denoPath);

            try
            {
                using (Process process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    Task<string> readOutput = Task.Run(() => process.StandardOutput.ReadToEnd());
                    Task<string> readError = Task.Run(() => process.StandardError.ReadToEnd());

                    while (!process.WaitForExit(200))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            CleanUp(stem);
                            return false;
                        }
                    }

                    if (process.ExitCode != 0)
                    {
                        Logger.Warn(
                            "ImageRotater: yt-dlp exited " + process.ExitCode
                            + " - " + readError.Result);

                        CleanUp(stem);
                        return false;
                    }
                }

                return Finish(stem, targetPath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: YouTube download failed");
                CleanUp(stem);
                return false;
            }
        }

        // Finds what yt-dlp actually produced and moves it into place.
        private bool Finish(string stem, string targetPath)
        {
            string produced = stem + ".mp4";

            if (!File.Exists(produced))
            {
                // The recode should always give .mp4, but a format yt-dlp could
                // not recode leaves something else behind rather than nothing.
                foreach (string candidate in Directory.GetFiles(
                    Path.GetDirectoryName(stem) ?? ".",
                    Path.GetFileName(stem) + ".*"))
                {
                    produced = candidate;
                    break;
                }
            }

            if (!File.Exists(produced))
            {
                Logger.Warn("ImageRotater: yt-dlp reported success but produced no file");
                return false;
            }

            try
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(produced, targetPath);

                // Remuxed by US, not trusted to yt-dlp's postprocessor.
                //
                // --recode-video mp4 SKIPS when the download already has an
                // .mp4 extension - and the format selector above prefers
                // [ext=mp4], so that skip is the common case. YouTube serves
                // DASH, so what arrives is a fragmented mp4 that Windows
                // renders as solid black while every media player refuses it.
                // The --postprocessor-args on the recode never run either,
                // because the recode itself never runs.
                //
                // Checking the actual bytes is the only thing that cannot be
                // fooled by extensions or yt-dlp option semantics.
                if (GifConverter.IsFragmented(targetPath))
                {
                    if (GifConverter.Remux(targetPath))
                    {
                    }
                    else
                    {
                        // The file exists but will render black. Say so loudly
                        // rather than letting it look like a finished download.
                        Logger.Warn(
                            "ImageRotater: downloaded video is fragmented and could not "
                            + "be remuxed - it will show as black until repaired");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not move the download into place");
                return false;
            }
            finally
            {
                CleanUp(stem);
            }
        }

        // Removes anything left under the temporary stem - a killed yt-dlp can
        // leave fragments behind.
        private static void CleanUp(string stem)
        {
            try
            {
                string dir = Path.GetDirectoryName(stem);

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    return;
                }

                foreach (string leftover in Directory.GetFiles(
                    dir, Path.GetFileName(stem) + ".*"))
                {
                    try { File.Delete(leftover); } catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
