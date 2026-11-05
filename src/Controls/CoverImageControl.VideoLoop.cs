using System;
using System.Windows.Threading;

namespace ImageRotater.Controls
{
    public partial class CoverImageControl
    {
        // Cover videos loop the same way as background videos: a seek back to
        // zero shortly BEFORE the end, while the player is still running, so
        // the pipeline never reaches end-of-stream and drops its surface.
        // No still frame is laid over the seek any more: a cached poster taken
        // at 1 s showed as a visible freeze and a jump on every loop.
        // MediaEnded remains the fallback (late tick, short clip, a tile that
        // was paused or off-screen near the end).
        private DispatcherTimer _videoLoopTimer;
        private int _videoLoopGeneration;

        private void ScheduleCoverVideoLoop()
        {
            StopCoverVideoLoop();
            if (DisplayVideo.Source == null || !DisplayVideo.NaturalDuration.HasTimeSpan ||
                DisplayVideo.NaturalDuration.TimeSpan < BackgroundImageControl.MinimumPreEndLoopDuration)
                return;

            int loopGeneration = _videoLoopGeneration;
            int videoGeneration = _videoGeneration;
            var timer = new DispatcherTimer(DispatcherPriority.Normal);
            _videoLoopTimer = timer;
            timer.Tick += (sender, args) =>
            {
                if (!ReferenceEquals(_videoLoopTimer, timer) || loopGeneration != _videoLoopGeneration ||
                    videoGeneration != _videoGeneration || DisplayVideo.Source == null || !IsLoaded)
                {
                    StopCoverVideoLoopTimer(timer);
                    return;
                }

                TimeSpan duration = DisplayVideo.NaturalDuration.TimeSpan;
                TimeSpan? wait = BackgroundImageControl.LoopRestartDelay(duration, DisplayVideo.Position);
                if (wait.HasValue)
                {
                    // A paused tile keeps its position; the check simply comes back later.
                    timer.Interval = wait.Value;
                    return;
                }

                // A paused or off-screen tile is left to MediaEnded and the
                // viewport logic, which reschedule this timer when it loops.
                if (!_videoPlaying || !IsActuallyInViewport())
                {
                    StopCoverVideoLoopTimer(timer);
                    return;
                }

                try
                {
                    DisplayVideo.Position = TimeSpan.Zero;
                    DisplayVideo.Play();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ImageRotater: could not loop cover video");
                }
                timer.Interval = BackgroundImageControl.LoopRestartDelay(duration, TimeSpan.Zero) ?? duration;
            };
            timer.Interval = BackgroundImageControl.LoopRestartDelay(
                DisplayVideo.NaturalDuration.TimeSpan, DisplayVideo.Position) ?? TimeSpan.FromMilliseconds(30);
            timer.Start();
        }

        private void StopCoverVideoLoopTimer(DispatcherTimer timer)
        {
            timer.Stop();
            if (ReferenceEquals(_videoLoopTimer, timer)) _videoLoopTimer = null;
        }

        private void StopCoverVideoLoop()
        {
            ++_videoLoopGeneration;
            _videoLoopTimer?.Stop();
            _videoLoopTimer = null;
        }
    }
}
