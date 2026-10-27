using UnityEngine;
using QuestMarkerApp.Models;

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

        private float nextCheck;
        private bool immersive;
        private Camera blackedCamera;
        private Color savedBackground;

        private void Update()
        {
#if !MOBILE_PREVIEW
            bool want = ModelOverlay.LifeSizeShown;
            if (want != immersive)
            {
                immersive = want;
                SetPassthrough(!want);
                nextCheck = 0f; // the Guardian follows at once
            }
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
        // Hides the passthrough layer and clears the eye camera to opaque black (its clear colour
        // is transparent so passthrough shows through); off, both go back as they were.
        private void SetPassthrough(bool on)
        {
            foreach (OVRPassthroughLayer layer in FindObjectsByType<OVRPassthroughLayer>(FindObjectsSortMode.None))
                layer.hidden = !on;
            if (!on)
            {
                blackedCamera = Camera.main;
                if (blackedCamera != null)
                {
                    savedBackground = blackedCamera.backgroundColor;
                    blackedCamera.backgroundColor = Color.black;
                }
            }
            else if (blackedCamera != null)
            {
                blackedCamera.backgroundColor = savedBackground;
                blackedCamera = null;
            }
            Debug.Log(on ? "[Boundary] Passthrough back on" : "[Boundary] Life-size model: passthrough off, Guardian on");
        }

        private void OnDisable()
        {
            if (immersive) SetPassthrough(true);
            immersive = false;
        }
#endif
    }
}
