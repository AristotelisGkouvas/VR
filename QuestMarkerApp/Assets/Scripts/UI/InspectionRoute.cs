using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Inspection route: an ordered list of sensors to visit. Flat chevrons on the floor lead
    /// from the user's feet to the next one, a small HUD ahead shows progress, standing within
    /// 1.5 m of a sensor for a second ticks it and the route moves on. At the end (or when
    /// stopped early with something visited) a report is shown and saved under
    /// persistentDataPath/reports as JSON plus a plain-text summary.
    /// </summary>
    public class InspectionRoute : MonoBehaviour
    {
        private const float ArrivedWithin = 1.5f;   // metres, horizontal
        private const float DwellSeconds = 1.0f;    // stay this long before it counts
        private const float ArrowSpacing = 1.5f;    // metres between chevrons
        private const float ArrowLift = 0.02f;      // above the floor, so they don't z-fight passthrough
        private const int MaxArrows = 30;
        private const int NearArrows = 3;           // the last ones before the target go yellow
        private const float MarchSpeed = 0.8f;      // m/s the chevrons appear to flow
        private const float PulseWithin = 25f;      // metres: the "getting warmer" pulses start here
        private const float HudPixel = 0.0006f;     // metres per canvas pixel
        private const float HudW = 400f, HudH = 112f, TrackW = HudW - 40f;

        public static bool IsActive { get; private set; }
        /// <summary>Frame a B press skipped or stopped the route, so the same press does nothing else.</summary>
        public static int StoppedFrame { get; private set; } = -1;

        /// <summary>Position in the route (stops done so far); 0 when idle.</summary>
        public int Index => IsActive ? index : 0;
        public int Count => stops.Count;
        public string NextName => IsActive && index < stops.Count ? stops[index].Name : null;
        /// <summary>The JSON file of the last saved report, null until one was written.</summary>
        public string LastReportPath { get; private set; }

        private class RouteStop
        {
            public ProjectPoint Point;
            public string MarkerId, Name;
            public Vector3 Position;       // world, at ordering time
            public DateTime? VisitedAt;
            public string StateAtVisit;
            public bool Skipped;
        }

        private ProjectAlignment alignment;
        private MarkerManager markers;
        private FlowPanel flowPanel;
        private readonly List<RouteStop> stops = new List<RouteStop>();
        private int index;
        private string routeTitle;
        private DateTime startedAt;
        private Marker targetMarker;
        private float dwellSince = -1f;
        private float nextPulse;
        private bool hudJustShown;

        // Floor chevrons
        private Transform arrowRoot;
        private readonly Transform[] arrows = new Transform[MaxArrows];
        private readonly MeshRenderer[] arrowRenderers = new MeshRenderer[MaxArrows];
        private int arrowsShown;
        private Mesh arrowMesh;
        private Material arrowFar, arrowNear;
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        // HUD
        private Transform hud;
        private TextMeshProUGUI title, detail, hint;
        private Image fill;
        private int textKey = int.MinValue;
        private int titleIndex = -1;
        private int fillIndex = -1;
        private bool textMissing;

        public void Init(ProjectAlignment projectAlignment, MarkerManager markerManager, FlowPanel panel)
        {
            alignment = projectAlignment;
            markers = markerManager;
            flowPanel = panel;
            Build();
            Hands.OnChanged += RefreshHint;
            L.Changed += OnLanguageChanged;
        }

        private void RefreshHint()
        {
            if (hint != null)
                hint.text = L.En ? $"{Hands.SecondaryName}: skip   ·   Menu: stop" : $"{Hands.SecondaryName}: παράλειψη   ·   Μενού: διακοπή";
        }

        // The HUD texts are rewritten on the next frame once their keys are reset.
        private void OnLanguageChanged()
        {
            RefreshHint();
            textKey = int.MinValue;
            textMissing = false;
        }

        private void OnDestroy()
        {
            Hands.OnChanged -= RefreshHint;
            L.Changed -= OnLanguageChanged;
            Cleanup();
            if (arrowMesh != null) Destroy(arrowMesh);
            if (arrowFar != null) Destroy(arrowFar);
            if (arrowNear != null) Destroy(arrowNear);
        }

        // ------------------------------------------------------------------
        //  Starting
        // ------------------------------------------------------------------

        /// <summary>Why a route cannot start right now; null when it can.</summary>
        public string Unavailable(bool alertsOnly = false)
        {
            if (alignment == null || !alignment.HasProject) return L.T("Χωρίς έργο", "No project");
            if (!alignment.IsAligned) return L.T("Πρώτα ευθυγράμμιση", "Align first");
            if (alertsOnly)
            {
                bool any = false;
                foreach (ProjectPoint p in alignment.Points)
                    if (IsAlarm(p) || IsAlert(p)) { any = true; break; }
                if (!any) return L.T("Κανένας αισθητήρας σε συναγερμό", "No sensors in alarm");
            }
            return null;
        }

        /// <summary>Only the sensors in alarm or alert, alarms first.</summary>
        public void StartAlerts()
        {
            if (alignment == null) return;
            var list = new List<ProjectPoint>();
            foreach (ProjectPoint p in alignment.Points) if (IsAlarm(p)) list.Add(p);
            int alarms = list.Count;
            foreach (ProjectPoint p in alignment.Points) if (IsAlert(p)) list.Add(p);
            Begin(list, L.T("Συναγερμοί", "Alarms"), alarms);
        }

        public void StartAll()
        {
            if (alignment == null) return;
            var list = new List<ProjectPoint>();
            foreach (ProjectPoint p in alignment.Points) if (p != null) list.Add(p);
            Begin(list, L.T("Όλοι οι αισθητήρες", "All sensors"), 0);
        }

        /// <summary>A caller-chosen set; the walking order is still solved from where the user stands.</summary>
        public void StartWith(IList<ProjectPoint> ordered, string title) => Begin(ordered, title, 0);

        // firstGroup: how many leading points must come before the rest (alarms before alerts);
        // each group is ordered nearest-first from where the previous one ended.
        private void Begin(IList<ProjectPoint> points, string title, int firstGroup)
        {
            End(false);
            if (points == null || points.Count == 0 || markers == null) return;

            var groupA = new List<RouteStop>();
            var groupB = new List<RouteStop>();
            for (int i = 0; i < points.Count; i++)
            {
                ProjectPoint p = points[i];
                if (p == null) continue;
                string id = ProjectAlignment.MarkerPrefix + p.id;
                // Not rendered (project not aligned, filtered layer): nothing to walk to.
                if (!markers.ActiveMarkers.TryGetValue(id, out Marker m) || m == null || !m.gameObject.activeInHierarchy) continue;
                var stop = new RouteStop
                {
                    Point = p, MarkerId = id,
                    Name = string.IsNullOrEmpty(p.label) ? p.id : p.label,
                    Position = m.transform.position
                };
                (i < firstGroup ? groupA : groupB).Add(stop);
            }
            if (groupA.Count + groupB.Count == 0)
            {
                Debug.LogWarning("[Route] No rendered sensors to visit");
                return;
            }

            Camera cam = Headset.Camera;
            Vector3 from = cam != null ? cam.transform.position : Vector3.zero;
            stops.Clear();
            from = OrderNearestFirst(groupA, from, stops);
            OrderNearestFirst(groupB, from, stops);

            routeTitle = title;
            startedAt = DateTime.Now;
            index = 0;
            dwellSince = -1f;
            nextPulse = 0f;
            textKey = int.MinValue;
            titleIndex = fillIndex = -1;
            textMissing = false;
            LastReportPath = null;
            IsActive = true;
            hudJustShown = true;
            Feedback.Play(Feedback.Sound.Click);
            if (hud != null) hud.gameObject.SetActive(true);
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
#endif
            Debug.Log($"[Route] '{title}' started with {stops.Count} stop(s)");
        }

        // Greedy nearest neighbour over the horizontal distance: good enough for a site walk,
        // and it never sends the user back past a sensor they just left. Returns the last position.
        private static Vector3 OrderNearestFirst(List<RouteStop> pool, Vector3 from, List<RouteStop> into)
        {
            while (pool.Count > 0)
            {
                int best = 0;
                float bestD = float.MaxValue;
                for (int i = 0; i < pool.Count; i++)
                {
                    Vector3 d = pool[i].Position - from;
                    float flat = d.x * d.x + d.z * d.z;
                    if (flat < bestD) { bestD = flat; best = i; }
                }
                into.Add(pool[best]);
                from = pool[best].Position;
                pool.RemoveAt(best);
            }
            return from;
        }

        private static bool IsAlarm(ProjectPoint p) => p != null && p.alertState == "alarm";
        private static bool IsAlert(ProjectPoint p) => p != null && p.alertState == "alert";

        // ------------------------------------------------------------------
        //  Progress
        // ------------------------------------------------------------------

        /// <summary>Mark the current sensor as skipped and move to the next.</summary>
        public void Skip()
        {
            if (!IsActive || index >= stops.Count) return;
            stops[index].Skipped = true;
            StoppedFrame = Time.frameCount;
            Feedback.Play(Feedback.Sound.Undo, 0.6f);
            Advance();
        }

        /// <summary>End early. What was visited is still reported (nothing visited: just stops).</summary>
        public void Stop()
        {
            if (!IsActive) return;
            StoppedFrame = Time.frameCount;
            End(true);
        }

        private void Visit(RouteStop stop)
        {
            stop.VisitedAt = DateTime.Now;
            // The marker carries the live state (readings may have refreshed since the project loaded).
            stop.StateAtVisit = targetMarker != null && targetMarker.Data != null && !string.IsNullOrEmpty(targetMarker.Data.status)
                ? targetMarker.Data.status
                : stop.Point.alertState;
            Feedback.Play(Feedback.Sound.Success);
#if !MOBILE_PREVIEW
            Feedback.Buzz(Hands.Dominant, 0.9f, 0.3f);
#endif
            Advance();
        }

        private void Advance()
        {
            ReleaseTarget();
            index++;
            dwellSince = -1f;
            nextPulse = 0f;
            textKey = int.MinValue;
            if (index >= stops.Count) End(true);
        }

        private void ReleaseTarget()
        {
            if (targetMarker != null) targetMarker.SetNavigationTarget(false);
            targetMarker = null;
        }

        // report: show and save what happened (a completed route always has something to say;
        // an early stop only when at least one sensor was reached).
        private void End(bool report)
        {
            if (!IsActive) return;
            bool completed = index >= stops.Count;
            int visited = 0;
            foreach (RouteStop s in stops) if (s.VisitedAt.HasValue) visited++;
            IsActive = false;
            if (report && (completed || visited > 0)) Report(completed, visited);
            Cleanup();
        }

        private void Cleanup()
        {
            ReleaseTarget();
            IsActive = false;
            if (hud != null) hud.gameObject.SetActive(false);
            for (int i = 0; i < arrowsShown; i++) arrows[i].gameObject.SetActive(false);
            arrowsShown = 0;
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private void Build()
        {
            arrowMesh = ArrowMesh();
            Shader sprite = Shader.Find("Sprites/Default");
            arrowFar = new Material(sprite) { name = "RouteArrow", color = Brand.Primary };
            arrowNear = new Material(sprite) { name = "RouteArrowNear", color = Brand.Control };
            arrowRoot = new GameObject("RouteArrows").transform;
            arrowRoot.SetParent(transform, false);
            for (int i = 0; i < MaxArrows; i++)
            {
                var go = new GameObject("Arrow");
                go.transform.SetParent(arrowRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = arrowMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = arrowFar;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.sortingOrder = 118;
                go.SetActive(false);
                arrows[i] = go.transform;
                arrowRenderers[i] = r;
            }

            hud = new GameObject("RouteHud").transform;
            hud.SetParent(transform, false);
            RectTransform c = VrUi.CreateCanvas("Canvas", hud, new Vector2(HudW, HudH), HudPixel);
            VrUi.Card("Bg", c, 0, 0, HudW, HudH, Brand.Surface.WithAlpha(0.96f), Brand.BorderStrong, 20);
            VrUi.Icon("Icon", c, 18, 14, 26, Brand.UiIconSprite("compass"), Brand.Primary);
            title = VrUi.Text("Title", c, 54, 10, HudW - 70, 34, "", 20f, Brand.Fg, true);
            detail = VrUi.Text("Detail", c, 54, 42, HudW - 70, 26, "", 15f, Brand.FgMuted);
            VrUi.Panel("Track", c, 20, 76, TrackW, 6, Brand.SurfaceSunken, 3);
            fill = VrUi.Panel("Fill", c, 20, 76, 6, 6, Brand.Primary, 3);
            hint = VrUi.Text("Hint", c, 20, 86, TrackW, 22, "", 13f, Brand.FgFaint, false, TextAlignmentOptions.Center);
            RefreshHint();
            hud.gameObject.SetActive(false);
        }

        // A flat chevron arrow in the XZ plane pointing +Z, 16 cm long (as SensorNavigator's).
        private static Mesh ArrowMesh()
        {
            var v = new[]
            {
                new Vector3(0f, 0f, 0.09f),       // tip
                new Vector3(-0.07f, 0f, 0.01f),
                new Vector3(-0.025f, 0f, 0.01f),
                new Vector3(-0.025f, 0f, -0.07f),
                new Vector3(0.025f, 0f, -0.07f),
                new Vector3(0.025f, 0f, 0.01f),
                new Vector3(0.07f, 0f, 0.01f),
            };
            var tris = new[] { 0, 6, 1, 2, 5, 4, 2, 4, 3 };
            var mesh = new Mesh { name = "RouteArrow", vertices = v, triangles = tris };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------
        //  Per frame
        // ------------------------------------------------------------------

        private void Update()
        {
#if !MOBILE_PREVIEW
            // B skips, unless a panel owns the buttons or alignment is placing (B undoes there).
            // Navigation and the time machine sit on top of a route and take B first (see
            // UiState.Context): one press used to stop those and skip a stop as well.
            bool aligning = alignment != null && alignment.IsModeActive;
            int frame = Time.frameCount;
            bool otherOwnsB = SensorNavigator.IsActive || SensorNavigator.StoppedFrame == frame
                              || DisplacementView.IsActive || DisplacementView.ClosedFrame == frame
                              || MeasureTool.IsActive || MeasureTool.ClosedFrame == frame;
            if (IsActive && !aligning && !otherOwnsB && !UiState.InputBlocked && Hands.DominantSecondaryDown()) Skip();
#endif
        }

        private void LateUpdate()
        {
            if (!IsActive || index >= stops.Count) return;
            Camera cam = Headset.Camera;
            if (cam == null || markers == null || hud == null) return;
            RouteStop stop = stops[index];

            // A re-solve or reload can recycle the pooled marker for another sensor: re-check.
            if (targetMarker != null && (targetMarker.MarkerId != stop.MarkerId || !targetMarker.gameObject.activeInHierarchy))
                ReleaseTarget();
            if (targetMarker == null)
            {
                if (!markers.ActiveMarkers.TryGetValue(stop.MarkerId, out targetMarker) || targetMarker == null)
                {
                    if (!textMissing)
                    {
                        textMissing = true;
                        textKey = int.MinValue;
                        detail.text = L.T("Ο αισθητήρας δεν φαίνεται — ευθυγράμμισε το έργο", "Sensor not visible — align the project");
                    }
                    RefreshTitle();
                    RefreshFill();
                    PlaceHud(cam);
                    HideArrows();
                    return;
                }
                targetMarker.SetNavigationTarget(true);
            }
            textMissing = false;

            // The card opened on this sensor counts: the user is clearly looking at it.
            if (SensorCard.IsOpen && SensorCard.MarkerIdOpen == stop.MarkerId) { Visit(stop); return; }

            Vector3 head = cam.transform.position;
            Vector3 at = targetMarker.transform.position;
            Vector3 flat = new Vector3(at.x - head.x, 0f, at.z - head.z);
            float horizontal = flat.magnitude;
            float dh = at.y - head.y; // eye-relative, like the sensor card

            if (horizontal <= ArrivedWithin)
            {
                if (dwellSince < 0f) dwellSince = Time.time;
                else if (Time.time - dwellSince >= DwellSeconds) { Visit(stop); return; }
            }
            else
            {
                dwellSince = -1f;
                Pulse(horizontal);
            }

            RefreshTitle();
            RefreshFill();
            // Re-format only when a shown digit changes (these ran every frame).
            int key = (horizontal < 10f ? Mathf.RoundToInt(horizontal * 10f) : 1000000 + Mathf.RoundToInt(horizontal)) * 4096
                      + Mathf.RoundToInt(Mathf.Abs(dh) * 10f) + (dh >= 0 ? 2048 : 0);
            if (key != textKey)
            {
                textKey = key;
                detail.text = horizontal <= ArrivedWithin
                    ? (L.En ? $"Arrived   ·   {(dh >= 0 ? "↑" : "↓")} {Mathf.Abs(dh):F1} m" : $"Έφτασες   ·   {(dh >= 0 ? "↑" : "↓")} {Mathf.Abs(dh):F1} m")
                    : $"{(horizontal < 10f ? horizontal.ToString("F1") : horizontal.ToString("F0"))} m   ·   "
                      + $"{(dh >= 0 ? "↑" : "↓")} {Mathf.Abs(dh):F1} m";
            }

            PlaceHud(cam);
            LayArrows(cam, at, horizontal);
        }

        private void RefreshTitle()
        {
            if (titleIndex == index) return;
            titleIndex = index;
            title.text = $"{index + 1} / {stops.Count}   ·   {stops[index].Name}";
        }

        private void RefreshFill()
        {
            if (fillIndex == index) return;
            fillIndex = index;
            float progress = stops.Count > 0 ? (float)index / stops.Count : 0f;
            fill.rectTransform.sizeDelta = new Vector2(Mathf.Max(6f, TrackW * progress), 6f);
        }

        // "Getting warmer": a tap on the pointing hand that comes faster and harder as the
        // target nears (every ~1.2 s at 25 m, ~5 a second at arrival), a soft ping within 4 m.
        private void Pulse(float horizontal)
        {
            if (horizontal > PulseWithin || Time.time < nextPulse) return;
            float k = Mathf.Clamp01(horizontal / PulseWithin);
            nextPulse = Time.time + Mathf.Lerp(0.18f, 1.2f, k);
#if !MOBILE_PREVIEW
            Feedback.Buzz(Hands.Dominant, Mathf.Lerp(0.7f, 0.2f, k), 0.04f, 0.8f);
#endif
            if (horizontal < 4f) Feedback.Play(Feedback.Sound.Ping, 0.35f);
        }

        // The HUD rides 55 cm ahead at chest height, a little to the left so it never sits on
        // top of the SensorNavigator guide should both be up.
        private void PlaceHud(Camera cam)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 target = cam.transform.position + forward * 0.55f + Vector3.down * 0.32f - right * 0.18f;
            hud.position = Vector3.Lerp(hud.position, target, hudJustShown ? 1f : 0.2f);
            hudJustShown = false;
            VrUi.FaceViewer(hud, cam.transform);
        }

        // Tracking floor when the rig is floor-level; otherwise assume the head is 1.5 m up.
        private float FloorY(Camera cam)
        {
#if !MOBILE_PREVIEW
            if (rig != null && rig.trackingSpace != null && OVRManager.instance != null
                && OVRManager.instance.trackingOriginType == OVRManager.TrackingOrigin.FloorLevel)
                return rig.trackingSpace.position.y;
#endif
            return cam.transform.position.y - 1.5f;
        }

        // Chevrons every 1.5 m along the straight line from the feet to the sensor's foot point,
        // drifting towards it so the line reads as "go this way"; the last few turn yellow.
        private void LayArrows(Camera cam, Vector3 target, float horizontal)
        {
            float y = FloorY(cam) + ArrowLift;
            Vector3 feet = new Vector3(cam.transform.position.x, y, cam.transform.position.z);
            Vector3 dir = new Vector3(target.x - feet.x, 0f, target.z - feet.z);
            if (horizontal < 0.5f) { HideArrows(); return; }
            dir /= horizontal;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);

            float offset = Mathf.Repeat(Time.time * MarchSpeed, ArrowSpacing);
            float end = horizontal - 0.4f; // stop short of the icon itself
            int n = 0;
            for (float d = offset; d < end && n < MaxArrows; d += ArrowSpacing)
            {
                if (d < 0.4f) continue; // right under the feet: out of view and in the way
                Transform a = arrows[n];
                a.SetPositionAndRotation(feet + dir * d, rot);
                Material m = end - d <= NearArrows * ArrowSpacing ? arrowNear : arrowFar;
                if (arrowRenderers[n].sharedMaterial != m) arrowRenderers[n].sharedMaterial = m;
                n++;
            }
            for (int i = arrowsShown; i < n; i++) arrows[i].gameObject.SetActive(true);
            for (int i = n; i < arrowsShown; i++) arrows[i].gameObject.SetActive(false);
            arrowsShown = n;
        }

        private void HideArrows()
        {
            for (int i = 0; i < arrowsShown; i++) arrows[i].gameObject.SetActive(false);
            arrowsShown = 0;
        }

        // ------------------------------------------------------------------
        //  Report
        // ------------------------------------------------------------------

        private void Report(bool completed, int visited)
        {
            DateTime finishedAt = DateTime.Now;
            int alarms = 0, alerts = 0, normal = 0;
            var skipped = new List<string>();
            foreach (RouteStop s in stops)
            {
                if (s.Skipped) skipped.Add(s.Name);
                if (!s.VisitedAt.HasValue) continue;
                if (s.StateAtVisit == "alarm") alarms++;
                else if (s.StateAtVisit == "alert") alerts++;
                else normal++;
            }

            // The summary is shown and also written to the .txt, both in the interface language;
            // the JSON keeps the state codes.
            string heading = L.En
                ? $"{(completed ? "Completed" : "Stopped")} · {visited} of {stops.Count}"
                : $"{(completed ? "Ολοκληρώθηκε" : "Διακόπηκε")} · {visited} από {stops.Count}";
            var lines = new List<string>
            {
                L.T("Διάρκεια ", "Duration ") + Duration(finishedAt - startedAt),
                L.En
                    ? $"Alarm {alarms}   ·   Alert {alerts}   ·   Reference {normal}"
                    : $"Συναγερμός {alarms}   ·   Προειδοποίηση {alerts}   ·   Κανονικό {normal}"
            };
            if (skipped.Count > 0) lines.Add(L.T("Παραλείφθηκαν: ", "Skipped: ") + string.Join(", ", skipped));
            int noted = FieldNotes.Since(alignment != null ? alignment.TerrawebProjectId : 0,
                alignment != null ? alignment.ProjectName : "", startedAt.ToUniversalTime()).Count;
            if (noted > 0) lines.Add(L.En ? $"Notes: {noted}" : $"Σημειώσεις: {noted}");

            string file = Save(completed, finishedAt, heading, lines);
            if (file != null) lines.Add(L.T("Αρχείο: ", "File: ") + Path.GetFileName(file));
            else lines.Add(L.T("Η αναφορά δεν αποθηκεύτηκε", "Report not saved"));

            string project = alignment != null ? alignment.ProjectName : "Project";
            if (flowPanel != null)
                flowPanel.ShowMessage(L.T("Επιθεώρηση", "Inspection"), project, heading, string.Join("\n", lines), false, flowPanel.Close);
            Debug.Log($"[Route] {heading} ({routeTitle})");
        }

        private static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return L.En ? $"{(int)t.TotalSeconds} s" : $"{(int)t.TotalSeconds} δευτ.";
            int minutes = (int)Math.Round(t.TotalMinutes);
            if (minutes < 60) return L.En ? $"{minutes} min" : minutes == 1 ? "1 λεπτό" : $"{minutes} λεπτά";
            return L.En ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes / 60} ώ. {minutes % 60} λ.";
        }

        // reports/route_yyyyMMdd_HHmm.json plus a .txt with the same summary lines. Returns the
        // JSON path, or null when nothing could be written (the headset may deny the folder).
        private string Save(bool completed, DateTime finishedAt, string heading, List<string> lines)
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "reports");
                Directory.CreateDirectory(dir);
                string stem = Path.Combine(dir, "route_" + finishedAt.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture));
                string json = stem + ".json";
                File.WriteAllText(json, Json(completed, finishedAt));

                var txt = new StringBuilder();
                txt.Append(L.T("Επιθεώρηση · ", "Inspection · ")).Append(alignment != null ? alignment.ProjectName : "Project")
                   .Append(" · ").AppendLine(routeTitle ?? "");
                txt.AppendLine(heading);
                foreach (string line in lines) txt.AppendLine(line);
                txt.AppendLine();
                foreach (RouteStop s in stops)
                {
                    txt.Append(s.VisitedAt.HasValue ? "[x] " : s.Skipped ? "[-] " : "[ ] ").Append(s.Name);
                    if (s.VisitedAt.HasValue)
                        txt.Append("  ").Append(s.VisitedAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture))
                           .Append("  ").Append(StateName(s.StateAtVisit));
                    else if (s.Skipped) txt.Append(L.T("  παραλείφθηκε", "  skipped"));
                    txt.AppendLine();
                }
                // Notes taken on site during the walk (sensor cards).
                List<FieldNotes.Note> notes = FieldNotes.Since(alignment != null ? alignment.TerrawebProjectId : 0,
                    alignment != null ? alignment.ProjectName : "", startedAt.ToUniversalTime());
                if (notes.Count > 0)
                {
                    txt.AppendLine().AppendLine(L.T("ΣΗΜΕΙΩΣΕΙΣ", "NOTES"));
                    foreach (FieldNotes.Note n in notes)
                        txt.Append(n.Utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)).Append("  ")
                           .Append(n.label).Append(": ").AppendLine(n.text);
                }
                File.WriteAllText(stem + ".txt", txt.ToString());
                LastReportPath = json;
                Debug.Log($"[Route] Report saved: {json}");
                return json;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Route] Could not save report: {e.Message}");
                return null;
            }
        }

        private static string StateName(string state) =>
            state == "alarm" ? L.T("Συναγερμός", "Alarm") : state == "alert" ? L.T("Προειδοποίηση", "Alert") : L.T("Κανονικό", "Reference");

        // Hand-built: JsonUtility cannot write a null for an unvisited stop.
        private string Json(bool completed, DateTime finishedAt)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"project\":\"").Append(JsonText.Escape(alignment != null ? alignment.ProjectName : "")).Append('"');
            sb.Append(",\"title\":\"").Append(JsonText.Escape(routeTitle ?? "")).Append('"');
            sb.Append(",\"completed\":").Append(completed ? "true" : "false");
            sb.Append(",\"startedAt\":\"").Append(Iso(startedAt)).Append('"');
            sb.Append(",\"finishedAt\":\"").Append(Iso(finishedAt)).Append('"');
            sb.Append(",\"items\":[");
            for (int i = 0; i < stops.Count; i++)
            {
                RouteStop s = stops[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(JsonText.Escape(s.Point.id ?? "")).Append('"');
                sb.Append(",\"name\":\"").Append(JsonText.Escape(s.Name ?? "")).Append('"');
                sb.Append(",\"sensorId\":\"").Append(JsonText.Escape(s.Point.sensorId ?? "")).Append('"');
                sb.Append(",\"alertState\":\"").Append(JsonText.Escape(s.StateAtVisit ?? s.Point.alertState ?? "")).Append('"');
                sb.Append(",\"visitedAt\":");
                if (s.VisitedAt.HasValue) sb.Append('"').Append(Iso(s.VisitedAt.Value)).Append('"');
                else sb.Append("null");
                sb.Append(",\"skipped\":").Append(s.Skipped ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}
