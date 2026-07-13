using System.Collections;
using UnityEngine;
using TMPro;
using QuestMarkerApp.API;

namespace QuestMarkerApp.Markers
{
    /// <summary>
    /// Individual marker component that represents a coordinate point in 3D space
    /// </summary>
    public class Marker : MonoBehaviour
    {
        [Header("Visual Components")]
        [SerializeField] private Renderer mainRenderer;
        [SerializeField] private LineRenderer indicatorLine;
        [SerializeField] private TextMeshPro labelText;

        [Header("Behavior")]
        [SerializeField] private bool faceCamera = true;
        [SerializeField] private bool pulseAnimation = false;
        [SerializeField] private float pulseSpeed = 2f;
        [SerializeField] private float pulseIntensity = 0.2f;

        private string markerId;
        private Vector3 localCoordinates;
        private CoordinateData currentData;
        private Transform referencePoint;
        private Material markerMaterial;
        private Color baseColor;
        private Vector3 originalScale;
        private Coroutine moveCoroutine;
        private Coroutine appearCoroutine;

        public string MarkerId => markerId;
        public Vector3 LocalCoordinates => localCoordinates;
        public CoordinateData Data => currentData;

        private void Awake()
        {
            CacheComponents();
        }

        private void Update()
        {
            if (faceCamera && Camera.main != null)
            {
                // Make label face the camera
                if (labelText != null)
                {
                    labelText.transform.LookAt(Camera.main.transform);
                    labelText.transform.Rotate(0, 180, 0);
                }
            }

            if (pulseAnimation)
            {
                float pulse = 1 + Mathf.Sin(Time.time * pulseSpeed) * pulseIntensity;
                transform.localScale = originalScale * pulse;
            }
        }

        private void CacheComponents()
        {
            if (mainRenderer == null)
            {
                mainRenderer = GetComponentInChildren<Renderer>();
            }

            if (indicatorLine == null)
            {
                indicatorLine = GetComponentInChildren<LineRenderer>();
            }

            if (labelText == null)
            {
                labelText = GetComponentInChildren<TextMeshPro>();
            }

            if (mainRenderer != null)
            {
                // Create instance material to avoid shared material issues
                markerMaterial = mainRenderer.material;
            }
        }

        /// <summary>
        /// Initialize the marker with coordinate data
        /// </summary>
        public void Initialize(string id, CoordinateData data, Transform reference)
        {
            markerId = id;
            currentData = data;
            referencePoint = reference;
            localCoordinates = data.ToVector3();
            originalScale = transform.localScale;

            gameObject.name = $"Marker_{id}";
        }

        /// <summary>
        /// Update marker with new data
        /// </summary>
        public void UpdateData(CoordinateData data)
        {
            currentData = data;
            localCoordinates = data.ToVector3();
        }

        /// <summary>
        /// Set the marker color
        /// </summary>
        public void SetColor(Color color)
        {
            baseColor = color;

            if (markerMaterial != null)
            {
                markerMaterial.color = color;

                // Set emission for glowing effect
                if (markerMaterial.HasProperty("_EmissionColor"))
                {
                    markerMaterial.EnableKeyword("_EMISSION");
                    markerMaterial.SetColor("_EmissionColor", color * 0.5f);
                }
            }

            if (indicatorLine != null)
            {
                indicatorLine.startColor = color;
                indicatorLine.endColor = new Color(color.r, color.g, color.b, 0.3f);
            }
        }

        /// <summary>
        /// Set the marker label text
        /// </summary>
        public void SetLabel(string text)
        {
            if (labelText == null)
            {
                CreateLabel();
            }

            if (labelText != null)
            {
                labelText.text = text;
                labelText.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }
        }

        /// <summary>
        /// Animate marker appearance
        /// </summary>
        public void AnimateAppear(float duration)
        {
            if (appearCoroutine != null)
            {
                StopCoroutine(appearCoroutine);
            }

            appearCoroutine = StartCoroutine(AppearAnimation(duration));
        }

        /// <summary>
        /// Move marker to new position with animation
        /// </summary>
        public void MoveTo(Vector3 worldPosition, float duration)
        {
            if (moveCoroutine != null)
            {
                StopCoroutine(moveCoroutine);
            }

            if (duration > 0)
            {
                moveCoroutine = StartCoroutine(MoveAnimation(worldPosition, duration));
            }
            else
            {
                transform.position = worldPosition;
            }
        }

        /// <summary>
        /// Highlight the marker
        /// </summary>
        public void Highlight(bool enable)
        {
            if (markerMaterial != null)
            {
                if (enable)
                {
                    markerMaterial.color = Color.Lerp(baseColor, Color.white, 0.5f);
                    transform.localScale = originalScale * 1.3f;
                }
                else
                {
                    markerMaterial.color = baseColor;
                    transform.localScale = originalScale;
                }
            }
        }

        /// <summary>
        /// Enable/disable pulse animation
        /// </summary>
        public void SetPulse(bool enable)
        {
            pulseAnimation = enable;
            if (!enable)
            {
                transform.localScale = originalScale;
            }
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

                // Elastic ease out
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
            {
                Destroy(markerMaterial);
            }
        }
    }
}
