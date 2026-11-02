using UnityEngine;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Panels (menu, lists, sensor card) fade while the user walks, so an open card does not
    /// hide the edge of a bridge deck underfoot; they come back as soon as the user stops.
    /// Head speed over the ground, smoothed; panels call <see cref="Apply"/> once a frame.
    /// </summary>
    public class WalkFade : MonoBehaviour
    {
        /// <summary>Walking pace (m/s) from which panels start to fade, and where they are faintest.</summary>
        private const float From = 0.5f, Full = 1.0f;
        private const float MinAlpha = 0.2f;

        /// <summary>Panel opacity now: 1 standing, down to MinAlpha walking.</summary>
        public static float Alpha { get; private set; } = 1f;

        private Vector3 last;
        private bool primed;
        private float speed;

        private void LateUpdate()
        {
            Camera cam = Headset.Camera;
            if (cam == null || Time.unscaledDeltaTime <= 0f) return;
            Vector3 p = cam.transform.position;
            if (primed)
            {
                Vector3 d = p - last;
                d.y = 0f;
                float now = d.magnitude / Time.unscaledDeltaTime;
                // ~0.3 s smoothing: a head turn or a lean is not walking.
                speed = Mathf.Lerp(speed, now, 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.3f));
            }
            last = p;
            primed = true;
            float target = Mathf.Lerp(1f, MinAlpha, Mathf.InverseLerp(From, Full, speed));
            Alpha = Mathf.MoveTowards(Alpha, target, Time.unscaledDeltaTime * 3f);
        }

        /// <summary>Fades the panel's canvas with the walking pace (adds a CanvasGroup the first time).</summary>
        public static void Apply(RectTransform panel, ref CanvasGroup group)
        {
            if (panel == null) return;
            if (group == null && !panel.TryGetComponent(out group)) group = panel.gameObject.AddComponent<CanvasGroup>();
            if (!Mathf.Approximately(group.alpha, Alpha)) group.alpha = Alpha;
        }
    }
}
