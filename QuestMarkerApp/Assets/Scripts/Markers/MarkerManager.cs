using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using QuestMarkerApp.API;

namespace QuestMarkerApp.Markers
{
    /// <summary>
    /// Manages the creation, updating, and removal of markers in 3D space
    /// </summary>
    public class MarkerManager : MonoBehaviour
    {
        [Header("Marker Settings")]
        [SerializeField] private GameObject markerPrefab;
        [SerializeField] private float defaultMarkerScale = 0.1f;
        [SerializeField] private Color defaultMarkerColor = Color.cyan;
        [SerializeField] private int maxMarkers = 200;
        [SerializeField] private float maxCoordinateAbs = 10000f;

        [Header("Pool Settings")]
        [SerializeField] private bool useObjectPooling = true;
        [SerializeField] private int initialPoolSize = 20;

        [Header("Animation")]
        [SerializeField] private bool animateMarkerAppearance = true;
        [SerializeField] private float animationDuration = 0.3f;

        [Header("Layers / Interaction")]
        [SerializeField] private bool showDistanceOnMarkers = true;
        [SerializeField] private string visibleLayerFilter = ""; // empty = all

        [Header("Reference")]
        [SerializeField] private Transform referencePoint;
        [SerializeField] private Transform markersContainer;

        // Events
        public event Action<Marker> OnMarkerCreated;
        public event Action<Marker> OnMarkerUpdated;
        public event Action<Marker> OnMarkerRemoved;
        public event Action OnMarkersCleared;

        private readonly Dictionary<string, Marker> activeMarkers = new Dictionary<string, Marker>();
        private readonly Queue<Marker> markerPool = new Queue<Marker>();
        private Marker highlightedMarker;

        public int ActiveMarkerCount => activeMarkers.Count;

        /// <summary>The count the PC is told about: practice sensors are left out, as in <see cref="ToCoordinatesJson(bool)"/>.</summary>
        public int ReportedMarkerCount => CountMarkers(!Core.PracticeProject.IsActive);

        private int CountMarkers(bool includeProject)
        {
            if (includeProject) return activeMarkers.Count;
            int n = 0;
            foreach (var kvp in activeMarkers)
                if (!IsProjectMarker(kvp.Key)) n++;
            return n;
        }
        public int MaxMarkers => maxMarkers;

        /// <summary>Raise the marker cap for a bulk load (e.g. a whole project area); never lowers it.</summary>
        public void EnsureCapacity(int count)
        {
            if (count > maxMarkers) maxMarkers = count;
        }
        public IReadOnlyDictionary<string, Marker> ActiveMarkers => activeMarkers;
        public Transform ReferencePoint => referencePoint;

        /// <summary>Show or hide every marker at once without touching the layer filter.</summary>
        public bool MarkersVisible
        {
            get => markersContainer == null || markersContainer.gameObject.activeSelf;
            set { if (markersContainer != null) markersContainer.gameObject.SetActive(value); }
        }
        public string VisibleLayerFilter
        {
            get => visibleLayerFilter;
            set { visibleLayerFilter = value; ApplyLayerVisibility(); }
        }

        private bool hideProjectLayer;
        private static bool? alarmsOnly;

        /// <summary>
        /// Only the project's sensors in alert or alarm stay on site (PlayerPrefs "ui.alarmsOnly"):
        /// among 80 icons the two that matter get lost. Off by default.
        /// </summary>
        public bool AlarmsOnly
        {
            get => alarmsOnly ?? (bool)(alarmsOnly = PlayerPrefs.GetInt("ui.alarmsOnly", 0) == 1);
            set
            {
                alarmsOnly = value;
                PlayerPrefs.SetInt("ui.alarmsOnly", value ? 1 : 0);
                PlayerPrefs.Save();
                ApplyLayerVisibility();
            }
        }

        /// <summary>Alert or alarm: what "alarms only" keeps and the edge beacons point at.</summary>
        public static bool IsAlerting(CoordinateData d) => d != null && (d.status == "alarm" || d.status == "alert");

        private bool IsShown(CoordinateData data)
        {
            string layer = data != null ? data.layer : "default";
            if (!IsLayerVisible(layer)) return false;
            return !AlarmsOnly || !string.Equals(layer, "project", StringComparison.OrdinalIgnoreCase) || IsAlerting(data);
        }

        /// <summary>
        /// Hides every marker on the site (the aligned sensors, the control points, anything the
        /// PC pushed) while the model is previewed on the table or at 1:1: there the model is the
        /// world, and its sensors show on it (or not at all, per the model menu).
        /// </summary>
        public bool HideProjectLayer
        {
            get => hideProjectLayer;
            set
            {
                if (hideProjectLayer == value) return;
                hideProjectLayer = value;
                ApplyLayerVisibility();
            }
        }

        private void Awake()
        {
            if (markersContainer == null)
            {
                GameObject container = new GameObject("MarkersContainer");
                markersContainer = container.transform;
                markersContainer.SetParent(transform);
                markersContainer.localPosition = Vector3.zero;
                markersContainer.localRotation = Quaternion.identity;
            }

            if (referencePoint == null)
            {
                referencePoint = markersContainer;
            }

            if (useObjectPooling)
            {
                InitializePool();
            }
        }

        private void InitializePool()
        {
            int size = Mathf.Min(initialPoolSize, maxMarkers);
            for (int i = 0; i < size; i++)
            {
                Marker marker = CreateNewMarker();
                marker.gameObject.SetActive(false);
                markerPool.Enqueue(marker);
            }
        }

        /// <summary>
        /// Process incoming coordinate data and update markers accordingly
        /// </summary>
        public void ProcessCoordinates(CoordinateResponse response)
        {
            if (response == null) return;

            if (response.clearExisting)
            {
                // Project sensors belong to ProjectAlignment, which re-renders them itself: a
                // PC batch, a session replay or an undo restore must not wipe them.
                if (!HasProjectMarkers()) ClearAllMarkers();
                else
                {
                    var toClear = new List<string>();
                    foreach (var kvp in activeMarkers)
                        if (!IsProjectMarker(kvp.Key)) toClear.Add(kvp.Key);
                    foreach (string id in toClear) RemoveMarker(id);
                }
            }

            if (response.coordinates == null) return;

            HashSet<string> receivedIds = new HashSet<string>();

            foreach (CoordinateData coord in response.coordinates)
            {
                if (coord == null) continue;

                // Apply the Inspector default before EnsureDefaults hardcodes 0.1
                if (coord.scale <= 0f) coord.scale = defaultMarkerScale;
                coord.EnsureDefaults();

                if (!coord.IsValid(maxCoordinateAbs))
                {
                    Debug.LogWarning($"[MarkerManager] Skipping invalid coordinate id={coord.id} ({coord.x},{coord.y},{coord.z})");
                    continue;
                }

                string id = coord.id;
                receivedIds.Add(id);

                if (activeMarkers.TryGetValue(id, out Marker existingMarker))
                {
                    UpdateMarker(existingMarker, coord);
                }
                else
                {
                    if (activeMarkers.Count >= maxMarkers)
                    {
                        Debug.LogWarning($"[MarkerManager] Max markers ({maxMarkers}) reached — skipping '{id}'");
                        continue;
                    }
                    CreateMarker(id, coord);
                }
            }

            // Optional full-state sync: drop markers not present in this payload
            if (response.removeMissing && !response.clearExisting)
            {
                List<string> toRemove = null;
                foreach (var kvp in activeMarkers)
                {
                    // A full-state sync from the PC only covers the PC's own markers.
                    if (!receivedIds.Contains(kvp.Key) && !IsProjectMarker(kvp.Key))
                    {
                        if (toRemove == null) toRemove = new List<string>();
                        toRemove.Add(kvp.Key);
                    }
                }

                if (toRemove != null)
                {
                    foreach (string id in toRemove)
                        RemoveMarker(id);
                }
            }
        }

        /// <summary>
        /// Create a new marker at the specified coordinates
        /// </summary>
        public Marker CreateMarker(string id, CoordinateData data)
        {
            if (referencePoint == null)
            {
                Debug.LogError("[MarkerManager] No reference point set — cannot place marker");
                return null;
            }

            Marker marker = GetMarkerFromPool();

            marker.Initialize(id, data, referencePoint);

            Vector3 worldPosition = referencePoint.TransformPoint(data.ToVector3());
            marker.transform.position = worldPosition;

            float scale = data.scale > 0 ? data.scale : defaultMarkerScale;
            marker.transform.localScale = Vector3.one * scale;

            marker.SetColor(data.TryGetColor(out Color color) ? color : defaultMarkerColor);

            if (!string.IsNullOrEmpty(data.label))
            {
                marker.SetLabel(data.label);
            }

            marker.SetShowDistance(showDistanceOnMarkers);
            marker.gameObject.SetActive(IsShown(data));

            if (animateMarkerAppearance)
            {
                marker.AnimateAppear(animationDuration);
            }

            activeMarkers[id] = marker;
            OnMarkerCreated?.Invoke(marker);

            return marker;
        }

        /// <summary>
        /// Update an existing marker with new data
        /// </summary>
        public void UpdateMarker(Marker marker, CoordinateData data)
        {
            if (marker == null || data == null) return;

            if (referencePoint != null)
            {
                Vector3 worldPosition = referencePoint.TransformPoint(data.ToVector3());
                // A re-render that leaves a marker where it is must not start 200 tweens.
                if ((worldPosition - marker.transform.position).sqrMagnitude > 1e-6f)
                    marker.MoveTo(worldPosition, marker.gameObject.activeInHierarchy ? animationDuration : 0f);
            }

            if (data.scale > 0)
            {
                marker.transform.localScale = Vector3.one * data.scale;
            }

            if (data.TryGetColor(out Color color))
            {
                marker.SetColor(color);
            }

            if (!string.IsNullOrEmpty(data.label))
            {
                marker.SetLabel(data.label);
            }

            marker.UpdateData(data);
            bool shown = IsShown(data); // a sensor going into alarm appears under "alarms only"
            if (marker.gameObject.activeSelf != shown) marker.gameObject.SetActive(shown);
            OnMarkerUpdated?.Invoke(marker);
        }

        /// <summary>
        /// Remove a marker by ID
        /// </summary>
        /// <summary>Id prefix of the sensors ProjectAlignment renders (same as ProjectAlignment.MarkerPrefix).</summary>
        public const string ProjectMarkerPrefix = "prj:";

        public static bool IsProjectMarker(string id) =>
            id != null && id.StartsWith(ProjectMarkerPrefix, StringComparison.Ordinal);

        private bool HasProjectMarkers()
        {
            foreach (var kvp in activeMarkers)
                if (IsProjectMarker(kvp.Key)) return true;
            return false;
        }

        public bool RemoveMarker(string id)
        {
            // Dictionary lookups throw on a null key (DELETE with an empty id, a stale UI row).
            if (id == null || !activeMarkers.TryGetValue(id, out Marker marker))
                return false;

            OnMarkerRemoved?.Invoke(marker);
            ReturnToPoolOrDestroy(marker);
            activeMarkers.Remove(id);
            return true;
        }

        /// <summary>
        /// Clear all active markers
        /// </summary>
        public void ClearAllMarkers()
        {
            foreach (var kvp in activeMarkers)
            {
                ReturnToPoolOrDestroy(kvp.Value);
            }

            activeMarkers.Clear();
            OnMarkersCleared?.Invoke();

            Debug.Log("[MarkerManager] All markers cleared");
        }

        /// <summary>
        /// Set the reference point for coordinate transformation
        /// </summary>
        public void SetReferencePoint(Transform newReference)
        {
            if (newReference == null)
            {
                Debug.LogWarning("[MarkerManager] Attempted to set null reference point");
                return;
            }

            referencePoint = newReference;
            shownPosition = newReference.position; // a new frame: no easing from the old one
            shownRotation = newReference.rotation;

            foreach (var kvp in activeMarkers)
            {
                Marker marker = kvp.Value;
                Vector3 worldPosition = referencePoint.TransformPoint(marker.LocalCoordinates);
                // Snap, and cancel any tween still heading for the old frame (a restored
                // spatial anchor used to leave every sensor offset by its pose).
                marker.MoveTo(worldPosition, 0f);
            }
            OnReferencePointChanged?.Invoke(referencePoint);
        }

        /// <summary>
        /// Get the closest marker to a world position
        /// </summary>
        public Marker GetClosestMarker(Vector3 worldPosition, float maxDistance = float.MaxValue)
        {
            Marker closest = null;
            float closestDistance = maxDistance;

            foreach (var kvp in activeMarkers)
            {
                if (kvp.Value == null) continue;
                float distance = Vector3.Distance(worldPosition, kvp.Value.transform.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = kvp.Value;
                }
            }

            return closest;
        }

        /// <summary>
        /// Get markers within a radius
        /// </summary>
        public List<Marker> GetMarkersInRadius(Vector3 center, float radius)
        {
            List<Marker> result = new List<Marker>();

            foreach (var kvp in activeMarkers)
            {
                if (kvp.Value == null) continue;
                if (Vector3.Distance(center, kvp.Value.transform.position) <= radius)
                {
                    result.Add(kvp.Value);
                }
            }

            return result;
        }

        /// <summary>Ray-highlight closest marker along a ray (controller pointing).</summary>
        [Header("Labels")]
        [Tooltip("Only this many nearest markers show a label at once.")]
        [SerializeField] private int maxLabels = 40;

        private readonly List<(float dist, Marker marker)> byDistance = new List<(float, Marker)>();
        private float nextLabelPass;

        // One loop drives every marker (see Marker.Tick); label slots go to the nearest few,
        // re-ranked 5x a second rather than every frame.
        private void Update()
        {
            Camera cam = Camera.main;
            if (cam == null || activeMarkers.Count == 0) return;
            Vector3 eye = cam.transform.position;
            bool rank = Time.unscaledTime >= nextLabelPass;
            if (rank)
            {
                nextLabelPass = Time.unscaledTime + 0.2f;
                byDistance.Clear();
            }

            foreach (Marker m in activeMarkers.Values)
            {
                if (m == null || !m.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(eye, m.transform.position);
                if (rank) byDistance.Add((d, m));
                m.Tick(cam, d);
            }

            if (!rank) return;
            byDistance.Sort((a, b) => a.dist.CompareTo(b.dist));
            for (int i = 0; i < byDistance.Count; i++)
                byDistance[i].marker.LabelSlot = i < maxLabels;
        }

        /// <summary>Anchor corrections up to this (metres / degrees) glide; larger ones snap.</summary>
        private const float GlideMetres = 0.1f, GlideDegrees = 3f;
        /// <summary>Time constant of the glide, seconds (~0.3 s to settle).</summary>
        private const float GlideTau = 0.1f;

        // The anchor pose the markers are drawn from: it eases toward the real one.
        private Vector3 shownPosition;
        private Quaternion shownRotation = Quaternion.identity;

        // A spatial anchor's transform is corrected by the system (relocalisation, recenter):
        // markers were placed once from it, so move them along whenever it shifts. A small
        // correction glides over ~0.3 s (every icon jumping at once in one frame was jarring);
        // a large one, a real relocalisation, snaps.
        private void LateUpdate()
        {
            if (referencePoint == null || referencePoint == markersContainer) return;
            Vector3 p = referencePoint.position;
            Quaternion r = referencePoint.rotation;
            float off = Vector3.Distance(p, shownPosition), turn = Quaternion.Angle(r, shownRotation);
            // Sub-millimetre tracking noise: nothing to move (moving every marker each frame
            // also cancelled their tweens).
            if (off < 1e-3f && turn < 0.05f) return;
            else if (off > GlideMetres || turn > GlideDegrees)
            {
                shownPosition = p;
                shownRotation = r;
            }
            else
            {
                float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / GlideTau);
                shownPosition = Vector3.Lerp(shownPosition, p, k);
                shownRotation = Quaternion.Slerp(shownRotation, r, k);
            }
            Matrix4x4 frame = Matrix4x4.TRS(shownPosition, shownRotation, referencePoint.lossyScale);
            // MoveTo(…, 0) rather than a plain assignment: it also stops a move tween still
            // running toward a target computed in the old frame, which would drag the marker back.
            foreach (Marker marker in activeMarkers.Values)
                if (marker != null) marker.MoveTo(frame.MultiplyPoint3x4(marker.LocalCoordinates), 0f);
        }

        /// <summary>The marker the controller ray currently points at, if any.</summary>
        public Marker Highlighted => highlightedMarker;

        /// <summary>The frame every marker's local coordinates are relative to was swapped.</summary>
        public event System.Action<Transform> OnReferencePointChanged;

        // Angular pick: icon markers keep a constant on-screen size, so a far one must be as
        // easy to hit as a near one. maxRadius still sets the floor for close-up markers.
        public Marker HighlightAlongRay(Ray ray, float maxDistance = 200f, float maxRadius = 0.35f,
            float maxAngleRad = 0.035f)
        {
            Marker best = null;
            float bestScore = float.MaxValue;

            foreach (var kvp in activeMarkers)
            {
                Marker m = kvp.Value;
                if (m == null || !m.gameObject.activeInHierarchy) continue;
                Vector3 to = m.transform.position - ray.origin;
                float proj = Vector3.Dot(to, ray.direction);
                if (proj < 0 || proj > maxDistance) continue;
                Vector3 closest = ray.origin + ray.direction * proj;
                float lateral = Vector3.Distance(closest, m.transform.position);
                if (lateral > Mathf.Max(maxRadius, proj * maxAngleRad)) continue;
                float score = lateral / Mathf.Max(proj, 0.1f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }

            SetHighlighted(best);
            return best;
        }

        public void SetHighlighted(Marker marker)
        {
            if (highlightedMarker == marker) return;
            if (highlightedMarker != null) highlightedMarker.Highlight(false);
            highlightedMarker = marker;
            if (highlightedMarker != null) highlightedMarker.Highlight(true);
        }

        public void ClearHighlight() => SetHighlighted(null);

        private bool IsLayerVisible(string layer)
        {
            if (hideProjectLayer) return false;
            if (string.IsNullOrEmpty(visibleLayerFilter)) return true;
            return string.Equals(visibleLayerFilter, layer ?? "default", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyLayerVisibility()
        {
            foreach (var kvp in activeMarkers)
            {
                if (kvp.Value == null) continue;
                kvp.Value.gameObject.SetActive(IsShown(kvp.Value.Data));
            }
        }

        /// <summary>
        /// Build a JSON payload of currently active markers (for GET /api/coordinates).
        /// Manual builder — avoids JsonUtility array wrapper limitations.
        /// </summary>
        public string ToCoordinatesJson() => ToCoordinatesJson(true);

        /// <param name="includeProject">False for undo snapshots: project sensors are not the
        /// user's edits, and restoring them would resurrect a project that was since reset.</param>
        public string ToCoordinatesJson(bool includeProject)
        {
            // Practice sensors are made up: never hand them to the PC as if they were the site's.
            if (Core.PracticeProject.IsActive) includeProject = false;
            var sb = new StringBuilder(256 + activeMarkers.Count * 128);
            sb.Append("{\"status\":\"ready\",\"markerCount\":");
            sb.Append(CountMarkers(includeProject));
            sb.Append(",\"maxMarkers\":");
            sb.Append(maxMarkers);
            sb.Append(",\"coordinates\":[");

            bool first = true;
            foreach (var kvp in activeMarkers)
            {
                Marker m = kvp.Value;
                if (m == null) continue;
                if (!includeProject && IsProjectMarker(kvp.Key)) continue;
                CoordinateData d = m.Data;
                if (d == null) continue;

                if (!first) sb.Append(',');
                first = false;

                sb.Append('{');
                AppendJsonString(sb, "id", d.id);
                sb.Append(',');
                sb.Append("\"x\":").Append(d.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"y\":").Append(d.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"z\":").Append(d.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(d.label))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "label", d.label);
                }
                if (!string.IsNullOrEmpty(d.color))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "color", d.color);
                }
                sb.Append(",\"scale\":").Append(d.scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(d.markerType))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "markerType", d.markerType);
                }
                if (!string.IsNullOrEmpty(d.layer))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "layer", d.layer);
                }
                if (!string.IsNullOrEmpty(d.status))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "status", d.status);
                }
                if (!string.IsNullOrEmpty(d.crs))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "crs", d.crs);
                }
                if (!string.IsNullOrEmpty(d.epsg))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "epsg", d.epsg);
                }
                if (d.latitude != 0 || d.longitude != 0 || d.altitude != 0)
                {
                    sb.Append(",\"latitude\":").Append(d.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"longitude\":").Append(d.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"altitude\":").Append(d.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (!string.IsNullOrEmpty(d.notes))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "notes", d.notes);
                }
                if (!string.IsNullOrEmpty(d.photoUrl))
                {
                    sb.Append(',');
                    AppendJsonString(sb, "photoUrl", d.photoUrl);
                }
                if (d.radius > 0)
                    sb.Append(",\"radius\":").Append(d.radius.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"timestamp\":").Append(d.timestamp);
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string key, string value)
            => JsonText.AppendField(sb, key, value);

        private void ReturnToPoolOrDestroy(Marker marker)
        {
            if (marker == null) return;

            if (highlightedMarker == marker) SetHighlighted(null);
            if (useObjectPooling)
            {
                marker.gameObject.SetActive(false);
                markerPool.Enqueue(marker);
            }
            else
            {
                Destroy(marker.gameObject);
            }
        }

        private Marker GetMarkerFromPool()
        {
            if (useObjectPooling && markerPool.Count > 0)
            {
                return markerPool.Dequeue();
            }

            return CreateNewMarker();
        }

        private Marker CreateNewMarker()
        {
            GameObject markerObj;

            if (markerPrefab != null)
            {
                markerObj = Instantiate(markerPrefab, markersContainer);
            }
            else
            {
                markerObj = CreateDefaultMarker();
            }

            Marker marker = markerObj.GetComponent<Marker>();
            if (marker == null)
            {
                marker = markerObj.AddComponent<Marker>();
            }

            return marker;
        }

        private GameObject CreateDefaultMarker()
        {
            GameObject markerObj = new GameObject("Marker");
            markerObj.transform.SetParent(markersContainer);

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.SetParent(markerObj.transform);
            sphere.transform.localPosition = Vector3.zero;
            sphere.transform.localScale = Vector3.one;

            Collider sphereCollider = sphere.GetComponent<Collider>();
            if (sphereCollider != null) Destroy(sphereCollider);

            return markerObj;
        }
    }
}
