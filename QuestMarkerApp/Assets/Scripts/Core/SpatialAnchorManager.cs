using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Manages spatial anchors for persistent placement in real-world space
    /// Uses Meta Quest's spatial anchors for room-scale tracking
    /// </summary>
    public class SpatialAnchorManager : MonoBehaviour
    {
        [Header("Anchor Settings")]
        [SerializeField] private bool autoCreateReferenceAnchor = true;
        [SerializeField] private bool persistAnchors = true;
        [SerializeField] private float anchorSaveDelay = 2f;

        [Header("Debug")]
        [SerializeField] private bool showAnchorGizmos = true;
        [SerializeField] private GameObject anchorVisualizerPrefab;

        // Events
        public event Action<Transform> OnReferenceAnchorCreated;
        public event Action<Transform> OnReferenceAnchorLoaded;
        public event Action<string> OnAnchorError;

        private Transform referenceAnchor;
        private Dictionary<string, Transform> savedAnchors = new Dictionary<string, Transform>();
        private bool isInitialized = false;

        public Transform ReferenceAnchor => referenceAnchor;
        public bool IsInitialized => isInitialized;

        // Additive accessors (used by the mobile preview to pin markers to world origin
        // instead of auto-anchoring at the camera position). No effect on Quest behavior.
        public bool AutoCreateReferenceAnchor { get => autoCreateReferenceAnchor; set => autoCreateReferenceAnchor = value; }
        public bool PersistAnchors { get => persistAnchors; set => persistAnchors = value; }

        private const string ANCHOR_PREFS_KEY = "SpatialAnchors";

        private void Start()
        {
            StartCoroutine(InitializeSpatialAnchors());
        }

        private IEnumerator InitializeSpatialAnchors()
        {
            // Wait for OVR system initialization
            yield return new WaitForSeconds(0.5f);

            // Try to load existing anchors
            if (persistAnchors)
            {
                LoadSavedAnchors();
            }

            // Create reference anchor if none exists
            if (autoCreateReferenceAnchor && referenceAnchor == null)
            {
                CreateReferenceAnchorAtCurrentPosition();
            }

            isInitialized = true;
        }

        /// <summary>
        /// Create a reference anchor at the user's current position
        /// This becomes the origin point for all coordinates
        /// </summary>
        public void CreateReferenceAnchorAtCurrentPosition()
        {
            Vector3 position = Camera.main.transform.position;
            position.y = 0; // Ground level

            Quaternion rotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y, 0);

            CreateReferenceAnchor(position, rotation);
        }

        /// <summary>
        /// Create a reference anchor at a specific position and rotation
        /// </summary>
        public void CreateReferenceAnchor(Vector3 position, Quaternion rotation)
        {
            if (referenceAnchor != null)
            {
                Debug.Log("[SpatialAnchor] Destroying existing reference anchor");
                Destroy(referenceAnchor.gameObject);
            }

            GameObject anchorObj = new GameObject("ReferenceAnchor");
            anchorObj.transform.position = position;
            anchorObj.transform.rotation = rotation;

            referenceAnchor = anchorObj.transform;

            // Add visualizer
            if (showAnchorGizmos)
            {
                CreateAnchorVisualizer(referenceAnchor);
            }

            // Try to add OVR Spatial Anchor component if available
            TryAddOVRSpatialAnchor(anchorObj);

            if (persistAnchors)
            {
                StartCoroutine(SaveAnchorDelayed("reference", position, rotation));
            }

            OnReferenceAnchorCreated?.Invoke(referenceAnchor);
            Debug.Log($"[SpatialAnchor] Reference anchor created at {position}");
        }

        /// <summary>
        /// Create a named anchor point
        /// </summary>
        public Transform CreateNamedAnchor(string name, Vector3 position, Quaternion rotation)
        {
            if (savedAnchors.ContainsKey(name))
            {
                Debug.LogWarning($"[SpatialAnchor] Anchor '{name}' already exists, updating position");
                savedAnchors[name].position = position;
                savedAnchors[name].rotation = rotation;
                return savedAnchors[name];
            }

            GameObject anchorObj = new GameObject($"Anchor_{name}");
            anchorObj.transform.position = position;
            anchorObj.transform.rotation = rotation;

            savedAnchors[name] = anchorObj.transform;

            if (showAnchorGizmos)
            {
                CreateAnchorVisualizer(anchorObj.transform, name);
            }

            TryAddOVRSpatialAnchor(anchorObj);

            if (persistAnchors)
            {
                StartCoroutine(SaveAnchorDelayed(name, position, rotation));
            }

            return anchorObj.transform;
        }

        /// <summary>
        /// Get an anchor by name
        /// </summary>
        public Transform GetAnchor(string name)
        {
            if (name == "reference") return referenceAnchor;
            return savedAnchors.TryGetValue(name, out Transform anchor) ? anchor : null;
        }

        /// <summary>
        /// Calibrate the reference anchor using controller position
        /// Call this when user points controller at a known real-world position
        /// </summary>
        public void CalibrateWithController(Transform controller, Vector3 realWorldOffset)
        {
            if (controller == null) return;

            Vector3 anchorPosition = controller.position + realWorldOffset;
            Quaternion anchorRotation = Quaternion.Euler(0, controller.eulerAngles.y, 0);

            CreateReferenceAnchor(anchorPosition, anchorRotation);
            Debug.Log("[SpatialAnchor] Reference anchor calibrated with controller");
        }

        /// <summary>
        /// Reset the reference anchor to current head position
        /// </summary>
        public void ResetReferenceAnchor()
        {
            CreateReferenceAnchorAtCurrentPosition();
        }

        /// <summary>
        /// Clear all anchors
        /// </summary>
        public void ClearAllAnchors()
        {
            foreach (var kvp in savedAnchors)
            {
                if (kvp.Value != null)
                {
                    Destroy(kvp.Value.gameObject);
                }
            }
            savedAnchors.Clear();

            if (referenceAnchor != null)
            {
                Destroy(referenceAnchor.gameObject);
                referenceAnchor = null;
            }

            if (persistAnchors)
            {
                PlayerPrefs.DeleteKey(ANCHOR_PREFS_KEY);
                PlayerPrefs.Save();
            }

            Debug.Log("[SpatialAnchor] All anchors cleared");
        }

        private void CreateAnchorVisualizer(Transform anchor, string label = null)
        {
            if (anchorVisualizerPrefab != null)
            {
                Instantiate(anchorVisualizerPrefab, anchor);
                return;
            }

            // Create default visualizer - coordinate axes
            GameObject visualizer = new GameObject("Visualizer");
            visualizer.transform.SetParent(anchor);
            visualizer.transform.localPosition = Vector3.zero;
            visualizer.transform.localRotation = Quaternion.identity;

            // X axis (red)
            CreateAxisLine(visualizer.transform, Vector3.right * 0.3f, Color.red);
            // Y axis (green)
            CreateAxisLine(visualizer.transform, Vector3.up * 0.3f, Color.green);
            // Z axis (blue)
            CreateAxisLine(visualizer.transform, Vector3.forward * 0.3f, Color.blue);

            // Center sphere
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.SetParent(visualizer.transform);
            sphere.transform.localPosition = Vector3.zero;
            sphere.transform.localScale = Vector3.one * 0.05f;

            Renderer sphereRenderer = sphere.GetComponent<Renderer>();
            sphereRenderer.material = new Material(Shader.Find("Sprites/Default"));
            sphereRenderer.material.color = Color.yellow;

            // Remove collider
            Collider col = sphere.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        private void CreateAxisLine(Transform parent, Vector3 direction, Color color)
        {
            GameObject lineObj = new GameObject($"Axis_{color}");
            lineObj.transform.SetParent(parent);
            lineObj.transform.localPosition = Vector3.zero;

            LineRenderer line = lineObj.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, Vector3.zero);
            line.SetPosition(1, direction);
            line.startWidth = 0.01f;
            line.endWidth = 0.01f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = color;
            line.endColor = color;
            line.useWorldSpace = false;
        }

        private void TryAddOVRSpatialAnchor(GameObject anchorObj)
        {
            // Attempt to add OVR Spatial Anchor component dynamically
            // This requires the Oculus Integration package to be installed
            Type ovrSpatialAnchorType = Type.GetType("OVRSpatialAnchor, Oculus.VR");
            if (ovrSpatialAnchorType != null)
            {
                anchorObj.AddComponent(ovrSpatialAnchorType);
                Debug.Log("[SpatialAnchor] Added OVRSpatialAnchor component");
            }
        }

        private IEnumerator SaveAnchorDelayed(string name, Vector3 position, Quaternion rotation)
        {
            yield return new WaitForSeconds(anchorSaveDelay);

            // Save to PlayerPrefs (simple persistence)
            // In production, use OVR Spatial Anchor persistence
            string key = $"{ANCHOR_PREFS_KEY}_{name}";
            string data = $"{position.x},{position.y},{position.z},{rotation.x},{rotation.y},{rotation.z},{rotation.w}";
            PlayerPrefs.SetString(key, data);
            PlayerPrefs.Save();

            Debug.Log($"[SpatialAnchor] Saved anchor '{name}'");
        }

        private void LoadSavedAnchors()
        {
            // Load reference anchor
            string refKey = $"{ANCHOR_PREFS_KEY}_reference";
            if (PlayerPrefs.HasKey(refKey))
            {
                string data = PlayerPrefs.GetString(refKey);
                if (TryParseAnchorData(data, out Vector3 pos, out Quaternion rot))
                {
                    CreateReferenceAnchor(pos, rot);
                    OnReferenceAnchorLoaded?.Invoke(referenceAnchor);
                    Debug.Log("[SpatialAnchor] Loaded reference anchor from saved data");
                }
            }
        }

        private bool TryParseAnchorData(string data, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            string[] parts = data.Split(',');
            if (parts.Length != 7) return false;

            try
            {
                position = new Vector3(
                    float.Parse(parts[0]),
                    float.Parse(parts[1]),
                    float.Parse(parts[2])
                );

                rotation = new Quaternion(
                    float.Parse(parts[3]),
                    float.Parse(parts[4]),
                    float.Parse(parts[5]),
                    float.Parse(parts[6])
                );

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
