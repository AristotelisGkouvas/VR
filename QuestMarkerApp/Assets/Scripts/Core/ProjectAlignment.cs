using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Markers;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Survey-project alignment: receives project points (EGSA87/UTM + absolute elevation),
    /// lets the user pick points from a list on the left controller and place each one by
    /// touching the physical spot with the right controller tip. With 2+ placed points it
    /// solves yaw + translation (no scale — a mismatch is reported as error, not absorbed)
    /// and renders every project point at its real elevation.
    ///
    /// Frames: project E/N/H → local metres (x = East, y = Up, z = North) minus a double
    /// origin, then → reference-anchor local space, which is what MarkerManager consumes.
    /// </summary>
    public class ProjectAlignment : MonoBehaviour
    {
        public const string MarkerPrefix = "prj:";
        private const string LegacySaveFile = "project_alignment.json";
        private const string LastProjectFile = "project_last.txt";
        /// <summary>Placement error assumed per touched point, for the predicted-error readout.</summary>
        private const float TouchSigma = 0.02f;
        /// <summary>A re-touched control point within this of its predicted spot confirms a restored alignment.</summary>
        private const float RecheckTolerance = 0.10f;

        [Header("Placement")]
        [Tooltip("Tip cursor offset from the right controller anchor (controller local space).")]
        [SerializeField] private Vector3 tipOffset = new Vector3(0f, -0.01f, 0.06f);
        [SerializeField] private float tipCursorSize = 0.012f;

        [Header("Markers")]
        [SerializeField] private float pointScale = 0.06f;
        [SerializeField] private string pointColor = "#00E5FF";
        [SerializeField] private string controlColor = "#FFD400";

        [Header("Panel")]
        [Tooltip("Panel centre above the left controller (metres, world up).")]
        [SerializeField] private float panelLift = 0.24f;
        [Tooltip("On: the panel rides above the off-hand controller like a clipboard. Off: it stays in " +
                 "the room like the menu and glides back in front once you turn more than 55° away.")]
        [SerializeField] private bool panelOnHand = true;
        private const float PanelDistance = 0.6f, PanelDrop = 0.08f; // as the main menu
        private bool panelPlaced, panelGliding;
        private const string PanelOnHandPref = "align.panelOnHand";
        private bool? panelOnHandPref;

        /// <summary>Clipboard on the off hand (true) or in the room with lazy follow. Remembered;
        /// the inspector value is only the first-run default.</summary>
        public bool PanelOnHand
        {
            get => panelOnHandPref ?? (bool)(panelOnHandPref = PlayerPrefs.GetInt(PanelOnHandPref, panelOnHand ? 1 : 0) == 1);
            set
            {
                panelOnHandPref = value;
                PlayerPrefs.SetInt(PanelOnHandPref, value ? 1 : 0);
                PlayerPrefs.Save();
                panelPlaced = false;
                panelGliding = false;
            }
        }

        [SerializeField] private MarkerManager markerManager;
        [SerializeField] private SpatialAnchorManager anchors;
        // Placements came from disk; cleared once the saved spatial anchor relocalises.
        private bool awaitingAnchor;

        public event Action OnAlignmentChanged;
        /// <summary>A control point was touched: its id and the tip's world position (after the solve).</summary>
        public event Action<string, Vector3> OnPointPlaced;
        /// <summary>A placed control point was removed (undo).</summary>
        public event Action<string> OnPointRemoved;

        private ProjectRequest project;
        private double originE, originN, originH;
        // Placed control points, in reference-anchor local space.
        private readonly Dictionary<string, Vector3> placements = new Dictionary<string, Vector3>();
        // Placement order, so B undoes the last touch rather than whatever row is selected.
        private readonly List<string> placementOrder = new List<string>();
        // Alignment restored from disk or survived a recenter: the tracking origin may have moved,
        // so it is shown but flagged until one control point is touched again.
        private bool needsCheck;
        private float farthestDistance, predictedError;
        private string lastMessage;
        // The last message when it was good news: compared by reference, so a language switch
        // after it was set does not change its colour.
        private string goodMessage;
        private bool recenterHooked;
        private readonly HashSet<string> renderedIds = new HashSet<string>();
        // Set by a touch: the next render plays the light wave outwards from that spot.
        private Vector3? waveFrom;

        private bool isAligned;
        private Quaternion solvedRotation = Quaternion.identity;
        private Vector3 solvedTranslation;
        private float solvedYawDeg;
        private float rmsHorizontal, rmsVertical, maxResidual;
        private readonly Dictionary<string, float> residuals = new Dictionary<string, float>();
        private string worstId, suggestedId;
        private PairCheck pairCheck;
        private LiveGuide live;
        private float nextLiveRefresh;

        /// <summary>The two placed points furthest apart: project vs as-placed horizontal distance.</summary>
        public struct PairCheck
        {
            public bool Valid;
            public string A, B;
            public float Project, Measured;
        }

        /// <summary>While placing: how far the tip is from an already-placed point vs how far it should be.</summary>
        public struct LiveGuide
        {
            public bool Valid;
            public string RefId;
            public float Measured, Expected, MeasuredDh, ExpectedDh;
        }

        private bool modeActive;
        private int selectedIndex;
        private float nextStickMove;
        // Held stick speeds up: one step per 0.2 s, then ~16 per second after 0.8 s.
        private float stickHeldSince = -1f;

        private float StickRepeatDelay()
        {
            if (stickHeldSince < 0f) stickHeldSince = Time.unscaledTime;
            return Time.unscaledTime - stickHeldSince > 0.8f ? 0.06f : 0.2f;
        }

        private ProjectPanel panel;
        private Transform tipCursor;
#if !MOBILE_PREVIEW
        private OVRCameraRig cameraRig;
#endif

        public bool HasProject => project != null && project.points != null && project.points.Length > 0;
        public bool IsModeActive => modeActive;
        public bool IsAligned => isAligned;
        /// <summary>
        /// The built-in office practice project (PracticeProject): never saved, restored or
        /// reported to the LAN, and its touches raise no placement events.
        /// </summary>
        public bool IsPractice { get; private set; }
        public string ProjectName => HasProject && !string.IsNullOrEmpty(project.name) ? project.name : "Project";
        /// <summary>Backend project the sensors came from (0 for CSV / bridge projects).</summary>
        public int TerrawebProjectId => HasProject ? project.terrawebProjectId : 0;
        public IReadOnlyList<ProjectPoint> Points =>
            HasProject ? (IReadOnlyList<ProjectPoint>)project.points : Array.Empty<ProjectPoint>();
        public int SelectedIndex => selectedIndex;
        public int PlacedCount => placements.Count;
        /// <summary>Placed control points, oldest first (the panel lists them in this order).</summary>
        public IReadOnlyList<string> PlacementOrder => placementOrder;
        public bool IsPlaced(string id) => !string.IsNullOrEmpty(id) && placements.ContainsKey(id);

        /// <summary>The project point behind a rendered marker id ("prj:&lt;id&gt;").</summary>
        public bool TryGetPoint(string markerId, out ProjectPoint point)
        {
            point = null;
            if (!HasProject || markerId == null || !markerId.StartsWith(MarkerPrefix)) return false;
            string id = markerId.Substring(MarkerPrefix.Length);
            foreach (ProjectPoint p in project.points)
                if (p != null && p.id == id) { point = p; return true; }
            return false;
        }

        /// <summary>
        /// A re-fetch of the loaded project with the same points at the same places (within a
        /// millimetre) only refreshes their details in place: no marker or model dot is rebuilt.
        /// False, changing nothing, when anything moved, appeared or went.
        /// </summary>
        public bool TryRefreshInPlace(ProjectRequest fresh)
        {
            if (!HasProject || IsPractice || fresh?.points == null) return false;
            if (fresh.terrawebProjectId != project.terrawebProjectId || fresh.points.Length != project.points.Length) return false;
            var byId = new Dictionary<string, ProjectPoint>();
            foreach (ProjectPoint p in project.points)
                if (p != null) byId[p.id] = p;
            foreach (ProjectPoint f in fresh.points)
            {
                if (f == null || !byId.TryGetValue(f.id, out ProjectPoint p)) return false;
                if (Math.Abs(f.e - p.e) > 0.001 || Math.Abs(f.n - p.n) > 0.001 || Math.Abs(f.h - p.h) > 0.001) return false;
            }
            foreach (ProjectPoint f in fresh.points)
            {
                ProjectPoint p = byId[f.id];
                p.alertState = f.alertState;
                p.latestDate = f.latestDate;
                p.notes = f.notes;
                p.inactive = f.inactive;
                p.label = f.label;
                p.typeLabel = f.typeLabel;
            }
            renderPending = true;
            return true;
        }

        /// <summary>
        /// Live alerts: the server's tier for a sensor changed. Updates the point and re-renders
        /// on the next frame (one pass for a whole poll) so its marker ring follows; false when
        /// the sensor is unknown or unchanged.
        /// </summary>
        public bool SetAlertState(string sensorId, string state)
        {
            if (!HasProject || string.IsNullOrEmpty(sensorId)) return false;
            foreach (ProjectPoint p in project.points)
            {
                if (p == null || p.sensorId != sensorId) continue;
                if (p.alertState == state) return false;
                p.alertState = state;
                // Deferred to the next Update: LiveAlerts applies a whole poll in one go, and a
                // full re-render of every marker per changed sensor (a baseline can change
                // hundreds) stalled the headset for seconds.
                renderPending = true;
                return true;
            }
            return false;
        }
        public float YawDeg => solvedYawDeg;
        /// <summary>Survey-grid → point-frame angle for measured dx/dy (see ProjectRequest.gridYawDeg).</summary>
        public float GridYawDeg => HasProject ? project.gridYawDeg : 0f;

        /// <summary>
        /// A vector in the project frame (x East, y Up, z North, metres) as a world vector.
        /// False until aligned — before that the project has no direction in the room.
        /// </summary>
        public bool TryWorldVector(Vector3 projectVector, out Vector3 world)
        {
            world = Vector3.zero;
            Transform reference = markerManager != null ? markerManager.ReferencePoint : null;
            if (!isAligned || reference == null) return false;
            world = reference.TransformVector(solvedRotation * projectVector);
            return true;
        }
        /// <summary>The E/N/H subtracted from the project's points (the "project local" origin).</summary>
        public bool TryGetOrigin(out double e, out double n, out double h)
        {
            e = originE; n = originN; h = originH;
            return HasProject;
        }

        /// <summary>
        /// Where project-local metres sit under the reference anchor once aligned: a point p
        /// renders at reference.TransformPoint(rotation * p + translation). Design models use
        /// this to ride along with the sensors.
        /// </summary>
        public bool TryGetLocalPose(out Quaternion rotation, out Vector3 translation)
        {
            rotation = solvedRotation;
            translation = solvedTranslation;
            return isAligned;
        }

        public float RmsHorizontal => rmsHorizontal;
        public float RmsVertical => rmsVertical;
        /// <summary>Distance (m) from a placed point to where the fit puts it; null if not placed / not aligned.</summary>
        public float? Residual(string id) => residuals.TryGetValue(id ?? "", out float r) ? r : (float?)null;
        /// <summary>With 3+ control points: the one that disagrees with the others (re-place it).</summary>
        public string WorstId => worstId;
        /// <summary>The best next control point: furthest from those already placed.</summary>
        public string SuggestedId => suggestedId;
        public PairCheck Pair => pairCheck;
        public LiveGuide Live => live;
        /// <summary>Restored or recentred: touch one control point again to confirm.</summary>
        public bool NeedsCheck => needsCheck;
        /// <summary>Horizontal distance from the control points' centre to the farthest sensor.</summary>
        public float FarthestDistance => farthestDistance;
        /// <summary>Expected sideways error at the farthest sensor from tip error over the baseline.</summary>
        public float PredictedError => predictedError;
        /// <summary>One-off feedback for the panel (e.g. the result of a re-check).</summary>
        public string LastMessage => lastMessage;
        /// <summary>The last message is good news (check OK, position restored), in either language.</summary>
        public bool LastMessageIsGood => lastMessage != null && ReferenceEquals(lastMessage, goodMessage);

        private static string DataPath(string file) => Path.Combine(Application.persistentDataPath, file);

        // One saved alignment per project, so switching project (or a PC push) and back keeps it.
        private static string SaveFileFor(string projectName) => SaveFileFor(projectName, false);

        // legacyHash: the name earlier builds used (string.GetHashCode, not stable across runtimes).
        private static string SaveFileFor(string projectName, bool legacyHash)
        {
            var sb = new StringBuilder("project_");
            foreach (char c in projectName ?? "project")
                sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '_');
            uint hash = legacyHash ? (uint)(projectName ?? "").GetHashCode() : Fnv1a(projectName ?? "");
            sb.Append('_').Append(hash.ToString("x8"));
            return sb.Append(".json").ToString();
        }

        // FNV-1a 32-bit over the UTF-16 code units (low byte, then high byte): deterministic
        // across Mono / IL2CPP / .NET, unlike string.GetHashCode.
        private static uint Fnv1a(string s)
        {
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h = (h ^ (byte)(c & 0xFF)) * 16777619u;
                h = (h ^ (byte)(c >> 8)) * 16777619u;
            }
            return h;
        }

        // The save for a project: the FNV-named file, or a file from a build that still used
        // string.GetHashCode, moved over to the new name once found.
        private static string ResolveSaveFile(string projectName)
        {
            string file = SaveFileFor(projectName);
            if (File.Exists(DataPath(file))) return file;
            string legacy = SaveFileFor(projectName, true);
            if (legacy == file || !File.Exists(DataPath(legacy))) return file;
            try
            {
                File.Copy(DataPath(legacy), DataPath(file), true);
                File.Delete(DataPath(legacy));
                Debug.Log($"[Project] Moved saved alignment {legacy} → {file}");
                return file;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Project] Could not migrate {legacy}: {e.Message}");
                return legacy;
            }
        }

        private void Awake()
        {
            if (markerManager == null) markerManager = FindAnyObjectByType<MarkerManager>();
            if (anchors == null) anchors = FindAnyObjectByType<SpatialAnchorManager>();
            if (anchors != null) anchors.OnReferenceAnchorLoaded += OnAnchorRestored;
            // The anchor frame can be swapped after our markers exist: re-render in it.
            if (markerManager != null) markerManager.OnReferencePointChanged += OnReferencePointChanged;
            // Session restore / undo / "new session" clear every marker, ours included: put the
            // project back. ClearAllMarkers is the only caller, so this never fires from Render.
            if (markerManager != null) markerManager.OnMarkersCleared += OnMarkersClearedExternally;
        }

        private void OnReferencePointChanged(Transform _)
        {
            if (HasProject) Render();
        }

        private void OnDestroy()
        {
            // These publishers can outlive us on scene teardown; a dangling handler would run
            // Render on a destroyed component.
            if (anchors != null) anchors.OnReferenceAnchorLoaded -= OnAnchorRestored;
            if (markerManager != null)
            {
                markerManager.OnReferencePointChanged -= OnReferencePointChanged;
                markerManager.OnMarkersCleared -= OnMarkersClearedExternally;
            }
#if !MOBILE_PREVIEW
            if (recenterHooked && OVRManager.display != null) OVRManager.display.RecenteredPose -= OnRecentered;
#endif
        }

        // Deferred to the next Update: a project switch clears the markers and sets the new
        // project in the same frame, and re-creating the old one in between would be wasted.
        private bool renderPending;

        private void OnMarkersClearedExternally()
        {
            if (rendering) return;
            renderedIds.Clear(); // the markers are gone; Render must create them again
            renderPending = HasProject;
        }

        private IEnumerator Start()
        {
            Load();
            if (!HasProject) yield break;
            // A restored project may still relocalise its spatial anchor (0.5 s + load + 8 s
            // localise), which clears the re-check; decide only once that has had its chance.
            float until = Time.time + 12f;
            while (anchors != null && !anchors.IsInitialized && Time.time < until) yield return null;
            yield return new WaitForSeconds(0.5f);
            if (!modeActive && !UiState.AnyModal) OpenIfNeeded();
        }

        private void Update()
        {
#if !MOBILE_PREVIEW
            if (!recenterHooked && OVRManager.display != null)
            {
                OVRManager.display.RecenteredPose += OnRecentered;
                recenterHooked = true;
            }
#endif
            if (renderPending)
            {
                renderPending = false;
                Render();
                RefreshPanel(); // alert states changed since the last frame show in the list too
            }
            if (pendingOpen && !UiState.AnyModal && !modeActive)
            {
                pendingOpen = false;
                OpenIfNeeded();
            }
            if (!modeActive) return;
            UpdateCursorAndPanelPose();
            if (Time.unscaledTime >= nextLiveRefresh)
            {
                nextLiveRefresh = Time.unscaledTime + 0.1f;
                UpdateLiveGuide();
            }
            // The main menu owns the sticks and buttons while it is open.
            if (!UiState.InputBlocked) HandleModeInput();
        }

        // ------------------------------------------------------------------
        //  Project
        // ------------------------------------------------------------------

        // A restored spatial anchor puts the saved control points back where they were in the
        // room, so the alignment needs no re-check. A pose-only fallback still does.
        private void OnAnchorRestored(Transform anchor)
        {
            if (!awaitingAnchor || anchors == null) return;
            awaitingAnchor = false;
            if (!anchors.IsPersistentAnchor) return;
            needsCheck = false;
            lastMessage = L.T("Η θέση επανήλθε από το spatial anchor του χώρου.", "Position restored from the room's spatial anchor.");
            goodMessage = lastMessage;
            Render();
            // The panel opened itself for a re-check that is no longer needed: close it rather
            // than leave it up with a prompt the message now contradicts.
            if (autoOpened && modeActive && !NeedsAlignment) SetMode(false);
            RefreshPanel();
            OnAlignmentChanged?.Invoke();
        }

        // A recenter moves the tracking origin. A localised spatial anchor follows the room
        // (the markers move with it), so only the pose-only fallback becomes suspect.
        private void OnRecentered()
        {
            if (placements.Count == 0) return;
            if (anchors != null && anchors.IsPersistentAnchor) return;
            needsCheck = true;
            lastMessage = L.T("Έγινε recenter — ακούμπησε ξανά ένα σημείο ελέγχου για έλεγχο.",
                "Recentered — touch a control point again to check.");
            Render();
            RefreshPanel();
            OnAlignmentChanged?.Invoke();
        }

        /// <param name="practice">The office practice project: starts with no placements and is never saved.</param>
        public void SetProject(ProjectRequest request, bool practice = false)
        {
            if (request?.points == null || request.points.Length == 0)
            {
                Debug.LogWarning("[Project] Empty project ignored");
                return;
            }

            for (int i = 0; i < request.points.Length; i++)
            {
                var p = request.points[i];
                if (p != null && string.IsNullOrEmpty(p.id)) p.id = (i + 1).ToString();
            }

            bool sameProject = HasProject && project.name == request.name && IsPractice == practice;
            var keep = sameProject ? new Dictionary<string, Vector3>(placements)
                : practice ? new Dictionary<string, Vector3>() : LoadPlacements(request.name);
            var keepOrder = sameProject ? new List<string>(placementOrder) : new List<string>(keep.Keys);
            bool restored = !sameProject && keep.Count > 0;

            project = request;
            IsPractice = practice;
            // Round the origin to whole metres so local offsets stay readable in logs. Taken from
            // the first usable point: a null or non-finite first entry would throw / turn every
            // project-local coordinate into NaN.
            originE = originN = originH = 0;
            foreach (ProjectPoint o in request.points)
            {
                if (o == null || !IsFinite(o.e) || !IsFinite(o.n) || !IsFinite(o.h)) continue;
                originE = Math.Round(o.e);
                originN = Math.Round(o.n);
                originH = Math.Round(o.h);
                break;
            }

            placements.Clear();
            placementOrder.Clear();
            var ids = new HashSet<string>();
            foreach (ProjectPoint pt in request.points) if (pt != null) ids.Add(pt.id);
            foreach (string id in keepOrder)
            {
                if (!ids.Contains(id) || !keep.TryGetValue(id, out Vector3 local)) continue;
                placements[id] = local;
                placementOrder.Add(id);
            }
            if (restored)
            {
                // Trusted again as soon as the saved spatial anchor relocalises (or already has).
                needsCheck = anchors == null || !anchors.IsPersistentAnchor;
                awaitingAnchor = needsCheck && anchors != null && !anchors.IsInitialized;
                lastMessage = L.T("Ευθυγράμμιση από προηγούμενη φορά — ακούμπησε ξανά ένα σημείο ελέγχου για έλεγχο.",
                    "Alignment from last time — touch a control point again to check.");
            }
            else if (!sameProject)
            {
                needsCheck = false;
                lastMessage = null;
            }
            selectedIndex = 0;
            Solve();
            Save();
            Debug.Log($"[Project] '{request.name}' loaded with {request.points.Length} points " +
                      $"(origin E{originE} N{originN} H{originH})");
        }

        /// <summary>
        /// Drop the practice project and bring back the last real project saved on this headset
        /// (its placements flagged for a re-check, as after a restart).
        /// </summary>
        public void LeavePractice()
        {
            if (!IsPractice) return;
            SetMode(false);
            project = null;
            IsPractice = false;
            placements.Clear();
            placementOrder.Clear();
            needsCheck = false;
            lastMessage = null;
            Solve(); // no project: removes its markers and tells listeners
            Load();
        }

        public void ResetPlacements()
        {
            placements.Clear();
            placementOrder.Clear();
            needsCheck = false;
            lastMessage = null;
            Solve();
            Save();
        }

        // ------------------------------------------------------------------
        //  Mode / input
        // ------------------------------------------------------------------

        /// <summary>Jump to the next sensor whose name contains <paramref name="query"/>.</summary>
        public bool SelectMatch(string query)
        {
            if (!HasProject || string.IsNullOrWhiteSpace(query)) return false;
            query = query.Trim();
            int n = project.points.Length;
            for (int step = 1; step <= n; step++)
            {
                int i = (selectedIndex + step) % n;
                ProjectPoint p = project.points[i];
                if (p == null) continue;
                if ((p.id ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || (p.label ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    selectedIndex = i;
                    lastMessage = null;
                    RefreshPanel();
                    return true;
                }
            }
            lastMessage = L.T($"Δεν βρέθηκε «{query}».", $"\u201c{query}\u201d not found.");
            RefreshPanel();
            return false;
        }

        /// <summary>Select a list row (pointer hover/click on the panel).</summary>
        public void Select(int index)
        {
            if (!HasProject) return;
            int clamped = Mathf.Clamp(index, 0, project.points.Length - 1);
            if (clamped == selectedIndex) return;
            selectedIndex = clamped;
            RefreshPanel();
        }

        /// <summary>Not usable yet: never aligned, or restored and waiting for a re-check touch.</summary>
        public bool NeedsAlignment => HasProject && (!isAligned || needsCheck);

        /// <summary>
        /// The panel opens by itself only while the project still needs aligning; once aligned
        /// it comes back only from the main menu.
        /// </summary>
        public void OpenIfNeeded()
        {
            if (!NeedsAlignment) return;
            // Never on top of a menu / card / keyboard: B and A would reach both. Wait.
            if (UiState.AnyModal) { pendingOpen = true; return; }
            SetMode(true);
            autoOpened = modeActive;
        }
        private bool pendingOpen;
        // Opened by OpenIfNeeded (not the user): may close itself once the need goes away.
        private bool autoOpened;

        public void ToggleMode()
        {
            if (modeActive) SetMode(false);
            else if (HasProject) SetMode(true);
            else Debug.LogWarning("[Project] No project loaded — POST /api/project first");
        }

        public void SetMode(bool active)
        {
            modeActive = active && HasProject;
            autoOpened = false;
            panelPlaced = false; // reopens in front, not wherever it was last left
            panelGliding = false;
            EnsureVisuals();
            if (panel != null) panel.gameObject.SetActive(modeActive);
            if (tipCursor != null) tipCursor.gameObject.SetActive(modeActive);
            if (modeActive) RefreshPanel();
        }

        private void HandleModeInput()
        {
#if !MOBILE_PREVIEW
            float stick = Hands.OffHandStickY();
            if (Mathf.Abs(stick) > 0.6f && Time.unscaledTime >= nextStickMove)
            {
                MoveSelection(stick > 0 ? -1 : 1);
                nextStickMove = Time.unscaledTime + StickRepeatDelay();
            }
            else if (Mathf.Abs(stick) < 0.3f)
            {
                nextStickMove = 0f;
                stickHeldSince = -1f;
            }

            if (!VrPointer.ConsumedThisFrame && Hands.TriggerDown())
            {
                bool wasAligned = isAligned;
                Vector3 tip = TipWorldPosition();
                if (PlaceSelectedAt(tip))
                {
                    Feedback.Buzz(Hands.Dominant, 0.6f, 0.08f);
                    bool moved = lastMessage != null && lastMessage.StartsWith(L.T("Ο χώρος μετακινήθηκε", "The room moved"));
                    if (moved) Feedback.Play(Feedback.Sound.Warn);
                    else if (isAligned && !wasAligned)
                    {
                        // The moment it locks in: chord, a longer buzz, sensors drop from the sky.
                        Feedback.Play(Feedback.Sound.Success);
                        Feedback.Buzz(Hands.Dominant, 0.8f, 0.25f);
                    }
                    else if (lastMessage != null && lastMessage.StartsWith(L.T("Έλεγχος OK", "Check OK")))
                        Feedback.Play(Feedback.Sound.Success);
                    else Feedback.Play(Feedback.Sound.Place);
                }
            }

            if (Hands.DominantSecondaryDown() && UndoLastPlacement())
            {
                Feedback.Play(Feedback.Sound.Undo);
                Feedback.Buzz(Hands.Dominant, 0.25f, 0.06f);
            }
#endif
        }

        private void MoveSelection(int delta)
        {
            if (!HasProject) return;
            int n = project.points.Length;
            selectedIndex = (selectedIndex + delta + n) % n;
            RefreshPanel();
        }

        private ProjectPoint SelectedPoint =>
            HasProject && selectedIndex >= 0 && selectedIndex < project.points.Length
                ? project.points[selectedIndex]
                : null;

        // ------------------------------------------------------------------
        //  Placement + solve
        // ------------------------------------------------------------------

        /// <returns>True when a point was actually placed (drives the haptic tick).</returns>
        public bool PlaceSelectedAt(Vector3 worldPosition)
        {
            var point = SelectedPoint;
            Transform reference = markerManager != null ? markerManager.ReferencePoint : null;
            if (point == null || reference == null) return false;

            Vector3 local = reference.InverseTransformPoint(worldPosition);
            lastMessage = null;
            if (needsCheck && isAligned)
            {
                // Re-check of a restored / recentred alignment: compare the touch with where the
                // current fit puts this point. Close enough confirms everything; otherwise the
                // old control points are in a stale frame and are dropped, keeping this touch.
                Vector3 predicted = solvedRotation * ProjectLocal(point) + solvedTranslation;
                float off = (predicted - local).magnitude;
                needsCheck = false;
                if (off <= RecheckTolerance)
                {
                    lastMessage = L.T($"Έλεγχος OK — το {point.id} απέχει {off * 100f:F0} cm.",
                        $"Check OK — {point.id} is {off * 100f:F0} cm off.");
                    goodMessage = lastMessage;
                }
                else
                {
                    placements.Clear();
                    placementOrder.Clear();
                    lastMessage = L.T($"Ο χώρος μετακινήθηκε ({off:F2} m) — τοποθέτησε ξανά τα σημεία ελέγχου.",
                        $"The room moved ({off:F2} m) — place the control points again.");
                }
            }
            else if (needsCheck)
            {
                // Restored placements that never solved (one point, or two too close together)
                // cannot be checked against this touch — and they may be in a stale tracking
                // frame. Drop them rather than mixing frames; this touch starts over.
                placements.Clear();
                placementOrder.Clear();
                needsCheck = false;
                awaitingAnchor = false;
                lastMessage = L.T("Τα παλιά σημεία ελέγχου δεν επαρκούσαν για έλεγχο και απορρίφθηκαν — τοποθέτησε ξανά τα σημεία ελέγχου.",
                    "The old control points were too few to check and were dropped — place the control points again.");
            }

            placements[point.id] = local;
            waveFrom = worldPosition;
            placementOrder.Remove(point.id);
            placementOrder.Add(point.id);
            Debug.Log($"[Project] Placed '{point.id}' at {worldPosition}");

            // Advance to the next unplaced point to speed up the 2-point workflow.
            for (int i = 1; i <= project.points.Length; i++)
            {
                int idx = (selectedIndex + i) % project.points.Length;
                ProjectPoint next = project.points[idx];
                if (next != null && !placements.ContainsKey(next.id)) { selectedIndex = idx; break; }
            }

            Solve();
            Save();
            // Practice touches are not field data: no anchors or log rows (AnchorFieldTest).
            if (!IsPractice) OnPointPlaced?.Invoke(point.id, worldPosition);
            return true;
        }

        public void RemovePlacement(string id)
        {
            if (string.IsNullOrEmpty(id) || !placements.Remove(id)) return;
            placementOrder.Remove(id);
            Solve();
            Save();
            if (!IsPractice) OnPointRemoved?.Invoke(id);
        }

        /// <summary>Undo the most recent touch and select that sensor again.</summary>
        public bool UndoLastPlacement()
        {
            if (placementOrder.Count == 0) return false;
            string id = placementOrder[placementOrder.Count - 1];
            for (int i = 0; i < project.points.Length; i++)
                if (project.points[i]?.id == id) { selectedIndex = i; break; }
            lastMessage = L.T($"Αναιρέθηκε το {id}.", $"Undid {id}.");
            RemovePlacement(id);
            return true;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // Projected E/N/H (any metric grid) minus the double origin, then to float: x = East,
        // y = Up, z = North. Float keeps ~1 mm out to 8 km from the origin.
        private Vector3 ProjectLocal(ProjectPoint p) =>
            new Vector3((float)(p.e - originE), (float)(p.h - originH), (float)(p.n - originN));

        private void Solve()
        {
            isAligned = false;
            rmsHorizontal = rmsVertical = maxResidual = 0f;

            residuals.Clear();
            worstId = null;
            pairCheck = default;
            var pairs = new List<(Vector3 p, Vector3 m)>();
            var ids = new List<string>();
            if (HasProject)
            {
                foreach (var pt in project.points)
                {
                    if (pt != null && placements.TryGetValue(pt.id, out Vector3 m))
                    {
                        pairs.Add((ProjectLocal(pt), m));
                        ids.Add(pt.id);
                    }
                }
            }
            suggestedId = SuggestNext(pairs);
            if (pairs.Count >= 2) pairCheck = FurthestPair(pairs, ids);
            farthestDistance = predictedError = 0f;
            if (pairs.Count >= 2 && HasProject)
            {
                Vector3 centre = Vector3.zero;
                foreach (var (pp, _) in pairs) centre += pp;
                centre /= pairs.Count;
                foreach (ProjectPoint pt in project.points)
                    if (pt != null) farthestDistance = Mathf.Max(farthestDistance, Flat(ProjectLocal(pt) - centre));
                // Sideways error at distance D from a baseline b with per-point error σ: σ·√2·D/b.
                if (pairCheck.Valid && pairCheck.Project > 0.01f)
                    predictedError = TouchSigma * 1.414f * farthestDistance / pairCheck.Project;
            }

            if (pairs.Count >= 2)
            {
                Vector3 pc = Vector3.zero, mc = Vector3.zero;
                foreach (var (p, m) in pairs) { pc += p; mc += m; }
                pc /= pairs.Count;
                mc /= pairs.Count;

                // Least-squares yaw on the XZ plane. Unity's Euler(0,θ,0) maps
                // (x,z) → (x cosθ + z sinθ, −x sinθ + z cosθ).
                double sCos = 0, sSin = 0, spread = 0, measuredSpread = 0;
                foreach (var (p, m) in pairs)
                {
                    float px = p.x - pc.x, pz = p.z - pc.z, mx = m.x - mc.x, mz = m.z - mc.z;
                    sCos += mx * px + mz * pz;
                    sSin += mx * pz - mz * px;
                    spread += px * px + pz * pz;
                    measuredSpread += mx * mx + mz * mz;
                }

                if (spread < 0.25 * 0.25)
                {
                    Debug.LogWarning("[Project] Control points too close horizontally to solve rotation");
                }
                else if (measuredSpread < 0.25 * 0.25)
                {
                    // Two sensors touched at the same physical spot: Atan2(0,0) would "solve"
                    // to 0 deg and the whole site would render at an arbitrary heading.
                    Debug.LogWarning("[Project] Touches too close together to solve rotation");
                    lastMessage = L.T("Τα σημεία ακουμπήθηκαν στο ίδιο μέρος. Ακούμπησε το καθένα στη δική του θέση.",
                        "The points were touched at the same spot. Touch each one at its own position.");
                }
                else
                {
                    solvedYawDeg = (float)(Math.Atan2(sSin, sCos) * 180.0 / Math.PI);
                    solvedRotation = Quaternion.Euler(0f, solvedYawDeg, 0f);
                    solvedTranslation = mc - solvedRotation * pc;
                    isAligned = true;

                    double sumH = 0, sumV = 0;
                    for (int i = 0; i < pairs.Count; i++)
                    {
                        var (p, m) = pairs[i];
                        Vector3 d = (solvedRotation * p + solvedTranslation) - m;
                        residuals[ids[i]] = d.magnitude;
                        float dh = new Vector2(d.x, d.z).magnitude;
                        sumH += dh * dh;
                        sumV += d.y * d.y;
                        maxResidual = Mathf.Max(maxResidual, d.magnitude);
                    }
                    rmsHorizontal = (float)Math.Sqrt(sumH / pairs.Count);
                    rmsVertical = (float)Math.Sqrt(sumV / pairs.Count);
                    worstId = FindOutlier(pairs, ids);
                    Debug.Log($"[Project] Aligned with {pairs.Count} pts: yaw {solvedYawDeg:F2}°, " +
                              $"RMS h {rmsHorizontal * 100f:F1} cm, v {rmsVertical * 100f:F1} cm");
                    // A rotation cannot undo a mirror: E/N swapped in the source (an N,E-ordered
                    // CRS or CSV, or a grid with X = North) fits badly with 3+ points and is
                    // invisible with 2. Say so instead of letting the user chase an "outlier".
                    if (pairs.Count >= 3 && rmsHorizontal > 0.10f && LooksMirrored(pairs, rmsHorizontal))
                    {
                        worstId = null;
                        lastMessage = L.T("Τα σημεία ταιριάζουν μόνο κατοπτρικά: πιθανώς έχουν αντιστραφεί οι στήλες E/N (X/Y) στα δεδομένα.",
                            "The points only fit mirrored: the E/N (X/Y) columns are probably swapped in the data.");
                        Debug.LogWarning("[Project] Control points fit far better with E and N swapped — check the CRS axis order / CSV columns");
                    }
                }
            }

            Render();
            RefreshPanel();
            OnAlignmentChanged?.Invoke();
        }

        // ------------------------------------------------------------------
        //  Diagnostics
        // ------------------------------------------------------------------

        // Best next control point: the unplaced one whose nearest placed neighbour is
        // furthest away — a longer, wider baseline pins the rotation down hardest.
        private string SuggestNext(List<(Vector3 p, Vector3 m)> pairs)
        {
            if (!HasProject || pairs.Count == 0 || pairs.Count >= project.points.Length) return null;
            string best = null;
            float bestScore = -1f;
            foreach (ProjectPoint pt in project.points)
            {
                if (pt == null || placements.ContainsKey(pt.id)) continue;
                Vector3 q = ProjectLocal(pt);
                float nearest = float.MaxValue;
                foreach (var (p, _) in pairs)
                    nearest = Mathf.Min(nearest, new Vector2(q.x - p.x, q.z - p.z).magnitude);
                if (nearest > bestScore) { bestScore = nearest; best = pt.id; }
            }
            return best;
        }

        private static PairCheck FurthestPair(List<(Vector3 p, Vector3 m)> pairs, List<string> ids)
        {
            var check = new PairCheck();
            for (int i = 0; i < pairs.Count; i++)
            for (int j = i + 1; j < pairs.Count; j++)
            {
                float proj = Flat(pairs[i].p - pairs[j].p);
                if (check.Valid && proj <= check.Project) continue;
                check = new PairCheck
                {
                    Valid = true, A = ids[i], B = ids[j], Project = proj,
                    Measured = Flat(pairs[i].m - pairs[j].m)
                };
            }
            return check;
        }

        // Leave-one-out: the point whose removal shrinks the others' misfit the most is the
        // suspect. Only meaningful with 3+ points; 2 points always share the error equally.
        private string FindOutlier(List<(Vector3 p, Vector3 m)> pairs, List<string> ids)
        {
            if (pairs.Count < 3 || maxResidual < 0.08f) return null;
            string worst = null;
            float bestRms = float.MaxValue;
            for (int skip = 0; skip < pairs.Count; skip++)
            {
                var subset = new List<(Vector3 p, Vector3 m)>(pairs);
                subset.RemoveAt(skip);
                float rms = FitRms(subset);
                if (rms < bestRms) { bestRms = rms; worst = ids[skip]; }
            }
            // Only blame a point if dropping it actually fixes things, and if that point is itself
            // clearly off from what the others predict (horizontally or vertically, separately:
            // a 10 cm height slip and a 10 cm sideways slip are different stories).
            float current = Mathf.Sqrt(rmsHorizontal * rmsHorizontal + rmsVertical * rmsVertical);
            if (worst == null || bestRms >= current * 0.5f) return null;
            int w = ids.IndexOf(worst);
            var others = new List<(Vector3 p, Vector3 m)>(pairs);
            others.RemoveAt(w);
            Vector3 predicted = PredictWith(others, pairs[w].p);
            Vector3 miss = predicted - pairs[w].m;
            bool off = Flat(miss) > 0.08f || Mathf.Abs(miss.y) > 0.08f;
            return off ? worst : null;
        }

        // Fit on a subset, then say where it puts project point p.
        private static Vector3 PredictWith(List<(Vector3 p, Vector3 m)> pairs, Vector3 p)
        {
            Vector3 pc = Vector3.zero, mc = Vector3.zero;
            foreach (var (pp, mm) in pairs) { pc += pp; mc += mm; }
            pc /= pairs.Count;
            mc /= pairs.Count;
            double sCos = 0, sSin = 0;
            foreach (var (pp, mm) in pairs)
            {
                float px = pp.x - pc.x, pz = pp.z - pc.z, mx = mm.x - mc.x, mz = mm.z - mc.z;
                sCos += mx * px + mz * pz;
                sSin += mx * pz - mz * px;
            }
            Quaternion rot = Quaternion.Euler(0f, (float)(Math.Atan2(sSin, sCos) * 180.0 / Math.PI), 0f);
            return rot * p + (mc - rot * pc);
        }

        private static float FitRms(List<(Vector3 p, Vector3 m)> pairs)
        {
            Vector3 pc = Vector3.zero, mc = Vector3.zero;
            foreach (var (p, m) in pairs) { pc += p; mc += m; }
            pc /= pairs.Count;
            mc /= pairs.Count;
            double sCos = 0, sSin = 0;
            foreach (var (p, m) in pairs)
            {
                float px = p.x - pc.x, pz = p.z - pc.z, mx = m.x - mc.x, mz = m.z - mc.z;
                sCos += mx * px + mz * pz;
                sSin += mx * pz - mz * px;
            }
            Quaternion rot = Quaternion.Euler(0f, (float)(Math.Atan2(sSin, sCos) * 180.0 / Math.PI), 0f);
            Vector3 t = mc - rot * pc;
            double sum = 0;
            foreach (var (p, m) in pairs) sum += ((rot * p + t) - m).sqrMagnitude;
            return (float)Math.Sqrt(sum / pairs.Count);
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        // Horizontal RMS of the same yaw fit with the project's E and N exchanged (a reflection).
        private static bool LooksMirrored(List<(Vector3 p, Vector3 m)> pairs, float rmsHorizontal)
        {
            var swapped = new List<(Vector3 p, Vector3 m)>(pairs.Count);
            foreach (var (p, m) in pairs) swapped.Add((new Vector3(p.z, 0f, p.x), new Vector3(m.x, 0f, m.z)));
            return FitRms(swapped) < 0.3f * rmsHorizontal;
        }

        // While a not-yet-placed point is selected and something is already placed, show the
        // tip's distance and height difference to the furthest placed point against what the
        // project says — the user can walk until they agree before pulling the trigger.
        private void UpdateLiveGuide()
        {
            live = default;
            ProjectPoint sel = SelectedPoint;
            Transform reference = markerManager != null ? markerManager.ReferencePoint : null;
            if (sel == null || reference == null || placements.Count == 0 || placements.ContainsKey(sel.id)) return;

            Vector3 tip = reference.InverseTransformPoint(TipWorldPosition());
            Vector3 selLocal = ProjectLocal(sel);
            ProjectPoint refPoint = null;
            float refDist = -1f;
            foreach (ProjectPoint pt in project.points)
            {
                if (pt == null || !placements.ContainsKey(pt.id)) continue;
                float d = Flat(ProjectLocal(pt) - selLocal);
                if (d > refDist) { refDist = d; refPoint = pt; }
            }
            if (refPoint == null) return;

            Vector3 placed = placements[refPoint.id];
            live = new LiveGuide
            {
                Valid = true,
                RefId = refPoint.id,
                Expected = refDist,
                Measured = Flat(tip - placed),
                ExpectedDh = (float)(sel.h - refPoint.h),
                MeasuredDh = tip.y - placed.y
            };
            if (panel != null && modeActive) panel.RefreshLive(this);
        }

        // ------------------------------------------------------------------
        //  Rendering
        // ------------------------------------------------------------------

        private bool rendering;

        private void Render()
        {
            if (markerManager == null || rendering) return;
            renderPending = false;
            rendering = true;
            try { RenderMarkers(); }
            finally { rendering = false; }
        }

        private void RenderMarkers()
        {
            // Update markers in place: removing and re-creating hundreds of icons on every
            // trigger press caused a visible hitch and leaked their materials.
            if (!HasProject)
            {
                foreach (string id in renderedIds) markerManager.RemoveMarker(id);
                renderedIds.Clear();
                return;
            }

            var shown = new HashSet<string>();
            var coords = new List<CoordinateData>();
            foreach (var pt in project.points)
            {
                if (pt == null) continue;
                bool placed = placements.TryGetValue(pt.id, out Vector3 measured);
                Vector3 local;
                if (isAligned) local = solvedRotation * ProjectLocal(pt) + solvedTranslation;
                else if (placed) local = measured; // before alignment show only the touched spots
                else continue;

                string name = string.IsNullOrEmpty(pt.label) ? pt.id : pt.label;
                coords.Add(new CoordinateData
                {
                    id = MarkerPrefix + pt.id,
                    x = local.x, y = local.y, z = local.z,
                    label = $"{name}\nH {pt.h:F2}",
                    color = placed ? controlColor : (string.IsNullOrEmpty(pt.color) ? pointColor : pt.color),
                    scale = pointScale,
                    markerType = string.IsNullOrEmpty(pt.icon) ? "sphere" : "icon",
                    icon = pt.icon,
                    status = pt.alertState,
                    layer = "project"
                });
                shown.Add(MarkerPrefix + pt.id);
            }

            foreach (string id in renderedIds)
                if (!shown.Contains(id)) markerManager.RemoveMarker(id);
            renderedIds.Clear();
            renderedIds.UnionWith(shown);

            var fresh = new List<string>();
            foreach (string id in shown)
                if (!markerManager.ActiveMarkers.ContainsKey(id)) fresh.Add(id);

            markerManager.EnsureCapacity(markerManager.ActiveMarkerCount + coords.Count);
            markerManager.ProcessCoordinates(new CoordinateResponse { coordinates = coords.ToArray() });
            Animate(fresh, shown);
        }

        // New icons fall in one after another, nearest the user (or the touched spot) first;
        // after a touch the ones already standing get a light wave rolling out from it.
        private void Animate(List<string> fresh, HashSet<string> shown)
        {
            Camera cam = Camera.main;
            Vector3 from = waveFrom ?? (cam != null ? cam.transform.position : Vector3.zero);
            bool wave = waveFrom.HasValue;
            waveFrom = null;

            var order = new List<(float d, Marker m)>();
            foreach (string id in fresh)
                if (markerManager.ActiveMarkers.TryGetValue(id, out Marker m) && m != null)
                    order.Add((Vector3.Distance(from, m.transform.position), m));
            if (order.Count > 1)
            {
                order.Sort((a, b) => a.d.CompareTo(b.d));
                // The whole set lands within ~1.6 s however many there are.
                float step = Mathf.Min(0.06f, 1.6f / order.Count);
                for (int i = 0; i < order.Count; i++) order[i].m.PlayIntro(i * step);
                if (order.Count >= 5) Feedback.Play(Feedback.Sound.Whoosh, 0.6f);
            }

            if (!wave || !isAligned) return;
            var freshSet = new HashSet<string>(fresh);
            foreach (string id in shown)
            {
                if (freshSet.Contains(id) || !markerManager.ActiveMarkers.TryGetValue(id, out Marker m) || m == null) continue;
                // Rolls outwards at 15 m/s, capped so a far corner of the site doesn't wait long.
                m.PlayWave(Mathf.Min(2f, Vector3.Distance(from, m.transform.position) / 15f));
            }
        }

        // ------------------------------------------------------------------
        //  Visuals (left-hand panel + right-hand tip cursor)
        // ------------------------------------------------------------------

        private void EnsureVisuals()
        {
            if (panel == null)
            {
                var go = new GameObject("ProjectPanel");
                go.transform.SetParent(transform, false);
                panel = go.AddComponent<ProjectPanel>();
                panel.Build(this);
                go.SetActive(false);
            }

            if (tipCursor == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "ProjectTipCursor";
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(transform, false);
                go.transform.localScale = Vector3.one * tipCursorSize;
                var r = go.GetComponent<Renderer>();
                r.material = new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 0.2f, 0.9f) };
                r.sortingOrder = 160;
                tipCursor = go.transform;
                tipCursor.gameObject.SetActive(false);
            }
        }

        private Vector3 TipWorldPosition()
        {
#if !MOBILE_PREVIEW
            if (cameraRig == null) cameraRig = FindAnyObjectByType<OVRCameraRig>();
            if (cameraRig != null && Hands.DominantConnected) return Hands.DominantAnchor(cameraRig).TransformPoint(tipOffset);
#endif
            Camera cam = Camera.main;
            return cam != null ? cam.transform.position + cam.transform.forward * 0.5f : Vector3.zero;
        }

        private void UpdateCursorAndPanelPose()
        {
            if (tipCursor != null) tipCursor.position = TipWorldPosition();

            Camera cam = Camera.main;
            if (panel == null || cam == null) return;
            // Once dragged off the left hand it stays where it was left. While the grip drags it,
            // VrPointer (earlier in the frame) owns the pose; snapping it back to the hand here
            // made the drag impossible.
            if (PanelPin.IsPinned(panel) || VrPointer.IsDragging) return;
            Transform panelT = panel.transform;
#if !MOBILE_PREVIEW
            if (cameraRig == null) cameraRig = FindAnyObjectByType<OVRCameraRig>();
            // Held above the off-hand controller like a clipboard; in the room like the other
            // panels when that is turned off or the controller is pocketed.
            if (PanelOnHand && cameraRig != null && OVRInput.IsControllerConnected(Hands.OffHand))
            {
                panelT.position = Hands.OffHandAnchor(cameraRig).position + Vector3.up * panelLift;
                VrUi.FaceViewer(panelT, cam.transform);
                panelPlaced = false;
                return;
            }
#endif
            // World-locked with lazy follow. It used to be head-locked here (re-placed every
            // frame along the full gaze, pitch included), which drags the panel with every
            // glance down at the sensor being touched.
            if (!panelPlaced)
            {
                panelT.position = VrUi.InFront(cam.transform, PanelDistance, PanelDrop);
                VrUi.FaceViewer(panelT, cam.transform);
                panelPlaced = true;
                panelGliding = false;
            }
            else if (!VrPointer.IsDragging)
            {
                VrUi.LazyFollow(panelT, cam.transform, PanelDistance, PanelDrop, ref panelGliding);
            }
        }

        private void RefreshPanel()
        {
            if (panel != null && modeActive) panel.Refresh(this);
        }

        // ------------------------------------------------------------------
        //  Status / persistence
        // ------------------------------------------------------------------

        public string StatusJson()
        {
            // Practice is never reported as a project: the PC must not take it for site data.
            if (IsPractice) return "{\"loaded\":false,\"practice\":true,\"aligned\":false}";
            var sb = new StringBuilder("{");
            sb.Append("\"loaded\":").Append(HasProject ? "true" : "false");
            if (HasProject)
            {
                sb.Append(",\"name\":\"").Append(JsonText.Escape(project.name ?? "")).Append('"');
                sb.Append(",\"pointCount\":").Append(project.points.Length);
                sb.Append(",\"placed\":[");
                bool firstItem = true;
                foreach (string id in placements.Keys)
                {
                    if (!firstItem) sb.Append(',');
                    sb.Append('"').Append(JsonText.Escape(id)).Append('"');
                    firstItem = false;
                }
                sb.Append(']');
            }
            sb.Append(",\"aligned\":").Append(isAligned ? "true" : "false");
            if (isAligned)
            {
                sb.Append(",\"yawDeg\":").Append(solvedYawDeg.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"rmsHorizontalM\":").Append(rmsHorizontal.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"rmsVerticalM\":").Append(rmsVertical.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"maxResidualM\":").Append(maxResidual.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            return sb.ToString();
        }

        [Serializable]
        private class SavedPlacement { public string id; public Vector3 local; }

        [Serializable]
        private class SaveData
        {
            public ProjectRequest project;
            public SavedPlacement[] placements;
        }

        private void Save()
        {
            // Practice stays in memory: on disk it would come back on the next launch as "the last project".
            if (!HasProject || IsPractice) return;
            try
            {
                var list = new List<SavedPlacement>();
                foreach (string id in placementOrder)
                    if (placements.TryGetValue(id, out Vector3 local)) list.Add(new SavedPlacement { id = id, local = local });
                string file = SaveFileFor(project.name);
                File.WriteAllText(DataPath(file), JsonUtility.ToJson(new SaveData { project = project, placements = list.ToArray() }));
                File.WriteAllText(DataPath(LastProjectFile), file);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Project] Save failed: {e.Message}");
            }
        }

        private static SaveData ReadSave(string file)
        {
            string path = DataPath(file);
            if (!File.Exists(path)) return null;
            var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(path));
            return data?.project?.points == null || data.project.points.Length == 0 ? null : data;
        }

        private static Dictionary<string, Vector3> LoadPlacements(string projectName)
        {
            var result = new Dictionary<string, Vector3>();
            try
            {
                SaveData data = ReadSave(ResolveSaveFile(projectName));
                if (data?.placements != null)
                    foreach (var p in data.placements) result[p.id] = p.local;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Project] Reading saved alignment failed: {e.Message}");
            }
            return result;
        }

        private void Load()
        {
            try
            {
                string lastPath = DataPath(LastProjectFile);
                SaveData data = File.Exists(lastPath) ? ReadSave(File.ReadAllText(lastPath).Trim()) : null;
                if (data == null)
                {
                    // Builds before per-project saves kept one file; move it over once.
                    data = ReadSave(LegacySaveFile);
                    if (data != null)
                    {
                        File.WriteAllText(DataPath(SaveFileFor(data.project.name)), File.ReadAllText(DataPath(LegacySaveFile)));
                        File.Delete(DataPath(LegacySaveFile));
                    }
                }
                if (data == null) return;

                SetProject(data.project); // restores this project's placements, flagged for a re-check
                Debug.Log($"[Project] Restored '{project.name}' with {placements.Count} placement(s) — needs re-check");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Project] Load failed: {e.Message}");
            }
        }
    }
}
