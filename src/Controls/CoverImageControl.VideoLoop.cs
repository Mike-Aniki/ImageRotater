using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ImageRotater.Controls
{
    public partial class CoverImageControl
    {
        // Cover videos loop the same way as background videos: a seek back to
        // zero shortly BEFORE the end, while the player is still running, so
        // the pipeline never reaches end-of-stream and drops its surface.
        // No still frame is laid over the seek: a cached poster taken
        // at 1 s showed as a visible freeze and a jump on every loop.
        // MediaEnded remains the fallback (late tick, short clip, a tile that
        // was paused or off-screen near the end).
        //
        // The Details cover needs more. After a seek, and also after Play()
        // on a parked player, WPF's player presents nothing for 50-170 ms
        // (measured on a 60 fps capture). A theme effect above the cover
        // (KNARZnite's DropShadowEffect on CoverDockPanel) makes WPF
        // re-render the subtree into an intermediate surface, and a video
        // background makes that region dirty every frame, so the empty
        // player is drawn as a black rectangle. The grid tile and the
        // background have no such effect and never showed it.
        //
        // So the Details cover never shows a player that has just been
        // seeked or started. Two players share the same cell and Stretch
        // (DisplayVideo on top, LoopStandbyVideo directly below) and take
        // turns: StandbyStartLead before the loop point the hidden one starts
        // from zero; at the loop point it has been presenting frames for a
        // while and becomes the visible one, and the other is parked on zero,
        // hidden. DisplayVideo is hidden by an empty Clip, the standby by
        // Collapsed. The cost is that each loop starts StandbyStartLead into
        // the clip instead of at frame 0.
        private DispatcherTimer _videoLoopTimer;
        private int _videoLoopGeneration;
        private readonly bool _desktopCoverLoop;
        private Window _loopWindow;

        internal static readonly TimeSpan StandbyStartLead = TimeSpan.FromMilliseconds(320);
        private static readonly TimeSpan StandbyCheckSlack = TimeSpan.FromMilliseconds(30);

        private bool _standbyOpened;
        // LoopStandbyVideo is the visible player; DisplayVideo is clipped away.
        private bool _standbyFront;
        // The hidden player has been started for the coming loop point.
        private bool _standbyStarting;

        private MediaElement FrontVideo => _standbyFront ? LoopStandbyVideo : DisplayVideo;
        private MediaElement BackVideo => _standbyFront ? DisplayVideo : LoopStandbyVideo;

        private void TraceCoverVideoLoop(string reason)
        {
            if (_fileLogger?.IsEnabled != true) return;
            string duration = DisplayVideo.NaturalDuration.HasTimeSpan
                ? DisplayVideo.NaturalDuration.TimeSpan.TotalMilliseconds.ToString("0")
                : "unknown";
            _fileLogger.Log($"COVER VIDEO LOOP reason={reason} control={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):x} game={GameContext?.Id} details={_nativeDetailsRegion} " +
                $"generation={_videoGeneration} durationMs={duration} positionMs={DisplayVideo.Position.TotalMilliseconds:0} " +
                $"playing={_videoPlaying} pausedByViewport={_pausedByViewport} file={System.IO.Path.GetFileName(DisplayVideo.Source?.LocalPath)} " +
                $"videoOpacity={DisplayVideo.Opacity:0.###} videoMasked={DisplayVideo.OpacityMask != null} videoClipped={DisplayVideo.Clip != null} " +
                $"standby={LoopStandbyVideo.Visibility}/opened={_standbyOpened}/front={_standbyFront}/starting={_standbyStarting}/positionMs={LoopStandbyVideo.Position.TotalMilliseconds:0} " +
                $"still={DescribeCoverLayer(DisplayImage)} previous={DescribeCoverLayer(PreviousImage)} " +
                $"unfocused={DescribeCoverLayer(UnfocusedStillImage)}");
        }

        private static string DescribeCoverLayer(Image image) =>
            $"{image.Visibility}/{image.Opacity:0.###}/{image.Source?.Width:0.##}x{image.Source?.Height:0.##}";

        // Only the injected Details cover uses the standby. Grid tiles,
        // Fullscreen and theme-integration covers keep the plain seek.
        private bool UsesLoopStandby => _loopWindow?.IsActive == true &&
            _loopWindow.WindowState != WindowState.Minimized && DisplayVideo.Source != null;

        private void OnLoopWindowActivityChanged(object sender, EventArgs e)
        {
            if (!UsesLoopStandby)
            {
                ReleaseLoopStandby();
                return;
            }

            if (_videoPlaying && IsActuallyInViewport()) OpenLoopStandby();
            if (_videoLoopTimer != null) _videoLoopTimer.Interval = StandbyCheckSlack;
        }

        // The standby may only be shown under DisplayVideo while DisplayVideo
        // is otherwise fully opaque: no reveal, fade or mask in progress.
        // DisplayVideo's own Clip is ours and is not part of the check.
        private bool DisplayVideoIsSettled =>
            DisplayVideo.Visibility == Visibility.Visible &&
            DisplayVideo.Opacity >= .999 &&
            DisplayVideo.OpacityMask == null;

        private void ScheduleCoverVideoLoop()
        {
            StopCoverVideoLoop();
            if (DisplayVideo.Source == null) return;
            if (!DisplayVideo.NaturalDuration.HasTimeSpan ||
                DisplayVideo.NaturalDuration.TimeSpan < BackgroundImageControl.MinimumPreEndLoopDuration)
            {
                TraceCoverVideoLoop("pre-end-unavailable");
                return;
            }

            if (_desktopCoverLoop && _nativeInjected && _nativeDetailsRegion)
            {
                _loopWindow = Window.GetWindow(this);
                if (_loopWindow != null)
                {
                    _loopWindow.Activated += OnLoopWindowActivityChanged;
                    _loopWindow.Deactivated += OnLoopWindowActivityChanged;
                    _loopWindow.StateChanged += OnLoopWindowActivityChanged;
                }
                if (UsesLoopStandby) OpenLoopStandby();
            }

            int loopGeneration = _videoLoopGeneration;
            int videoGeneration = _videoGeneration;
            var timer = new DispatcherTimer(DispatcherPriority.Normal);
            _videoLoopTimer = timer;
            timer.Tick += (sender, args) =>
            {
                if (!ReferenceEquals(_videoLoopTimer, timer) || loopGeneration != _videoLoopGeneration ||
                    videoGeneration != _videoGeneration || DisplayVideo.Source == null || !IsLoaded)
                {
                    if (ReferenceEquals(_videoLoopTimer, timer)) StopCoverVideoLoop();
                    else StopCoverVideoLoopTimer(timer);
                    return;
                }

                TimeSpan duration = DisplayVideo.NaturalDuration.TimeSpan;
                TimeSpan? wait = BackgroundImageControl.LoopRestartDelay(duration, FrontVideo.Position);

                if (wait.HasValue && UsesLoopStandby)
                {
                    if (wait.Value > StandbyStartLead + StandbyCheckSlack)
                    {
                        timer.Interval = wait.Value - StandbyStartLead;
                        return;
                    }
                    if (!_standbyStarting && _videoPlaying && IsActuallyInViewport()) StartBackVideo();
                }

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
                    TraceCoverVideoLoop(_videoPlaying ? "pre-end-offscreen" : "pre-end-not-playing");
                    HideLoopStandby();
                    StopCoverVideoLoopTimer(timer);
                    return;
                }

                try
                {
                    if (_standbyStarting)
                    {
                        SwapLoopVideos();
                    }
                    else
                    {
                        TraceCoverVideoLoop("pre-end");
                        HideLoopStandby();
                        DisplayVideo.Position = TimeSpan.Zero;
                        DisplayVideo.Play();
                    }
                }
                catch (Exception ex)
                {
                    HideLoopStandby();
                    Logger.Warn(ex, "ImageRotater: could not loop cover video");
                }

                TimeSpan? next = BackgroundImageControl.LoopRestartDelay(duration, FrontVideo.Position);
                timer.Interval = next.HasValue && next.Value > StandbyStartLead + StandbyCheckSlack
                    ? next.Value - (UsesLoopStandby ? StandbyStartLead : TimeSpan.Zero)
                    : StandbyCheckSlack;
            };
            timer.Interval = StandbyCheckSlack;
            timer.Start();
        }

        // Opens the standby on the same file, hidden and parked on zero.
        private void OpenLoopStandby()
        {
            if (LoopStandbyVideo.Source != null && LoopStandbyVideo.Source == DisplayVideo.Source) return;
            try
            {
                _standbyOpened = false;
                LoopStandbyVideo.Visibility = Visibility.Collapsed;
                LoopStandbyVideo.Source = DisplayVideo.Source;
                // Pause opens the media without playing; MediaOpened parks it on 0.
                LoopStandbyVideo.Pause();
                TraceCoverVideoLoop("standby-opening");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not open cover loop standby");
                ReleaseLoopStandby();
            }
        }

        private void LoopStandbyVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (LoopStandbyVideo.Source == null) return;
            try
            {
                LoopStandbyVideo.Pause();
                LoopStandbyVideo.Position = TimeSpan.Zero;
                _standbyOpened = true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not park cover loop standby");
                ReleaseLoopStandby();
                return;
            }
            TraceCoverVideoLoop("standby-opened");
        }

        private void LoopStandbyVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            TraceCoverVideoLoop("standby-failed");
            ReleaseLoopStandby();
        }

        // Only reached when a tick came too late and the visible standby ran
        // to the end: hand over if the other player is already running.
        private void LoopStandbyVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (!_standbyFront) return;
            TraceCoverVideoLoop("standby-media-ended");
            try
            {
                if (_standbyStarting) SwapLoopVideos();
                else
                {
                    LoopStandbyVideo.Position = TimeSpan.Zero;
                    LoopStandbyVideo.Play();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not loop cover loop standby");
                HideLoopStandby();
            }
        }

        // Starts the hidden player from zero, StandbyStartLead before the
        // loop point, so it is already presenting frames when it is shown.
        private void StartBackVideo()
        {
            if (!_standbyOpened || !DisplayVideoIsSettled || LoopStandbyVideo.Source != DisplayVideo.Source)
            {
                TraceCoverVideoLoop("standby-not-ready");
                return;
            }

            try
            {
                MediaElement back = BackVideo;
                if (ReferenceEquals(back, LoopStandbyVideo)) LoopStandbyVideo.Visibility = Visibility.Visible;
                back.Position = TimeSpan.Zero;
                back.Play();
                _standbyStarting = true;
                TraceCoverVideoLoop(ReferenceEquals(back, LoopStandbyVideo) ? "standby-started" : "display-started-hidden");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not start cover loop standby");
                HideLoopStandby();
            }
        }

        // The hidden player becomes the visible one in a single dispatcher
        // turn; the previous one is parked on zero while hidden.
        private void SwapLoopVideos()
        {
            MediaElement leaving = FrontVideo;
            if (_standbyFront)
            {
                DisplayVideo.Clip = null;
                _standbyFront = false;
            }
            else
            {
                DisplayVideo.Clip = Geometry.Empty;
                _standbyFront = true;
            }
            _standbyStarting = false;

            leaving.Pause();
            if (ReferenceEquals(leaving, LoopStandbyVideo)) LoopStandbyVideo.Visibility = Visibility.Collapsed;
            leaving.Position = TimeSpan.Zero;
            TraceCoverVideoLoop(_standbyFront ? "pre-end-standby-front" : "pre-end-display-front");
        }

        // Puts DisplayVideo back in front and parks the standby, hidden, on
        // zero. The standby's source stays open for the next loop.
        //
        // When the standby was in front, DisplayVideo has to take over at the
        // standby's position, which is a visible seek. That only happens when
        // the loop is abandoned (stop, fade, new video, off-screen).
        private void HideLoopStandby()
        {
            bool standbyWasFront = _standbyFront;
            _standbyFront = false;
            _standbyStarting = false;

            try
            {
                if (standbyWasFront && LoopStandbyVideo.Source != null && DisplayVideo.Source != null)
                {
                    DisplayVideo.Position = LoopStandbyVideo.Position;
                    if (_videoPlaying) DisplayVideo.Play();
                    else DisplayVideo.Pause();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not restore cover video from loop standby");
            }

            if (DisplayVideo.Clip != null) DisplayVideo.Clip = null;
            if (LoopStandbyVideo.Source == null) return;
            try
            {
                LoopStandbyVideo.Pause();
                LoopStandbyVideo.Visibility = Visibility.Collapsed;
                if (_standbyOpened) LoopStandbyVideo.Position = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not park cover loop standby");
            }
        }

        // The video itself is going away: close the standby's media too.
        private void ReleaseLoopStandby()
        {
            HideLoopStandby();
            _standbyOpened = false;
            if (LoopStandbyVideo.Source == null) return;
            try
            {
                LoopStandbyVideo.Stop();
                LoopStandbyVideo.Source = null;
                LoopStandbyVideo.Close();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not close cover loop standby");
            }
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
            if (_loopWindow != null)
            {
                _loopWindow.Activated -= OnLoopWindowActivityChanged;
                _loopWindow.Deactivated -= OnLoopWindowActivityChanged;
                _loopWindow.StateChanged -= OnLoopWindowActivityChanged;
                _loopWindow = null;
            }
            HideLoopStandby();
        }
    }
}
