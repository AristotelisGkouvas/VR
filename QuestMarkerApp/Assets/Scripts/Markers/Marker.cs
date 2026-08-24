using System.Collections;
using UnityEngine;
using TMPro;
using QuestMarkerApp.API;

namespace QuestMarkerApp.Markers
{
    /// <summary>
    /// Individual marker with types (sphere/pin/arrow/zone), LOD, distance label, highlight.
    /// </summary>
    public class Marker : MonoBehaviour
    {
        [Header("Visual Components")]
        [SerializeField] private Renderer mainRenderer;
        [SerializeField] private LineRenderer indicatorLine;
        [SerializeField] private TextMeshPro labelText;
        [SerializeField] private Transform visualRoot;

        [Header("Behavior")]
        [SerializeField] private bool faceCamera = true;
        [SerializeField] private bool pulseAnimation = false;
        [SerializeField] private float pulseSpeed = 2f;
        [SerializeField] private float pulseIntensity = 0.2f;
        [SerializeField] private bool showDistance = true;
        [SerializeField] private bool showGroundLine = true;

        [Header("LOD")]
        [SerializeField] private float lodHideLabelDistance = 12f;
        [SerializeField] private float lodShrinkDistance = 8f;
        [SerializeField] private float lodMinScaleFactor = 0.45f;

        private string markerId;
        private Vector3 localCoordinates;
        private CoordinateData currentData;
        private Transform referencePoint;
        private Material markerMaterial;
        private Color baseColor = Color.cyan;
        private Vector3 originalScale = Vector3.one;
        private Coroutine moveCoroutine;
        private Coroutine appearCoroutine;
        private bool isHighlighted;
        private MarkerType currentType = MarkerType.Sphere;
        private GameObject typeVisual;
        private LineRenderer groundLine;
        private float baseScale = 0.1f;

        public string MarkerId => markerId;
        public Vector3 LocalCoordinates => localCoordinates;
        public CoordinateData Data => currentData;
        public MarkerType Type => currentType;
        public string Layer => currentData != null ? currentData.layer : "default";

        private void Awake()
        {
            CacheComponents();
        }

        private void Update()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            float dist = Vector3.Distance(cam.transform.position, transform.position);

            if (faceCamera && labelText != null)
            {
                labelText.transform.LookAt(cam.transform);
                labelText.transform.Rotate(0, 180, 0);
            }

            UpdateLabelContent(dist);
            ApplyLod(dist);

            if (pulseAnimation && !isHighlighted)
            {
                float pulse = 1 + Mathf.Sin(Time.time * pulseSpeed) * pulseIntensity;
                transform.localScale = originalScale * pulse;
            }

            if (showGroundLine && groundLine != null)
            {
                groundLine.SetPosition(0, transform.position);
                Vector3 ground = transform.position;
                ground.y = referencePoint != null ? referencePoint.position.y : 0f;
                groundLine.SetPosition(1, ground);
            }
        }

        private void CacheComponents()
        {
            if (mainRenderer == null)
                mainRenderer = GetComponentInChildren<Renderer>();
            if (indicatorLine == null)
                indicatorLine = GetComponentInChildren<LineRenderer>();
            if (labelText == null)
                labelText = GetComponentInChildren<TextMeshPro>();
            if (visualRoot == null)
                visualRoot = transform;

            if (mainRenderer != null)
                markerMaterial = mainRenderer.material;
        }

        public void Initialize(string id, CoordinateData data, Transform reference)
        {
            markerId = id;
            currentData = data;
            referencePoint = reference;
            localCoordinates = data.ToVector3();
            baseScale = data.scale > 0 ? data.scale : 0.1f;
            originalScale = Vector3.one * baseScale;
            transform.localScale = originalScale;
            gameObject.name = $"Marker_{id}";

            if (markerMaterial == null)
                CacheComponents();

            EnsureGroundLine();
            ApplyType(data.GetMarkerType(), data);
            // Only apply an explicitly provided color; MarkerManager applies its default
            // right after Initialize, so a pooled marker's stale baseColor never leaks.
            if (data.TryGetColor(out Color parsedColor))
                SetColor(parsedColor);
            if (!string.IsNullOrEmpty(data.label))
                SetLabel(data.label);
        }

        public void UpdateData(CoordinateData data)
        {
            currentData = data;
            localCoordinates = data.ToVector3();
            if (data.scale > 0)
            {
                baseScale = data.scale;
                originalScale = Vector3.one * baseScale;
            }

            var type = data.GetMarkerType();
            if (type != currentType)
                ApplyType(type, data);
            else if (type == MarkerType.Zone)
                UpdateZoneRadius(data.radius > 0 ? data.radius : data.scale);
        }

        public void SetColor(Color color)
        {
            baseColor = color;
            ApplyColorToRenderers(color);
            if (indicatorLine != null)
            {
                indicatorLine.startColor = color;
                indicatorLine.endColor = new Color(color.r, color.g, color.b, 0.3f);
            }
            if (groundLine != null)
            {
                groundLine.startColor = new Color(color.r, color.g, color.b, 0.8f);
                groundLine.endColor = new Color(color.r, color.g, color.b, 0.15f);
            }
        }

        public void SetLabel(string text)
        {
            if (labelText == null)
                CreateLabel();
            if (labelText != null)
            {
                labelText.text = text;
                labelText.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }
        }

        public void AnimateAppear(float duration)
        {
            if (appearCoroutine != null) StopCoroutine(appearCoroutine);
            appearCoroutine = StartCoroutine(AppearAnimation(duration));
        }

        public void MoveTo(Vector3 worldPosition, float duration)
        {
            if (moveCoroutine != null) StopCoroutine(moveCoroutine);
            if (duration > 0)
                moveCoroutine = StartCoroutine(MoveAnimation(worldPosition, duration));
            else
                transform.position = worldPosition;
        }

        public void Highlight(bool enable)
        {
            isHighlighted = enable;
            if (markerMaterial != null)
            {
                if (enable)
                {
                    ApplyColorToRenderers(Color.Lerp(baseColor, Color.white, 0.5f));
                    transform.localScale = originalScale * 1.3f;
                }
                else
                {
                    ApplyColorToRenderers(baseColor);
                    transform.localScale = originalScale;
                }
            }
        }

        public void SetPulse(bool enable)
        {
            pulseAnimation = enable;
            if (!enable && !isHighlighted)
                transform.localScale = originalScale;
        }

        public void SetShowDistance(bool enable) => showDistance = enable;

        private void ApplyType(MarkerType type, CoordinateData data)
        {
            currentType = type;
            // Clear previous procedural type visual (keep sphere if prefab provided)
            if (typeVisual != null)
            {
                Destroy(typeVisual);
                typeVisual = null;
            }

            switch (type)
            {
                case MarkerType.Pin:
                    typeVisual = CreatePinVisual();
                    break;
                case MarkerType.Arrow:
                    typeVisual = CreateArrowVisual();
                    break;
                case MarkerType.Zone:
                    typeVisual = CreateZoneVisual(data.radius > 0 ? data.radius : Mathf.Max(data.scale, 0.5f));
                    break;
                default:
                    // sphere — use existing child renderer
                    break;
            }
        }

        private void UpdateZoneRadius(float radius)
        {
            if (typeVisual == null) return;
            float d = radius * 2f;
            typeVisual.transform.localScale = new Vector3(d, 0.02f, d);
        }

        private GameObject CreatePinVisual()
        {
            var root = new GameObject("PinVisual");
            root.transform.SetParent(transform, false);

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = Vector3.up * 0.15f;
            head.transform.localScale = Vector3.one * 0.12f;
            DestroyCollider(head);

            var stem = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stem.transform.SetParent(root.transform, false);
            stem.transform.localPosition = Vector3.up * 0.05f;
            stem.transform.localScale = new Vector3(0.02f, 0.05f, 0.02f);
            DestroyCollider(stem);

            return root;
        }

        private GameObject CreateArrowVisual()
        {
            var root = new GameObject("ArrowVisual");
            root.transform.SetParent(transform, false);

            var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.transform.SetParent(root.transform, false);
            body.transform.localRotation = Quaternion.Euler(90, 0, 0);
            body.transform.localPosition = Vector3.forward * 0.1f;
            body.transform.localScale = new Vector3(0.03f, 0.1f, 0.03f);
            DestroyCollider(body);

            var tip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tip.transform.SetParent(root.transform, false);
            tip.transform.localPosition = Vector3.forward * 0.22f;
            tip.transform.localRotation = Quaternion.Euler(45, 0, 0);
            tip.transform.localScale = Vector3.one * 0.08f;
            DestroyCollider(tip);

            return root;
        }

        private GameObject CreateZoneVisual(float radius)
        {
            var root = new GameObject("ZoneVisual");
            root.transform.SetParent(transform, false);
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.transform.SetParent(root.transform, false);
            float d = radius * 2f;
            disc.transform.localScale = new Vector3(d, 0.01f, d);
            disc.transform.localPosition = Vector3.zero;
            DestroyCollider(disc);
            var r = disc.GetComponent<Renderer>();
            if (r != null)
            {
                r.material = new Material(Shader.Find("Sprites/Default"));
                var c = baseColor;
                c.a = 0.25f;
                r.material.color = c;
            }
            return root;
        }

        private static void DestroyCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) Destroy(c);
        }

        private void EnsureGroundLine()
        {
            if (!showGroundLine) return;
            if (groundLine != null) return;

            var go = new GameObject("GroundLine");
            go.transform.SetParent(transform, false);
            groundLine = go.AddComponent<LineRenderer>();
            groundLine.positionCount = 2;
            groundLine.startWidth = 0.01f;
            groundLine.endWidth = 0.005f;
            groundLine.material = new Material(Shader.Find("Sprites/Default"));
            groundLine.useWorldSpace = true;
        }

        private void ApplyColorToRenderers(Color color)
        {
            if (markerMaterial != null)
            {
                markerMaterial.color = color;
                if (markerMaterial.HasProperty("_EmissionColor"))
                {
                    markerMaterial.EnableKeyword("_EMISSION");
                    markerMaterial.SetColor("_EmissionColor", color * 0.5f);
                }
            }

            if (typeVisual != null)
            {
                foreach (var r in typeVisual.GetComponentsInChildren<Renderer>())
                {
                    if (r.material != null)
                        r.material.color = color;
                }
            }
        }

        private void UpdateLabelContent(float dist)
        {
            if (labelText == null || currentData == null) return;

            string baseLabel = currentData.label ?? "";
            if (showDistance)
            {
                string distStr = dist < 10f ? dist.ToString("F1") : dist.ToString("F0");
                string status = string.IsNullOrEmpty(currentData.status) ? "" : $" [{currentData.status}]";
                labelText.text = string.IsNullOrEmpty(baseLabel)
                    ? $"{distStr} m{status}"
                    : $"{baseLabel}\n{distStr} m{status}";
            }
            else
            {
                labelText.text = baseLabel;
            }
        }

        private void ApplyLod(float dist)
        {
            if (labelText != null)
            {
                bool show = dist <= lodHideLabelDistance;
                if (labelText.gameObject.activeSelf != show)
                    labelText.gameObject.SetActive(show && !string.IsNullOrEmpty(labelText.text));
            }

            if (isHighlighted || pulseAnimation) return;

            float t = Mathf.InverseLerp(lodShrinkDistance, lodHideLabelDistance * 1.5f, dist);
            float factor = Mathf.Lerp(1f, lodMinScaleFactor, t);
            transform.localScale = originalScale * factor;
        }

        private void CreateLabel()
        {
            GameObject labelObj = new GameObject("Label");
            labelObj.transform.SetParent(transform);
            labelObj.transform.localPosition = Vector3.up * 0.15f;
            labelObj.transform.localScale = Vector3.one * 0.05f;

            labelText = labelObj.AddComponent<TextMeshPro>();
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.fontSize = 36;
            labelText.color = Color.white;
            labelText.outlineWidth = 0.2f;
            labelText.outlineColor = Color.black;
        }

        private IEnumerator AppearAnimation(float duration)
        {
            Vector3 startScale = Vector3.zero;
            Vector3 endScale = originalScale;
            float elapsed = 0;
            transform.localScale = startScale;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                float eased = 1 - Mathf.Pow(2, -10 * t) * Mathf.Cos(t * Mathf.PI * 3);
                transform.localScale = Vector3.Lerp(startScale, endScale, eased);
                yield return null;
            }

            transform.localScale = endScale;
            appearCoroutine = null;
        }

        private IEnumerator MoveAnimation(Vector3 targetPosition, float duration)
        {
            Vector3 startPosition = transform.position;
            float elapsed = 0;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0, 1, elapsed / duration);
                transform.position = Vector3.Lerp(startPosition, targetPosition, t);
                yield return null;
            }
            transform.position = targetPosition;
            moveCoroutine = null;
        }

        private void OnDestroy()
        {
            if (markerMaterial != null)
                Destroy(markerMaterial);
        }
    }
}
