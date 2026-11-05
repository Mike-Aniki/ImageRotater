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

        // One file only. Past this size it is deleted and a fresh one starts.
        internal const long MaxBytes = 10L * 1024L * 1024L;

        // The size is checked every so many lines, not on every append.
        private const int SizeCheckInterval = 200;
        private int _linesSinceSizeCheck;

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

        // Appends a new session to the existing debug log so a restart does
        // not erase the previous investigation. The file is restarted only
        // when it grows beyond 10 MiB.
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
                    DeleteOldArchives();
                    RestartIfTooLarge();
                    bool hasExisting = File.Exists(_path) && new FileInfo(_path).Length > 0;
                    string prefix = hasExisting ? Environment.NewLine : string.Empty;
                    File.AppendAllText(
                        _path,
                        prefix + $"=== ImageRotater {version} - {mode} - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}",
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
            Log($"RotateLogos      = {settings.RotateLogos}");
            Log($"LogoMode         = {settings.LogoSelectionMode}");
            Log($"LogoOrder        = {settings.LogoSelectionOrder}");
            Log($"ShowUnfocusedCovers = {settings.ShowUnfocusedCovers}");
            Log($"AnimateUnfocusedCovers = {settings.AnimateUnfocusedCovers}");
            Log($"CoverVideoStart  = {settings.CoverVideoStartMode}");
            Log($"Transitions      = {settings.EnableTransitions} cover={settings.CoverTransition} background={settings.BackgroundTransition}");
            Log($"RotateBackgrounds = {settings.RotateBackgrounds}");
            Log("Renderer         = NativeInjection");
            Log($"BackgroundMode    = {settings.SelectionMode}");
            Log($"BackgroundOrder   = {settings.BackgroundSelectionOrder}");
            Log($"CoverMode         = {settings.CoverSelectionMode}");
            Log($"CoverOrder        = {settings.CoverSelectionOrder}");
            Log($"EnableCoverImage = {settings.EnableCoverImage}");
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
                    if (++_linesSinceSizeCheck >= SizeCheckInterval)
                    {
                        _linesSinceSizeCheck = 0;
                        RestartIfTooLarge();
                    }

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

        private void RestartIfTooLarge()
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaxBytes)
                {
                    File.Delete(_path);
                }
            }
            catch (Exception)
            {
                // Best-effort. Never sacrifice logging/plugin stability.
            }
        }

        // Releases before 1.6 kept every full log as ImageRotater.<stamp>.log.
        private void DeleteOldArchives()
        {
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (string archive in Directory.GetFiles(directory, "ImageRotater.*.log"))
                {
                    if (!string.Equals(Path.GetFullPath(archive), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(archive);
                    }
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup only.
            }
        }
    }
}
