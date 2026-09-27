using System;
using System.Collections.Generic;

namespace ImageRotater.Services
{
    // Checks the settings a user can get wrong, in one place.
    //
    // Split from the view model for the same reason UniPlaySong splits it: the
    // rules are pure functions over a settings object, so they are testable
    // without a Playnite instance, a dialog, or a UI thread. The view model
    // only decides what to do with the answers.
    //
    // Deliberately not everything. Checkboxes, enums and the slider-backed
    // numbers cannot hold an invalid value, and ImagesRoot is written by the
    // plugin at startup rather than typed. Validating those would be error
    // messages nobody can trigger.
    public static class SettingsValidator
    {
        // A SteamGridDB key is a 32-character hex string. Checking the shape
        // catches the common paste accident - a truncated copy, or the whole
        // "Bearer xyz" header line - without a network round trip.
        //
        // Shape only. Whether the key is live, revoked or rate-limited is a
        // question for the server, and the search dialog already reports what
        // it gets back.
        public const int ApiKeyLength = 32;

        // Longest slideshow interval offered: an hour. Above this the setting
        // is indistinguishable from "off" and is nearly always a typo - a user
        // meaning 30 seconds who typed 30000.
        public const int MaxSlideshowSeconds = 3600;

        public static List<string> Validate(ImageRotaterSettings settings)
        {
            var errors = new List<string>();

            if (settings == null)
            {
                errors.Add(Loc.Get("LOCImageRotaterSettingsUnreadable"));
                return errors;
            }

            // The API key is NOT checked here either. A malformed key means
            // SteamGridDB search fails and says so; the status line under the
            // box already flags the shape as you type. Blocking save on it
            // would trap a user who pasted badly and wants to fix it later.

            // Tool paths are deliberately NOT checked here.
            //
            // Save blocks on whatever this returns, and a wrong ffmpeg path is
            // not worth trapping a user in the settings window over - the
            // status line under the box already says it does not work, and the
            // only consequence is that GIF conversion stays unavailable. This
            // matches what UniPlaySong and PlayGif do.
            //
            // Probing here would also mean shelling out to two binaries on the
            // UI thread every time Save is pressed.

            ValidateInterval(
                settings.BackgroundSlideshowSeconds,
                Loc.Get("LOCImageRotaterBackgroundInterval"),
                errors);

            ValidateInterval(
                settings.CoverSlideshowSeconds,
                Loc.Get("LOCImageRotaterCoverInterval"),
                errors);

            return errors;
        }

        // The API key check on its own, for the live status line beside the
        // box. Returns null when the key is fine (or absent).
        public static string CheckApiKey(string key)
        {
            var errors = new List<string>();
            ValidateApiKey(key, errors);

            return errors.Count > 0 ? errors[0] : null;
        }

        private static void ValidateApiKey(string key, List<string> errors)
        {
            // Empty is valid and normal: SteamGridDB search is one source of
            // several, and the plugin works without it.
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            string trimmed = key.Trim();

            if (trimmed.Length != ApiKeyLength)
            {
                errors.Add(Loc.Format(
                    "LOCImageRotaterApiKeyLength", ApiKeyLength, trimmed.Length));
                return;
            }

            foreach (char c in trimmed)
            {
                if (!Uri.IsHexDigit(c))
                {
                    errors.Add(Loc.Get("LOCImageRotaterApiKeyChars"));
                    return;
                }
            }
        }

        private static void ValidateInterval(int seconds, string label, List<string> errors)
        {
            if (seconds < 0)
            {
                errors.Add(Loc.Format("LOCImageRotaterIntervalNegative", label));
                return;
            }

            // An absurdly LARGE interval is not blocked: it is recoverable by
            // editing the box, and behaves as "off" until then. Only a negative
            // value is refused, because there is no sensible reading of it.
        }
    }
}
