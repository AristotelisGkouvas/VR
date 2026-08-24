using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Named marker sessions: export/import JSON & CSV, undo history.
    /// </summary>
    public class SessionManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MarkerManager markerManager;

        [Header("Session")]
        [SerializeField] private string sessionName = "default";
        [SerializeField] private string siteName = "";
        [SerializeField] private int historyLimit = 50;

        public event Action<MarkerSession> OnSessionSaved;
        public event Action<MarkerSession> OnSessionLoaded;
        public event Action OnHistoryChanged;

        private string sessionId;
        private long createdAt;
        private GPSReference gpsOrigin;
        private string crs = "local";
        private string epsg;
        private readonly Stack<string> undoStack = new Stack<string>(); // JSON snapshots of markers

        public string SessionId => sessionId;
        public string SessionName { get => sessionName; set => sessionName = value; }
        public string SiteName { get => siteName; set => siteName = value; }
        public GPSReference GpsOrigin { get => gpsOrigin; set => gpsOrigin = value; }
        public string Crs { get => crs; set => crs = value; }
        public int UndoCount => undoStack.Count;

        private void Awake()
        {
            if (markerManager == null)
                markerManager = FindObjectOfType<MarkerManager>();

            sessionId = Guid.NewGuid().ToString("N");
            createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>Push current marker state for undo.</summary>
        public void PushHistory()
        {
            if (markerManager == null) return;
            string snap = markerManager.ToCoordinatesJson();
            // Skip no-op snapshots so periodic full-state pushes don't flood the history.
            if (undoStack.Count > 0 && undoStack.Peek() == snap) return;
            undoStack.Push(snap);
            if (undoStack.Count > historyLimit)
            {
                // Drop oldest: ToArray() is newest-first, keep the first historyLimit
                // entries and push them back oldest-first to preserve order.
                var arr = undoStack.ToArray();
                undoStack.Clear();
                int keep = Mathf.Min(historyLimit, arr.Length);
                for (int i = keep - 1; i >= 0; i--)
                    undoStack.Push(arr[i]);
            }
            OnHistoryChanged?.Invoke();
        }

        /// <summary>Undo last mutation if possible (restores previous full marker set).</summary>
        public bool Undo()
        {
            if (undoStack.Count == 0 || markerManager == null) return false;
            // Current state discarded; previous snapshot reapplied via clear+create
            // We stored full GET json — parse coordinates array roughly via JsonUtility wrapper
            string snap = undoStack.Pop();
            // Extract coordinates using a lightweight approach: re-process via stored session
            // Fallback: clear only if parse fails
            try
            {
                // Wrap is {"status":...,"coordinates":[...]} — JsonUtility needs a root type
                var wrapper = JsonUtility.FromJson<CoordinateResponseWrapper>("{\"coordinates\":" + ExtractCoordinatesArray(snap) + "}");
                if (wrapper?.coordinates != null)
                {
                    markerManager.ProcessCoordinates(new CoordinateResponse
                    {
                        clearExisting = true,
                        coordinates = wrapper.coordinates
                    });
                    OnHistoryChanged?.Invoke();
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Session] Undo failed: {e.Message}");
            }
            OnHistoryChanged?.Invoke();
            return false;
        }

        public MarkerSession BuildSession()
        {
            var markers = CollectMarkers();
            return new MarkerSession
            {
                id = sessionId,
                name = sessionName,
                site = siteName,
                createdAt = createdAt,
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                crs = crs,
                epsg = epsg,
                gpsOrigin = gpsOrigin,
                markers = markers,
                notes = ""
            };
        }

        public string ExportJson()
        {
            return JsonUtility.ToJson(BuildSession(), true);
        }

        public string ExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("id,x,y,z,label,color,scale,markerType,radius,crs,layer,status,notes,latitude,longitude,altitude,timestamp");
            foreach (var m in CollectMarkers())
            {
                sb.Append(EscapeCsv(m.id)).Append(',')
                  .Append(F(m.x)).Append(',').Append(F(m.y)).Append(',').Append(F(m.z)).Append(',')
                  .Append(EscapeCsv(m.label)).Append(',')
                  .Append(EscapeCsv(m.color)).Append(',')
                  .Append(F(m.scale)).Append(',')
                  .Append(EscapeCsv(m.markerType)).Append(',')
                  .Append(F(m.radius)).Append(',')
                  .Append(EscapeCsv(m.crs)).Append(',')
                  .Append(EscapeCsv(m.layer)).Append(',')
                  .Append(EscapeCsv(m.status)).Append(',')
                  .Append(EscapeCsv(m.notes)).Append(',')
                  .Append(m.latitude.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(m.longitude.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(m.altitude.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(m.timestamp)
                  .AppendLine();
            }
            return sb.ToString();
        }

        public bool SaveToFile(string absolutePath = null, bool csv = false)
        {
            try
            {
                if (string.IsNullOrEmpty(absolutePath))
                {
                    string file = csv
                        ? $"session_{sessionName}_{sessionId.Substring(0, 8)}.csv"
                        : $"session_{sessionName}_{sessionId.Substring(0, 8)}.json";
                    absolutePath = Path.Combine(Application.persistentDataPath, file);
                }

                string content = csv ? ExportCsv() : ExportJson();
                File.WriteAllText(absolutePath, content, Encoding.UTF8);
                Debug.Log($"[Session] Saved to {absolutePath}");
                OnSessionSaved?.Invoke(BuildSession());
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Session] Save failed: {e.Message}");
                return false;
            }
        }

        public bool LoadFromJson(string json)
        {
            try
            {
                var session = JsonUtility.FromJson<MarkerSession>(json);
                if (session == null) return false;
                ApplySession(session);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Session] Load JSON failed: {e.Message}");
                return false;
            }
        }

        public bool LoadFromFile(string absolutePath)
        {
            try
            {
                if (!File.Exists(absolutePath)) return false;
                string text = File.ReadAllText(absolutePath, Encoding.UTF8);
                if (absolutePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    return LoadFromCsv(text);
                return LoadFromJson(text);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Session] Load file failed: {e.Message}");
                return false;
            }
        }

        public bool LoadFromCsv(string csv)
        {
            try
            {
                var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) return false;
                var list = new List<CoordinateData>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var cols = SplitCsv(lines[i]);
                    if (cols.Count < 4) continue;
                    var d = new CoordinateData
                    {
                        id = cols.Count > 0 ? cols[0] : null,
                        x = ParseF(cols, 1),
                        y = ParseF(cols, 2),
                        z = ParseF(cols, 3),
                        label = Get(cols, 4),
                        color = Get(cols, 5),
                        scale = ParseF(cols, 6, 0.1f),
                        markerType = Get(cols, 7, "sphere"),
                        radius = ParseF(cols, 8),
                        crs = Get(cols, 9, "local"),
                        layer = Get(cols, 10, "default"),
                        status = Get(cols, 11),
                        notes = Get(cols, 12),
                    };
                    if (cols.Count > 13) double.TryParse(cols[13], NumberStyles.Float, CultureInfo.InvariantCulture, out d.latitude);
                    if (cols.Count > 14) double.TryParse(cols[14], NumberStyles.Float, CultureInfo.InvariantCulture, out d.longitude);
                    if (cols.Count > 15) double.TryParse(cols[15], NumberStyles.Float, CultureInfo.InvariantCulture, out d.altitude);
                    if (cols.Count > 16) long.TryParse(cols[16], out d.timestamp);
                    d.EnsureDefaults();
                    list.Add(d);
                }

                PushHistory();
                markerManager.ProcessCoordinates(new CoordinateResponse
                {
                    clearExisting = true,
                    coordinates = list.ToArray()
                });
                OnSessionLoaded?.Invoke(BuildSession());
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Session] CSV load failed: {e.Message}");
                return false;
            }
        }

        public void ApplySession(MarkerSession session)
        {
            if (session == null || markerManager == null) return;
            PushHistory();
            sessionId = string.IsNullOrEmpty(session.id) ? sessionId : session.id;
            sessionName = session.name ?? sessionName;
            siteName = session.site ?? siteName;
            createdAt = session.createdAt > 0 ? session.createdAt : createdAt;
            crs = session.crs ?? "local";
            epsg = session.epsg;
            gpsOrigin = session.gpsOrigin;

            markerManager.ProcessCoordinates(new CoordinateResponse
            {
                clearExisting = true,
                coordinates = session.markers ?? Array.Empty<CoordinateData>(),
                sessionId = sessionId,
                sessionName = sessionName
            });
            OnSessionLoaded?.Invoke(session);
            Debug.Log($"[Session] Loaded '{sessionName}' with {session.markers?.Length ?? 0} markers");
        }

        public void NewSession(string name = null)
        {
            PushHistory();
            sessionId = Guid.NewGuid().ToString("N");
            sessionName = string.IsNullOrEmpty(name) ? "session" : name;
            createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (markerManager != null)
                markerManager.ClearAllMarkers();
        }

        private CoordinateData[] CollectMarkers()
        {
            if (markerManager == null) return Array.Empty<CoordinateData>();
            var list = new List<CoordinateData>();
            foreach (var kvp in markerManager.ActiveMarkers)
            {
                if (kvp.Value?.Data != null)
                    list.Add(kvp.Value.Data.Clone());
            }
            return list.ToArray();
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static List<string> SplitCsv(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQ && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQ = !inQ;
                }
                else if (c == ',' && !inQ)
                {
                    result.Add(sb.ToString());
                    sb.Length = 0;
                }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result;
        }

        private static string Get(List<string> cols, int i, string def = "")
            => i < cols.Count ? cols[i] : def;

        private static float ParseF(List<string> cols, int i, float def = 0f)
        {
            if (i >= cols.Count) return def;
            return float.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
        }

        private static string ExtractCoordinatesArray(string json)
        {
            int idx = json.IndexOf("\"coordinates\"", StringComparison.Ordinal);
            if (idx < 0) return "[]";
            int start = json.IndexOf('[', idx);
            if (start < 0) return "[]";
            int depth = 0;
            for (int i = start; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']')
                {
                    depth--;
                    if (depth == 0)
                        return json.Substring(start, i - start + 1);
                }
            }
            return "[]";
        }

        [Serializable]
        private class CoordinateResponseWrapper
        {
            public CoordinateData[] coordinates;
        }
    }
}
