using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Asks Horizon OS to hide the Guardian boundary while the app runs. Walking out of a
    /// stationary circle otherwise pauses the app ("move to your existing boundary") — useless
    /// on site, where control points sit metres apart. Only honoured for passthrough apps
    /// with Boundary Visibility Support (OculusProjectConfig) and the BOUNDARY_VISIBILITY
    /// permission; the user's own surroundings stay visible through passthrough.
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

        private void Update()
        {
#if !MOBILE_PREVIEW
            // Re-asserted periodically: the request lapses if passthrough restarts.
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            OVRManager manager = OVRManager.instance;
            if (manager != null && manager.shouldBoundaryVisibilityBeSuppressed != Suppress)
            {
                manager.shouldBoundaryVisibilityBeSuppressed = Suppress;
                Debug.Log($"[Boundary] Suppression requested: {Suppress}");
            }
#endif
        }
    }
}
