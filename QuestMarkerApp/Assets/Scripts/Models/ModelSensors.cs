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
    /// The project's sensors on the tabletop miniature and the life-size copy: a small dot per
    /// sensor, coloured by alert state, with a thin stem down to the model's lowest point so
    /// heights read. The dots hang under the model root at their project-local position, so
    /// moving, turning and zooming the model carries them for free; only their size is undone
    /// against the root's scale, to stay readable. Aim at a dot for its name, pull the trigger
    /// for the sensor card. Hidden on the site placement, where the real markers stand.
    /// No colliders (IL2CPP may strip them): aiming is ray maths over at most a few hundred dots.
    /// </summary>
    public class ModelSensors : MonoBehaviour
    {
        private const string EnabledKey = "model.sensors";
        /// <summary>World size of a dot at 1:1 and on the smallest miniature, metres.</summary>
        private const float DotMax = 0.06f, DotMin = 0.012f;
        /// <summary>Sensors beyond the model's extent by more than this are not drawn (metres, model scale).</summary>
        private const float FarMargin = 50f;
        /// <summary>Aim tolerance around the ray: an angle, or a distance near the hand.</summary>
        private const float AimDegrees = 1.5f, AimMinMetres = 0.02f;
        private const float RecolourInterval = 1f;
        private const float TagW = 320f, TagH = 70f, TagScale = 0.0008f, TagDistance = 0.9f;

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
            }
        }

        private enum Reason { Off, NoProject, Practice, NoModel, Far, Ok }

        private sealed class Dot
        {
            public ProjectPoint Point;
            public Vector3 Local;
            public Transform T;
            public SpriteRenderer Disc;
            public string State;
        }

        private ModelOverlay overlay;
        private ProjectAlignment project;
        private MarkerManager markers;
        private SensorCard card;
        private Transform container;
        private Mesh stemMesh;
        private static Material stemMaterial;
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
            if (stemMesh != null) Destroy(stemMesh);
            if (container != null) Destroy(container.gameObject);
        }

        private void Update()
        {
            if (overlay == null) return;
            CheckSource();
            if (dirty) Rebuild();

            Transform root = overlay.Root;
            bool show = container != null && dots.Count > 0 && root != null && root.gameObject.activeInHierarchy
                        && overlay.CurrentPlacement != ModelOverlay.Placement.Project;
            if (container != null && container.gameObject.activeSelf != show) container.gameObject.SetActive(show);
            Camera cam = Camera.main;
            if (!show || cam == null)
            {
                SetAimed(null);
                return;
            }

            ApplyScale(root.lossyScale.x);
            if (Time.unscaledTime >= nextRecolour) Recolour();
            Vector3 eye = cam.transform.position;
            Vector3 up = cam.transform.up;
            foreach (Dot d in dots)
            {
                Vector3 away = d.T.position - eye;
                if (away.sqrMagnitude > 1e-8f) d.T.rotation = Quaternion.LookRotation(away, up);
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
            foreach (ProjectPoint p in project.Points)
            {
                if (p == null || !IsFinite(p.e) || !IsFinite(p.n) || !IsFinite(p.h)) continue;
                // Double maths first: E/N are millions of metres, past float precision.
                var local = new Vector3((float)(p.e - m.OriginE), (float)(p.h - m.OriginH), (float)(p.n - m.OriginN));
                if (!near.Contains(local)) continue;
                AddDot(p, local);
            }
            // None near the model: another site, or another CRS. Say so rather than draw dots
            // in empty space kilometres off the table.
            reason = dots.Count == 0 ? Reason.Far : Reason.Ok;
            BuildStems();
            appliedScale = -1f;
            Debug.Log($"[ModelSensors] {dots.Count} of {total} sensors on '{overlay.LoadedName}'");
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private void EnsureContainer(Transform root)
        {
            if (container == null)
            {
                container = new GameObject("ModelSensors").transform;
                var stems = new GameObject("Stems");
                stems.transform.SetParent(container, false);
                stemMesh = new Mesh { name = "Sensor stems" };
                stems.AddComponent<MeshFilter>().sharedMesh = stemMesh;
                var r = stems.AddComponent<MeshRenderer>();
                r.sharedMaterial = StemMaterial();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
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
            if (stemMesh != null) stemMesh.Clear();
        }

        // Halo and disc like a real marker's icon, on the same shared sprite material, so the
        // dots batch; the colour rides in the sprite's vertex colour.
        private void AddDot(ProjectPoint p, Vector3 local)
        {
            var go = new GameObject(string.IsNullOrEmpty(p.label) ? p.id : p.label);
            go.transform.SetParent(container, false);
            go.transform.localPosition = local;
            Sprite disc = Brand.DiscSprite;
            Piece(go.transform, "Halo", disc, Brand.MapHalo.WithAlpha(0.9f), 1f, 0.02f, 1);
            SpriteRenderer r = Piece(go.transform, "Disc", disc, StateColor(p.alertState), 0.74f, 0.01f, 2);
            dots.Add(new Dot { Point = p, Local = local, T = go.transform, Disc = r, State = p.alertState });
        }

        private static SpriteRenderer Piece(Transform parent, string name, Sprite sprite, Color color, float size, float depth, int order)
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
            r.sharedMaterial = Brand.MarkerSpriteMaterial;
            r.sortingOrder = order;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        // One line mesh for every stem, in model metres down to the model's lowest point: a
        // single draw call, and it scales with the root like the model does.
        private void BuildStems()
        {
            if (stemMesh == null) return;
            stemMesh.Clear();
            float ground = overlay.ModelBounds.min.y;
            var verts = new List<Vector3>(dots.Count * 2);
            var colors = new List<Color>(dots.Count * 2);
            var indices = new List<int>(dots.Count * 2);
            foreach (Dot d in dots)
            {
                if (d.Local.y - ground < 0.05f) continue; // on the ground already
                Color c = StateColor(d.State);
                indices.Add(verts.Count);
                verts.Add(d.Local);
                colors.Add(c.WithAlpha(0.75f));
                indices.Add(verts.Count);
                verts.Add(new Vector3(d.Local.x, ground, d.Local.z));
                colors.Add(c.WithAlpha(0.15f));
            }
            stemMesh.indexFormat = verts.Count <= ushort.MaxValue ? IndexFormat.UInt16 : IndexFormat.UInt32;
            stemMesh.SetVertices(verts);
            stemMesh.SetColors(colors);
            stemMesh.SetIndices(indices, MeshTopology.Lines, 0, true);
        }

        private static Material StemMaterial()
        {
            if (stemMaterial != null) return stemMaterial;
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            stemMaterial = new Material(shader) { name = "SensorStems" };
            // After the model's fills, like its edges, so a stem shows through a see-through wall.
            stemMaterial.renderQueue = (int)RenderQueue.Transparent + 1;
            return stemMaterial;
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
            bool changed = false;
            foreach (Dot d in dots)
            {
                string s = d.Point.alertState;
                if (s == d.State) continue;
                d.State = s;
                d.Disc.color = StateColor(s);
                changed = true;
            }
            if (changed) BuildStems();
        }

        // Constant world size, whatever the zoom: 6 cm at 1:1 (a real marker's scale), shrinking
        // with the model down to 1.2 cm, where a miniature's dots would otherwise vanish.
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
