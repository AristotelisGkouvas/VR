using System;
using System.Globalization;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    public enum WorkModeKind { None = 0, Field = 1, Office = 2 }

    /// <summary>
    /// Where the headset is being used, asked at start (WorkModeChooser): <b>Field</b> — on the
    /// structure: the alignment is required, the model stands on the real building with the
    /// heatmap over it, and the thesis measurement tools (accuracy check, anchor field test)
    /// can run; <b>Office</b> — no alignment is asked for, the model is a tabletop miniature or
    /// a life-size copy, and the measurement tools are locked so office touches can never be
    /// logged as site data (that happened, 2026-11-03).
    ///
    /// Not asked again when the app was in use less than <see cref="RememberMinutes"/> ago:
    /// taking the headset off for a coffee keeps the mode. The last-use time is written while
    /// the app runs and when it pauses.
    /// </summary>
    public static class WorkMode
    {
        public const double RememberMinutes = 30;
        private const string ModeKey = "work.mode";
        private const string LastUseKey = "work.lastUseUtc";

        public static WorkModeKind Current { get; private set; }
        public static bool IsField => Current == WorkModeKind.Field;
        public static bool IsOffice => Current == WorkModeKind.Office;

        /// <summary>Raised after every change of mode, on the main thread.</summary>
        public static event Action Changed;

        /// <summary>"field" / "office" / "" for the CSV logs.</summary>
        public static string Tag => IsField ? "field" : IsOffice ? "office" : "";

        public static string Name => IsField ? L.T("Πεδίο", "Field") : IsOffice ? L.T("Γραφείο", "Office") : L.T("—", "—");

        /// <summary>
        /// The saved mode, if the app was in use less than <see cref="RememberMinutes"/> ago.
        /// True when it was restored and the question can be skipped.
        /// </summary>
        public static bool TryRestore()
        {
            if (Recent())
            {
                var saved = (WorkModeKind)Mathf.Clamp(PlayerPrefs.GetInt(ModeKey, 0), 0, 2);
                if (saved != WorkModeKind.None)
                {
                    Current = saved;
                    Touch();
                    Changed?.Invoke();
                    return true;
                }
            }
            return false;
        }

        /// <summary>Whether the last use (or pause) was within <see cref="RememberMinutes"/>.</summary>
        public static bool Recent()
        {
            string s = PlayerPrefs.GetString(LastUseKey, "");
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime last)) return false;
            double minutes = (DateTime.UtcNow - last).TotalMinutes;
            return minutes >= 0 && minutes < RememberMinutes;
        }

        public static void Set(WorkModeKind mode)
        {
            Current = mode;
            PlayerPrefs.SetInt(ModeKey, (int)mode);
            Touch();
            Debug.Log($"[WorkMode] {mode}");
            Changed?.Invoke();
        }

        /// <summary>Marks the app as in use now. <paramref name="flush"/> writes to disk at once (on pause).</summary>
        public static void Touch(bool flush = false)
        {
            if (Current == WorkModeKind.None) return;
            PlayerPrefs.SetString(LastUseKey, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            if (flush) PlayerPrefs.Save();
            else Prefs.Save();
        }
    }
}
