using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    public partial class CoverImageControl
    {
        // One extraction at a time across visible tiles, sharing the existing disk cache.
        private static readonly SemaphoreSlim StaticPosterGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _staticPosterCancellation;
        private string _staticPosterRequest;

        private void CancelStaticVideoPoster()
        {
            _staticPosterCancellation?.Cancel();
            _staticPosterCancellation?.Dispose();
            _staticPosterCancellation = null;
            _staticPosterRequest = null;
        }

        private async void ShowStaticVideoPoster(string path, string fallback)
        {
            if (string.Equals(path, _staticPosterRequest, StringComparison.OrdinalIgnoreCase)) return;
            CancelStaticVideoPoster();
            var cancellation = _staticPosterCancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            var gameId = GameContext.Id;
            _staticPosterRequest = path;
            // Do not tear down the visible IR video when its tile loses
            // selection: ShowNothing() asks NativeCoverHost to reveal Playnite's
            // native cover for the entire FFmpeg/poster decode interval. That
            // is the native-artwork flash seen on every selection change.
            // Keep the last displayed frame paused until the poster is ready.
            // The image handoff already fades the paused video out once the
            // new still is decoded (no native image appears in between).
            bool wasShowingThisVideo = DisplayVideo.Visibility == System.Windows.Visibility.Visible &&
                DisplayVideo.Source != null &&
                string.Equals(_videoPath, path, StringComparison.OrdinalIgnoreCase);
            if (wasShowingThisVideo)
            {
                try { DisplayVideo.Pause(); _videoPlaying = false; }
                catch (Exception ex) { Logger.Warn(ex, "ImageRotater: could not pause deselected cover video"); }
            }
            if (_fileLogger?.IsEnabled == true)
                _fileLogger.Log($"COVER deselected-video game={gameId} preserve-frame={wasShowingThisVideo}");
            try
            {
                string poster = await Task.Run(async () =>
                {
                    await StaticPosterGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        return PosterFrame.ForVideo(path);
                    }
                    finally { StaticPosterGate.Release(); }
                }).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    var settings = _settings?.Invoke();
                    if (token.IsCancellationRequested || _nativeReleased || GameContext?.Id != gameId ||
                        !ReferenceEquals(cancellation, _staticPosterCancellation) || IsSelectedTile ||
                        settings?.ShowUnfocusedCovers != true || !settings.EnableRotation ||
                        !settings.IsArtworkRotationEnabled(gameId, Models.ArtworkKind.Cover) ||
                        GameContext.IsRunning || settings.ShouldAnimateCover(false)) return;
                    if (_fileLogger?.IsEnabled == true)
                        _fileLogger.Log($"COVER poster-ready game={gameId} poster={IsUsable(poster)} keep-video={wasShowingThisVideo && !IsUsable(poster)}");
                    if (IsUsable(poster))
                    {
                        PresentCover(poster);
                    }
                    else if (!wasShowingThisVideo && IsUsable(fallback))
                    {
                        // No previous IR video to preserve (initial/unrealised
                        // tile): show another static IR artwork if available.
                        PresentCover(fallback);
                    }
                    // If FFmpeg is missing while a video was already playing,
                    // retain its paused last frame rather than replacing the
                    // selected artwork with another cover or the native tile.
                }, DispatcherPriority.Loaded);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not load static cover video poster");
            }
        }
    }
}
