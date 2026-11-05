using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageRotater.Services
{
    /// <summary>
    /// Guards Mosaic's asynchronous image handoff. An older IsAsync binding
    /// completion can report a BitmapImage after a newer path was requested.
    /// Keep the departing still intact until the current bitmap arrives;
    /// otherwise Mosaic can reveal a third cover for several frames.
    /// </summary>
    internal static class MosaicSourceGuard
    {
        internal static bool MatchesRequestedPath(ImageSource source, string requestedPath)
        {
            if (source == null || string.IsNullOrWhiteSpace(requestedPath)) return false;
            // Dynamic GIF frames, in-memory stills and drawings do not always
            // carry a reliable file URI. Never reject those on a guess.
            if (!(source is BitmapImage bitmap) || bitmap.UriSource == null)
                return true;

            Uri actualUri = bitmap.UriSource;
            if (!actualUri.IsAbsoluteUri || !actualUri.IsFile)
                return true;

            try
            {
                string expectedPath = Path.GetFullPath(requestedPath);
                string actualPath = Path.GetFullPath(actualUri.LocalPath);
                return string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Unusual URI/malformed path: don't block rendering forever.
                return true;
            }
        }
    }
}
