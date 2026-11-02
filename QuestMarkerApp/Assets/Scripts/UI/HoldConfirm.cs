using UnityEngine;
using UnityEngine.UI;
using TMPro;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Press-and-hold for the buttons that undo work in one press (clear every marker, move
    /// the origin, stop a route): a ring fills over <see cref="Seconds"/> beside the hand and
    /// the action runs only when it closes. A glove brushing a button does nothing.
    /// One hold at a time; callers pass the button's held state every frame.
    /// </summary>
    public class HoldConfirm : MonoBehaviour
    {
        public const float Seconds = 0.8f;

        private static HoldConfirm instance;
        private static string activeKey;
        private static float started;
        private static int lastFed = -1;
        private static bool fired;

        private RectTransform canvas;
        private Image fill;
        private TextMeshProUGUI label;
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        /// <summary>
        /// Feed the held state of the button for <paramref name="key"/> each frame; true once,
        /// on the frame the hold completes. <paramref name="what"/> names the action on the ring.
        /// </summary>
        public static bool Run(string key, bool held, string what)
        {
            if (!held)
            {
                if (activeKey == key) { activeKey = null; fired = false; }
                return false;
            }
            bool fresh = lastFed < Time.frameCount - 1; // not fed last frame: any earlier hold is over
            if (activeKey != key || fresh)
            {
                if (activeKey != null && activeKey != key && !fresh) return false; // another hold runs
                activeKey = key;
                started = Time.unscaledTime;
                fired = false;
                if (instance != null) instance.label.text = what;
                Feedback.Play(Feedback.Sound.Tick, 0.4f);
            }
            lastFed = Time.frameCount;
            if (fired || Time.unscaledTime - started < Seconds) return false;
            fired = true; // once per press: keep holding does not repeat it
            return true;
        }

        /// <summary>True while a hold is filling (the ring shows).</summary>
        public static bool Holding => activeKey != null && !fired && lastFed >= Time.frameCount - 1;

        private void Awake()
        {
            instance = this;
            canvas = VrUi.CreateCanvas("HoldRing", transform, new Vector2(120f, 150f), 0.0005f);
            VrUi.Icon("Back", canvas, 10, 0, 100, Brand.DiscSprite, Brand.Surface.WithAlpha(0.9f));
            fill = VrUi.Icon("Fill", canvas, 18, 8, 84, Brand.DiscSprite, Brand.Warning);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Radial360;
            fill.fillOrigin = (int)Image.Origin360.Top;
            fill.fillClockwise = true;
            label = VrUi.Text("What", canvas, 0, 106, 120, 40, "", 14f, Brand.Fg, true, TextAlignmentOptions.Center);
            canvas.gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            bool show = Holding;
            if (canvas.gameObject.activeSelf != show) canvas.gameObject.SetActive(show);
            if (!show) return;
            fill.fillAmount = Mathf.Clamp01((Time.unscaledTime - started) / Seconds);
            Camera cam = Headset.Camera;
            if (cam == null) return;
            Vector3 at = cam.transform.position + cam.transform.forward * 0.5f;
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            Transform hand = rig != null && Hands.DominantConnected ? Hands.DominantAnchor(rig) : null;
            if (hand != null) at = hand.position + Vector3.up * 0.09f;
#endif
            canvas.position = at;
            VrUi.FaceViewer(canvas, cam.transform);
        }
    }
}
