using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace ImageRotater.Services
{
    // Central selection policy. SelectionMode decides WHEN a new artwork is
    // needed; SelectionOrder decides HOW that artwork is chosen.
    public class ImageSelector
    {
        private readonly ImagePicker _picker;
        private readonly SessionSelectionCache _sessionCache;
        public ImageSelector(ImagePicker picker, SessionSelectionCache sessionCache)
        {
            _picker = picker;
            _sessionCache = sessionCache;
        }

        public string Select(
            Guid gameId,
            IReadOnlyList<string> candidates,
            string previousPick,
            SelectionMode mode,
            SelectionOrder order = SelectionOrder.Random)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            if (mode == SelectionMode.Fixed)
            {
                return ResolveFixed(candidates);
            }

            if (mode == SelectionMode.Daily)
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                string rememberedToday = _sessionCache?.GetDailyChoice(gameId, candidates, today);
                if (!string.IsNullOrEmpty(rememberedToday))
                {
                    return rememberedToday;
                }

                string yesterday = _sessionCache?.GetMostRecentDailyChoice(gameId, candidates);
                string dailyPick = PickByOrder(gameId, candidates, yesterday, order);
                _sessionCache?.RememberDaily(gameId, dailyPick, today);
                return dailyPick;
            }

            if (mode == SelectionMode.Session || mode == SelectionMode.Slideshow)
            {
                string remembered = _sessionCache?.GetRemembered(gameId, candidates);
                if (!string.IsNullOrEmpty(remembered))
                {
                    return remembered;
                }

                string avoid = previousPick;
                if (mode == SelectionMode.Session)
                {
                    string previousSession = _sessionCache?.GetPreviousSessionChoice(gameId, candidates);
                    if (!string.IsNullOrEmpty(previousSession))
                    {
                        avoid = previousSession;
                    }
                }

                string picked = PickByOrder(gameId, candidates, avoid, order);
                if (mode == SelectionMode.Session)
                {
                    _sessionCache?.RememberSession(gameId, picked);
                }
                else
                {
                    _sessionCache?.Remember(gameId, picked);
                }
                return picked;
            }

            return PickByOrder(gameId, candidates, previousPick, order);
        }

        public void Remember(Guid gameId, string path)
        {
            _sessionCache?.Remember(gameId, path);
        }

        private static string ResolveFixed(IReadOnlyList<string> candidates)
        {
            try
            {
                // Theme Integration can put the virtual Playnite Original at
                // index 0. Its directory is Playnite's library store, not the
                // ImageRotater candidate folder where .imagerotater-fixed lives.
                // Search candidate directories until the marker is found.
                string fixedName = null;
                var visitedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < candidates.Count; i++)
                {
                    string folder = Path.GetDirectoryName(candidates[i]);
                    if (string.IsNullOrEmpty(folder) || !visitedFolders.Add(folder))
                    {
                        continue;
                    }

                    string marker = Path.Combine(folder, ".imagerotater-fixed");
                    if (File.Exists(marker))
                    {
                        fixedName = (File.ReadAllText(marker) ?? string.Empty).Trim();
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(fixedName))
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (string.Equals(Path.GetFileName(candidates[i]), fixedName, StringComparison.OrdinalIgnoreCase))
                        {
                            return candidates[i];
                        }
                    }

                    // A Fixed choice made on Compatibility's preserved
                    // original_* maps to Theme Integration's virtual Original.
                    if (fixedName.StartsWith(GameImageStore.PreservedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidates[0];
                    }
                }
            }
            catch
            {
                // Fall back to the first ordered artwork if fixed metadata is unavailable.
            }

            return candidates[0];
        }

        private string PickByOrder(
            Guid gameId,
            IReadOnlyList<string> candidates,
            string previousPick,
            SelectionOrder order)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            switch (order)
            {
                case SelectionOrder.Sequential:
                    return NextInOrder(candidates, previousPick);
                case SelectionOrder.Shuffle:
                    return NextInShuffleCycle(gameId, candidates, previousPick);
                default:
                    return _picker != null ? _picker.Pick(candidates, previousPick) : candidates[0];
            }
        }

        private static string NextInOrder(IReadOnlyList<string> candidates, string previousPick)
        {
            if (string.IsNullOrEmpty(previousPick))
            {
                return candidates[0];
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], previousPick, StringComparison.OrdinalIgnoreCase))
                {
                    return candidates[(i + 1) % candidates.Count];
                }
            }

            return candidates[0];
        }

        // A stable per-game shuffled cycle. Every candidate appears exactly once
        // before the cycle wraps, while still looking random instead of 1-2-3-4.
        // Keeping the permutation deterministic means Session/Daily can continue
        // the cycle across Playnite restarts using only their persisted last pick.
        private static string NextInShuffleCycle(
            Guid gameId,
            IReadOnlyList<string> candidates,
            string previousPick)
        {
            List<string> cycle = candidates
                .OrderBy(path => StableHash(gameId, path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrEmpty(previousPick))
            {
                return cycle[0];
            }

            for (int i = 0; i < cycle.Count; i++)
            {
                if (string.Equals(cycle[i], previousPick, StringComparison.OrdinalIgnoreCase))
                {
                    return cycle[(i + 1) % cycle.Count];
                }
            }

            return cycle[0];
        }

        private static uint StableHash(Guid gameId, string path)
        {
            unchecked
            {
                uint hash = 2166136261;
                byte[] bytes = gameId.ToByteArray();
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash = (hash ^ bytes[i]) * 16777619;
                }

                string value = path ?? string.Empty;
                for (int i = 0; i < value.Length; i++)
                {
                    char c = char.ToUpperInvariant(value[i]);
                    hash = (hash ^ c) * 16777619;
                }

                return hash;
            }
        }
    }
}
