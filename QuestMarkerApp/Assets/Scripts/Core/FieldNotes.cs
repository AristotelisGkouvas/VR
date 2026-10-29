using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Notes taken on site against a sensor ("crack beside K5, cover plate loose"): typed or
    /// dictated from its card, kept on the headset (persistentDataPath/field_notes.json), shown
    /// on the card, listed in the inspection route's report and served to the PC at
    /// GET /api/fieldnotes. Local only: Terraweb has no endpoint for them yet.
    /// </summary>
    public static class FieldNotes
    {
        [Serializable]
        public class Note
        {
            public int projectId;
            public string project;
            public string pointId;
            public string sensorId;
            public string label;
            public string text;
            /// <summary>ISO 8601 UTC.</summary>
            public string utc;

            public DateTime Utc => DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                                                | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime t)
                ? t : DateTime.MinValue;
        }

        [Serializable]
        private class FileData
        {
            public List<Note> notes = new List<Note>();
        }

        private static FileData data;

        public static string FilePath => Path.Combine(Application.persistentDataPath, "field_notes.json");

        private static FileData Data
        {
            get
            {
                if (data != null) return data;
                data = new FileData();
                try
                {
                    if (File.Exists(FilePath)) data = JsonUtility.FromJson<FileData>(File.ReadAllText(FilePath)) ?? new FileData();
                }
                catch (Exception e) { Debug.LogWarning("[Notes] Could not read the notes: " + e.Message); }
                if (data.notes == null) data.notes = new List<Note>();
                return data;
            }
        }

        /// <summary>Adds a note for <paramref name="pointId"/> now and saves; false if it could not be written.</summary>
        public static bool Add(int projectId, string project, string pointId, string sensorId, string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            Data.notes.Add(new Note
            {
                projectId = projectId, project = project, pointId = pointId, sensorId = sensorId, label = label,
                text = text.Trim(), utc = DateTime.UtcNow.ToString("o")
            });
            return Save();
        }

        /// <summary>A sensor's notes in this project, newest first.</summary>
        public static List<Note> For(string project, string pointId)
        {
            var list = new List<Note>();
            foreach (Note n in Data.notes)
                if (n.pointId == pointId && n.project == project) list.Add(n);
            list.Sort((a, b) => string.CompareOrdinal(b.utc, a.utc));
            return list;
        }

        /// <summary>This project's notes taken since <paramref name="sinceUtc"/>, oldest first.</summary>
        public static List<Note> Since(string project, DateTime sinceUtc)
        {
            var list = new List<Note>();
            foreach (Note n in Data.notes)
                if (n.project == project && n.Utc >= sinceUtc) list.Add(n);
            list.Sort((a, b) => string.CompareOrdinal(a.utc, b.utc));
            return list;
        }

        /// <summary>The whole file as JSON (for the PC).</summary>
        public static string Json() => JsonUtility.ToJson(Data, true);

        private static bool Save()
        {
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(Data, true));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Notes] Could not save: " + e.Message);
                return false;
            }
        }
    }
}
