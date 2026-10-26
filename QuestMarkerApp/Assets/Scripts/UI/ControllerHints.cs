using System;
using System.Collections.Generic;
using UnityEngine;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>Button positions on the Quest 3 (Touch Plus) controllers, in controller-anchor space.</summary>
    [Serializable]
    public class ControllerButtonPoses
    {
        public Hand left;
        public Hand right;

        [Serializable]
        public class Hand
        {
            /// <summary>A on the right, X on the left.</summary>
            public Vector3 primary;
            /// <summary>B on the right, Y on the left.</summary>
            public Vector3 secondary;
            public Vector3 menu;
            public Vector3 thumbstick;
            public Vector3 trigger;
            public Vector3 grip;
            public Vector3 tip;
        }

        /// <summary>
        /// Generated in the editor from the SDK's controller models (ControllerButtonPoseExporter).
        /// The fallback is the same models read straight from the FBX with Unity's X flip.
        /// </summary>
        public static ControllerButtonPoses Load()
        {
            var json = Resources.Load<TextAsset>("ControllerButtonPoses");
            if (json != null)
            {
                var poses = JsonUtility.FromJson<ControllerButtonPoses>(json.text);
                if (poses?.left != null && poses.right != null) return poses;
            }
            Debug.LogWarning("[ControllerHints] Resources/ControllerButtonPoses.json missing — using built-in poses");
            return new ControllerButtonPoses
            {
                left = new Hand
                {
                    primary = new Vector3(0.00537f, 0.00426f, -0.00963f),
                    secondary = new Vector3(0.01561f, 0.00597f, 0.00321f),
                    menu = new Vector3(-0.01047f, 0.00248f, -0.01125f),
                    thumbstick = new Vector3(-0.00629f, -0.00187f, 0.00868f),
                    trigger = new Vector3(0.01485f, -0.00199f, 0.02472f),
                    grip = new Vector3(0.01032f, -0.02178f, 0.01290f)
                },
                right = new Hand
                {
                    primary = new Vector3(-0.00487f, 0.00438f, -0.00973f),
                    secondary = new Vector3(-0.01542f, 0.00552f, 0.00293f),
                    menu = new Vector3(0.01141f, 0.00195f, -0.01085f),
                    thumbstick = new Vector3(0.00596f, -0.00220f, 0.00894f),
                    trigger = new Vector3(-0.01565f, -0.00145f, 0.02432f),
                    grip = new Vector3(-0.01157f, -0.02142f, 0.01263f)
                }
            };
        }
    }

    /// <summary>
    /// Stencil-style controllers: the Touch Plus model drawn over each real controller with a
    /// small action icon sitting on every live button. Resting a finger on a button (the
    /// capacitive sensors on A/B/X/Y, stick and trigger) shows its action in words beside it.
    /// The mapping follows the context — main menu, lists, card, alignment, viewing.
    /// </summary>
    public class ControllerHints : MonoBehaviour
    {
        private enum Button { Secondary, Primary, Thumbstick, Menu, Trigger, Grip }

        private const float BadgeSize = 0.0125f;
        private const float StickBadgeSize = 0.015f;
        private const float SurfaceLift = 0.0075f;   // badge sits just above the button
        // The trigger is under the ring: show its badge below and ahead, where the finger is,
        // not on the face where it crowded the A/X icon.
        private static readonly Vector3 TriggerOffset = new Vector3(0f, -0.016f, 0.014f);
        // Labels sit in a column clear of the controller, one row per visible label.
        private const float LabelColumn = 0.055f;
        private const float LabelRow = 0.019f;
        // 18 px text ≈ 6 mm tall: about 0.85° at a held controller's ~40 cm, legible on Quest 3
        // (0.0003 put it under 0.8°). Rows stay 19 mm apart, so pills still don't touch.
        private const float LabelMetersPerPixel = 0.00034f;

        /// <summary>Main-menu switches.</summary>
        // Remembered across launches; cached because they are read every frame.
        private static bool? enabled, showModel, allLabels;

        private static bool Pref(ref bool? cache, string key, bool fallback) =>
            cache ?? (bool)(cache = PlayerPrefs.GetInt(key, fallback ? 1 : 0) == 1);

        private static void SetPref(ref bool? cache, string key, bool value)
        {
            cache = value;
            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
        }

        public static bool Enabled
        {
            get => Pref(ref enabled, "ui.hints", true);
            set => SetPref(ref enabled, "ui.hints", value);
        }
        public static bool ShowModel
        {
            get => Pref(ref showModel, "ui.model", true);
            set => SetPref(ref showModel, "ui.model", value);
        }
        /// <summary>Labels on every button, not only the touched one.</summary>
        public static bool AlwaysShowLabels
        {
            get => Pref(ref allLabels, "ui.allLabels", false);
            set => SetPref(ref allLabels, "ui.allLabels", value);
        }
        /// <summary>
        /// Until the main menu has been opened once, every label shows — the Menu button has no
        /// touch sensor, so its label could otherwise never appear to a first-time user.
        /// </summary>
        private static bool? seenMenu;
        public static bool FirstRun
        {
            get => !Pref(ref seenMenu, "ui.seenMenu", false);
            set => SetPref(ref seenMenu, "ui.seenMenu", !value);
        }

        private ProjectAlignment project;
        private ControllerButtonPoses poses;
        private readonly HandView left = new HandView { IsLeft = true };
        private readonly HandView right = new HandView { IsLeft = false };
        private string context;
        private bool handedness;
        private bool timeMachine;
        private bool voice;
        private bool walk;
        // Badges are rebuilt on every context change: share one material per (texture, tint)
        // instead of leaking a new one per quad.
        private static readonly Dictionary<(Texture2D, Color), Material> quadMaterials =
            new Dictionary<(Texture2D, Color), Material>();
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        private class HandView
        {
            public bool IsLeft;
            public Transform Root;
            public Transform Model;
            public readonly List<Badge> Badges = new List<Badge>();
        }

        private class Badge
        {
            public Button Button;
            public Vector3 Local;
            public Transform Icon;
            public Transform Label;
        }

        public void Init(ProjectAlignment alignment)
        {
            project = alignment;
            poses = ControllerButtonPoses.Load();
            left.Root = new GameObject("LeftController").transform;
            right.Root = new GameObject("RightController").transform;
            left.Root.SetParent(transform, false);
            right.Root.SetParent(transform, false);
            left.Model = LoadModel("TouchPlusLeft", left.Root);
            right.Model = LoadModel("TouchPlusRight", right.Root);
        }

        private static Transform LoadModel(string name, Transform parent)
        {
            var prefab = Resources.Load<GameObject>("ControllerModels/" + name);
            if (prefab == null)
            {
                Debug.LogWarning($"[ControllerHints] Resources/ControllerModels/{name} missing — badges only");
                return null;
            }
            GameObject model = Instantiate(prefab, parent, false);
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                // The FBX importer gives the models pipeline materials; fall back to a flat
                // dark shell if that shader didn't make it into the build.
                if (r.sharedMaterial == null || !r.sharedMaterial.shader.isSupported)
                    r.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = Brand.Hex(0x3a4240) };
            }
            return model.transform;
        }

        private void LateUpdate()
        {
            if (poses == null) return;

            string ctx = UiState.Context(project);
            // Navigation outranks the time machine in the context key, yet its A and sticks
            // keep working: the badges must follow it opening and closing underneath.
            bool time = DisplacementView.IsActive;
            bool voiceOn = VoiceCommands.Enabled;
            bool walkOn = Models.ModelOverlay.WalkActive;
            if (ctx != context || handedness != Hands.LeftHanded || timeMachine != time || voice != voiceOn
                || walk != walkOn)
            {
                context = ctx;
                timeMachine = time;
                voice = voiceOn;
                walk = walkOn;
                // Roles, not sides: the pointing hand gets the pointer map.
                List<(Button, string, string, bool)> offMap = LeftMap(ctx), domMap = RightMap(ctx);
                if (walkOn) AddWalk(offMap, domMap);
                Rebuild(left, poses.left, Hands.LeftHanded ? domMap : offMap);
                Rebuild(right, poses.right, Hands.LeftHanded ? offMap : domMap);
                handedness = Hands.LeftHanded;
            }

#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            Camera cam = Camera.main;
            if (rig == null || cam == null) return;
            Place(left, rig.leftControllerAnchor, cam.transform, OVRInput.Controller.LTouch);
            Place(right, rig.rightControllerAnchor, cam.transform, OVRInput.Controller.RTouch);
#endif
        }

#if !MOBILE_PREVIEW
        private static void Place(HandView hand, Transform anchor, Transform head, OVRInput.Controller c)
        {
            bool tracked = OVRInput.IsControllerConnected(c) && OVRInput.GetControllerPositionTracked(c);
            bool show = Enabled && tracked;
            if (hand.Root.gameObject.activeSelf != show) hand.Root.gameObject.SetActive(show);
            if (!show) return;

            hand.Root.SetPositionAndRotation(anchor.position, anchor.rotation);
            if (hand.Model != null && hand.Model.gameObject.activeSelf != ShowModel)
                hand.Model.gameObject.SetActive(ShowModel);

            // Outer side and up as seen from the head, so the column reads the same however
            // the wrist is turned.
            Vector3 side = Vector3.ProjectOnPlane(head.right, head.forward).normalized * (hand.IsLeft ? -1f : 1f);
            Vector3 up = Vector3.ProjectOnPlane(head.up, head.forward).normalized;

            bool firstRun = FirstRun;
            int visible = 0;
            foreach (Badge b in hand.Badges)
                if (AlwaysShowLabels || firstRun || Touched(b.Button, c)) visible++;
            float top = (visible - 1) * LabelRow * 0.5f;

            int row = 0;
            foreach (Badge b in hand.Badges) // already ordered top-to-bottom on the controller
            {
                Vector3 local = b.Button == Button.Trigger ? b.Local + TriggerOffset : b.Local + Vector3.up * SurfaceLift;
                Vector3 p = anchor.TransformPoint(local);
                b.Icon.position = p;
                b.Icon.rotation = Quaternion.LookRotation(p - head.position, head.up);

                bool label = AlwaysShowLabels || firstRun || Touched(b.Button, c);
                if (b.Label.gameObject.activeSelf != label) b.Label.gameObject.SetActive(label);
                if (!label) continue;
                b.Label.position = anchor.position + side * LabelColumn + up * (top - row * LabelRow);
                b.Label.rotation = Quaternion.LookRotation(b.Label.position - head.position, head.up);
                row++;
            }
        }

        private static bool Touched(Button b, OVRInput.Controller c)
        {
            switch (b)
            {
                case Button.Primary: return OVRInput.Get(OVRInput.Touch.One, c);
                case Button.Secondary: return OVRInput.Get(OVRInput.Touch.Two, c);
                case Button.Thumbstick: return OVRInput.Get(OVRInput.Touch.PrimaryThumbstick, c);
                case Button.Trigger: return OVRInput.Get(OVRInput.Touch.PrimaryIndexTrigger, c);
                // The grip has no touch sensor: explain it while the ray is on a panel.
                case Button.Grip: return VrPointer.IsHovering || VrPointer.IsDragging;
                default: return false; // the Menu button has no touch sensor
            }
        }
#endif

        // ------------------------------------------------------------------
        //  Button maps — keep in step with MarkerAppController / panels / ProjectAlignment.
        //  (button, brand-kit icon, action, mirror icon)
        // ------------------------------------------------------------------

        private static List<(Button, string, string, bool)> LeftMap(string ctx)
        {
            switch (ctx)
            {
                case "menu":
                case "list":
                case "form":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Thumbstick, "sort", ctx == "form" ? "Πεδίο" : "Πλοήγηση", false),
                        (Button.Menu, "close", "Κλείσιμο", false)
                    };
                case "card":
                case "keyboard":
                    return new List<(Button, string, string, bool)>
                        { (Button.Menu, "close", ctx == "keyboard" ? "Ακύρωση" : "Κλείσιμο", false) };
                case "align":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Primary, "list", "Κλείσιμο ευθυγρ.", false),
                        (Button.Thumbstick, "sort", "Επιλογή αισθητήρα", false),
                        (Button.Menu, "menu", "Μενού", false)
                    };
                case "project":
                    return WithVoice(new List<(Button, string, string, bool)>
                    {
                        (Button.Primary, "menu", "Μενού", false),
                        (Button.Menu, "menu", "Μενού", false)
                    });
                case "route":
                    return WithVoice(new List<(Button, string, string, bool)>
                    {
                        (Button.Primary, "menu", "Μενού", false),
                        (Button.Menu, "menu", "Μενού / διακοπή", false)
                    });
                case "nav":
                    {
                        var map = new List<(Button, string, string, bool)>
                        {
                            (Button.Primary, "menu", "Μενού", false),
                            (Button.Menu, "menu", "Μενού", false)
                        };
                        if (DisplacementView.IsActive) map.Add((Button.Thumbstick, "clock", "Χρόνος / κλίμακα", false));
                        else WithVoice(map);
                        return map;
                    }
                case "time":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Primary, "menu", "Μενού", false),
                        (Button.Thumbstick, "clock", "Χρόνος / κλίμακα", false),
                        (Button.Menu, "menu", "Μενού", false)
                    };
                case "message":
                    return new List<(Button, string, string, bool)>
                        { (Button.Menu, "close", "Κλείσιμο", false) };
                default:
                    return WithVoice(new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "history", "Αναίρεση", false),
                        (Button.Primary, "menu", "Μενού", false),
                        (Button.Menu, "menu", "Μενού", false)
                    });
            }
        }

        // Push-to-talk (VoiceCommands): the off-hand stick click, wherever no panel holds the
        // buttons. It used to be labelled in the project view only, and even with voice off.
        private static List<(Button, string, string, bool)> WithVoice(List<(Button, string, string, bool)> map)
        {
            if (VoiceCommands.Enabled) map.Add((Button.Thumbstick, "edit", "Κράτα κλικ: φωνή", false));
            return map;
        }

        // Life-size model (ModelOverlay.Walk): off-hand stick moves, dominant stick snap-turns.
        // The off-hand stick's click stays push-to-talk, so the two share one label.
        private static void AddWalk(List<(Button, string, string, bool)> offMap, List<(Button, string, string, bool)> domMap)
        {
            int voiceRow = offMap.FindIndex(e => e.Item1 == Button.Thumbstick);
            if (voiceRow >= 0) offMap[voiceRow] = (Button.Thumbstick, "compass", "Κίνηση · κράτα κλικ: φωνή", false);
            else offMap.Add((Button.Thumbstick, "compass", "Κίνηση στο μοντέλο", false));
            domMap.RemoveAll(e => e.Item1 == Button.Thumbstick);
            domMap.Add((Button.Thumbstick, "history", "Στροφή 30°", false));
        }

        private static List<(Button, string, string, bool)> RightMap(string ctx)
        {
            switch (ctx)
            {
                case "menu":
                case "list":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "chevron-right", "Πίσω / Κλείσιμο", true),
                        (Button.Primary, "check", "Επιλογή", false),
                        (Button.Thumbstick, "sort", "Πλοήγηση", false),
                        (Button.Trigger, "check", "Κλικ / Επιλογή", false)
                    };
                case "form":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "chevron-right", "Πίσω", true),
                        (Button.Primary, "edit", "Επεξεργασία πεδίου", false),
                        (Button.Thumbstick, "sort", "Πεδίο", false),
                        (Button.Trigger, "check", "Κλικ / Επεξεργασία", false)
                    };
                case "card":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "close", "Κλείσιμο", false),
                        (Button.Primary, "compass", "Πλοήγηση εδώ", false),
                        // Aimed at another sensor, the trigger switches the card (MarkerAppController).
                        (Button.Trigger, "check", "Κλικ / άλλος αισθητήρας", false)
                    };
                case "keyboard":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "close", "Ακύρωση", false),
                        (Button.Primary, "check", "OK", false),
                        (Button.Trigger, "check", "Πάτα πλήκτρο", false)
                    };
                case "align":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "history", "Αναίρεση σημείου", false),
                        (Button.Trigger, "pin", "Τοποθέτηση στη μύτη", false)
                    };
                case "project":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Trigger, "info", "Καρτέλα αισθητήρα", false)
                    };
                case "route":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "chevron-right", "Παράλειψη σημείου", false),
                        (Button.Trigger, "info", "Καρτέλα αισθητήρα", false)
                    };
                case "nav":
                    {
                        var map = new List<(Button, string, string, bool)>
                        {
                            (Button.Secondary, "close", "Διακοπή πλοήγησης", false),
                            (Button.Trigger, "info", "Καρτέλα αισθητήρα", false)
                        };
                        if (DisplacementView.IsActive)
                        {
                            map.Add((Button.Primary, "play", "Αναπαραγωγή / Παύση", false));
                            map.Add((Button.Thumbstick, "clock", "Χρόνος / κλίμακα", false));
                        }
                        return map;
                    }
                case "time":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "close", "Έξοδος χρονομηχανής", false),
                        (Button.Primary, "play", "Αναπαραγωγή / Παύση", false),
                        (Button.Thumbstick, "clock", "Χρόνος / κλίμακα", false),
                        (Button.Trigger, "info", "Καρτέλα αισθητήρα", false)
                    };
                case "message":
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "close", "Πίσω", false),
                        (Button.Primary, "check", "OK", false)
                    };
                default:
                    return new List<(Button, string, string, bool)>
                    {
                        (Button.Secondary, "trash", "Καθαρισμός όλων", false),
                        (Button.Primary, "location", "Αρχή (0,0,0) εδώ", false)
                    };
            }
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private void Rebuild(HandView hand, ControllerButtonPoses.Hand pose,
            List<(Button button, string icon, string action, bool mirror)> map)
        {
            foreach (Badge b in hand.Badges)
            {
                Destroy(b.Icon.gameObject);
                Destroy(b.Label.gameObject);
            }
            hand.Badges.Clear();

            if (hand.IsLeft == Hands.LeftHanded && context != "basic" && context != "project" && context != "time"
                && context != "nav" && context != "route")
                map.Add((Button.Grip, "pin", "Κράτα: μετακίνηση", false));
            map.Sort((a, b) => a.button.CompareTo(b.button)); // top of the controller first
            foreach (var (button, icon, action, mirror) in map)
            {
                float size = button == Button.Thumbstick ? StickBadgeSize : BadgeSize;
                hand.Badges.Add(new Badge
                {
                    Button = button,
                    Local = PoseOf(pose, button),
                    Icon = BuildIcon(hand.Root, icon, size, mirror),
                    Label = BuildLabel(hand.Root, action, hand.IsLeft)
                });
            }
        }

        private static Vector3 PoseOf(ControllerButtonPoses.Hand pose, Button b)
        {
            switch (b)
            {
                case Button.Primary: return pose.primary;
                case Button.Secondary: return pose.secondary;
                case Button.Menu: return pose.menu;
                case Button.Thumbstick: return pose.thumbstick;
                case Button.Trigger: return pose.trigger;
                default: return pose.grip;
            }
        }

        // Dark disc with a thin light rim and the white glyph — legible on the grey model
        // and on passthrough alike.
        private static Transform BuildIcon(Transform parent, string icon, float size, bool mirror)
        {
            var root = new GameObject("Badge_" + icon).transform;
            root.SetParent(parent, false);
            Quad(root, Brand.DiscTexture, Brand.Fg.WithAlpha(0.85f), size, 0.0004f, 150);
            Quad(root, Brand.DiscTexture, Brand.Canvas.WithAlpha(0.95f), size * 0.88f, 0.0002f, 151);
            Transform glyph = Quad(root, Brand.UiIconTexture(icon), Color.white, size * 0.6f, 0f, 152);
            if (mirror) glyph.localScale = new Vector3(-glyph.localScale.x, glyph.localScale.y, 1f);
            return root;
        }

        private static Transform Quad(Transform parent, Texture2D tex, Color color, float size, float depth, int order)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(parent, false);
            q.transform.localPosition = new Vector3(0f, 0f, depth);
            q.transform.localScale = new Vector3(size, size, 1f);
            var r = q.GetComponent<Renderer>();
            if (!quadMaterials.TryGetValue((tex, color), out Material mat) || mat == null)
                quadMaterials[(tex, color)] = mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex, color = color };
            r.sharedMaterial = mat;
            r.sortingOrder = order; // over panels (100) and markers: these sit on your hand
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return q.transform;
        }

        private static Transform BuildLabel(Transform parent, string action, bool isLeft)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            RectTransform canvas = VrUi.CreateCanvas("Canvas", go.transform, new Vector2(260f, 36f), LabelMetersPerPixel);
            canvas.pivot = new Vector2(isLeft ? 1f : 0f, 0.5f);
            var text = VrUi.Text("Text", canvas, 14f, 0f, 236f, 36f, action, 18f, Brand.Fg, true);
            float w = Mathf.Min(260f, text.preferredWidth + 28f);
            float x0 = isLeft ? 260f - w : 0f;
            VrUi.Card("Pill", canvas, x0, 0, w, 36f, Brand.Surface.WithAlpha(0.95f), Brand.BorderStrong, 18);
            canvas.Find("Pill").SetAsFirstSibling();
            canvas.Find("PillBorder").SetAsFirstSibling();
            text.rectTransform.anchoredPosition = new Vector2(x0 + 14f, 0f);
            go.SetActive(false);
            return go.transform;
        }
    }
}
