using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// The headset's SensorDetailCard: aim at a sensor, pull the trigger, and see what it is,
    /// how it stands (alert state), where it is relative to you and its latest readings with
    /// a sparkline. Readings come live from Terraweb when the headset is logged in.
    /// </summary>
    public class SensorCard : MonoBehaviour
    {
        private const float Width = 560f;
        private const float Height = 640f;
        private const float MetersPerPixel = 0.0006f;
        private const int History = 40;
        private const int ChartW = 496, ChartH = 120;

        public static bool IsOpen { get; private set; }
        /// <summary>Marker id the open card shows (aiming at another one switches the card).</summary>
        public string MarkerId => IsOpen ? markerId : null;
        /// <summary>The same, for code without a reference to the card.</summary>
        public static string MarkerIdOpen { get; private set; }

        /// <summary>Raised with the marker id when the user asks to navigate to this sensor.</summary>
        public event Action<string, ProjectPoint> OnNavigateRequested;

        private TerrawebClient client;
        private Func<int> projectId;
        private RectTransform root;
        private Image chip, glyph, stateDot;
        private TextMeshProUGUI nameText, typeText, stateText, distanceText, heightText, dateText, valuesText,
            chartMin, chartMax, chartCaption, notesText, footer;
        private RawImage chart;
        private RectTransform retryButton;
        private Texture2D chartTexture;
        // One pixel buffer for the sparkline: ClearChart + GetPixels32 used to allocate ~0.7 MB
        // of garbage per card open.
        private readonly Color32[] chartPixels = new Color32[ChartW * ChartH];

        private ProjectPoint point;
        private string markerId;
        private Transform target;
        private MarkerManager markers;
        private int requestSerial;

        public void Init(TerrawebClient terraweb, Func<int> terrawebProjectId = null)
        {
            client = terraweb;
            projectId = terrawebProjectId;
            markers = FindAnyObjectByType<MarkerManager>();
            Build();
            root.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private void Build()
        {
            root = VrUi.CreateCanvas("SensorCardCanvas", transform, new Vector2(Width, Height), MetersPerPixel);
            VrUi.Card("Card", root, 0, 0, Width, Height, Brand.Surface.WithAlpha(0.97f), Brand.BorderStrong, 24);
            VrPointer.RegisterDraggable(root, transform);

            chip = VrUi.Icon("Chip", root, 28, 28, 64, Brand.DiscSprite, Brand.Primary);
            glyph = VrUi.Icon("Glyph", root, 40, 40, 40, null, Color.white);
            nameText = VrUi.Text("Name", root, 108, 26, Width - 184, 38, "", 28f, Brand.Fg, true);
            typeText = VrUi.Text("Type", root, 108, 62, 250, 24, "", 15f, Brand.FgSubtle);
            VrUi.Panel("StateChip", root, Width - 196, 70, 168, 28, Brand.SurfaceSunken, 14);
            stateDot = VrUi.Panel("StateDot", root, Width - 184, 80, 8, 8, Brand.Primary, 4);
            stateText = VrUi.Text("State", root, Width - 170, 70, 140, 28, "", 13f, Brand.FgMuted, true);

            // Where it is
            VrUi.Panel("WhereBg", root, 20, 112, Width - 40, 74, Brand.SurfaceMuted, 14);
            distanceText = Metric(root, 36, 120, "Απόσταση");
            heightText = Metric(root, 210, 120, "Υψόμετρο · ΔH από τα μάτια");
            dateText = Metric(root, 384, 120, "Τελευταία μέτρηση");
            dateText.fontSize = 15f; // "21/02/22 · πριν 3 έτη" is longer than a number

            // Readings
            VrUi.Text("ReadHeader", root, 28, 202, 300, 22, "ΜΕΤΡΗΣΕΙΣ", 13f, Brand.FgSubtle, true)
                .characterSpacing = 6f;
            retryButton = VrUi.Button("Retry", root, Width - 148, 194, 120, 30, "history", "Ξανά",
                () => { if (point != null) Open(point, markerId, target); });
            retryButton.gameObject.SetActive(false);
            valuesText = VrUi.Text("Values", root, 28, 228, Width - 56, 60, "", 17f, Brand.Fg);
            valuesText.textWrappingMode = TextWrappingModes.Normal;
            valuesText.alignment = TextAlignmentOptions.TopLeft;

            VrUi.Panel("ChartBg", root, 20, 296, Width - 40, ChartH + 40, Brand.SurfaceSunken, 14);
            chartTexture = new Texture2D(ChartW, ChartH, TextureFormat.RGBA32, false)
            {
                name = "SensorSparkline",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var chartRt = VrUi.Rect("Chart", root, 32, 306, ChartW, ChartH);
            chart = chartRt.gameObject.AddComponent<RawImage>();
            chart.texture = chartTexture;
            chart.raycastTarget = false;
            chartMax = VrUi.Text("Max", root, 36, 304, 200, 18, "", 12f, Brand.FgSubtle);
            chartMin = VrUi.Text("Min", root, 36, 306 + ChartH - 16, 200, 18, "", 12f, Brand.FgSubtle);
            chartCaption = VrUi.Text("Caption", root, 32, 306 + ChartH + 6, Width - 64, 20, "", 12f, Brand.FgSubtle,
                false, TextAlignmentOptions.MidlineRight);

            notesText = VrUi.Text("Notes", root, 28, 478, Width - 56, 80, "", 14f, Brand.FgMuted);
            notesText.textWrappingMode = TextWrappingModes.Normal;
            notesText.alignment = TextAlignmentOptions.TopLeft;

            float fy = Height - 56f;
            VrUi.Panel("Divider", root, 28, fy - 12, Width - 56, 1, Brand.Border, 0);
            VrUi.Button("Navigate", root, 28, fy - 6, 210, 44, "compass", "Πλοήγηση εδώ", NavigateHere, true);
            VrUi.Button("CloseBtn", root, 248, fy - 6, 150, 44, "close", "Κλείσιμο", Close);
            VrUi.Button("Close", root, Width - 64, 22, 40, 40, "close", null, Close);
            footer = VrUi.Text("Footer", root, Width - 160, fy, 132, 30, "", 13f, Brand.FgFaint, false,
                TextAlignmentOptions.MidlineRight);
        }

        private static TextMeshProUGUI Metric(Transform parent, float x, float y, string caption)
        {
            VrUi.Text("Caption", parent, x, y, 170, 18, caption, 13f, Brand.FgSubtle);
            return VrUi.Text("Value", parent, x, y + 22, 170, 34, "", 19f, Brand.Fg, true);
        }

        // ------------------------------------------------------------------
        //  Open / close
        // ------------------------------------------------------------------

        public void Open(ProjectPoint p, string id, Transform markerTransform)
        {
            if (root == null || p == null) return;
            point = p;
            markerId = id;
            target = markerTransform;
            IsOpen = true;
            MarkerIdOpen = markerId;

            Color family = ColorUtility.TryParseHtmlString(p.color ?? "", out Color c) ? c : Brand.Primary;
            chip.color = family;
            glyph.sprite = Brand.IconSprite(p.icon);
            nameText.text = string.IsNullOrEmpty(p.label) ? p.id : p.label;
            typeText.text = (string.IsNullOrEmpty(p.typeLabel) ? "Σημείο" : p.typeLabel) + (p.inactive ? " · ανενεργός" : "");
            SetState(p.alertState);
            dateText.text = FormatDate(p.latestDate);
            notesText.text = string.IsNullOrEmpty(p.notes) ? "" : "Σημειώσεις: " + p.notes;
            ClearChart();
            footer.text = "";
            retryButton.gameObject.SetActive(false);

            if (string.IsNullOrEmpty(p.sensorId))
                valuesText.text = "<color=#8ca09e>Δεν υπάρχει σύνδεση με αισθητήρα του Terraweb.</color>";
            else if (client == null || !client.IsLoggedIn)
                valuesText.text = "<color=#8ca09e>Συνδέσου στο Terraweb από το μενού για να δεις μετρήσεις.</color>";
            else
            {
                valuesText.text = "<color=#8ca09e>Φόρτωση μετρήσεων…</color>";
                // Sensor endpoints read the user's *current* project; after a restart or a
                // switch on the web that may be another one, so re-select it first.
                int pid = projectId?.Invoke() ?? 0;
                if (pid > 0 && client.CurrentProjectId != pid)
                {
                    int serial = ++requestSerial;
                    StartCoroutine(client.SelectProject(pid, null,
                        () => { if (serial == requestSerial) LoadReadings(p); },
                        error => { if (serial == requestSerial) { valuesText.text = $"<color=#db5a46>{error}</color>"; retryButton.gameObject.SetActive(true); } }));
                }
                else LoadReadings(p);
            }

            if (!PanelPin.IsPinned(this)) PlaceInFront();
            root.gameObject.SetActive(true);
            whereKey = long.MinValue + 1; // another sensor: rewrite distance and height
            UpdateWhere();
        }

        public void Close()
        {
            if (IsOpen) UiState.NoteClosed();
            IsOpen = false;
            MarkerIdOpen = null;
            requestSerial++; // drop any in-flight response
            if (root != null) root.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            // Static flags outlive the object: never leave the app thinking a card is up.
            if (!IsOpen) return;
            IsOpen = false;
            MarkerIdOpen = null;
            UiState.NoteClosed();
        }

        private const float Distance = 0.65f, Drop = 0.05f;
        private bool gliding;

        private void PlaceInFront()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            gliding = false;
            transform.position = VrUi.InFront(cam.transform, Distance, Drop);
            VrUi.FaceViewer(transform, cam.transform);
        }

        private void Update()
        {
            if (!IsOpen) return;
            // Like the menu and lists: a card left behind by turning away glides back in front.
            Camera cam = Camera.main;
            if (cam != null && !VrPointer.IsDragging && !PanelPin.IsPinned(this))
                VrUi.LazyFollow(transform, cam.transform, Distance, Drop, ref gliding);
            UpdateWhere();

#if !MOBILE_PREVIEW
            if (VrKeyboard.IsOpen || VrKeyboard.ClosedFrame == Time.frameCount || MainMenu.IsOpen || FlowPanel.IsOpen) return;
            if (Hands.AnyBackDown()) { Close(); return; }
            if (Hands.DominantPrimaryDown()) NavigateHere();
#else
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
#endif
        }

        private void NavigateHere()
        {
            ProjectPoint p = point;
            string id = markerId;
            Close();
            OnNavigateRequested?.Invoke(id, p);
        }

        private void UpdateWhere()
        {
            Camera cam = Camera.main;
            if (cam == null || point == null) return;
            // The transform belongs to a pooled marker: after a project reload it may carry
            // another sensor, so resolve the marker by id each time and close if it is gone.
            if (markers != null && !string.IsNullOrEmpty(markerId))
            {
                if (!markers.ActiveMarkers.TryGetValue(markerId, out Marker m) || m == null) { Close(); return; }
                target = m.transform;
            }
            if (target == null)
            {
                if (whereKey == long.MinValue) return;
                whereKey = long.MinValue;
                distanceText.text = "—";
                heightText.text = $"H {point.h:F2}";
                return;
            }

            Vector3 d = target.position - cam.transform.position;
            float horizontal = new Vector2(d.x, d.z).magnitude;
            // Relative to eye level: the tracking floor is one flat plane from where the session
            // started, so on a sloping site "from the floor" was wrong by the slope.
            float dh = target.position.y - cam.transform.position.y;
            // Re-format only when a shown digit changes (this ran every frame: two strings a frame).
            long hKey = horizontal < 10f ? Mathf.RoundToInt(horizontal * 10f) : 100000 + Mathf.RoundToInt(horizontal);
            long key = hKey * 10_000_000L + (Mathf.RoundToInt(dh * 10f) + 1_000_000L) * 2 + (dh >= 0 ? 1 : 0);
            if (key == whereKey) return;
            whereKey = key;
            distanceText.text = horizontal < 10f ? $"{horizontal:F1} m" : $"{horizontal:F0} m";
            heightText.text = $"{point.h:F2} · {(dh >= 0 ? "+" : "")}{dh:F1} m";
        }

        // Key of the shown distance / ΔH; long.MinValue = "—" shown, MinValue + 1 = nothing yet.
        private long whereKey = long.MinValue + 1;

        private static float EyeHeight(Camera cam)
        {
            // Floor-level tracking puts y=0 on the floor; the eye height is the camera's y there.
            return cam.transform.parent != null ? cam.transform.localPosition.y : cam.transform.position.y;
        }

        private void SetState(string state)
        {
            switch ((state ?? "none").ToLowerInvariant())
            {
                case "alarm": stateDot.color = Brand.Danger; stateText.text = "Συναγερμός"; break;
                case "alert": stateDot.color = Brand.Warning; stateText.text = "Προειδοποίηση"; break;
                case "reference": stateDot.color = Brand.Control; stateText.text = "Αναφορά"; break;
                case "none": stateDot.color = Brand.Primary; stateText.text = "Κανονικό"; break;
                // An unknown state must never read as "normal".
                default: stateDot.color = Brand.FgFaint; stateText.text = "Άγνωστο"; break;
            }
        }

        // ------------------------------------------------------------------
        //  Readings
        // ------------------------------------------------------------------

        private void LoadReadings(ProjectPoint p)
        {
            int serial = ++requestSerial;
            // No type id → don't ask for /types/0/; a failed type request must not stall readings.
            JObject type = p.typeId > 0 ? null : new JObject();
            if (p.typeId > 0)
                StartCoroutine(client.GetSensorType(p.typeId, t => type = t, _ => type = new JObject()));
            // The alert state in the project is a snapshot from load time: refresh it.
            StartCoroutine(client.GetSensor(p.sensorId, s =>
            {
                if (serial != requestSerial || s == null) return;
                string state = (string)s["current_alert_state"];
                if (!string.IsNullOrEmpty(state)) { p.alertState = state; SetState(state); }
                string latest = (string)s["latest_date"];
                if (!string.IsNullOrEmpty(latest)) p.latestDate = latest;
            }, _ => { }));
            StartCoroutine(client.GetMeasurements(p.sensorId, History,
                list => { if (serial == requestSerial) StartCoroutine(ShowWhenTyped(serial, list, () => type)); },
                error =>
                {
                    if (serial != requestSerial) return;
                    valuesText.text = $"<color=#db5a46>{error}</color>";
                    retryButton.gameObject.SetActive(true);
                }));
        }

        // Readings may be numbers, numeric strings, or (vertical sensors) whole objects.
        private static bool TryNumber(JToken v, out double d)
        {
            d = 0;
            if (v == null) return false;
            switch (v.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    d = (double)v;
                    return !double.IsNaN(d) && !double.IsInfinity(d);
                case JTokenType.String:
                    return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                default:
                    return false;
            }
        }

        private static bool IsEnabled(JToken m) => m != null && ((bool?)m["is_enabled"] ?? true);

        // The type (units, field order) usually arrives first; wait briefly if not.
        private System.Collections.IEnumerator ShowWhenTyped(int serial, JArray list, Func<JObject> type)
        {
            float until = Time.unscaledTime + 3f;
            while (type() == null && Time.unscaledTime < until) yield return null;
            if (serial != requestSerial) yield break;
            try
            {
                ShowReadings(list, type());
            }
            catch (Exception e)
            {
                // Never leave the card stuck on "loading".
                valuesText.text = "<color=#8ca09e>Οι μετρήσεις αυτού του τύπου δεν εμφανίζονται εδώ.</color>";
                Debug.LogWarning($"[SensorCard] {e.Message}");
            }
        }

        private void ShowReadings(JArray list, JObject type)
        {
            if (list == null || list.Count == 0)
            {
                valuesText.text = "<color=#8ca09e>Δεν υπάρχουν μετρήσεις.</color>";
                return;
            }

            // Fields in slot order, measured ones first (stable); units from the type when it loaded.
            var fields = new List<(string code, string unit, bool calculated)>();
            if (type?["fields"] is JArray defs)
            {
                var measured = new List<(string, string, bool)>();
                var calculated = new List<(string, string, bool)>();
                foreach (JToken f in defs)
                {
                    if (!((bool?)f["is_active"] ?? true)) continue;
                    var entry = ((string)f["codename"], (string)f["unit"] ?? "", (bool?)f["is_calculated"] ?? false);
                    (entry.Item3 ? calculated : measured).Add(entry);
                }
                fields.AddRange(measured);
                fields.AddRange(calculated);
            }
            // The newest *enabled* reading — a rejected one must not show as current.
            JObject latest = null;
            foreach (JToken m in list)
                if (IsEnabled(m)) { latest = m as JObject; break; }
            if (latest == null)
            {
                valuesText.text = "<color=#8ca09e>Καμία ενεργή μέτρηση.</color>";
                return;
            }
            JObject values = latest["values"] as JObject;
            if (fields.Count == 0 && values != null)
                foreach (var kv in values) fields.Add((kv.Key, "", false));

            var parts = new List<string>();
            foreach (var (code, unit, _) in fields)
            {
                if (!TryNumber(values?[code], out double num)) continue;
                parts.Add($"<color=#8ca09e>{code}</color>  <b>{FormatNumber(num)}</b> {unit}");
                if (parts.Count == 4) break;
            }
            valuesText.text = parts.Count > 0 ? string.Join("     ", parts) : "<color=#8ca09e>Κενή μέτρηση.</color>";
            dateText.text = FormatDate((string)latest?["meas_date"]);

            // Chart the first field that actually has a series.
            foreach (var (code, unit, _) in fields)
            {
                int count = 0;
                foreach (JToken m in list)
                    if (IsEnabled(m) && TryNumber(m["values"]?[code], out _)) count++;
                if (count < 2) continue;
                DrawChart(list, code, unit);
                break;
            }
            footer.text = $"{list.Count} πιο πρόσφατες";
        }

        private void DrawChart(JArray list, string code, string unit)
        {
            var series = new List<double>();
            for (int i = list.Count - 1; i >= 0; i--) // oldest → newest
            {
                if (IsEnabled(list[i]) && TryNumber(list[i]?["values"]?[code], out double num))
                    series.Add(num);
            }
            ClearChart();
            if (series.Count < 2) { chartCaption.text = "Λίγες μετρήσεις για γράφημα"; return; }

            double min = double.MaxValue, max = double.MinValue;
            foreach (double s in series) { min = Math.Min(min, s); max = Math.Max(max, s); }
            if (max - min < 1e-9) { max += 1; min -= 1; }

            Color32[] pixels = chartPixels;
            Color32 line = Brand.Primary;
            int px = -1, py = -1;
            for (int i = 0; i < series.Count; i++)
            {
                int x = Mathf.RoundToInt(i / (float)(series.Count - 1) * (ChartW - 5)) + 2;
                int y = Mathf.RoundToInt((float)((series[i] - min) / (max - min)) * (ChartH - 9)) + 4;
                if (px >= 0) Plot(pixels, px, py, x, y, line);
                Dot(pixels, x, y, 2, line);
                px = x; py = y;
            }
            Dot(pixels, px, py, 4, Brand.Fg);
            chartTexture.SetPixels32(pixels);
            chartTexture.Apply(false);

            chartMax.text = $"{FormatNumber(max)} {unit}";
            chartMin.text = $"{FormatNumber(min)} {unit}";
            chartCaption.text = $"{code} · {series.Count} μετρήσεις";
        }

        private void ClearChart()
        {
            Color32[] clear = chartPixels;
            Array.Clear(clear, 0, clear.Length);
            Color32 grid = Brand.Border;
            for (int y = 0; y < ChartH; y += ChartH / 4)
                for (int x = 0; x < ChartW; x++) clear[y * ChartW + x] = grid;
            chartTexture.SetPixels32(clear);
            chartTexture.Apply(false);
            chartMin.text = chartMax.text = chartCaption.text = "";
        }

        // Thick-ish line: stamp small dots along a Bresenham walk.
        private static void Plot(Color32[] px, int x0, int y0, int x1, int y1, Color32 c)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            while (true)
            {
                Dot(px, x0, y0, 1, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private static void Dot(Color32[] px, int cx, int cy, int r, Color32 c)
        {
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                    if (x >= 0 && x < ChartW && y >= 0 && y < ChartH && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                        px[y * ChartW + x] = c;
        }

        private static string FormatNumber(double v) =>
            Math.Abs(v) >= 1000 ? v.ToString("F0", CultureInfo.InvariantCulture)
            : Math.Abs(v) >= 10 ? v.ToString("F2", CultureInfo.InvariantCulture)
            : v.ToString("F3", CultureInfo.InvariantCulture);

        internal static string FormatDate(string iso)
        {
            if (string.IsNullOrEmpty(iso) ||
                !DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d))
                return "—";
            TimeSpan ago = DateTime.UtcNow - d.ToUniversalTime();
            string rel = ago.TotalDays < 1 ? "σήμερα"
                : ago.TotalDays < 60 ? $"πριν {(int)ago.TotalDays} ημ."
                : ago.TotalDays < 730 ? $"πριν {(int)(ago.TotalDays / 30)} μήν."
                : $"πριν {(int)(ago.TotalDays / 365)} έτη";
            return $"{d:dd/MM/yy} · {rel}";
        }
    }
}
