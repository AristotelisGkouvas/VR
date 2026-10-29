using UnityEngine;
using QuestMarkerApp.Models;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Asks Horizon OS to hide the Guardian boundary while the app runs. Walking out of a
    /// stationary circle otherwise pauses the app ("move to your existing boundary") — useless
    /// on site, where control points sit metres apart. Only honoured for passthrough apps
    /// with Boundary Visibility Support (OculusProjectConfig) and the BOUNDARY_VISIBILITY
    /// permission; the user's own surroundings stay visible through passthrough.
    ///
    /// The life-size model is the exception: there passthrough is blacked out (only the model
    /// is seen) and, with the room hidden, the Guardian comes back so the user does not walk
    /// into anything. Back to the tabletop or the site, both return as they were. Horizon OS
    /// would bring the Guardian back by itself anyway: hiding it needs passthrough on show.
    /// </summary>
    public class BoundaryControl : MonoBehaviour
    {
        /// <summary>Main-menu switch; on by default for site use.</summary>
        public static bool Suppress
        {
            get => PlayerPrefs.GetInt("ui.noBoundary", 1) == 1;
            set { PlayerPrefs.SetInt("ui.noBoundary", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static bool IsSuppressed
        {
            get
            {
#if !MOBILE_PREVIEW
                return OVRManager.instance != null && OVRManager.instance.isBoundaryVisibilitySuppressed;
#else
                return false;
#endif
            }
        }

        /// <summary>Walking this far (m) from where the life-size view began brings the room back.</summary>
        private const float WalkAwayMetres = 1f;

        /// <summary>The room shows through the life-size model right now (peek or walked off).</summary>
        public static bool Peeking { get; private set; }

        private float nextCheck;
        private bool immersive, passthroughOn = true;
        private Vector3 immersiveFrom;
        private Camera blackedCamera;
        private Color savedBackground;

        private void Update()
        {
#if !MOBILE_PREVIEW
            bool want = ModelOverlay.LifeSizeShown;
            Camera cam = Camera.main;
            if (want != immersive)
            {
                immersive = want;
                if (want && cam != null) immersiveFrom = cam.transform.position;
                nextCheck = 0f; // the Guardian follows at once
            }
            // In the black the room comes back while the off-hand trigger is held, and by itself
            // once the user has physically walked off (the sticks move the model, not them).
            bool walkedOff = false;
            if (immersive && cam != null)
            {
                Vector3 d = cam.transform.position - immersiveFrom;
                d.y = 0f;
                walkedOff = d.magnitude > WalkAwayMetres;
            }
            Peeking = immersive && (Hands.OffHandTriggerHeld() || walkedOff);
            bool show = !immersive || Peeking;
            if (show != passthroughOn) SetPassthrough(show);
            // Re-asserted periodically: the request lapses if passthrough restarts.
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            bool suppress = Suppress && !immersive;
            OVRManager manager = OVRManager.instance;
            if (manager != null && manager.shouldBoundaryVisibilityBeSuppressed != suppress)
            {
                manager.shouldBoundaryVisibilityBeSuppressed = suppress;
                Debug.Log($"[Boundary] Suppression requested: {suppress}" + (immersive ? " (life-size model)" : ""));
            }
#endif
        }

#if !MOBILE_PREVIEW
        /// <summary>Passthrough fades in and out over this long instead of cutting, seconds.</summary>
        private const float FadeSeconds = 0.25f;
        private float black; // 0 room visible … 1 black
        private bool fading;

        // Fades the eye camera's clear colour from transparent (the room shows through) to
        // opaque black, then hides the passthrough layer; on, the layer comes back first and the
        // black fades out. A hard cut between the room and the black was harsh on the eyes.
        private void SetPassthrough(bool on)
        {
            passthroughOn = on;
            if (!on && blackedCamera == null)
            {
                blackedCamera = Camera.main;
                if (blackedCamera != null) savedBackground = blackedCamera.backgroundColor;
            }
            if (on) ShowLayers(true);
            fading = true;
            Debug.Log(on ? (immersive ? "[Boundary] Peek: passthrough on" : "[Boundary] Passthrough back on")
                : "[Boundary] Life-size model: passthrough off, Guardian on");
        }

        private static void ShowLayers(bool on)
        {
            foreach (OVRPassthroughLayer layer in FindObjectsByType<OVRPassthroughLayer>(FindObjectsSortMode.None))
                layer.hidden = !on;
        }

        private void LateUpdate()
        {
            if (!fading) return;
            black = Mathf.MoveTowards(black, passthroughOn ? 0f : 1f, Time.unscaledDeltaTime / FadeSeconds);
            if (blackedCamera != null)
                blackedCamera.backgroundColor = Color.Lerp(savedBackground, Color.black, black);
            if (black > 0f && black < 1f) return;
            fading = false;
            if (!passthroughOn) ShowLayers(false); // fully black: the layer can stop
            else if (blackedCamera != null)
            {
                blackedCamera.backgroundColor = savedBackground;
                blackedCamera = null;
            }
        }

        private void OnDisable()
        {
            // No fade left to run: the room and the camera come back at once.
            ShowLayers(true);
            if (blackedCamera != null) blackedCamera.backgroundColor = savedBackground;
            blackedCamera = null;
            passthroughOn = true;
            fading = false;
            black = 0f;
            immersive = false;
            Peeking = false;
        }
#endif
    }
}
