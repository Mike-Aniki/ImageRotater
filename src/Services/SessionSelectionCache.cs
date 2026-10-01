using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ImageRotater.Services
{
    // Remembers choices for the current Playnite process and, for Session mode,
    // the final choice from the previous clean session. The persisted data is
    // only a tiny key/path map; image bytes are never cached here.
    public class SessionSelectionCache
    {
        private readonly object _lock = new object();
        private readonly Dictionary<Guid, string> _choices = new Dictionary<Guid, string>();
        private readonly Dictionary<Guid, string> _previousSessionChoices = new Dictionary<Guid, string>();
        private readonly Dictionary<Guid, string> _sessionChoicesToPersist = new Dictionary<Guid, string>();
        private readonly Dictionary<Guid, DailyChoice> _dailyChoices = new Dictionary<Guid, DailyChoice>();
        private readonly string _persistencePath;
        private readonly string _dailyPersistencePath;

        public SessionSelectionCache(string persistencePath = null)
        {
            _persistencePath = persistencePath;
            _dailyPersistencePath = string.IsNullOrEmpty(persistencePath)
                ? null
                : Path.Combine(Path.GetDirectoryName(persistencePath) ?? string.Empty, "daily-picks.json");
            LoadPreviousSessionChoices();
            LoadDailyChoices();
        }

        public int Count
        {
            get { lock (_lock) { return _choices.Count; } }
        }

        public int PreviousSessionCount
        {
            get { lock (_lock) { return _previousSessionChoices.Count; } }
        }

        public string GetRemembered(Guid gameId, IReadOnlyList<string> candidates)
        {
            return GetValidChoice(_choices, gameId, candidates, true);
        }

        // Returns the Session-mode choice saved by the previous clean Playnite
        // run, but only while that file is still a valid candidate. This is
        // used as the picker exclusion for the first Session choice of the new
        // run, so Playnite does not reopen on exactly the same artwork when
        // another candidate exists.
        public string GetPreviousSessionChoice(Guid gameId, IReadOnlyList<string> candidates)
        {
            return GetValidChoice(_previousSessionChoices, gameId, candidates, false);
        }


        public string GetDailyChoice(Guid gameId, IReadOnlyList<string> candidates, string dateKey)
        {
            if (string.IsNullOrEmpty(dateKey))
            {
                return null;
            }

            lock (_lock)
            {
                DailyChoice choice;
                if (!_dailyChoices.TryGetValue(gameId, out choice) || choice == null ||
                    !string.Equals(choice.Date, dateKey, StringComparison.Ordinal))
                {
                    return null;
                }

                return CandidateContains(candidates, choice.Path) ? choice.Path : null;
            }
        }

        public string GetMostRecentDailyChoice(Guid gameId, IReadOnlyList<string> candidates)
        {
            lock (_lock)
            {
                DailyChoice choice;
                if (!_dailyChoices.TryGetValue(gameId, out choice) || choice == null)
                {
                    return null;
                }

                return CandidateContains(candidates, choice.Path) ? choice.Path : null;
            }
        }

        public void RememberDaily(Guid gameId, string path, string dateKey)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dateKey))
            {
                return;
            }

            lock (_lock)
            {
                _dailyChoices[gameId] = new DailyChoice { Date = dateKey, Path = path };
            }
        }

        public int DailyChoiceCount
        {
            get { lock (_lock) { return _dailyChoices.Count; } }
        }

        public void Remember(Guid gameId, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            lock (_lock)
            {
                _choices[gameId] = path;
            }
        }

        // Session choices are tracked separately from Slideshow's remembered
        // current frame. Only these entries are persisted for the next launch.
        public void RememberSession(Guid gameId, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            lock (_lock)
            {
                _choices[gameId] = path;
                _sessionChoicesToPersist[gameId] = path;
            }
        }

        public void Forget(Guid gameId)
        {
            lock (_lock)
            {
                _choices.Remove(gameId);
                _sessionChoicesToPersist.Remove(gameId);
            }
        }

        // Clears only this process' remembered picks. The previous-session map
        // intentionally survives settings/artwork refreshes for the rest of
        // the run so a fresh Session roll can still avoid last launch's image.
        public void Clear()
        {
            lock (_lock)
            {
                _choices.Clear();
                _sessionChoicesToPersist.Clear();
            }
        }

        // One small disk write at clean shutdown, rather than writing while the
        // user navigates. If Playnite crashes, the last successfully saved map
        // simply remains in place.
        public int SaveSessionChoices()
        {
            if (string.IsNullOrEmpty(_persistencePath))
            {
                return 0;
            }

            Dictionary<string, string> snapshot = new Dictionary<string, string>();
            lock (_lock)
            {
                foreach (KeyValuePair<Guid, string> pair in _sessionChoicesToPersist)
                {
                    if (!string.IsNullOrEmpty(pair.Value))
                    {
                        snapshot[pair.Key.ToString("D")] = pair.Value;
                    }
                }
            }

            try
            {
                string folder = Path.GetDirectoryName(_persistencePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string temp = _persistencePath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot));

                if (File.Exists(_persistencePath))
                {
                    File.Delete(_persistencePath);
                }

                File.Move(temp, _persistencePath);
                return snapshot.Count;
            }
            catch
            {
                // Persistence is a cosmetic convenience only. Never let a
                // failed cache write interfere with Playnite shutdown.
                return 0;
            }
        }


        public int SaveDailyChoices()
        {
            if (string.IsNullOrEmpty(_dailyPersistencePath))
            {
                return 0;
            }

            Dictionary<string, DailyChoice> snapshot = new Dictionary<string, DailyChoice>();
            lock (_lock)
            {
                foreach (KeyValuePair<Guid, DailyChoice> pair in _dailyChoices)
                {
                    if (pair.Value != null && !string.IsNullOrEmpty(pair.Value.Path))
                    {
                        snapshot[pair.Key.ToString("D")] = new DailyChoice
                        {
                            Date = pair.Value.Date,
                            Path = pair.Value.Path
                        };
                    }
                }
            }

            try
            {
                string folder = Path.GetDirectoryName(_dailyPersistencePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string temp = _dailyPersistencePath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot));
                if (File.Exists(_dailyPersistencePath))
                {
                    File.Delete(_dailyPersistencePath);
                }
                File.Move(temp, _dailyPersistencePath);
                return snapshot.Count;
            }
            catch
            {
                return 0;
            }
        }

        private void LoadDailyChoices()
        {
            if (string.IsNullOrEmpty(_dailyPersistencePath) || !File.Exists(_dailyPersistencePath))
            {
                return;
            }

            try
            {
                Dictionary<string, DailyChoice> saved = JsonConvert.DeserializeObject<Dictionary<string, DailyChoice>>(
                    File.ReadAllText(_dailyPersistencePath));
                if (saved == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, DailyChoice> pair in saved)
                {
                    Guid key;
                    if (Guid.TryParse(pair.Key, out key) && pair.Value != null &&
                        !string.IsNullOrEmpty(pair.Value.Path))
                    {
                        _dailyChoices[key] = pair.Value;
                    }
                }
            }
            catch
            {
                _dailyChoices.Clear();
            }
        }

        private static bool CandidateContains(IReadOnlyList<string> candidates, string path)
        {
            if (candidates == null || candidates.Count == 0 || string.IsNullOrEmpty(path))
            {
                return false;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class DailyChoice
        {
            public string Date { get; set; }
            public string Path { get; set; }
        }

        private string GetValidChoice(
            Dictionary<Guid, string> source,
            Guid gameId,
            IReadOnlyList<string> candidates,
            bool removeWhenInvalid)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            string remembered;
            lock (_lock)
            {
                if (!source.TryGetValue(gameId, out remembered))
                {
                    return null;
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], remembered, StringComparison.OrdinalIgnoreCase))
                {
                    return remembered;
                }
            }

            if (removeWhenInvalid)
            {
                lock (_lock)
                {
                    source.Remove(gameId);
                }
            }

            return null;
        }

        private void LoadPreviousSessionChoices()
        {
            if (string.IsNullOrEmpty(_persistencePath) || !File.Exists(_persistencePath))
            {
                return;
            }

            try
            {
                Dictionary<string, string> saved = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(_persistencePath));

                if (saved == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, string> pair in saved)
                {
                    Guid key;
                    if (Guid.TryParse(pair.Key, out key) && !string.IsNullOrEmpty(pair.Value))
                    {
                        _previousSessionChoices[key] = pair.Value;
                    }
                }
            }
            catch
            {
                // A missing/corrupt cosmetic cache should behave like no
                // previous session data at all.
                _previousSessionChoices.Clear();
            }
        }
    }
}
