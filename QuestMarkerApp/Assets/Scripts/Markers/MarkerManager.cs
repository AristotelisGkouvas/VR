using System;
using System.Collections.Generic;
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

        [Header("Pool Settings")]
        [SerializeField] private bool useObjectPooling = true;
        [SerializeField] private int initialPoolSize = 20;

        [Header("Animation")]
        [SerializeField] private bool animateMarkerAppearance = true;
        [SerializeField] private float animationDuration = 0.3f;

        [Header("Reference")]
        [SerializeField] private Transform referencePoint;
        [SerializeField] private Transform markersContainer;

        // Events
        public event Action<Marker> OnMarkerCreated;
        public event Action<Marker> OnMarkerUpdated;
        public event Action<Marker> OnMarkerRemoved;
        public event Action OnMarkersCleared;

        private Dictionary<string, Marker> activeMarkers = new Dictionary<string, Marker>();
        private Queue<Marker> markerPool = new Queue<Marker>();

        public int ActiveMarkerCount => activeMarkers.Count;
        public IReadOnlyDictionary<string, Marker> ActiveMarkers => activeMarkers;

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
            for (int i = 0; i < initialPoolSize; i++)
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
                string id = string.IsNullOrEmpty(coord.id) ? Guid.NewGuid().ToString() : coord.id;
                receivedIds.Add(id);

                if (activeMarkers.TryGetValue(id, out Marker existingMarker))
                {
                    UpdateMarker(existingMarker, coord);
                }
                else
                {
                    CreateMarker(id, coord);
                }
            }
        }

        /// <summary>
        /// Create a new marker at the specified coordinates
        /// </summary>
        public Marker CreateMarker(string id, CoordinateData data)
        {
            Marker marker = GetMarkerFromPool();

            marker.Initialize(id, data, referencePoint);

            Vector3 worldPosition = referencePoint.TransformPoint(data.ToVector3());
            marker.transform.position = worldPosition;

            float scale = data.scale > 0 ? data.scale : defaultMarkerScale;
            marker.transform.localScale = Vector3.one * scale;

            Color color = data.GetColor();
            if (color == Color.white && defaultMarkerColor != Color.white)
            {
                color = defaultMarkerColor;
            }
            marker.SetColor(color);

            if (!string.IsNullOrEmpty(data.label))
            {
                marker.SetLabel(data.label);
            }

            marker.gameObject.SetActive(true);

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
            Vector3 worldPosition = referencePoint.TransformPoint(data.ToVector3());
            marker.MoveTo(worldPosition, animationDuration);

            if (data.scale > 0)
            {
                marker.transform.localScale = Vector3.one * data.scale;
            }

            Color color = data.GetColor();
            if (color != Color.white)
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
        public void RemoveMarker(string id)
        {
            if (activeMarkers.TryGetValue(id, out Marker marker))
            {
                OnMarkerRemoved?.Invoke(marker);

                if (useObjectPooling)
                {
                    marker.gameObject.SetActive(false);
                    markerPool.Enqueue(marker);
                }
                else
                {
                    Destroy(marker.gameObject);
                }

                activeMarkers.Remove(id);
            }
        }

        /// <summary>
        /// Clear all active markers
        /// </summary>
        public void ClearAllMarkers()
        {
            foreach (var kvp in activeMarkers)
            {
                Marker marker = kvp.Value;

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

            activeMarkers.Clear();
            OnMarkersCleared?.Invoke();

            Debug.Log("[MarkerManager] All markers cleared");
        }

        /// <summary>
        /// Set the reference point for coordinate transformation
        /// </summary>
        public void SetReferencePoint(Transform newReference)
        {
            referencePoint = newReference;

            // Update all existing markers
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
                if (Vector3.Distance(center, kvp.Value.transform.position) <= radius)
                {
                    result.Add(kvp.Value);
                }
            }

            return result;
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
                // Create default marker if no prefab assigned
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

            // Main sphere
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.SetParent(markerObj.transform);
            sphere.transform.localPosition = Vector3.zero;
            sphere.transform.localScale = Vector3.one;

            // Remove collider from sphere
            Collider sphereCollider = sphere.GetComponent<Collider>();
            if (sphereCollider != null) Destroy(sphereCollider);

            // Add pointing indicator (line going down)
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
