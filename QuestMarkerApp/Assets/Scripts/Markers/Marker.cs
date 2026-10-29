using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.Rendering;
using QuestMarkerApp.API;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Markers
{
    /// <summary>
    /// Individual marker with types (sphere/pin/arrow/zone/icon), LOD, distance label, highlight.
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
        [SerializeField] private bool showGroundLine = false;
        [Tooltip("Label size in world meters per TMP unit, independent of marker scale.")]
        [SerializeField] private float labelWorldScale = 0.01f;
        [Tooltip("Gap in meters between the top of the marker and its label.")]
        [SerializeField] private float labelGap = 0.04f;

        [Header("Icon markers")]
        [Tooltip("Icon diameter per metre of viewing distance — keeps icons a constant on-screen size.")]
        [SerializeField] private float iconAngularSize = 0.03f;
        [SerializeField] private float iconLabelHideDistance = 40f;

        [Header("LOD")]
        [SerializeField] private float lodHideLabelDistance = 12f;
        [SerializeField] private float lodShrinkDistance = 8f;
        [SerializeField] private float lodMinScaleFactor = 0.45f;

        private string markerId;
        private Vector3 localCoordinates;
        private CoordinateData currentData;
        private Transform referencePoint;
        // The sphere renderer's own (untinted) material; the renderer shows a shared tint of it.
        private Material sphereSource;
        private Color baseColor = Color.cyan;
        private Vector3 originalScale = Vector3.one;
        private Coroutine moveCoroutine;
        private Coroutine appearCoroutine;
        private bool isHighlighted;
        private bool isNavigationTarget;
        private MarkerType currentType = MarkerType.Sphere;
        private GameObject typeVisual;
        private LineRenderer groundLine;
        private float baseScale = 0.1f;
        private Transform iconBillboard;
        private SpriteRenderer alertRing;
        private bool labelShown = true;
        // Show pieces (icon markers): drop-in on load, light wave when an alignment changes.
        private float introStart = -1f, waveStart = -1f;
        private bool introLanded;
        private const float IntroDuration = 0.75f, IntroHeight = 1.4f, WaveDuration = 0.65f;

        private SpriteRenderer labelPlate;
        private string plateText;
        private SpriteRenderer iconDisc;
        private string iconName;
        // Label layout is re-fitted only when these change (every Render used to force a
        // TextMeshPro mesh rebuild on all 200 labels).
        private MarkerType? fittedType;
        private float fittedScale = -1f;
        private int lastDistKey = int.MinValue;
        private string lastDistBase, lastDistStatus;
        // Inspector default of pulseAnimation, restored when a pooled marker is re-initialised.
        private bool defaultPulse;

        // The zone disc keeps its shared sprite material and takes its colour from a property
        // block: `renderer.material` instantiated a material per piece that Destroy(typeVisual)
        // never released.
        private static MaterialPropertyBlock colorBlock;
        private static Material zoneMaterial;
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        // Sphere / pin / arrow pieces: one shared, GPU-instanced material per (source material,
        // colour), so every marker of one colour draws as a single instanced call. Before,
        // `mainRenderer.material` gave each of 200 markers its own copy (200 draw calls that
        // can never batch), and a property block on a lit material also defeats instancing.
        // Bounded by the number of distinct marker colours (plus their highlight tints).
        private static readonly Dictionary<(Material, Color32), Material> tinted = new Dictionary<(Material, Color32), Material>();
        private static readonly Dictionary<Material, Material> tintSource = new Dictionary<Material, Material>();

        private static Material SourceOf(Material m) =>
            m != null && tintSource.TryGetValue(m, out Material s) && s != null ? s : m;

        private static Material Tinted(Material current, Color color)
        {
            Material source = SourceOf(current);
            if (source == null) return null;
            var key = (source, (Color32)color);
            if (tinted.TryGetValue(key, out Material m) && m != null) return m;
            m = new Material(source) { name = source.name + " (tint)", enableInstancing = true };
            // Built-in Standard reads _Color, URP Lit _BaseColor: set whichever exists.
            if (m.HasProperty(ColorId)) m.SetColor(ColorId, color);
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, color);
            if (m.HasProperty(EmissionColorId))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor(EmissionColorId, color * 0.5f);
            }
            tinted[key] = m;
            tintSource[m] = source;
            return m;
        }

        private static Material ZoneMaterial
        {
            get
            {
                if (zoneMaterial != null) return zoneMaterial;
                Shader shader = Shader.Find("Sprites/Default");
                zoneMaterial = shader != null
                    ? new Material(shader) { name = "MarkerZone" }
                    : Brand.MarkerSpriteFadeMaterial;
                return zoneMaterial;
            }
        }

        private static void SetRendererColor(Renderer r, Color color)
        {
            if (r == null) return;
            if (colorBlock == null) colorBlock = new MaterialPropertyBlock();
            colorBlock.Clear();
            colorBlock.SetColor(ColorId, color);
            r.SetPropertyBlock(colorBlock);
        }


        /// <summary>Global label switch (main menu); per-marker distance rules still apply.</summary>
        public static bool LabelsVisible
        {
            get => labelsVisible ?? (bool)(labelsVisible = PlayerPrefs.GetInt("ui.labels", 1) == 1);
            set { labelsVisible = value; PlayerPrefs.SetInt("ui.labels", value ? 1 : 0); PlayerPrefs.Save(); }
        }
        private static bool? labelsVisible;

        public string MarkerId => markerId;
        public Vector3 LocalCoordinates => localCoordinates;
        public CoordinateData Data => currentData;
        public MarkerType Type => currentType;
        public string Layer => currentData != null ? currentData.layer : "default";

        private void Awake()
        {
            defaultPulse = pulseAnimation;
            CacheComponents();
        }

        /// <summary>
        /// Allowed to show its label this frame — MarkerManager grants it to the nearest few
        /// only, so a dense cluster doesn't turn into a wall of names.
        /// </summary>
        public bool LabelSlot { get; set; } = true;

        /// <summary>
        /// Per-frame work, driven by MarkerManager in one loop (hundreds of separate Update()
        /// calls each re-fetching Camera.main cost ~1–2 ms a frame on Quest).
        /// </summary>
        public void Tick(Camera cam, float dist)
        {
            if (cam == null) return;

            UpdateLabelContent(dist);
            FitPlate();
            if (currentType == MarkerType.Icon)
            {
                UpdateIcon(cam, dist);
            }
            else
            {
                ApplyLod(dist);

                if (pulseAnimation && !isHighlighted)
                {
                    float pulse = 1 + Mathf.Sin(Time.time * pulseSpeed) * pulseIntensity;
                    transform.localScale = originalScale * pulse;
                }
            }

            if (showGroundLine && groundLine != null)
            {
                groundLine.SetPosition(0, transform.position);
                Vector3 ground = transform.position;
                ground.y = referencePoint != null ? referencePoint.position.y : 0f;
                groundLine.SetPosition(1, ground);
            }

            // After the LOD / icon pass has decided visibility: only a shown label is turned to
            // the viewer (most of 200 labels are hidden at any time; two transform writes each).
            if (faceCamera && labelText != null && labelText.gameObject.activeSelf)
            {
                labelText.transform.LookAt(cam.transform);
                labelText.transform.Rotate(0, 180, 0);
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
            {
                sphereSource = SourceOf(mainRenderer.sharedMaterial);
                // Nothing in passthrough receives a marker's shadow; a caster still costs a
                // shadow-map draw per marker whenever the scene light has shadows on.
                mainRenderer.shadowCastingMode = ShadowCastingMode.Off;
                mainRenderer.receiveShadows = false;
            }
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
            // Pooled markers are reused: never inherit the previous sensor's navigation flag,
            // highlight (1.3x scale, white tint), label state or pulse.
            isNavigationTarget = false;
            isHighlighted = false;
            labelShown = true;
            pulseAnimation = defaultPulse;
            introStart = waveStart = -1f;
            // Markers used to be SortingGroups (one unit each, back to front), which made every
            // quad its own draw call. Depth-writing sprites (Brand.MarkerSpriteMaterial) now keep
            // near over far, and all halos / discs / glyphs batch across markers.
            if (TryGetComponent(out SortingGroup stale)) Destroy(stale);

            if (sphereSource == null)
                CacheComponents();

            EnsureGroundLine();
            ApplyType(data.GetMarkerType(), data);
            // Only apply an explicitly provided color; MarkerManager applies its default
            // right after Initialize, so a pooled marker's stale baseColor never leaks.
            if (data.TryGetColor(out Color parsedColor))
                SetColor(parsedColor);
            SetLabel(DisplayName);
        }

        // Label falls back to the id so every marker shows a name.
        private string DisplayName =>
            currentData != null && !string.IsNullOrEmpty(currentData.label) ? currentData.label : markerId;

        public void UpdateData(CoordinateData data)
        {
            currentData = data;
            localCoordinates = data.ToVector3();
            if (data.scale > 0)
            {
                baseScale = data.scale;
                originalScale = Vector3.one * baseScale;
                FitLabelToMarker();
            }

            var type = data.GetMarkerType();
            if (type != currentType || (type == MarkerType.Icon && data.icon != iconName))
            {
                ApplyType(type, data);
                ApplyColorToRenderers(baseColor);
                FitLabelToMarker();
            }
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
                FitLabelToMarker();
                labelText.text = text;
                lastDistKey = int.MinValue;
                labelText.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }
        }

        // The label is a child of the scaled marker, so undo that scale to keep the text a
        // constant real-world size and sit it just above the marker's top (unit mesh radius 0.5).
        private void FitLabelToMarker()
        {
            if (labelText == null || baseScale <= 0f) return;
            if (fittedType == currentType && Mathf.Approximately(fittedScale, baseScale)) return;
            fittedType = currentType;
            fittedScale = baseScale;
            labelText.transform.localScale = Vector3.one * (labelWorldScale / baseScale);

            if (currentType == MarkerType.Icon)
            {
                // Under the icon, top-anchored, ink on a paper halo — as on the plan view.
                labelText.rectTransform.pivot = new Vector2(0.5f, 1f);
                labelText.alignment = TextAlignmentOptions.Top;
                labelText.transform.localPosition = Vector3.down * (0.5f + labelGap / baseScale);
            }
            else
            {
                labelText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                labelText.alignment = TextAlignmentOptions.Center;
                labelText.transform.localPosition = Vector3.up * (0.5f + labelGap / baseScale);
            }
            // White on a dark plate reads on sunlit and on night passthrough alike; the old
            // map style (ink on a thin paper halo) vanished in the dark.
            labelText.color = Color.white;
            Material shared = Brand.LabelMaterial;
            if (shared != null && labelText.fontSharedMaterial != shared) labelText.fontSharedMaterial = shared;
            labelText.sortingOrder = 6;
            EnsurePlate();
            plateText = null; // re-fit on the next frame
        }

        private void EnsurePlate()
        {
            if (labelPlate != null || labelText == null) return;
            var go = new GameObject("LabelPlate");
            go.transform.SetParent(labelText.transform, false);
            labelPlate = go.AddComponent<SpriteRenderer>();
            labelPlate.sprite = Brand.Plate;
            labelPlate.sharedMaterial = Brand.MarkerSpriteMaterial;
            labelPlate.drawMode = SpriteDrawMode.Sliced;
            labelPlate.color = Brand.Surface.WithAlpha(0.88f);
            labelPlate.sortingOrder = 5;
            labelPlate.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // Size the plate to the text whenever the text changes.
        private void FitPlate()
        {
            if (labelPlate == null || labelText == null || !labelText.gameObject.activeInHierarchy) return;
            if (plateText == labelText.text) return;
            plateText = labelText.text;
            labelText.ForceMeshUpdate();
            Bounds b = labelText.textBounds;
            bool empty = string.IsNullOrEmpty(plateText) || b.size.x <= 0f;
            labelPlate.enabled = !empty;
            if (empty) return;
            labelPlate.size = new Vector2(b.size.x + 3f, b.size.y + 1.6f);
            // Just behind the glyphs (the label's +Z faces away from the viewer).
            labelPlate.transform.localPosition = new Vector3(b.center.x, b.center.y, 0.05f);
        }

        /// <summary>Fall into place from above with a bounce, after <paramref name="delay"/> seconds.</summary>
        public void PlayIntro(float delay)
        {
            if (currentType != MarkerType.Icon) return;
            introStart = Time.time + delay;
            introLanded = false;
        }

        /// <summary>A light ring sweeps out and the icon swells, after <paramref name="delay"/> seconds.</summary>
        public void PlayWave(float delay)
        {
            if (currentType != MarkerType.Icon) return;
            waveStart = Time.time + delay;
        }

        public void AnimateAppear(float duration)
        {
            // Icons size themselves every frame (and get PlayIntro); the scale tween fought it.
            if (currentType == MarkerType.Icon || !gameObject.activeInHierarchy) return;
            if (appearCoroutine != null) StopCoroutine(appearCoroutine);
            appearCoroutine = StartCoroutine(AppearAnimation(duration));
        }

        public void MoveTo(Vector3 worldPosition, float duration)
        {
            if (moveCoroutine != null) { StopCoroutine(moveCoroutine); moveCoroutine = null; }
            if (duration > 0 && gameObject.activeInHierarchy)
                moveCoroutine = StartCoroutine(MoveAnimation(worldPosition, duration));
            else
                transform.position = worldPosition;
        }

        public void Highlight(bool enable)
        {
            isHighlighted = enable;
            if (sphereSource != null)
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

        /// <summary>Mark this marker as the one the navigator is guiding to.</summary>
        public void SetNavigationTarget(bool enable) => isNavigationTarget = enable;

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
            iconBillboard = null;
            iconDisc = null;
            alertRing = null;
            iconName = null;
            // The icon replaces the sphere; every other type is drawn around it.
            if (mainRenderer != null)
                mainRenderer.enabled = type != MarkerType.Icon;

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
                case MarkerType.Icon:
                    iconName = data.icon;
                    typeVisual = CreateIconVisual(data.icon);
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
                r.sharedMaterial = ZoneMaterial;
                var c = baseColor;
                c.a = 0.25f;
                SetRendererColor(r, c);
            }
            return root;
        }

        /// <summary>
        /// The plan view's haloed dot with the brand-kit glyph on it. Back to front: paper
        /// halo, family-coloured disc, white glyph — all on one camera-facing billboard.
        /// </summary>
        private GameObject CreateIconVisual(string icon)
        {
            var root = new GameObject("IconVisual");
            root.transform.SetParent(transform, false);
            iconBillboard = root.transform;

            Sprite disc = Brand.DiscSprite;
            alertRing = CreateIconSprite(root.transform, "AlertRing", disc, Brand.Danger, 1.35f, 0.03f, 0, false);
            alertRing.enabled = false;
            CreateIconSprite(root.transform, "Halo", disc, Brand.MapHalo.WithAlpha(0.85f), 1.0f, 0.02f, 1, true);
            iconDisc = CreateIconSprite(root.transform, "Disc", disc, baseColor, 0.84f, 0.01f, 2, true);
            Sprite glyph = Brand.IconSprite(icon);
            if (glyph != null)
                CreateIconSprite(root.transform, "Glyph", glyph, Color.white, 0.56f, 0f, 3, true);
            return root;
        }

        // One of two shared materials for every piece of every marker; the colour rides in the
        // sprite's vertex colour, so pieces with the same texture batch into one draw call.
        // sortingOrder keeps the pieces of one marker back to front; the depth test (the
        // material writes depth) keeps a near marker over a far one.
        private static SpriteRenderer CreateIconSprite(Transform parent, string name, Sprite sprite, Color color,
            float size, float depth, int order, bool writesDepth)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            // The billboard's +Z points away from the camera, so larger depth sits behind.
            go.transform.localPosition = new Vector3(0f, 0f, depth);
            float spriteWidth = sprite != null && sprite.bounds.size.x > 0f ? sprite.bounds.size.x : 1f;
            go.transform.localScale = Vector3.one * (size / spriteWidth);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = sprite;
            r.color = color;
            r.sharedMaterial = writesDepth ? Brand.MarkerSpriteLayer(order - 1) : Brand.MarkerSpriteFadeMaterial;
            r.sortingOrder = order;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        // Constant on-screen size, like the plan view's scaleByDistance: a fixed-size icon
        // would vanish across a 100 m site and swamp the view up close.
        private void UpdateIcon(Camera cam, float dist)
        {
            if (iconBillboard != null)
            {
                iconBillboard.rotation = Quaternion.LookRotation(
                    iconBillboard.position - cam.transform.position, cam.transform.up);
            }

            float size = Mathf.Max(baseScale, dist * iconAngularSize) * (isHighlighted ? 1.3f : 1f);
            if (alertRing != null)
            {
                string state = currentData?.status;
                bool alarm = state == "alarm", alert = state == "alert";
                bool stale = currentData != null && currentData.stale;
                alertRing.enabled = alarm || alert || stale;
                if (alarm || alert)
                {
                    alertRing.color = alarm ? Brand.Danger : Brand.Warning;
                    float pulse = 1f + 0.12f * Mathf.Sin(Time.time * (alarm ? 6f : 3f));
                    alertRing.transform.localScale = new Vector3(1.35f * pulse, 1.35f * pulse, 1f);
                }
                else if (stale)
                {
                    // Old reading: a steady grey ring, so it reads apart from a live state.
                    alertRing.color = Brand.FgFaint.WithAlpha(0.95f);
                    alertRing.transform.localScale = new Vector3(1.3f, 1.3f, 1f);
                }
            }
            // The navigation target breathes and stands out so it can be spotted from afar.
            if (isNavigationTarget) size *= 1.6f + 0.25f * Mathf.Sin(Time.time * 4f);

            bool hideLabel = false;
            float drop = 0f;
            if (introStart >= 0f)
            {
                float u = (Time.time - introStart) / IntroDuration;
                if (u < 0f)
                {
                    size *= 0.0001f; // waiting its turn
                    hideLabel = true;
                }
                else if (u < 1f)
                {
                    drop = IntroHeight * (1f - Bounce(u));
                    size *= Mathf.Clamp01(u * 5f);
                    hideLabel = true;
                    if (!introLanded && u >= 0.364f) // first touch-down of the bounce
                    {
                        introLanded = true;
                        Feedback.Play(Feedback.Sound.Drop, 0.35f, 0.045f);
                    }
                }
                else introStart = -1f;
            }

            bool waving = false;
            if (waveStart >= 0f)
            {
                float w = (Time.time - waveStart) / WaveDuration;
                if (w >= 1f) waveStart = -1f;
                else if (w >= 0f)
                {
                    waving = true;
                    size *= 1f + 0.55f * Mathf.Sin(Mathf.PI * w);
                    if (alertRing != null && !alertRing.enabled)
                    {
                        alertRing.enabled = true;
                        alertRing.color = Brand.Primary.WithAlpha(0.55f * (1f - w));
                        float r = 1.2f + 1.3f * w;
                        alertRing.transform.localScale = new Vector3(r, r, 1f);
                    }
                }
            }
            // The ring is shared with the alarm pulse: give it back once the wave has passed.
            if (!waving && alertRing != null && alertRing.enabled && currentData?.status != "alarm"
                && currentData?.status != "alert" && !(currentData != null && currentData.stale))
                alertRing.enabled = false;

            transform.localScale = Vector3.one * size;
            if (iconBillboard != null)
                iconBillboard.localPosition = drop > 0f ? Vector3.up * (drop / Mathf.Max(size, 1e-4f)) : Vector3.zero;

            if (labelText != null)
            {
                float limit = labelShown ? iconLabelHideDistance : iconLabelHideDistance - 2f;
                labelShown = dist <= limit;
                bool show = LabelsVisible && labelShown && LabelSlot && !hideLabel && !string.IsNullOrEmpty(labelText.text);
                if (labelText.gameObject.activeSelf != show)
                    labelText.gameObject.SetActive(show);
            }
        }

        // easeOutBounce: lands at 0.36, two shrinking hops after.
        private static float Bounce(float x)
        {
            const float n = 7.5625f, d = 2.75f;
            if (x < 1f / d) return n * x * x;
            if (x < 2f / d) { x -= 1.5f / d; return n * x * x + 0.75f; }
            if (x < 2.5f / d) { x -= 2.25f / d; return n * x * x + 0.9375f; }
            x -= 2.625f / d;
            return n * x * x + 0.984375f;
        }

        // Primitive pieces (pin, arrow, zone): no collider, and no shadow-map draw.
        private static void DestroyCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) Destroy(c);
            if (go.TryGetComponent(out Renderer r))
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
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
            // Colours come from start/endColor, so the shared sprite material does.
            groundLine.sharedMaterial = Brand.MarkerSpriteFadeMaterial;
            groundLine.useWorldSpace = true;
        }

        private void ApplyColorToRenderers(Color color)
        {
            if (mainRenderer != null && sphereSource != null)
                mainRenderer.sharedMaterial = Tinted(sphereSource, color);

            if (currentType == MarkerType.Icon)
            {
                // Only the disc carries the family colour; halo and glyph keep theirs.
                if (iconDisc != null) iconDisc.color = color;
            }
            else if (typeVisual != null)
            {
                // The zone disc stays translucent whatever colour it is given.
                Color c = currentType == MarkerType.Zone ? new Color(color.r, color.g, color.b, 0.25f) : color;
                bool zone = currentType == MarkerType.Zone;
                foreach (var r in typeVisual.GetComponentsInChildren<Renderer>())
                {
                    if (r.sharedMaterial == null) continue;
                    // Zone: shared sprite material + block. Pin / arrow: shared instanced tint.
                    if (zone) SetRendererColor(r, c);
                    else r.sharedMaterial = Tinted(r.sharedMaterial, c);
                }
            }
        }

        private void UpdateLabelContent(float dist)
        {
            if (labelText == null || currentData == null) return;

            string baseLabel = DisplayName ?? "";
            // Icons label like the plan view: the name only (plus whatever the sender put in it).
            if (showDistance && currentType != MarkerType.Icon)
            {
                // "12.3 m" changes a few times a second at most; formatting it every frame for
                // every marker was ~800 string allocations and 200 TMP mesh rebuilds a frame.
                int key = dist < 10f ? Mathf.RoundToInt(dist * 10f) : 1000000 + Mathf.RoundToInt(dist);
                if (key == lastDistKey && baseLabel == lastDistBase && currentData.status == lastDistStatus) return;
                lastDistKey = key;
                lastDistBase = baseLabel;
                lastDistStatus = currentData.status;
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
                // Empty text folded in before the compare: otherwise an empty label toggled
                // SetActive(false) on an already hidden object every frame.
                bool show = LabelsVisible && LabelSlot && dist <= lodHideLabelDistance
                            && !string.IsNullOrEmpty(labelText.text);
                if (labelText.gameObject.activeSelf != show)
                    labelText.gameObject.SetActive(show);
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
            // Manrope with a Greek fallback, so Greek sensor names render instead of boxes.
            Brand.Style(labelText, true);
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.fontSize = 36;
            labelText.color = Color.white;
            // Without TMP Essential Resources there is no default font/material and setting the
            // outline throws, which aborted marker creation entirely.
            if (labelText.font == null || labelText.fontSharedMaterial == null)
            {
                Debug.LogWarning("[Marker] TextMeshPro has no default font — labels hidden. " +
                                 "Window → TextMeshPro → Import TMP Essential Resources.");
                return;
            }
            Material shared = Brand.LabelMaterial;
            if (shared != null) labelText.fontSharedMaterial = shared;
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
        // No OnDestroy material cleanup: sphere / pin / arrow tints are shared (see Tinted).
    }
}
