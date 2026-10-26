using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Code-built world-space UGUI in the brand style. Panels are display-only: input
    /// comes from the controller sticks and buttons, so there is no raycaster.
    /// Sizes are canvas pixels; <see cref="CreateCanvas"/> maps them to metres.
    /// </summary>
    public static class VrUi
    {
        /// <summary>Box height per point of font size that always fits one Manrope line (ascent + descent ≈ 1.37 em).</summary>
        private const float LineHeight = 1.45f;

        public static RectTransform CreateCanvas(string name, Transform parent, Vector2 sizePx, float metersPerPixel)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // Above every marker (sorting order 0..6 inside their groups): a sensor 30 m away
            // must never paint through the menu in your hand.
            canvas.sortingOrder = 100;
            // Crisper sliced sprites and text when viewed close up.
            go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 4f;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = sizePx;
            rt.localScale = Vector3.one * metersPerPixel;
            return rt;
        }

        /// <summary>A child rect pinned to the parent's top-left at (x, y), y growing downwards.</summary>
        public static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static Image Panel(string name, Transform parent, float x, float y, float w, float h,
            Color color, int radius = 12)
        {
            var img = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Image>();
            img.sprite = Brand.Rounded(radius);
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>A panel with a 1-px border, drawn as a border-coloured plate behind an inset fill.</summary>
        public static Image Card(string name, Transform parent, float x, float y, float w, float h,
            Color fill, Color border, int radius = 18)
        {
            Panel(name + "Border", parent, x, y, w, h, border, radius);
            return Panel(name, parent, x + 2, y + 2, w - 4, h - 4, fill, radius);
        }

        public static TextMeshProUGUI Text(string name, Transform parent, float x, float y, float w, float h,
            string text, float size, Color color, bool bold = false,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            // Manrope's line is ~1.37 em tall. With Ellipsis overflow, TMP hides the whole line
            // when it does not fit vertically: a 28 px title in a 38 px box simply vanished (the
            // sensor card's name). Grow short boxes around their centre so the line always fits.
            float minH = size * LineHeight;
            if (h < minH)
            {
                y -= (minH - h) * 0.5f;
                h = minH;
            }
            var t = Rect(name, parent, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
            Brand.Style(t, bold);
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            return t;
        }

        public static Image Icon(string name, Transform parent, float x, float y, float size, Sprite sprite, Color color)
        {
            var img = Rect(name, parent, x, y, size, size).gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.preserveAspect = true;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>
        /// A rounded "key cap" with a label, for the controller legends. <paramref name="maxRight"/>
        /// bounds the label (a column edge): it shrinks to fit instead of running into the next column.
        /// </summary>
        public static float KeyCap(Transform parent, float x, float y, string key, string action, float height = 30f,
            float maxRight = float.MaxValue)
        {
            float keyW = Mathf.Max(height, 14f + key.Length * 10f);
            Panel("Key", parent, x, y, keyW, height, Brand.SurfaceSunken, 8);
            Text("KeyText", parent, x, y, keyW, height, key, 14f, Brand.Fg, true, TextAlignmentOptions.Center);
            float labelW = Mathf.Max(40f, Mathf.Min(200f, maxRight - (x + keyW + 8f)));
            var label = Text("Action", parent, x + keyW + 8f, y, labelW, height, action, 15f, Brand.FgMuted);
            if (labelW < 200f)
            {
                label.enableAutoSizing = true;
                label.fontSizeMin = 11f;
                label.fontSizeMax = 15f;
            }
            return x + keyW + 8f + Mathf.Min(label.preferredWidth, labelW) + 18f;
        }

        /// <summary>
        /// A clickable pill: optional brand-kit action icon and a label, registered with
        /// <see cref="VrPointer"/>. Primary buttons are filled green, the rest are quiet.
        /// Pass <paramref name="flipIcon"/> to mirror a glyph (chevron-right → back).
        /// </summary>
        public static RectTransform Button(string name, Transform parent, float x, float y, float w, float h,
            string icon, string label, System.Action click, bool primary = false, bool flipIcon = false,
            System.Func<bool> enabled = null)
        {
            Color fill = primary ? Brand.Primary : Brand.SurfaceSunken;
            Color ink = primary ? Brand.PrimaryFg : Brand.Fg;
            Image bg = Panel(name, parent, x, y, w, h, fill, (int)Mathf.Min(h / 2f, 22f));
            var rt = bg.rectTransform;
            float iconSize = h * 0.5f;
            bool hasLabel = !string.IsNullOrEmpty(label);
            float iconX = hasLabel ? 14f : (w - iconSize) / 2f;
            if (!string.IsNullOrEmpty(icon))
            {
                Image glyph = Icon(name + "Icon", rt, iconX, (h - iconSize) / 2f, iconSize, Brand.UiIconSprite(icon), ink);
                if (flipIcon)
                {
                    // Rect() pivots at the top-left corner, so a plain x-flip mirrored the glyph
                    // onto the left of its box, outside the button (every "back" chevron). Move
                    // the pivot to the centre first, keeping the box where it is, then mirror.
                    RectTransform g = glyph.rectTransform;
                    g.pivot = new Vector2(0.5f, 0.5f);
                    g.anchoredPosition += new Vector2(iconSize * 0.5f, -iconSize * 0.5f);
                    g.localScale = new Vector3(-1f, 1f, 1f);
                }
            }
            if (hasLabel)
            {
                float tx = string.IsNullOrEmpty(icon) ? 0f : iconX + iconSize + 8f;
                Text(name + "Label", rt, tx, 0, w - tx - 10f, h, label, h * 0.36f, ink, true,
                    string.IsNullOrEmpty(icon) ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft);
            }
            VrPointer.Register(rt, click, null, enabled, bg, primary ? Brand.Hex(0xa0d048) : Brand.BorderStrong);
            return rt;
        }

        /// <summary>Face the panel towards the viewer, upright.</summary>
        public static void FaceViewer(Transform panel, Transform viewer)
        {
            Vector3 away = panel.position - viewer.position;
            if (away.sqrMagnitude < 1e-6f) return;
            panel.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        /// <summary>
        /// Where a panel opens: <paramref name="distance"/> ahead along the level gaze (pitch
        /// ignored, so looking down at a controller doesn't push it into the floor) and
        /// <paramref name="drop"/> below the eyes.
        /// </summary>
        public static Vector3 InFront(Transform head, float distance, float drop)
        {
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            return head.position + forward.normalized * distance + Vector3.down * drop;
        }

        /// <summary>
        /// Lazy follow for a world-locked panel: it stays put while it is roughly ahead, and once
        /// the user has turned more than <paramref name="maxYaw"/> away it glides (ease-out, about
        /// half a second) back in front instead of teleporting — a jump of the whole panel in
        /// the periphery reads as the world moving. Only yaw counts: looking down at the
        /// controllers never moves it. <paramref name="gliding"/> is the caller's state.
        /// </summary>
        public static void LazyFollow(Transform panel, Transform head, float distance, float drop, ref bool gliding,
            float maxYaw = 55f)
        {
            if (!gliding)
            {
                Vector3 look = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                Vector3 to = Vector3.ProjectOnPlane(panel.position - head.position, Vector3.up);
                if (look.sqrMagnitude < 1e-4f || to.sqrMagnitude < 1e-4f) return;
                // Walked away from it (aligning means walking between control points): past
                // 1.5x its distance the text is unreadable, so that brings it back too.
                bool tooFar = to.magnitude > distance * 1.5f;
                if (!tooFar && Vector3.Angle(look, to) <= maxYaw) return;
                gliding = true;
            }
            Vector3 target = InFront(head, distance, drop);
            float k = 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
            panel.position = Vector3.Lerp(panel.position, target, k);
            FaceViewer(panel, head);
            if ((panel.position - target).sqrMagnitude < 1e-4f) gliding = false; // within 1 cm
        }
    }
}
