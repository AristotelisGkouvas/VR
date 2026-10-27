using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Models
{
    /// <summary>
    /// The project's sensors on the tabletop miniature and the life-size copy, drawn like the
    /// alignment's icons: type glyph on the family colour, alert ring, name underneath. The dots hang under the model root at their project-local position, so
    /// moving, turning and zooming the model carries them for free; only their size is undone
    /// against the root's scale, to stay readable. Aim at a dot for its name, pull the trigger
    /// for the sensor card. Hidden on the site placement, where the real markers stand.
    /// No colliders (IL2CPP may strip them): aiming is ray maths over at most a few hundred dots.
    /// </summary>
    public class ModelSensors : MonoBehaviour
    {
        private const string EnabledKey = "model.sensors";
        /// <summary>World size of a dot at 1:1 and on the smallest miniature, metres.</summary>
        private const float DotMax = 0.2f, DotMin = 0.025f;
        /// <summary>
        /// At 1:1 far dots grow with distance so they keep this angular size (radians, ~1.7°):
        /// a 20 cm dot 40 m away along the bridge would be a speck.
        /// </summary>
        private const float DotAngular = 0.03f;
        /// <summary>Sensors beyond the model's extent by more than this are not drawn (metres, model scale).</summary>
        private const float FarMargin = 50f;
        /// <summary>Aim tolerance around the ray: an angle, or a distance near the hand.</summary>
        private const float AimDegrees = 1.5f, AimMinMetres = 0.02f;
        private const float RecolourInterval = 1f;
        private const float TagW = 320f, TagH = 70f, TagScale = 0.0008f, TagDistance = 0.9f;
        /// <summary>Names show within this distance of the eye: at 1:1 like a real marker, on the table at arm's length.</summary>
        private const float LabelLifeSize = 40f, LabelTabletop = 1.5f;
        /// <summary>A project point with no colour of its own: the alignment's point colour.</summary>
        private static readonly Color DefaultColor = new Color32(0x00, 0xE5, 0xFF, 0xFF);

        private static bool? enabledCache;

        /// <summary>Menu switch (PlayerPrefs "model.sensors"), on by default.</summary>
        public static bool Enabled
        {
            get => enabledCache ?? (bool)(enabledCache = PlayerPrefs.GetInt(EnabledKey, 1) == 1);
            set
            {
                enabledCache = value;
                PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0);
                PlayerPrefs.Save();
                if (value) refreshNow = true; // switched on: fetch the positions at once
            }
        }

        /// <summary>While the dots show, Terraweb's positions are fetched again after this long.</summary>
        private const double RefreshMinutes = 10;
        /// <summary>Between background attempts, so a failing network is not hammered.</summary>
        private const float RetrySeconds = 60f;
        private static bool refreshNow = true; // once per run, and on switching the dots on
        private float nextRefreshTry;
        private TerrawebFlow flow;

        // The dots follow Terraweb by themselves: while they show on the preview, the loaded
        // project is fetched again in the background (no panel, the alignment kept) on first
        // sight, when switched on, and every RefreshMinutes after that.
        private void AutoRefresh()
        {
            if (Time.unscaledTime < nextRefreshTry) return;
            if (flow == null) flow = FindAnyObjectByType<TerrawebFlow>();
            if (flow == null || !flow.CanReload || flow.IsRefreshing) return;
            if (project == null || project.IsPractice || project.IsModeActive) return;
            bool stale = (DateTime.UtcNow - flow.LastLoaded).TotalMinutes >= RefreshMinutes;
            if (!refreshNow && !stale) return;
            refreshNow = false;
            nextRefreshTry = Time.unscaledTime + RetrySeconds;
            flow.RefreshQuietly();
        }

        private enum Reason { Off, NoProject, Practice, NoModel, Far, Ok }

        private sealed class Dot
        {
            public ProjectPoint Point;
            public Vector3 Local;
            public Transform T;
            public SpriteRenderer Ring;
            public TextMeshPro Label;
            public string State;
        }

        private ModelOverlay overlay;
        private ProjectAlignment project;
        private MarkerManager markers;
        private SensorCard card;
        private Transform container;
        private readonly List<Dot> dots = new List<Dot>();

        // What the dots were built from; any change rebuilds them.
        private ModelData builtModel;
        private object builtPoints;
        private bool builtPractice, builtEnabled, dirty = true;
        private Reason reason = Reason.NoProject;
        private int total;
        private float appliedScale = -1f, dotLocal = 1f, nextRecolour;

        private Dot aimed;
        private float aimedSince;
        private RectTransform hoverTag;
        private Image tagState;
        private TextMeshProUGUI tagName, tagSub;
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        /// <summary>One line for the menu: how many dots, or why there are none.</summary>
        public string Status
        {
            get
            {
                if (Enabled && flow != null && flow.IsRefreshing) return L.T("Ενημέρωση από Terraweb…", "Updating from Terraweb…");
                switch (reason)
                {
                    case Reason.Off: return L.T("Απόκρυψη", "Hidden");
                    case Reason.NoProject: return L.T("Χωρίς έργο", "No project");
                    case Reason.Practice: return L.T("Όχι στο έργο εξάσκησης", "Not in the practice project");
                    case Reason.NoModel: return L.T("Χωρίς μοντέλο", "No model");
                    case Reason.Far: return L.T("Μοντέλο και αισθητήρες μακριά", "Model and sensors far apart");
                    default:
                        return dots.Count < total
                            ? L.T($"{dots.Count} από {total}", $"{dots.Count} of {total}")
                            : L.T($"{dots.Count} αισθητήρες", dots.Count == 1 ? "1 sensor" : $"{dots.Count} sensors");
                }
            }
        }

        public void Init(ModelOverlay modelOverlay, ProjectAlignment alignment, MarkerManager markerManager, SensorCard sensorCard)
        {
            if (overlay != null) overlay.OnChanged -= MarkDirty;
            overlay = modelOverlay;
            project = alignment;
            markers = markerManager;
            card = sensorCard;
            if (overlay != null) overlay.OnChanged += MarkDirty;
            dirty = true;
        }

        private void MarkDirty() => dirty = true;

        private void OnDestroy()
        {
            if (overlay != null) overlay.OnChanged -= MarkDirty;
            if (container != null) Destroy(container.gameObject);
        }

        private void Update()
        {
            if (overlay == null) return;
            CheckSource();
            if (dirty) Rebuild();

            Transform root = overlay.Root;
            // Fetched even with no dot yet: the positions may be what is missing.
            if (Enabled && root != null && root.gameObject.activeInHierarchy
                && overlay.CurrentPlacement != ModelOverlay.Placement.Project)
                AutoRefresh();
            bool show = container != null && dots.Count > 0 && root != null && root.gameObject.activeInHierarchy
                        && overlay.CurrentPlacement != ModelOverlay.Placement.Project;
            if (container != null && container.gameObject.activeSelf != show) container.gameObject.SetActive(show);
            Camera cam = Camera.main;
            if (!show || cam == null)
            {
                SetAimed(null);
                return;
            }

            float rootScale = Mathf.Max(root.lossyScale.x, 1e-6f);
            ApplyScale(rootScale);
            if (Time.unscaledTime >= nextRecolour) Recolour();
            Vector3 eye = cam.transform.position;
            Vector3 up = cam.transform.up;
            float baseWorld = dotLocal * rootScale;
            float labelReach = overlay.CurrentPlacement == ModelOverlay.Placement.LifeSize ? LabelLifeSize : LabelTabletop;
            float pulseAlarm = 1.35f * (1f + 0.12f * Mathf.Sin(Time.time * 6f));
            float pulseAlert = 1.35f * (1f + 0.12f * Mathf.Sin(Time.time * 3f));
            foreach (Dot d in dots)
            {
                Vector3 away = d.T.position - eye;
                if (away.sqrMagnitude > 1e-8f) d.T.rotation = Quaternion.LookRotation(away, up);
                float dist = away.magnitude;
                float world = Mathf.Max(baseWorld, dist * DotAngular);
                d.T.localScale = Vector3.one * (world / rootScale) * (d == aimed ? 1.4f : 1f);
                // Alarm and alert pulse in a ring, as on the real markers.
                if (d.Ring.enabled)
                {
                    float r = (d.State == "alarm" ? pulseAlarm : pulseAlert) * ringBase;
                    d.Ring.transform.localScale = new Vector3(r, r, 1f);
                }
                if (d.Label != null)
                {
                    bool label = Marker.LabelsVisible && dist <= labelReach && d != aimed;
                    if (d.Label.gameObject.activeSelf != label) d.Label.gameObject.SetActive(label);
                }
            }
#if !MOBILE_PREVIEW
            Aim(cam);
#endif
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        // A cheap key: the model object, the project's point array, practice or not, the switch.
        // OnChanged covers loads and unloads too; the poll catches a project swap.
        private void CheckSource()
        {
            ModelData m = overlay.IsLoaded ? overlay.Model : null;
            object pts = project != null ? project.Points : null;
            bool practice = project != null && project.IsPractice;
            if (m != builtModel || !ReferenceEquals(pts, builtPoints) || practice != builtPractice || Enabled != builtEnabled)
                dirty = true;
        }

        private void Rebuild()
        {
            dirty = false;
            ModelData m = overlay.IsLoaded ? overlay.Model : null;
            builtModel = m;
            builtPoints = project != null ? project.Points : null;
            builtPractice = project != null && project.IsPractice;
            builtEnabled = Enabled;
            Clear();

            total = 0;
            if (project != null) foreach (ProjectPoint p in project.Points) if (p != null) total++;
            if (!builtEnabled) { reason = Reason.Off; return; }
            if (project == null || !project.HasProject) { reason = Reason.NoProject; return; }
            // The practice targets sit in a made-up local frame: on a real model they would be noise.
            if (builtPractice) { reason = Reason.Practice; return; }
            if (m == null || overlay.Root == null) { reason = Reason.NoModel; return; }

            EnsureContainer(overlay.Root);
            Bounds near = overlay.ModelBounds;
            near.Expand(FarMargin * 2f); // Expand grows the full size: 50 m each side
            // The sensors' own x/y/z, in the same grid as the model's vertices: nothing is
            // guessed. A typed height offset (menu) covers a model in another height datum.
            heightOffset = ManualOffset ?? 0f;
            foreach (ProjectPoint p in project.Points)
                if (p != null && TryLocal(p, m, heightOffset, out Vector3 local) && near.Contains(local))
                    AddDot(p, local);
            // None near the model: another site, or another CRS. Say so rather than draw dots
            // in empty space kilometres off the table.
            reason = dots.Count == 0 ? Reason.Far : Reason.Ok;
            appliedScale = -1f;
            Debug.Log($"[ModelSensors] {dots.Count} of {total} sensors on '{overlay.LoadedName}'" +
                      (heightOffset != 0f ? $", heights shifted {heightOffset:F1} m" : ""));
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // Model-local position of a sensor: its e/n/h less the model's origin, plus the offset.
        private static bool TryLocal(ProjectPoint p, ModelData m, float offset, out Vector3 local)
        {
            local = default;
            if (!IsFinite(p.e) || !IsFinite(p.n) || !IsFinite(p.h)) return false;
            // Double maths first: E/N are millions of metres, past float precision.
            local = new Vector3((float)(p.e - m.OriginE), (float)(p.h + offset - m.OriginH), (float)(p.n - m.OriginN));
            return true;
        }

        /// <summary>Vertical shift applied in the last build (0 unless typed in the menu).</summary>
        private float heightOffset;

        /// <summary>
        /// The height difference typed in the menu for this project (sensor heights + offset =
        /// model heights), or null for none. Only for a model in another height datum than the
        /// sensors' (Konitsa's ΕΓΣΑ87 model: Terraweb z + 426 m); a model in the same local
        /// system as the sensors needs none.
        /// </summary>
        public float? ManualOffset
        {
            get
            {
                string key = OffsetKey();
                return key != null && PlayerPrefs.HasKey(key) ? PlayerPrefs.GetFloat(key) : (float?)null;
            }
            set
            {
                string key = OffsetKey();
                if (key == null) return;
                if (value.HasValue) PlayerPrefs.SetFloat(key, value.Value);
                else PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
                dirty = true;
            }
        }

        /// <summary>The offset in use after the last build, metres.</summary>
        public float AppliedOffset => heightOffset;

        private string OffsetKey() =>
            project != null && project.HasProject ? "model.hoffset." + project.ProjectName : null;

        private void EnsureContainer(Transform root)
        {
            if (container == null)
            {
                container = new GameObject("ModelSensors").transform;
            }
            if (container.parent != root) container.SetParent(root, false);
            container.localPosition = Vector3.zero;
            container.localRotation = Quaternion.identity;
            container.localScale = Vector3.one;
        }

        private void Clear()
        {
            SetAimed(null);
            foreach (Dot d in dots) if (d.T != null) Destroy(d.T.gameObject);
            dots.Clear();
        }

        // The alignment's icon piece for piece (Marker.CreateIconVisual): alert ring, paper halo,
        // the sensor family's disc, the white type glyph, and the name underneath. All on the
        // shared sprite materials, so the dots batch; the colour rides in the vertex colour.
        private void AddDot(ProjectPoint p, Vector3 local)
        {
            string name = string.IsNullOrEmpty(p.label) ? p.id : p.label;
            var go = new GameObject(name);
            go.transform.SetParent(container, false);
            go.transform.localPosition = local;
            Sprite disc = Brand.DiscSprite;
            SpriteRenderer ring = Piece(go.transform, "AlertRing", disc, Brand.Danger, 1.35f, 0.03f, 0, false);
            ringBase = ring.transform.localScale.x / 1.35f;
            Piece(go.transform, "Halo", disc, Brand.MapHalo.WithAlpha(0.85f), 1f, 0.02f, 1);
            Piece(go.transform, "Disc", disc, FamilyColor(p), 0.84f, 0.01f, 2);
            Sprite glyph = Brand.IconSprite(p.icon);
            if (glyph != null) Piece(go.transform, "Glyph", glyph, Color.white, 0.56f, 0f, 3);
            var dot = new Dot { Point = p, Local = local, T = go.transform, Ring = ring, Label = AddLabel(go.transform, name) };
            SetState(dot, p.alertState);
            dots.Add(dot);
        }

        /// <summary>The ring sprite's unit scale (its sprite width undone), from the last dot built.</summary>
        private float ringBase = 1f;

        private static Color FamilyColor(ProjectPoint p) =>
            !string.IsNullOrEmpty(p.color) && ColorUtility.TryParseHtmlString(p.color, out Color c) ? c : DefaultColor;

        // Under the icon in dot units, so it grows with the dot: white on the shared outlined
        // label material, as on the real markers.
        private static TextMeshPro AddLabel(Transform parent, string text)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.down * 0.6f;
            go.transform.localScale = Vector3.one * 0.1f;
            var t = go.AddComponent<TextMeshPro>();
            Brand.Style(t, true);
            if (t.font == null || t.fontSharedMaterial == null)
            {
                Destroy(go);
                return null;
            }
            t.rectTransform.pivot = new Vector2(0.5f, 1f);
            t.rectTransform.sizeDelta = new Vector2(40f, 6f);
            t.alignment = TextAlignmentOptions.Top;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.fontSize = 36;
            t.color = Color.white;
            t.text = text;
            t.sortingOrder = 6;
            Material shared = Brand.LabelMaterial;
            if (shared != null) t.fontSharedMaterial = shared;
            go.SetActive(false);
            return t;
        }

        private static void SetState(Dot d, string state)
        {
            d.State = (state ?? "none").ToLowerInvariant();
            bool alarm = d.State == "alarm", alert = d.State == "alert";
            d.Ring.enabled = alarm || alert;
            d.Ring.color = alarm ? Brand.Danger : Brand.Warning;
        }

        private static SpriteRenderer Piece(Transform parent, string name, Sprite sprite, Color color, float size, float depth, int order,
            bool writesDepth = true)
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
            r.sharedMaterial = writesDepth ? Brand.MarkerSpriteMaterial : Brand.MarkerSpriteFadeMaterial;
            r.sortingOrder = order;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        /// <summary>The marker ring colours: alarm red, alert amber, reference yellow, normal green.</summary>
        private static Color StateColor(string state)
        {
            switch ((state ?? "none").ToLowerInvariant())
            {
                case "alarm": return Brand.Danger;
                case "alert": return Brand.Warning;
                case "reference": return Brand.Control;
                case "none": return Brand.Primary;
                default: return Brand.FgFaint;
            }
        }

        // Live alerts change a point's state in place: follow it, a second later at most.
        private void Recolour()
        {
            nextRecolour = Time.unscaledTime + RecolourInterval;
            foreach (Dot d in dots)
            {
                string s = (d.Point.alertState ?? "none").ToLowerInvariant();
                if (s != d.State) SetState(d, s);
            }
        }

        // Base world size, whatever the zoom: 20 cm at 1:1 (more far away, see DotAngular), shrinking
        // with the model down to 2.5 cm, where a miniature's dots would otherwise vanish.
        private void ApplyScale(float rootScale)
        {
            if (Mathf.Approximately(rootScale, appliedScale)) return;
            appliedScale = rootScale;
            float world = Mathf.Clamp(DotMax * rootScale, DotMin, DotMax);
            dotLocal = world / Mathf.Max(rootScale, 1e-6f);
            foreach (Dot d in dots) d.T.localScale = Vector3.one * dotLocal * (d == aimed ? 1.4f : 1f);
        }

        // ------------------------------------------------------------------
        //  Aim and pick
        // ------------------------------------------------------------------

#if !MOBILE_PREVIEW
        private void Aim(Camera cam)
        {
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            Transform hand = rig != null && Hands.DominantConnected ? Hands.DominantAnchor(rig) : null;
            // The ray belongs to panels first, then to real markers, then to the miniature.
            bool cardOnly = SensorCard.IsOpen && !MainMenu.IsOpen && !FlowPanel.IsOpen && !VrKeyboard.IsOpen;
            bool free = hand != null && (!UiState.AnyModal || cardOnly)
                        && !VrPointer.IsHovering && !VrPointer.IsDragging
                        && (project == null || !project.IsModeActive)
                        && (markers == null || markers.Highlighted == null);
            if (!free)
            {
                SetAimed(null);
                return;
            }

            Vector3 o = hand.position, dir = hand.forward;
            float tan = Mathf.Tan(AimDegrees * Mathf.Deg2Rad);
            Dot best = null;
            float bestScore = float.MaxValue;
            foreach (Dot d in dots)
            {
                Vector3 w = d.T.position - o;
                float along = Vector3.Dot(w, dir);
                if (along <= 0f) continue;
                float off = (w - dir * along).magnitude;
                float tol = Mathf.Max(along * tan, AimMinMetres);
                if (off > tol) continue;
                // Closest to the ray relative to the tolerance; a hair nearer wins a tie.
                float score = off / tol + along * 0.001f;
                if (score < bestScore) { bestScore = score; best = d; }
            }
            SetAimed(best);
            if (aimed == null) return;
            PlaceTag(cam);

            if (Hands.TriggerDown() && !VrPointer.ConsumedThisFrame && (!UiState.InputBlocked || cardOnly) && card != null)
            {
                Feedback.Play(Feedback.Sound.Click);
                Feedback.Buzz(Hands.Dominant, 0.35f, 0.04f);
                // Same id as the real marker: the card measures to it when the site is aligned.
                card.Open(aimed.Point, MarkerManager.ProjectMarkerPrefix + aimed.Point.id, aimed.T);
            }
        }
#endif

        private void SetAimed(Dot d)
        {
            if (d == aimed) return;
            if (aimed != null && aimed.T != null) aimed.T.localScale = Vector3.one * dotLocal;
            aimed = d;
            aimedSince = Time.unscaledTime;
            if (d == null)
            {
                if (hoverTag != null && hoverTag.gameObject.activeSelf) hoverTag.gameObject.SetActive(false);
                return;
            }
            d.T.localScale = Vector3.one * dotLocal * 1.4f;
            if (d.Label != null) d.Label.gameObject.SetActive(false); // the hover tag names it
#if !MOBILE_PREVIEW
            Feedback.Buzz(Hands.Dominant, 0.12f, 0.02f, 0.9f); // a faint "snap", as on a real marker
#endif
            Fill(d.Point);
        }

        private void Fill(ProjectPoint p)
        {
            if (hoverTag == null) BuildTag();
            tagName.text = string.IsNullOrEmpty(p.label) ? p.id : p.label;
            string state;
            switch ((p.alertState ?? "none").ToLowerInvariant())
            {
                case "alarm": state = L.T("Συναγερμός", "Alarm"); break;
                case "alert": state = L.T("Προειδοποίηση", "Alert"); break;
                case "reference": state = L.T("Αναφορά", "Reference"); break;
                case "none": state = L.T("Κανονικό", "Normal"); break;
                default: state = L.T("Άγνωστο", "Unknown"); break;
            }
            tagState.color = StateColor(p.alertState);
            string type = string.IsNullOrEmpty(p.typeLabel) ? "" : p.typeLabel + "  ·  ";
            tagSub.text = $"{type}{state}  ·  H {p.h:F2}  ·  " + L.T("Trigger: καρτέλα", "Trigger: card");
        }

        // At arm's length on the line of sight to the dot, just above it, like HoverInfo's tag:
        // the same size whether the dot is on a table or 20 m off in the life-size copy.
        private void PlaceTag(Camera cam)
        {
            bool on = Time.unscaledTime - aimedSince >= 0.08f
                      && (!SensorCard.IsOpen || SensorCard.MarkerIdOpen != MarkerManager.ProjectMarkerPrefix + aimed.Point.id);
            if (hoverTag.gameObject.activeSelf != on) hoverTag.gameObject.SetActive(on);
            if (!on) return;
            Transform head = cam.transform;
            Vector3 to = aimed.T.position - head.position;
            float d = Mathf.Max(0.05f, to.magnitude);
            float place = Mathf.Min(d, TagDistance);
            float k = place / TagDistance;
            hoverTag.localScale = Vector3.one * TagScale * k;
            hoverTag.position = head.position + to / d * place + head.up * Mathf.Max(0.035f * k, aimed.T.lossyScale.x);
            VrUi.FaceViewer(hoverTag, head);
        }

        private void BuildTag()
        {
            hoverTag = VrUi.CreateCanvas("ModelSensorTag", transform, new Vector2(TagW, TagH), TagScale);
            hoverTag.pivot = new Vector2(0.5f, 0f); // bottom edge just above the line of sight
            hoverTag.GetComponent<Canvas>().sortingOrder = 110;
            VrUi.Card("Bg", hoverTag, 0, 0, TagW, TagH, Brand.Surface.WithAlpha(0.95f), Brand.BorderStrong, 16);
            tagState = VrUi.Icon("State", hoverTag, 16, 14, 16, Brand.DiscSprite, Brand.Primary);
            tagName = VrUi.Text("Name", hoverTag, 40, 6, TagW - 52, 30, "", 19f, Brand.Fg, true);
            tagSub = VrUi.Text("Sub", hoverTag, 16, 38, TagW - 28, 24, "", 13f, Brand.FgMuted);
            hoverTag.gameObject.SetActive(false);
        }
    }
}
