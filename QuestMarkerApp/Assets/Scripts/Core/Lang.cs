using System;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// The interface language, Greek or English (Μενού › Ρυθμίσεις › Γλώσσα / Language).
    /// Texts stay inline beside the code that shows them, as pairs: <c>L.T("Ρυθμίσεις", "Settings")</c>.
    /// That keeps every string next to its layout (widths were sized for the Greek) and needs no
    /// key table to keep in step. In a path that runs every frame, prefer
    /// <c>L.En ? $"…" : $"…"</c> so only one interpolated string is built.
    /// Log messages and comments are not translated.
    /// </summary>
    public static class L
    {
        public enum Language { Greek = 0, English = 1 }

        private const string Key = "ui.lang";
        private static Language? current;

        /// <summary>Raised after a change, so panels that built their text once can rebuild it.</summary>
        public static event Action Changed;

        /// <summary>Greek unless the user picked English: existing installs keep their language.</summary>
        public static Language Current
        {
            get => current ?? (current = (Language)Mathf.Clamp(PlayerPrefs.GetInt(Key, 0), 0, 1)).Value;
            set
            {
                if (Current == value) return;
                current = value;
                PlayerPrefs.SetInt(Key, (int)value);
                Prefs.Save();
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Reads the saved choice now, on the main thread. PlayerPrefs is main-thread only, and
        /// the model importers build their warnings with <see cref="T"/> on a worker thread:
        /// after this they only read the cached field. MarkerAppController calls it in Awake;
        /// calling it again is harmless.
        /// </summary>
        public static void Init() => _ = Current;

        public static bool En => Current == Language.English;

        /// <summary>The text in the current language.</summary>
        public static string T(string el, string en) => En ? en : el;

        /// <summary>The language's own name, for the settings row.</summary>
        public static string Name(Language l) => l == Language.English ? "English" : "Ελληνικά";
    }
}
