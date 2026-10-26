using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Phase 0 of auto re-alignment (docs/proposals/auto-realignment.md §9): a measurement
    /// tool, not the feature. While on (Μενού › Ευθυγράμμιση, off by default) every control
    /// point touch also pins a spatial anchor there, one per project and point, replacing the
    /// point's previous one. On a later visit the project's anchors are loaded and localised,
    /// and per anchor the CSV gets: found or not, how fast, how far from a new touch / the new
    /// solution, and how far the anchors' mutual distances are from the project's.
    ///
    /// It never applies anything: alignment works exactly as without it, and the user still
    /// confirms every alignment by touching control points. Files: <see cref="FieldTestLog"/>.
    /// </summary>
    public class AnchorFieldTest : MonoBehaviour
    {
        public const int MaxAnchorsPerProject = 12;
        // Longer than the reference anchor's 8 s: outdoors, finding the map takes a while.
        private const double LocalizeTimeout = 20.0;
        private const string EnabledKey = "fieldtest.enabled";
        private const string ConditionKey = "fieldtest.condition";
        private const string SiteChangedKey = "fieldtest.siteChanged";

        /// <summary>Set by hand: the app cannot tell the light. Goes into every CSV row.</summary>
        public static readonly string[] Conditions = { "sunny", "cloudy", "indoor", "morning", "noon", "evening" };
        private static readonly string[] ConditionNamesEl = { "Ηλιοφάνεια", "Συννεφιά", "Εσωτερικός χώρος", "Πρωί", "Μεσημέρι", "Βράδυ" };
        private static readonly string[] ConditionNamesEn = { "Sunny", "Cloudy", "Indoor", "Morning", "Noon", "Evening" };

        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(EnabledKey, 0) == 1;
            set { PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static int Condition
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(ConditionKey, 0), 0, Conditions.Length - 1);
            set { PlayerPrefs.SetInt(ConditionKey, ((value % Conditions.Length) + Conditions.Length) % Conditions.Length); PlayerPrefs.Save(); }
        }

        public static string ConditionTag => Conditions[Condition];
        public static string ConditionName => (L.En ? ConditionNamesEn : ConditionNamesEl)[Condition];

        /// <summary>Excavation, scaffolding, machines moved since the anchors were made.</summary>
        public static bool SiteChanged
        {
            get => PlayerPrefs.GetInt(SiteChangedKey, 0) == 1;
            set { PlayerPrefs.SetInt(SiteChangedKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        // One saved anchor in this visit's measurement.
        private class Probe
        {
            public FieldTestLog.Entry Entry;
            public Transform Anchor;      // bound and localised, else null
            public float Seconds;         // from the start of the measurement
            public float? ErrH, PairErrH, PairErrV;
            public string Note;
        }

        private ProjectAlignment project;
        private MarkerManager markers;
        private SpatialAnchorManager reference;
        private string projectName;
        private FieldTestLog.Store store;
        private readonly Dictionary<string, Probe> probes = new Dictionary<string, Probe>();
        // Anchors pinned in this session, by point: measured directly by "Measure now".
        private readonly Dictionary<string, Transform> current = new Dictionary<string, Transform>();
        private readonly Dictionary<string, Vector3> createdAt = new Dictionary<string, Vector3>();
        // Bumped per touch / undo of a point, so a slow save that lost the race is discarded.
        private readonly Dictionary<string, int> generation = new Dictionary<string, int>();
        private readonly List<GameObject> owned = new List<GameObject>();
        private bool busy, touchedSinceSolution, wasModeActive;
        private Coroutine measureRoutine;

        public bool Busy => busy;

        /// <summary>Menu readout: "3/4 εντοπίστηκαν · 2.1 s · max 4.2 cm".</summary>
        public string Summary
        {
            get
            {
                if (!Enabled) return L.T("Ανενεργή", "Off");
                if (busy) return L.T("Μέτρηση…", "Measuring…");
                if (store == null || store.anchors.Count == 0) return L.T("Κανένα anchor ακόμα", "No anchors yet");
                if (probes.Count == 0) return L.T($"{store.anchors.Count} anchors · μέτρηση", $"{store.anchors.Count} anchors · measure");
                int ok = 0;
                float slowest = 0f, worst = -1f;
                foreach (Probe p in probes.Values)
                {
                    if (p.Anchor == null) continue;
                    ok++;
                    slowest = Mathf.Max(slowest, p.Seconds);
                    float? cm = p.ErrH ?? p.PairErrH;
                    if (cm.HasValue) worst = Mathf.Max(worst, cm.Value * 100f);
                }
                string max = worst >= 0f ? $" · max {worst:F1} cm" : "";
                return L.T($"{ok}/{probes.Count} εντοπίστηκαν · {slowest:F1} s{max}",
                    $"{ok}/{probes.Count} localized · {slowest:F1} s{max}");
            }
        }

        private void Start()
        {
            project = FindAnyObjectByType<ProjectAlignment>();
            markers = FindAnyObjectByType<MarkerManager>();
            reference = FindAnyObjectByType<SpatialAnchorManager>();
            if (project == null) { enabled = false; return; }
            project.OnPointPlaced += OnPointPlaced;
            project.OnPointRemoved += OnPointRemoved;
            project.OnAlignmentChanged += CheckProject;
            CheckProject();
        }

        private void OnDestroy()
        {
            if (project == null) return;
            project.OnPointPlaced -= OnPointPlaced;
            project.OnPointRemoved -= OnPointRemoved;
            project.OnAlignmentChanged -= CheckProject;
        }

        private void Update()
        {
            // Logged once the user has aligned again and put the alignment list away.
            bool mode = project.IsModeActive;
            if (wasModeActive && !mode && touchedSinceSolution) LogSolution();
            wasModeActive = mode;
        }

        // ------------------------------------------------------------------
        //  Project switch / measurement
        // ------------------------------------------------------------------

        private void CheckProject()
        {
            // Practice is not a site: it must neither log nor release the real project's anchors.
            if (!project.HasProject || project.IsPractice || project.ProjectName == projectName) return;
            if (measureRoutine != null) StopCoroutine(measureRoutine);
            measureRoutine = StartCoroutine(SwitchProject(project.ProjectName));
        }

        private IEnumerator SwitchProject(string name)
        {
            busy = true;
            projectName = name;
            touchedSinceSolution = false;
            ReleaseAll(false);
            store = FieldTestLog.LoadStore(name);
            yield return null; // destroyed anchors unbind at the end of the frame
            yield return ErasePending(store);
            // The reference anchor gives "distance from the original position": give it its chance.
            float until = Time.realtimeSinceStartup + 12f;
            while (reference != null && !reference.IsInitialized && Time.realtimeSinceStartup < until) yield return null;
            if (Enabled && store.anchors.Count > 0) yield return Measure("load");
            busy = false;
            measureRoutine = null;
        }

        /// <summary>Menu "Μέτρηση τώρα": localise every saved anchor of the project and log a row each.</summary>
        public void MeasureNow()
        {
            if (busy || store == null || !project.HasProject || project.IsPractice) return;
            measureRoutine = StartCoroutine(MeasureAgain());
        }

        private IEnumerator MeasureAgain()
        {
            busy = true;
            ReleaseAll(true);
            yield return null;
            yield return ErasePending(store);
            yield return Measure("manual");
            busy = false;
            measureRoutine = null;
        }

        /// <summary>Menu: erase this project's test anchors from the headset (CSV files stay).</summary>
        public void EraseAll()
        {
            if (busy || store == null || project.IsPractice) return;
            foreach (FieldTestLog.Entry e in store.anchors) store.pendingErase.Add(e.uuid);
            int n = store.anchors.Count;
            store.anchors.Clear();
            FieldTestLog.SaveStore(store);
            FieldTestLog.Append(new FieldTestLog.Row { Project = projectName, Phase = "erase", Note = $"{n} anchors erased by the user" });
            measureRoutine = StartCoroutine(EraseAllRoutine());
        }

        private IEnumerator EraseAllRoutine()
        {
            busy = true;
            ReleaseAll(false);
            yield return null;
            yield return ErasePending(store);
            busy = false;
            measureRoutine = null;
        }

        private IEnumerator Measure(string phase)
        {
            FieldTestLog.Store st = store;
            probes.Clear();
            touchedSinceSolution = false;
            float t0 = Time.realtimeSinceStartup;
#if !MOBILE_PREVIEW
            var byUuid = new Dictionary<Guid, Probe>();
            foreach (FieldTestLog.Entry e in st.anchors)
            {
                var probe = new Probe { Entry = e };
                probes[e.pointId] = probe;
                if (current.TryGetValue(e.pointId, out Transform live) && live != null)
                {
                    // Pinned earlier in this session: already bound, nothing to load.
                    if (IsLocalized(live)) probe.Anchor = live;
                    else probe.Note = "same session, not localized";
                }
                else if (Guid.TryParse(e.uuid, out Guid g)) byUuid[g] = probe;
                else probe.Note = "bad uuid";
            }

            if (byUuid.Count > 0)
            {
                var found = new List<OVRSpatialAnchor.UnboundAnchor>();
                var load = OVRSpatialAnchor.LoadUnboundAnchorsAsync(byUuid.Keys, found);
                while (!load.IsCompleted) yield return null;
                var loaded = load.GetResult();
                if (!loaded.Success)
                {
                    foreach (Probe p in byUuid.Values) p.Note = "load failed: " + loaded.Status;
                }
                else
                {
                    var ready = new List<(Probe, OVRSpatialAnchor.UnboundAnchor)>();
                    var waiting = new List<(Probe, OVRSpatialAnchor.UnboundAnchor, OVRTask<bool>)>();
                    foreach (Probe p in byUuid.Values) p.Note = "not found on the headset";
                    foreach (OVRSpatialAnchor.UnboundAnchor u in found)
                    {
                        if (!byUuid.TryGetValue(u.Uuid, out Probe p)) continue;
                        p.Note = null;
                        if (u.Localized) { p.Seconds = Time.realtimeSinceStartup - t0; ready.Add((p, u)); }
                        else waiting.Add((p, u, u.LocalizeAsync(LocalizeTimeout)));
                    }

                    // All localise in parallel; each one's time is taken when it finishes.
                    while (waiting.Count > 0)
                    {
                        for (int i = waiting.Count - 1; i >= 0; i--)
                        {
                            var (p, u, task) = waiting[i];
                            if (!task.IsCompleted) continue;
                            waiting.RemoveAt(i);
                            if (task.GetResult()) { p.Seconds = Time.realtimeSinceStartup - t0; ready.Add((p, u)); }
                            else p.Note = $"not localized within {LocalizeTimeout:0} s";
                        }
                        if (waiting.Count > 0) yield return null;
                    }

                    // Bound on the same frame the component is created (SDK requirement).
                    foreach (var (p, u) in ready)
                    {
                        var go = new GameObject("FieldTestAnchor " + p.Entry.pointId);
                        owned.Add(go);
                        var component = go.AddComponent<OVRSpatialAnchor>();
                        try
                        {
                            u.BindTo(component);
                            p.Anchor = go.transform;
                        }
                        catch (Exception ex)
                        {
                            p.Note = "bind failed: " + ex.Message;
                            Destroy(go);
                        }
                    }
                }
            }
            // A few frames for the bound anchors to report their tracked pose.
            yield return new WaitForSecondsRealtime(0.5f);
#else
            foreach (FieldTestLog.Entry e in st.anchors)
                probes[e.pointId] = new Probe { Entry = e, Note = "no anchor support in this build" };
            yield return null;
#endif
            if (st != store) yield break; // project switched meanwhile
            ComputePairs();
            foreach (Probe p in probes.Values) FieldTestLog.Append(ProbeRow(phase, p));
            Debug.Log($"[FieldTest] {phase}: {Summary}");
        }

        // After the user aligned again: each old anchor vs where the new solution puts its point.
        private void LogSolution()
        {
            touchedSinceSolution = false;
            if (!Enabled || probes.Count == 0 || !project.IsAligned || project.NeedsCheck) return;
            ComputePairs();
            foreach (Probe p in probes.Values)
                if (p.Anchor != null) FieldTestLog.Append(ProbeRow("solution", p));
        }

        private FieldTestLog.Row ProbeRow(string phase, Probe p)
        {
            FieldTestLog.Row r = NewRow(phase, p.Entry.pointId, p.Entry);
            r.Localized = p.Anchor != null;
            r.Note = p.Note;
            if (p.Anchor == null) return r;
            Vector3 pos = p.Anchor.position;
            r.Seconds = p.Seconds;
            r.Tracked = Tracked(p.Anchor);
            r.HeadsetDist = HeadsetDistance(pos);
            r.DistFromOriginal = DistanceFromOriginal(p.Entry, pos);
            if (p.PairErrH.HasValue) r.PairErrH = p.PairErrH.Value * 100.0;
            if (p.PairErrV.HasValue) r.PairErrV = p.PairErrV.Value * 100.0;
            if (TryPredict(p.Entry.pointId, out Vector3 predicted))
            {
                Vector3 d = pos - predicted;
                r.ErrHSolution = Flat(d) * 100.0;
                r.ErrVSolution = d.y * 100.0;
                p.ErrH = Flat(d);
            }
            return r;
        }

        // Consistency without any new touch: each anchor's distances to the other localised
        // anchors against the same distances in the project (worst pair, metres).
        private void ComputePairs()
        {
            foreach (Probe a in probes.Values)
            {
                a.PairErrH = a.PairErrV = null;
                if (a.Anchor == null || !TryPoint(a.Entry.pointId, out ProjectPoint pa)) continue;
                foreach (Probe b in probes.Values)
                {
                    if (b == a || b.Anchor == null || !TryPoint(b.Entry.pointId, out ProjectPoint pb)) continue;
                    Vector3 m = a.Anchor.position - b.Anchor.position;
                    double projH = Math.Sqrt((pa.e - pb.e) * (pa.e - pb.e) + (pa.n - pb.n) * (pa.n - pb.n));
                    float errH = (float)Math.Abs(Flat(m) - projH);
                    float errV = (float)Math.Abs(m.y - (pa.h - pb.h));
                    a.PairErrH = Mathf.Max(a.PairErrH ?? 0f, errH);
                    a.PairErrV = Mathf.Max(a.PairErrV ?? 0f, errV);
                }
            }
        }

        // ------------------------------------------------------------------
        //  Touches
        // ------------------------------------------------------------------

        private void OnPointPlaced(string pointId, Vector3 world)
        {
            if (!Enabled || store == null) return;
            touchedSinceSolution = true;
            // A new touch on a point whose old anchor was found: the direct comparison.
            if (probes.TryGetValue(pointId, out Probe p) && p.Anchor != null)
            {
                Vector3 d = p.Anchor.position - world;
                FieldTestLog.Row r = NewRow("touch", pointId, p.Entry);
                r.Localized = true;
                r.Seconds = p.Seconds;
                r.Tracked = Tracked(p.Anchor);
                r.HeadsetDist = HeadsetDistance(world);
                r.DistFromOriginal = DistanceFromOriginal(p.Entry, p.Anchor.position);
                r.ErrHTouch = Flat(d) * 100.0;
                r.ErrVTouch = d.y * 100.0;
                p.ErrH = Flat(d);
                FieldTestLog.Append(r);
            }
            StartCoroutine(CreateAnchor(pointId, world));
        }

        // Undo of a touch made in this session: its anchor goes too. Older anchors stay, they
        // are what is being measured.
        private void OnPointRemoved(string pointId)
        {
            if (!Enabled || store == null) return;
            Bump(pointId);
            if (!current.Remove(pointId)) return;
            FieldTestLog.Entry e = store.Find(pointId);
            if (e == null) return;
            store.anchors.Remove(e);
            store.pendingErase.Add(e.uuid);
            FieldTestLog.SaveStore(store);
            FieldTestLog.Append(new FieldTestLog.Row { Project = projectName, PointId = pointId, Phase = "undo", Uuid = e.uuid });
        }

        private int Bump(string pointId)
        {
            string key = projectName + "/" + pointId;
            int gen = generation.TryGetValue(key, out int g) ? g + 1 : 1;
            generation[key] = gen;
            return gen;
        }

        private IEnumerator CreateAnchor(string pointId, Vector3 world)
        {
            FieldTestLog.Store st = store;
            string name = projectName;
            if (st.Find(pointId) == null && st.anchors.Count >= MaxAnchorsPerProject)
            {
                FieldTestLog.Append(new FieldTestLog.Row { Project = name, PointId = pointId, Phase = "create",
                    Note = $"limit of {MaxAnchorsPerProject} anchors per project reached: not saved" });
                yield break;
            }
            int gen = Bump(pointId);
#if !MOBILE_PREVIEW
            var go = new GameObject("FieldTestAnchor " + pointId);
            go.transform.SetPositionAndRotation(world, Quaternion.identity);
            owned.Add(go);
            var anchor = go.AddComponent<OVRSpatialAnchor>();
            var created = anchor.WhenCreatedAsync();
            while (!created.IsCompleted) yield return null;
            if (anchor == null || !created.GetResult())
            {
                CreateFailed(name, pointId, "anchor not created (anchor support / permission?)");
                if (go != null) Destroy(go);
                yield break;
            }
            Guid uuid = anchor.Uuid;
            var save = anchor.SaveAnchorAsync();
            while (!save.IsCompleted) yield return null;
            var saved = save.GetResult();
            if (!saved.Success)
            {
                CreateFailed(name, pointId, "save failed: " + saved.Status);
                if (go != null) Destroy(go);
                yield break;
            }

            string key = name + "/" + pointId;
            if (anchor == null || generation[key] != gen)
            {
                // Released (project switch) or a newer touch / undo won: don't keep it.
                st.pendingErase.Add(uuid.ToString());
                FieldTestLog.SaveStore(st);
                if (go != null) Destroy(go);
                yield break;
            }

            FieldTestLog.Entry old = st.Find(pointId);
            if (old != null)
            {
                st.anchors.Remove(old);
                if (old.uuid != uuid.ToString()) st.pendingErase.Add(old.uuid); // erased once unbound
            }
            var entry = new FieldTestLog.Entry
            {
                pointId = pointId,
                uuid = uuid.ToString(),
                createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            if (reference != null && reference.IsPersistentAnchor && reference.ReferenceAnchor != null)
            {
                entry.referenceUuid = reference.ReferenceAnchorUuid;
                entry.referenceLocal = reference.ReferenceAnchor.InverseTransformPoint(world);
            }
            st.anchors.Add(entry);
            FieldTestLog.SaveStore(st);

            if (st == store)
            {
                current[pointId] = go.transform;
                createdAt[pointId] = world;
            }
            else Destroy(go);

            FieldTestLog.Row r = NewRow("create", pointId, entry);
            r.Localized = anchor.Localized;
            r.Tracked = anchor.IsTracked;
            r.HeadsetDist = HeadsetDistance(world);
            r.Note = old != null ? "replaced " + old.uuid : null;
            r.Project = name;
            FieldTestLog.Append(r);
#else
            yield break;
#endif
        }

        private void CreateFailed(string name, string pointId, string note)
        {
            Debug.LogWarning($"[FieldTest] {pointId}: {note}");
            FieldTestLog.Append(new FieldTestLog.Row { Project = name, PointId = pointId, Phase = "create", Localized = false, Note = note });
        }

        // ------------------------------------------------------------------
        //  Anchor bookkeeping
        // ------------------------------------------------------------------

        // Destroying unbinds; the saved anchor stays on the headset until erased.
        private void ReleaseAll(bool keepCurrent)
        {
            var keep = new HashSet<GameObject>();
            if (keepCurrent)
                foreach (Transform t in current.Values) if (t != null) keep.Add(t.gameObject);
            foreach (GameObject go in owned) if (go != null && !keep.Contains(go)) Destroy(go);
            owned.RemoveAll(go => go == null || !keep.Contains(go));
            probes.Clear();
            if (!keepCurrent) { current.Clear(); createdAt.Clear(); }
        }

        // Replaced / undone anchors, erased from the headset once nothing binds them.
        private IEnumerator ErasePending(FieldTestLog.Store st)
        {
            if (st == null || st.pendingErase.Count == 0) yield break;
#if !MOBILE_PREVIEW
            var sent = new List<string>(st.pendingErase);
            var uuids = new List<Guid>();
            foreach (string s in sent) if (Guid.TryParse(s, out Guid g)) uuids.Add(g);
            if (uuids.Count > 0)
            {
                var erase = OVRSpatialAnchor.EraseAnchorsAsync(Array.Empty<OVRSpatialAnchor>(), uuids);
                while (!erase.IsCompleted) yield return null;
                var result = erase.GetResult();
                if (!result.Success)
                {
                    // Not retried forever: an anchor already gone would fail every launch.
                    Debug.LogWarning($"[FieldTest] Erasing {uuids.Count} old anchor(s) failed: {result.Status}");
                    FieldTestLog.Append(new FieldTestLog.Row { Project = st.project, Phase = "erase",
                        Note = $"erasing {uuids.Count} old anchors failed: {result.Status}" });
                }
            }
            st.pendingErase.RemoveAll(sent.Contains);
            FieldTestLog.SaveStore(st);
#else
            yield break;
#endif
        }

        // ------------------------------------------------------------------
        //  Geometry helpers
        // ------------------------------------------------------------------

        private FieldTestLog.Row NewRow(string phase, string pointId, FieldTestLog.Entry e)
        {
            var r = new FieldTestLog.Row { Project = projectName, PointId = pointId, Phase = phase, Uuid = e?.uuid };
            if (e != null && DateTime.TryParse(e.createdUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime t))
                r.AgeHours = (DateTime.UtcNow - t.ToUniversalTime()).TotalHours;
            return r;
        }

        private bool TryPoint(string pointId, out ProjectPoint point) =>
            project.TryGetPoint(ProjectAlignment.MarkerPrefix + pointId, out point);

        // Where the current solution renders this control point (as MarkerManager places it).
        private bool TryPredict(string pointId, out Vector3 world)
        {
            world = Vector3.zero;
            Transform frame = markers != null ? markers.ReferencePoint : null;
            if (frame == null || !project.IsAligned || project.NeedsCheck) return false;
            if (!TryPoint(pointId, out ProjectPoint p)
                || !project.TryGetOrigin(out double oe, out double on, out double oh)
                || !project.TryGetLocalPose(out Quaternion rotation, out Vector3 translation)) return false;
            var local = new Vector3((float)(p.e - oe), (float)(p.h - oh), (float)(p.n - on));
            world = frame.TransformPoint(rotation * local + translation);
            return true;
        }

        // Known when the room's reference anchor that was localised at creation is localised
        // again (both anchors, so it is their relative drift), or within the same session.
        private double? DistanceFromOriginal(FieldTestLog.Entry e, Vector3 world)
        {
            if (reference != null && reference.IsPersistentAnchor && reference.ReferenceAnchor != null
                && !string.IsNullOrEmpty(e.referenceUuid) && e.referenceUuid == reference.ReferenceAnchorUuid)
                return (reference.ReferenceAnchor.InverseTransformPoint(world) - e.referenceLocal).magnitude;
            if (current.TryGetValue(e.pointId, out Transform t) && t != null && createdAt.TryGetValue(e.pointId, out Vector3 at)
                && string.Equals(store?.Find(e.pointId)?.uuid, e.uuid))
                return (world - at).magnitude;
            return null;
        }

        private static double? HeadsetDistance(Vector3 world)
        {
            Camera cam = Camera.main;
            return cam != null ? (cam.transform.position - world).magnitude : (double?)null;
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private static bool? Tracked(Transform t)
        {
#if !MOBILE_PREVIEW
            var a = t != null ? t.GetComponent<OVRSpatialAnchor>() : null;
            return a != null ? a.IsTracked : (bool?)null;
#else
            return null;
#endif
        }

        private static bool IsLocalized(Transform t)
        {
#if !MOBILE_PREVIEW
            var a = t != null ? t.GetComponent<OVRSpatialAnchor>() : null;
            return a != null && a.Localized;
#else
            return false;
#endif
        }
    }
}
