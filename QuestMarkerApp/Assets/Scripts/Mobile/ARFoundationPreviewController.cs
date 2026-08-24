using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.Mobile
{
    /// <summary>
    /// Optional AR Foundation path for mobile.
    /// Compiles only when AR_FOUNDATION define is set and AR packages are installed.
    /// Without the define this is a safe no-op stub that documents the integration points.
    ///
    /// Enable: Quest Marker App → Mobile Preview → Enable AR_FOUNDATION Define
    /// Packages: com.unity.xr.arfoundation, com.unity.xr.arcore / arkit
    /// </summary>
    public class ARFoundationPreviewController : MonoBehaviour
    {
        [SerializeField] private MarkerManager markerManager;
        [SerializeField] private MarkerAppController appController;
        [SerializeField] private bool placeOriginOnFirstPlane = true;

#if AR_FOUNDATION
        // Uncomment when AR Foundation is in the project:
        // using UnityEngine.XR.ARFoundation;
        // [SerializeField] private ARSession arSession;
        // [SerializeField] private ARPlaneManager planeManager;
        // [SerializeField] private ARRaycastManager raycastManager;
#endif

        private Transform arOrigin;
        private bool originPlaced;

        private void Start()
        {
            if (markerManager == null) markerManager = FindObjectOfType<MarkerManager>();
            if (appController == null) appController = FindObjectOfType<MarkerAppController>();

#if !AR_FOUNDATION
            Debug.Log("[AR] AR_FOUNDATION define not set — stub active. Use MobilePreviewController or enable AR packages.");
#else
            Debug.Log("[AR] AR Foundation path active — wait for plane detection to place origin.");
#endif
            // Always create a local origin; AR path will move it onto a plane.
            var go = new GameObject("ARReferenceOrigin");
            arOrigin = go.transform;
            if (markerManager != null)
                markerManager.SetReferencePoint(arOrigin);
        }

        /// <summary>
        /// Place / re-place the marker origin at a world pose (e.g. from AR plane hit).
        /// </summary>
        public void PlaceOrigin(Pose pose)
        {
            if (arOrigin == null) return;
            arOrigin.SetPositionAndRotation(pose.position, pose.rotation);
            originPlaced = true;
            if (markerManager != null)
                markerManager.SetReferencePoint(arOrigin);
            Debug.Log($"[AR] Origin placed at {pose.position}");
        }

        /// <summary>
        /// Shared calibration JSON can be imported so phone & Quest use the same real-world frame
        /// (relative to a QR / known corner). Call after scanning a shared origin marker.
        /// </summary>
        public void ImportSharedOrigin(Vector3 localOffset, float yawDegrees)
        {
            if (arOrigin == null) return;
            arOrigin.position += localOffset;
            arOrigin.Rotate(0f, yawDegrees, 0f, Space.World);
            if (markerManager != null)
                markerManager.SetReferencePoint(arOrigin);
        }

#if AR_FOUNDATION && false
        // Example wiring (enable when packages present):
        // private void Update() {
        //   if (originPlaced || !placeOriginOnFirstPlane || planeManager == null) return;
        //   foreach (var plane in planeManager.trackables) {
        //     PlaceOrigin(new Pose(plane.transform.position, plane.transform.rotation));
        //     break;
        //   }
        // }
#endif
    }
}
