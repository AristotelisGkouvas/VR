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
        public int MaxMarkers => maxMarkers;
        public IReadOnlyDictionary<string, Marker> ActiveMarkers => activeMarkers;
        public Transform ReferencePoint => referencePoint;
        public string VisibleLayerFilter
        {
            get => visibleLayerFilter;
            set { visibleLayerFilter = value; ApplyLayerVisibility(); }
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
                ClearAllMarkers();
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
                    if (!receivedIds.Contains(kvp.Key))
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
            marker.gameObject.SetActive(IsLayerVisible(data.layer));

            if (animateMarkerAppearance)
            {
                marker.AnimateAppear(animationDuration);
            }

            activeMarkers[id] = marker;
            OnMarkerCreated?.Invoke(marker);

            Debug.Log($"[MarkerManager] Created marker '{id}' at {worldPosition}");
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
                marker.MoveTo(worldPosition, animationDuration);
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
            OnMarkerUpdated?.Invoke(marker);
        }

        /// <summary>
        /// Remove a marker by ID
        /// </summary>
        public bool RemoveMarker(string id)
        {
            if (!activeMarkers.TryGetValue(id, out Marker marker))
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

            foreach (var kvp in activeMarkers)
            {
                Marker marker = kvp.Value;
                Vector3 worldPosition = referencePoint.TransformPoint(marker.LocalCoordinates);
                marker.transform.position = worldPosition;
            }
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
        public Marker HighlightAlongRay(Ray ray, float maxDistance = 30f, float maxRadius = 0.35f)
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
                if (lateral > maxRadius) continue;
                float score = proj + lateral * 2f;
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
            if (string.IsNullOrEmpty(visibleLayerFilter)) return true;
            return string.Equals(visibleLayerFilter, layer ?? "default", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyLayerVisibility()
        {
            foreach (var kvp in activeMarkers)
            {
                if (kvp.Value == null) continue;
                kvp.Value.gameObject.SetActive(IsLayerVisible(kvp.Value.Layer));
            }
        }

        /// <summary>
        /// Build a JSON payload of currently active markers (for GET /api/coordinates).
        /// Manual builder — avoids JsonUtility array wrapper limitations.
        /// </summary>
        public string ToCoordinatesJson()
        {
            var sb = new StringBuilder(256 + activeMarkers.Count * 128);
            sb.Append("{\"status\":\"ready\",\"markerCount\":");
            sb.Append(activeMarkers.Count);
            sb.Append(",\"maxMarkers\":");
            sb.Append(maxMarkers);
            sb.Append(",\"coordinates\":[");

            bool first = true;
            foreach (var kvp in activeMarkers)
            {
                Marker m = kvp.Value;
                if (m == null) continue;
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

            GameObject indicator = new GameObject("Indicator");
            indicator.transform.SetParent(markerObj.transform);

            LineRenderer line = indicator.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, Vector3.zero);
            line.SetPosition(1, Vector3.down * 0.5f);
            line.startWidth = 0.02f;
            line.endWidth = 0.01f;
            line.material = new Material(Shader.Find("Sprites/Default"));

            return markerObj;
        }
    }
}
