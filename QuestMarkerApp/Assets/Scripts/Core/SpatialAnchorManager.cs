using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Manages spatial anchors for persistent placement in real-world space.
    /// Prefers Meta OVRSpatialAnchor (via reflection so MOBILE_PREVIEW still compiles);
    /// falls back to transform + UUID PlayerPrefs persistence.
    /// </summary>
    public class SpatialAnchorManager : MonoBehaviour
    {
        [Header("Anchor Settings")]
        [SerializeField] private bool autoCreateReferenceAnchor = true;
        [SerializeField] private bool persistAnchors = true;
        [SerializeField] private float anchorSaveDelay = 2f;
        [SerializeField] private bool preferOvrSpatialAnchors = true;

        [Header("Debug")]
        [SerializeField] private bool showAnchorGizmos = true;
        [SerializeField] private GameObject anchorVisualizerPrefab;

        public event Action<Transform> OnReferenceAnchorCreated;
        public event Action<Transform> OnReferenceAnchorLoaded;
        public event Action<string> OnAnchorError;
        public event Action<string, string> OnAnchorUuidSaved; // name, uuid

        private Transform referenceAnchor;
        private readonly Dictionary<string, Transform> savedAnchors = new Dictionary<string, Transform>();
        private readonly Dictionary<string, string> anchorUuids = new Dictionary<string, string>();
        private bool isInitialized;
        private bool ovrAvailable;
        private Type ovrSpatialAnchorType;
        private Component ovrReferenceComponent;

        public Transform ReferenceAnchor => referenceAnchor;
        public bool IsInitialized => isInitialized;
        public bool OvrSpatialAnchorsAvailable => ovrAvailable;
        public string ReferenceAnchorUuid =>
            anchorUuids.TryGetValue("reference", out string u) ? u : null;

        public bool AutoCreateReferenceAnchor { get => autoCreateReferenceAnchor; set => autoCreateReferenceAnchor = value; }
        public bool PersistAnchors { get => persistAnchors; set => persistAnchors = value; }

        private const string ANCHOR_PREFS_KEY = "SpatialAnchors";
        private const string ANCHOR_UUID_KEY = "SpatialAnchorUuids";

        private void Start()
        {
            DetectOvr();
            StartCoroutine(InitializeSpatialAnchors());
        }

        private void DetectOvr()
        {
#if MOBILE_PREVIEW
            ovrAvailable = false;
            return;
#else
            if (!preferOvrSpatialAnchors)
            {
                ovrAvailable = false;
                return;
            }

            ovrSpatialAnchorType = Type.GetType("OVRSpatialAnchor, Oculus.VR")
                ?? Type.GetType("OVRSpatialAnchor, Meta.XR.SDK")
                ?? Type.GetType("OVRSpatialAnchor");
            ovrAvailable = ovrSpatialAnchorType != null;
            if (ovrAvailable)
                Debug.Log("[SpatialAnchor] OVRSpatialAnchor type found — using Meta spatial anchors");
            else
                Debug.Log("[SpatialAnchor] OVRSpatialAnchor not found — using transform fallback");
#endif
        }

        private IEnumerator InitializeSpatialAnchors()
        {
            yield return new WaitForSeconds(0.5f);

            if (persistAnchors)
            {
                LoadUuidMap();
                bool loaded = false;
                yield return StartCoroutine(LoadSavedAnchorsCoroutine(success => loaded = success));

                if (!loaded && autoCreateReferenceAnchor && referenceAnchor == null)
                {
                    CreateReferenceAnchorAtCurrentPosition();
                }
            }
            else if (autoCreateReferenceAnchor && referenceAnchor == null)
            {
                CreateReferenceAnchorAtCurrentPosition();
            }

            isInitialized = true;
        }

        public void CreateReferenceAnchorAtCurrentPosition()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[SpatialAnchor] Camera.main is null — creating at world origin");
                CreateReferenceAnchor(Vector3.zero, Quaternion.identity);
                return;
            }

            Vector3 position = cam.transform.position;
            position.y = 0f;
            Quaternion rotation = Quaternion.Euler(0, cam.transform.eulerAngles.y, 0);
            CreateReferenceAnchor(position, rotation);
        }

        public void CreateReferenceAnchor(Vector3 position, Quaternion rotation)
        {
            if (referenceAnchor != null)
            {
                Destroy(referenceAnchor.gameObject);
                referenceAnchor = null;
                ovrReferenceComponent = null;
            }

            GameObject anchorObj = new GameObject("ReferenceAnchor");
            anchorObj.transform.position = position;
            anchorObj.transform.rotation = rotation;
            referenceAnchor = anchorObj.transform;

            if (showAnchorGizmos)
                CreateAnchorVisualizer(referenceAnchor);

            if (ovrAvailable)
            {
                ovrReferenceComponent = TryAddOVRSpatialAnchor(anchorObj);
                if (persistAnchors)
                    StartCoroutine(SaveOvrAnchorWhenReady("reference", ovrReferenceComponent, position, rotation));
            }
            else if (persistAnchors)
            {
                string uuid = Guid.NewGuid().ToString();
                anchorUuids["reference"] = uuid;
                StartCoroutine(SaveAnchorDelayed("reference", position, rotation, uuid));
            }

            OnReferenceAnchorCreated?.Invoke(referenceAnchor);
            Debug.Log($"[SpatialAnchor] Reference anchor created at {position} (ovr={ovrAvailable})");
        }

        public Transform CreateNamedAnchor(string name, Vector3 position, Quaternion rotation)
        {
            if (string.IsNullOrEmpty(name)) name = Guid.NewGuid().ToString("N");

            if (savedAnchors.TryGetValue(name, out Transform existing) && existing != null)
            {
                existing.position = position;
                existing.rotation = rotation;
                return existing;
            }

            GameObject anchorObj = new GameObject($"Anchor_{name}");
            anchorObj.transform.position = position;
            anchorObj.transform.rotation = rotation;
            savedAnchors[name] = anchorObj.transform;

            if (showAnchorGizmos)
                CreateAnchorVisualizer(anchorObj.transform, name);

            Component ovr = null;
            if (ovrAvailable)
                ovr = TryAddOVRSpatialAnchor(anchorObj);

            if (persistAnchors)
            {
                if (ovr != null)
                    StartCoroutine(SaveOvrAnchorWhenReady(name, ovr, position, rotation));
                else
                {
                    string uuid = Guid.NewGuid().ToString();
                    anchorUuids[name] = uuid;
                    StartCoroutine(SaveAnchorDelayed(name, position, rotation, uuid));
                }
            }

            return anchorObj.transform;
        }

        public Transform GetAnchor(string name)
        {
            if (name == "reference") return referenceAnchor;
            return savedAnchors.TryGetValue(name, out Transform anchor) ? anchor : null;
        }

        public void CalibrateWithController(Transform controller, Vector3 realWorldOffset)
        {
            if (controller == null) return;
            Vector3 anchorPosition = controller.position + realWorldOffset;
            Quaternion anchorRotation = Quaternion.Euler(0, controller.eulerAngles.y, 0);
            CreateReferenceAnchor(anchorPosition, anchorRotation);
        }

        public void ResetReferenceAnchor()
        {
            CreateReferenceAnchorAtCurrentPosition();
        }

        public void ClearAllAnchors()
        {
            foreach (var kvp in savedAnchors)
            {
                if (kvp.Value != null) Destroy(kvp.Value.gameObject);
            }
            savedAnchors.Clear();
            anchorUuids.Clear();

            if (referenceAnchor != null)
            {
                Destroy(referenceAnchor.gameObject);
                referenceAnchor = null;
            }
            ovrReferenceComponent = null;

            if (persistAnchors)
            {
                PlayerPrefs.DeleteKey(ANCHOR_PREFS_KEY + "_reference");
                PlayerPrefs.DeleteKey(ANCHOR_UUID_KEY);
                PlayerPrefs.Save();
            }

            Debug.Log("[SpatialAnchor] All anchors cleared");
        }

        /// <summary>JSON status for API / UI.</summary>
        public string GetStatusJson()
        {
            string uuid = ReferenceAnchorUuid ?? "";
            Vector3 p = referenceAnchor != null ? referenceAnchor.position : Vector3.zero;
            return "{" +
                   "\"initialized\":" + (isInitialized ? "true" : "false") + "," +
                   "\"ovrAvailable\":" + (ovrAvailable ? "true" : "false") + "," +
                   "\"hasReference\":" + (referenceAnchor != null ? "true" : "false") + "," +
                   "\"referenceUuid\":\"" + uuid + "\"," +
                   "\"position\":{" +
                   "\"x\":" + p.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   "\"y\":" + p.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   "\"z\":" + p.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                   "}}";
        }

        private Component TryAddOVRSpatialAnchor(GameObject anchorObj)
        {
            if (ovrSpatialAnchorType == null) return null;
            try
            {
                var comp = anchorObj.AddComponent(ovrSpatialAnchorType);
                Debug.Log("[SpatialAnchor] Added OVRSpatialAnchor component");
                return comp;
            }
            catch (Exception e)
            {
                OnAnchorError?.Invoke(e.Message);
                Debug.LogWarning($"[SpatialAnchor] Failed to add OVRSpatialAnchor: {e.Message}");
                return null;
            }
        }

        private IEnumerator SaveOvrAnchorWhenReady(string name, Component ovrComp, Vector3 position, Quaternion rotation)
        {
            if (ovrComp == null)
            {
                string uuid = Guid.NewGuid().ToString();
                anchorUuids[name] = uuid;
                yield return SaveAnchorDelayed(name, position, rotation, uuid);
                yield break;
            }

            // Wait for OVR creation (Created property or similar)
            float timeout = 5f;
            float elapsed = 0f;
            PropertyInfo createdProp = ovrSpatialAnchorType.GetProperty("Created");
            PropertyInfo uuidProp = ovrSpatialAnchorType.GetProperty("Uuid")
                                    ?? ovrSpatialAnchorType.GetProperty("UUID");

            while (elapsed < timeout)
            {
                bool created = true;
                if (createdProp != null)
                {
                    object val = createdProp.GetValue(ovrComp);
                    created = val is bool b && b;
                }
                if (created) break;
                elapsed += 0.1f;
                yield return new WaitForSeconds(0.1f);
            }

            string uuidStr = Guid.NewGuid().ToString();
            if (uuidProp != null)
            {
                object uuidVal = uuidProp.GetValue(ovrComp);
                if (uuidVal != null)
                    uuidStr = uuidVal.ToString();
            }

            // Try Save / SaveAsync via reflection
            bool savedViaOvr = false;
            MethodInfo saveMethod = ovrSpatialAnchorType.GetMethod("Save", Type.EmptyTypes)
                                    ?? ovrSpatialAnchorType.GetMethod("SaveAsync", Type.EmptyTypes);
            if (saveMethod != null)
            {
                try
                {
                    object result = saveMethod.Invoke(ovrComp, null);
                    // If Task-like, wait a bit
                    if (result != null)
                        yield return new WaitForSeconds(0.5f);
                    savedViaOvr = true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SpatialAnchor] OVR Save failed: {e.Message}");
                }
            }

            anchorUuids[name] = uuidStr;
            SaveUuidMap();
            // Always also store pose as fallback for non-OVR load path
            yield return SaveAnchorDelayed(name, position, rotation, uuidStr);

            OnAnchorUuidSaved?.Invoke(name, uuidStr);
            Debug.Log($"[SpatialAnchor] Saved '{name}' uuid={uuidStr} ovrSave={savedViaOvr}");
        }

        private IEnumerator SaveAnchorDelayed(string name, Vector3 position, Quaternion rotation, string uuid)
        {
            yield return new WaitForSeconds(anchorSaveDelay);

            string key = $"{ANCHOR_PREFS_KEY}_{name}";
            string data =
                $"{position.x},{position.y},{position.z}," +
                $"{rotation.x},{rotation.y},{rotation.z},{rotation.w}," +
                $"{uuid ?? ""}";
            PlayerPrefs.SetString(key, data);
            if (!string.IsNullOrEmpty(uuid))
                anchorUuids[name] = uuid;
            SaveUuidMap();
            PlayerPrefs.Save();
            Debug.Log($"[SpatialAnchor] Persisted anchor '{name}'");
        }

        private void SaveUuidMap()
        {
            // name=uuid;name=uuid
            var parts = new List<string>();
            foreach (var kvp in anchorUuids)
                parts.Add($"{kvp.Key}={kvp.Value}");
            PlayerPrefs.SetString(ANCHOR_UUID_KEY, string.Join(";", parts));
        }

        private void LoadUuidMap()
        {
            anchorUuids.Clear();
            if (!PlayerPrefs.HasKey(ANCHOR_UUID_KEY)) return;
            string raw = PlayerPrefs.GetString(ANCHOR_UUID_KEY);
            if (string.IsNullOrEmpty(raw)) return;
            foreach (string part in raw.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                anchorUuids[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
        }

        private IEnumerator LoadSavedAnchorsCoroutine(Action<bool> done)
        {
            string refKey = $"{ANCHOR_PREFS_KEY}_reference";
            if (!PlayerPrefs.HasKey(refKey))
            {
                done?.Invoke(false);
                yield break;
            }

            string data = PlayerPrefs.GetString(refKey);
            if (!TryParseAnchorData(data, out Vector3 pos, out Quaternion rot, out string uuid))
            {
                done?.Invoke(false);
                yield break;
            }

            // OVR anchor re-binding by UUID is not wired — the LoadUnboundAnchors API
            // differs per SDK version. The saved pose below is the supported path.
            CreateReferenceAnchor(pos, rot);
            if (!string.IsNullOrEmpty(uuid))
                anchorUuids["reference"] = uuid;
            OnReferenceAnchorLoaded?.Invoke(referenceAnchor);
            Debug.Log("[SpatialAnchor] Loaded reference anchor from saved pose");
            done?.Invoke(true);
        }

        private void CreateAnchorVisualizer(Transform anchor, string label = null)
        {
            if (anchorVisualizerPrefab != null)
            {
                Instantiate(anchorVisualizerPrefab, anchor);
                return;
            }

            GameObject visualizer = new GameObject("Visualizer");
            visualizer.transform.SetParent(anchor);
            visualizer.transform.localPosition = Vector3.zero;
            visualizer.transform.localRotation = Quaternion.identity;

            CreateAxisLine(visualizer.transform, Vector3.right * 0.3f, Color.red);
            CreateAxisLine(visualizer.transform, Vector3.up * 0.3f, Color.green);
            CreateAxisLine(visualizer.transform, Vector3.forward * 0.3f, Color.blue);

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.SetParent(visualizer.transform);
            sphere.transform.localPosition = Vector3.zero;
            sphere.transform.localScale = Vector3.one * 0.05f;
            var sphereRenderer = sphere.GetComponent<Renderer>();
            sphereRenderer.material = new Material(Shader.Find("Sprites/Default")) { color = Color.yellow };
            var col = sphere.GetComponent<Collider>();
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

        private bool TryParseAnchorData(string data, out Vector3 position, out Quaternion rotation, out string uuid)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            uuid = null;

            string[] parts = data.Split(',');
            if (parts.Length < 7) return false;

            try
            {
                position = new Vector3(
                    float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                rotation = new Quaternion(
                    float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[6], System.Globalization.CultureInfo.InvariantCulture));
                if (parts.Length >= 8)
                    uuid = parts[7];
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
