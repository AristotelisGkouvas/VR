using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Live alerts: while logged in to Terraweb2 with that same project loaded in the room,
    /// polls the backend's active alert events and, when a sensor's tier changes, recolours
    /// its marker ring at once, plays a sound (and a buzz for an alarm) and shows a short
    /// toast in front of the user. Polling because the server has no push channel; 20 s is
    /// well inside how often field readings arrive, and backs off to 60 s on a dead link.
    ///
    /// A sensor's tier is the highest among its active events (alarm > alert > reference);
    /// no event means "none". The first poll after a project load only sets the baseline:
    /// the imported states may be days old and a flood of toasts would say nothing useful.
    /// </summary>
    public class LiveAlerts : MonoBehaviour
    {
        private const string PrefEnabled = "tw.live";
        private const float FirstPollDelay = 3f;
        private const float BackoffSeconds = 60f;
        private const int FailuresBeforeBackoff = 3;
        /// <summary>More sensors than this changing in one poll get a single summary toast.</summary>
        private const int BatchThreshold = 3;
        private const float ToastSeconds = 4f;
        private const int MaxQueuedToasts = 3;
        private const float MetersPerPixel = 0.0006f;
        private const float ToastWidth = 360f, ToastHeight = 64f;

        private const int RankNone = 0, RankReference = 1, RankAlert = 2, RankAlarm = 3;

        /// <summary>Main-menu switch, remembered across launches.</summary>
        public static bool Enabled
        {
            get => enabled ?? (bool)(enabled = PlayerPrefs.GetInt(PrefEnabled, 1) == 1);
            set
            {
                if (enabled == value) return;
                enabled = value;
                PlayerPrefs.SetInt(PrefEnabled, value ? 1 : 0);
                PlayerPrefs.Save();
                // Switched back on: catch up now rather than at the next scheduled poll.
                if (value && instance != null) instance.pollRequested = true;
            }
        }
        private static bool? enabled;
        private static LiveAlerts instance;

        public float PollSeconds = 20f;
        /// <summary>Sensors currently in alarm / alert on the server (per sensor, not per event).</summary>
        public int ActiveAlarms { get; private set; }
        public int ActiveAlerts { get; private set; }
        /// <summary>(sensor name, new tier, detail of the strongest breach) — once per changed sensor.</summary>
        public event Action<string, string, string> OnTierChanged;

        private TerrawebClient client;
        private ProjectAlignment alignment;
        private Coroutine loop;
        private bool requesting;
        private bool pollRequested;
        private float nextPoll;
        private int failures;
        private float lastSuccess = -1f;
        private string lastError;
        // Project id whose states the last poll was applied to: another id means baseline only.
        private int baselinedProjectId = -1;
        private int seenProjectId = -1;

        // Status text: rebuilt only when the shown age bucket changes (the menu reads it often).
        private int ageKey = int.MinValue;
        private string ageText;

        // Scratch per poll, reused: sensorId to tier rank / the event carrying that rank.
        private readonly Dictionary<string, int> tiers = new Dictionary<string, int>();
        private readonly Dictionary<string, JObject> strongest = new Dictionary<string, JObject>();
        private readonly List<Change> changes = new List<Change>();

        private struct Change
        {
            public ProjectPoint Point;
            public int From, To;
        }

        private struct Toast
        {
            public string Text;
            public Color Dot;
        }

        private readonly Queue<Toast> toastQueue = new Queue<Toast>();
        private RectTransform toast;
        private Image toastDot;
        private TextMeshProUGUI toastText;
        private bool toastVisible;
        private float toastShownAt;

        /// <summary>Short line for the main menu, in the interface language.</summary>
        public string Status
        {
            get
            {
                if (!Enabled) return L.T("Ανενεργό", "Off");
                if (!Connected) return L.T("Χωρίς σύνδεση", "Not connected");
                if (requesting) return L.T("Ενημέρωση…", "Updating…");
                if (lastError != null) return lastError;
                if (lastSuccess < 0f) return L.T("Ενημέρωση…", "Updating…");
                int seconds = (int)(Time.unscaledTime - lastSuccess);
                int key = seconds < 60 ? seconds : seconds < 3600 ? 1000 + seconds / 60 : 100000 + seconds / 3600;
                key = key * 2 + (L.En ? 1 : 0); // a language change rewrites the cached text
                if (key != ageKey)
                {
                    ageKey = key;
                    ageText = Ago(seconds);
                }
                return ageText;
            }
        }

        private static string Ago(int seconds)
        {
            if (seconds < 60) return L.En ? $"{seconds} s ago" : $"πριν {seconds} δευτ.";
            int minutes = seconds / 60;
            if (minutes < 60) return minutes == 1 ? L.T("πριν 1 λεπτό", "1 min ago") : L.En ? $"{minutes} min ago" : $"πριν {minutes} λεπτά";
            int hours = minutes / 60;
            return hours == 1 ? L.T("πριν 1 ώρα", "1 hour ago") : L.En ? $"{hours} hours ago" : $"πριν {hours} ώρες";
        }

        /// <summary>Events are scoped to the server's current project: it must be the one in the room.</summary>
        private bool Connected =>
            client != null && alignment != null && client.IsLoggedIn && alignment.HasProject
            && alignment.TerrawebProjectId != 0 && client.CurrentProjectId == alignment.TerrawebProjectId;

        public void Init(TerrawebClient terraweb, ProjectAlignment projectAlignment)
        {
            client = terraweb;
            alignment = projectAlignment;
            instance = this;
            if (alignment != null)
            {
                alignment.OnAlignmentChanged += OnAlignmentChanged;
                seenProjectId = alignment.TerrawebProjectId;
            }
            nextPoll = Time.unscaledTime + FirstPollDelay;
            if (isActiveAndEnabled && loop == null) loop = StartCoroutine(Loop());
        }

        public void PollNow()
        {
            if (!requesting) pollRequested = true;
        }

        private void OnEnable()
        {
            if (client != null && loop == null) loop = StartCoroutine(Loop());
        }

        private void OnDisable()
        {
            if (loop != null) StopCoroutine(loop);
            loop = null;
            requesting = false;
            HideToast();
            toastQueue.Clear();
        }

        private void OnDestroy()
        {
            if (alignment != null) alignment.OnAlignmentChanged -= OnAlignmentChanged;
            if (instance == this) instance = null;
        }

        // Fires on every touch too; only a different project id matters here.
        private void OnAlignmentChanged()
        {
            int id = alignment != null ? alignment.TerrawebProjectId : 0;
            if (id == seenProjectId) return;
            seenProjectId = id;
            pollRequested = false;
            nextPoll = Time.unscaledTime + FirstPollDelay; // the load may still be settling
        }

        // ------------------------------------------------------------------
        //  Polling
        // ------------------------------------------------------------------

        private IEnumerator Loop()
        {
            var tick = new WaitForSeconds(0.5f);
            while (true)
            {
                yield return tick;
                if (!Enabled || !Connected) continue;
                if (!pollRequested && Time.unscaledTime < nextPoll) continue;
                pollRequested = false;
                yield return Poll();
                nextPoll = Time.unscaledTime + (failures >= FailuresBeforeBackoff ? BackoffSeconds : PollSeconds);
            }
        }

        private IEnumerator Poll()
        {
            int projectId = alignment.TerrawebProjectId;
            requesting = true;
            JArray events = null;
            string error = null;
            yield return client.GetActiveAlertEvents(a => events = a, e => error = e);
            requesting = false;

            if (error != null)
            {
                failures++;
                lastError = L.T("Σφάλμα: ", "Error: ") + Shorten(error, 48);
                Debug.LogWarning($"[LiveAlerts] Poll failed ({failures}): {error}");
                yield break;
            }
            failures = 0;
            lastError = null;
            lastSuccess = Time.unscaledTime;
            ageKey = int.MinValue;
            // The project switched while the request was out: these events belong to another one.
            if (!Connected || alignment.TerrawebProjectId != projectId) yield break;

            bool baseline = baselinedProjectId != projectId;
            baselinedProjectId = projectId;
            Apply(events, baseline);
        }

        private static string Shorten(string s, int max) =>
            s == null ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private void Apply(JArray events, bool baseline)
        {
            tiers.Clear();
            strongest.Clear();
            // A success with no body is "no information", not "every alarm cleared".
            if (events == null) return;
            foreach (JToken t in events)
            {
                var ev = t as JObject;
                if (ev == null) continue;
                if (ev["active"] != null && ev["active"].Type == JTokenType.Boolean && !(bool)ev["active"]) continue;
                string sensorId = (string)ev["sensor_id"];
                if (string.IsNullOrEmpty(sensorId)) continue;
                int rank = Rank((string)ev["tier"]);
                if (tiers.TryGetValue(sensorId, out int have) && have >= rank) continue;
                tiers[sensorId] = rank;
                strongest[sensorId] = ev;
            }

            int alarms = 0, alerts = 0;
            foreach (int rank in tiers.Values)
            {
                if (rank == RankAlarm) alarms++;
                else if (rank == RankAlert) alerts++;
            }
            ActiveAlarms = alarms;
            ActiveAlerts = alerts;

            changes.Clear();
            foreach (ProjectPoint pt in alignment.Points)
            {
                if (pt == null || string.IsNullOrEmpty(pt.sensorId)) continue;
                int to = tiers.TryGetValue(pt.sensorId, out int rank) ? rank : RankNone;
                int from = Rank(pt.alertState);
                if (from != to) changes.Add(new Change { Point = pt, From = from, To = to });
            }
            if (changes.Count == 0) return;

            int worst = RankNone;
            bool escalated = false;
            foreach (Change c in changes)
            {
                alignment.SetAlertState(c.Point.sensorId, TierName(c.To));
                if (c.To > c.From) { escalated = true; worst = Math.Max(worst, c.To); }
            }
            if (baseline)
            {
                Debug.Log($"[LiveAlerts] Baseline: {changes.Count} sensor state(s) updated");
                return;
            }

            // One sound per poll, for the worst thing that happened in it.
            if (escalated && worst >= RankAlert)
            {
                Feedback.Play(Feedback.Sound.Warn);
#if !MOBILE_PREVIEW
                if (worst == RankAlarm) Feedback.Buzz(Hands.Dominant, 0.8f, 0.3f);
#endif
            }
            else Feedback.Play(Feedback.Sound.Tick);

            if (changes.Count > BatchThreshold)
            {
                int top = RankNone;
                foreach (Change c in changes) top = Math.Max(top, c.To);
                Enqueue(L.T($"{changes.Count} αλλαγές συναγερμών", $"{changes.Count} alert changes"), DotColor(top));
            }
            foreach (Change c in changes)
            {
                string name = string.IsNullOrEmpty(c.Point.label) ? c.Point.id : c.Point.label;
                string tier = TierName(c.To);
                string detail = strongest.TryGetValue(c.Point.sensorId, out JObject ev) ? Detail(ev, c.Point.typeId) : "";
                if (string.IsNullOrEmpty(name)) name = ev != null ? (string)ev["sensor_name"] ?? "?" : "?";
                if (changes.Count <= BatchThreshold)
                {
                    Enqueue(string.IsNullOrEmpty(detail)
                            ? $"<b>{name}</b> · {TierLabel(c.To)}"
                            : $"<b>{name}</b> · {TierLabel(c.To)} · {detail}",
                        DotColor(c.To));
                }
                OnTierChanged?.Invoke(name, tier, detail);
            }
        }

        private static int Rank(string tier)
        {
            switch (tier)
            {
                case "alarm": return RankAlarm;
                case "alert": return RankAlert;
                case "reference": return RankReference;
                default: return RankNone;
            }
        }

        private static string TierName(int rank)
        {
            switch (rank)
            {
                case RankAlarm: return "alarm";
                case RankAlert: return "alert";
                case RankReference: return "reference";
                default: return "none";
            }
        }

        // The tier names are the same in both languages (the terms the backend and the web use).
        private static string TierLabel(int rank)
        {
            switch (rank)
            {
                case RankAlarm: return "alarm";
                case RankAlert: return "alert";
                case RankReference: return "reference";
                default: return "normal";
            }
        }

        private static Color DotColor(int rank) =>
            rank == RankAlarm ? Brand.Danger : rank == RankAlert ? Brand.Warning : Brand.Primary;

        /// <summary>
        /// "dz -6.4 mm (όριο 5.0)" from the event's strongest breach; empty when it has none. The
        /// unit is the sensor type's own (kPa, mrad, µε…), none when unknown: never assumed mm.
        /// </summary>
        private static string Detail(JObject ev, int typeId)
        {
            var breaches = ev?["breaches"] as JArray;
            if (breaches == null) return "";
            JObject best = null;
            int bestRank = -1;
            double bestMagnitude = -1;
            foreach (JToken t in breaches)
            {
                var b = t as JObject;
                if (b == null) continue;
                int rank = Rank((string)b["tier"]);
                double magnitude = Math.Abs(Number(b["value"]));
                if (rank > bestRank || (rank == bestRank && magnitude > bestMagnitude))
                {
                    best = b;
                    bestRank = rank;
                    bestMagnitude = magnitude;
                }
            }
            if (best == null) return "";
            string field = (string)best["field"] ?? "";
            string value = Number(best["value"]).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
            string limit = Number(best["limit"]).ToString("0.0##", CultureInfo.InvariantCulture);
            string unit = SensorLimits.Unit(typeId, field);
            if (unit != "") unit = " " + unit;
            return L.T($"{field} {value}{unit} (όριο {limit})", $"{field} {value}{unit} (limit {limit})");
        }

        // The backend serialises decimals as strings in places; take either.
        private static double Number(JToken t)
        {
            if (t == null) return 0;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (double)t;
            return double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }

        // ------------------------------------------------------------------
        //  Toast
        // ------------------------------------------------------------------

        private void Enqueue(string text, Color dot)
        {
            // Full: the oldest unseen one goes, the newest news is the one that matters.
            if (toastQueue.Count >= MaxQueuedToasts) toastQueue.Dequeue();
            toastQueue.Enqueue(new Toast { Text = text, Dot = dot });
        }

        private void Update()
        {
            if (!toastVisible && toastQueue.Count == 0) return;
            if (toastVisible && Time.unscaledTime - toastShownAt >= ToastSeconds) HideToast();
            Camera cam = Camera.main;
            if (cam == null) return;
            if (!toastVisible && toastQueue.Count > 0) ShowToast(toastQueue.Dequeue(), cam);
            if (toastVisible) Place(cam, false);
        }

        private void ShowToast(Toast t, Camera cam)
        {
            EnsureToast();
            toastText.text = t.Text;
            toastDot.color = t.Dot;
            toastVisible = true;
            toastShownAt = Time.unscaledTime;
            Place(cam, true);
            toast.gameObject.SetActive(true);
        }

        private void HideToast()
        {
            toastVisible = false;
            if (toast != null) toast.gameObject.SetActive(false);
        }

        // In front of the head, a little below the eyes, following the gaze with a lag so it
        // does not jitter with every head movement (same feel as the navigation guide).
        private void Place(Camera cam, bool snap)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 target = cam.transform.position + forward * 0.7f + Vector3.down * 0.12f;
            toast.position = snap ? target : Vector3.Lerp(toast.position, target, 0.15f);
            VrUi.FaceViewer(toast, cam.transform);
        }

        private void EnsureToast()
        {
            if (toast != null) return;
            toast = VrUi.CreateCanvas("LiveAlertToast", transform, new Vector2(ToastWidth, ToastHeight), MetersPerPixel);
            VrUi.Card("Toast", toast, 0f, 0f, ToastWidth, ToastHeight, Brand.Surface, Brand.BorderStrong, 20);
            const float dot = 14f;
            toastDot = VrUi.Icon("Dot", toast, 20f, (ToastHeight - dot) / 2f, dot, Brand.DiscSprite, Brand.Primary);
            toastText = VrUi.Text("Text", toast, 44f, 0f, ToastWidth - 58f, ToastHeight, "", 17f, Brand.Fg);
            toast.gameObject.SetActive(false);
        }
    }
}
