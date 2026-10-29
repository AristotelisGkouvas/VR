using UnityEngine;
#if !MOBILE_PREVIEW
using Meta.XR.EnvironmentDepth;
using UnityEngine.Android;
#endif

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Sensors behind real surfaces: with the Quest 3 depth sensor on, a marker hidden by a
    /// pier or a wall draws faint (MarkerSprite's _XrayAlpha) rather than solid in front of
    /// the concrete, which misread its depth. Experimental and off by default (Display menu):
    /// it needs the spatial data permission and costs some GPU time.
    /// </summary>
    public class DepthOcclusion : MonoBehaviour
    {
        private const string EnabledKey = "ui.depthOcclusion";

        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(EnabledKey, 0) == 1;
            set { PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>One line for the menu.</summary>
        public static string Status { get; private set; } = "";

#if !MOBILE_PREVIEW
        private EnvironmentDepthManager manager;
        private bool asked;
        private float nextCheck;

        private void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            bool want = Enabled;
            if (!want)
            {
                if (manager != null && manager.enabled) manager.enabled = false; // its OnDisable clears the shader keywords
                Status = L.T("Όχι", "Off");
                return;
            }
            if (!EnvironmentDepthManager.IsSupported)
            {
                Status = L.T("Δεν υποστηρίζεται", "Not supported");
                return;
            }
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            {
                if (!asked)
                {
                    asked = true;
                    Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission);
                }
                Status = L.T("Χρειάζεται άδεια χωρικών δεδομένων", "Needs the spatial data permission");
                return;
            }
            if (manager == null)
            {
                manager = gameObject.AddComponent<EnvironmentDepthManager>();
                manager.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
                manager.RemoveHands = true; // the controllers and hands never hide a marker
                Debug.Log("[Depth] Environment depth on: markers behind real surfaces turn faint");
            }
            else if (!manager.enabled) manager.enabled = true;
            Status = manager.IsDepthAvailable ? L.T("Ναι", "On") : L.T("Ναι · εκκίνηση…", "On · starting…");
        }
#endif
    }
}
