using System;
using System.IO;
using System.Text;

namespace ImageRotater.Services
{
    // Writes ImageRotater.log next to the plugin's own data.
    //
    // Playnite's shared extension.log interleaves every plugin's output, so
    // tracing one rotation there means reading past hundreds of unrelated
    // lines. This file carries only this plugin's activity, which is the
    // difference between reading a log and searching one.
    //
    // Gated behind the debug-logging setting, like the rest of the plugin's
    // diagnostics, so it stays absent in normal use.
    public class FileLogger
    {
        private readonly string _path;
        private readonly object _lock = new object();

        // The logger owns the on/off decision, so callers never repeat it.
        //
        // Previously every call site carried its own
        // "if (settings != null && settings.EnableDebugLogging)", which is the
        // kind of check that gets forgotten at exactly the site you later need.
        // An accessor rather than a value because saving settings replaces the
        // whole object.
        private readonly Func<bool> _enabled;

        // Rotation writes from the UI thread and image loads report from the
        // thread pool, so appends have to be serialised.
        public FileLogger(string pluginUserDataPath, Func<bool> enabled = null)
        {
            _path = Path.Combine(pluginUserDataPath ?? string.Empty, "ImageRotater.log");
            _enabled = enabled ?? (() => false);
        }

        public bool IsEnabled
        {
            get
            {
                try
                {
                    return _enabled();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public string Path_ => _path;

        // Starts a fresh file per session. A log that grows across every
        // restart buries the run being investigated.
        public void StartSession(string version, string mode, ImageRotaterSettings settings = null)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                lock (_lock)
                {
                    File.WriteAllText(
                        _path,
                        $"=== ImageRotater {version} - {mode} - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never take the plugin down with it.
                return;
            }

            WriteHeader(settings);
        }

        // The values a theme binds, written once per session.
        //
        // Kept here rather than at the call site so a log on its own shows
        // whether the theme side can work, without also needing the settings
        // file - and so the plugin entry point is not formatting diagnostics.
        private void WriteHeader(ImageRotaterSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            Log($"ImagesRoot       = {settings.ImagesRoot}");
            Log($"RotateCovers     = {settings.RotateCovers}");
            Log($"RotateBackgrounds = {settings.RotateBackgrounds}");
            Log($"BackgroundMode    = {settings.SelectionMode}");
            Log($"Letterbox         = {settings.LetterboxBackgrounds}");
            Log($"Normalise         = {settings.NormaliseBackgroundSize}");
            Log($"EnableCoverImage = {settings.EnableCoverImage}");
            Log($"Theme binds: {{ImagesRoot}}\\{{game id}}\\covers{GameImageStore.PublishedFolderSuffix}\\{GameImageStore.PublishedFileName}");
            Log($"Log file: {_path}");
        }

        public void Log(string message)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                lock (_lock)
                {
                    File.AppendAllText(
                        _path,
                        $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
