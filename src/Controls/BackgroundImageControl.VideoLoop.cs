using System;
using System.Windows.Threading;

namespace ImageRotater.Controls
{
    public partial class BackgroundImageControl
    {
        // Background videos loop by seeking back to zero shortly BEFORE the
        // end, while the player is still running. Waiting for MediaEnded
        // means the pipeline has already reached end-of-stream and may drop
        // its video surface; the seek then happens over an empty surface,
        // which themes compositing logos above the background showed as a
        // flash on every loop. MediaEnded remains as the fallback when a
        // tick lands too late or the duration is unknown.
        internal static readonly TimeSpan LoopLead = TimeSpan.FromMilliseconds(150);

        // Videos this short loop through MediaEnded only: a lead would cut
        // a noticeable share of them.
        internal static readonly TimeSpan MinimumPreEndLoopDuration = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan MinimumLoopCheck = TimeSpan.FromMilliseconds(30);

        // Null means "restart now"; otherwise how long to wait before checking again.
        internal static TimeSpan? LoopRestartDelay(TimeSpan duration, TimeSpan position)
        {
            TimeSpan wait = duration - position - LoopLead;
            if (wait <= MinimumLoopCheck) return null;
            return wait;
        }

        private void ScheduleBackgroundVideoLoop(RenderSlot slot)
        {
            if (slot == null) return;
            StopBackgroundVideoLoop(slot);
            if (slot.Kind != SlotKind.Video || slot.Video.Source == null ||
                !slot.Video.NaturalDuration.HasTimeSpan ||
                slot.Video.NaturalDuration.TimeSpan < MinimumPreEndLoopDuration)
                return;

            int generation = slot.LoopGeneration;
            string path = slot.Path;
            var timer = new DispatcherTimer(DispatcherPriority.Normal);
            slot.LoopTimer = timer;
            timer.Tick += (sender, args) =>
            {
                if (!ReferenceEquals(slot.LoopTimer, timer) || generation != slot.LoopGeneration ||
                    slot.Kind != SlotKind.Video || slot.Video.Source == null || !IsLoaded ||
                    !string.Equals(path, slot.Path, StringComparison.OrdinalIgnoreCase))
                {
                    timer.Stop();
                    if (ReferenceEquals(slot.LoopTimer, timer)) slot.LoopTimer = null;
                    return;
                }

                TimeSpan duration = slot.Video.NaturalDuration.TimeSpan;
                TimeSpan position = slot.Video.Position;
                TimeSpan? wait = LoopRestartDelay(duration, position);
                if (wait.HasValue)
                {
                    // Also covers a paused player: its position stays put and
                    // the check simply comes back later.
                    timer.Interval = wait.Value;
                    return;
                }

                RestartBackgroundVideo(slot, "pre-end", position);
                timer.Interval = LoopRestartDelay(duration, TimeSpan.Zero) ?? duration;
            };
            timer.Interval = LoopRestartDelay(slot.Video.NaturalDuration.TimeSpan, slot.Video.Position)
                ?? MinimumLoopCheck;
            timer.Start();
        }

        private void RestartBackgroundVideo(RenderSlot slot, string reason, TimeSpan position)
        {
            if (_fileLogger?.IsEnabled == true)
            {
                string duration = slot.Video.NaturalDuration.HasTimeSpan
                    ? slot.Video.NaturalDuration.TimeSpan.TotalMilliseconds.ToString("0")
                    : "unknown";
                _fileLogger.Log($"BG VIDEO LOOP reason={reason} game={slot.GameId} slot={slot.Name} " +
                    $"durationMs={duration} positionMs={position.TotalMilliseconds:0} path={slot.Path}");
            }

            try
            {
                slot.Video.Position = TimeSpan.Zero;
                slot.Video.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not loop background video");
            }
        }

        private static void StopBackgroundVideoLoop(RenderSlot slot)
        {
            if (slot == null) return;
            ++slot.LoopGeneration;
            slot.LoopTimer?.Stop();
            slot.LoopTimer = null;
        }
    }
}
