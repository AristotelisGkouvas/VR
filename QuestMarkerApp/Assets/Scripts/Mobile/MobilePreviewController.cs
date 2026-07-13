using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.Mobile
{
    /// <summary>
    /// Οδηγεί το preview σε κινητό:
    /// - Κλειδώνει το σημείο αναφοράς στο world origin (ώστε η orbit κάμερα να κεντράρει σωστά).
    /// - Δημιουργεί ένα demo σύνολο markers για να φαίνεται αμέσως πώς μοιάζει.
    /// - Εμφανίζει on-screen κουμπιά που αντικαθιστούν τα κουμπιά των Quest χειριστηρίων.
    /// Το ίδιο API server (LocalAPIServer) εξακολουθεί να τρέχει, οπότε μπορείς και να
    /// στέλνεις πραγματικά coordinates από άλλη συσκευή στο ίδιο δίκτυο.
    /// </summary>
    public class MobilePreviewController : MonoBehaviour
    {
        [Header("References (auto-found αν μείνουν κενά)")]
        [SerializeField] private MarkerManager markerManager;
        [SerializeField] private MarkerAppController appController;
        [SerializeField] private SpatialAnchorManager anchorManager;
        [SerializeField] private MobileCameraController cameraController;

        [Header("Demo")]
        [SerializeField] private bool spawnDemoOnStart = true;

        private Transform originAnchor;
        private bool showInfo = true;

        private void Start()
        {
            if (markerManager == null) markerManager = FindObjectOfType<MarkerManager>();
            if (appController == null) appController = FindObjectOfType<MarkerAppController>();
            if (anchorManager == null) anchorManager = FindObjectOfType<SpatialAnchorManager>();
            if (cameraController == null) cameraController = FindObjectOfType<MobileCameraController>();

            // Κράτα τα markers αγκυρωμένα στο world origin αντί να δημιουργείται
            // reference anchor στη θέση της κάμερας (που δεν έχει νόημα χωρίς headset tracking).
            if (anchorManager != null)
            {
                anchorManager.AutoCreateReferenceAnchor = false;
                anchorManager.PersistAnchors = false;
            }

            GameObject originObj = new GameObject("MobileReferenceOrigin");
            originObj.transform.position = Vector3.zero;
            originObj.transform.rotation = Quaternion.identity;
            originAnchor = originObj.transform;

            if (markerManager != null)
                markerManager.SetReferencePoint(originAnchor);

            if (cameraController != null)
                cameraController.SetPivot(Vector3.zero);

            if (spawnDemoOnStart)
                SpawnDemoMarkers();
        }

        /// <summary>Δημιουργεί ένα δείγμα markers γύρω από το origin.</summary>
        public void SpawnDemoMarkers()
        {
            if (markerManager == null) return;

            CoordinateResponse response = new CoordinateResponse
            {
                clearExisting = true,
                coordinates = new[]
                {
                    Demo("p1",  0.0f, 0.0f,  0.0f, "Origin",  "#FFFFFF", 0.25f),
                    Demo("p2",  1.5f, 0.5f,  1.0f, "Point A", "#FF3B30", 0.30f),
                    Demo("p3", -1.5f, 1.0f,  1.5f, "Point B", "#34C759", 0.30f),
                    Demo("p4",  0.0f, 1.5f,  2.5f, "Point C", "#0A84FF", 0.30f),
                    Demo("p5",  2.0f, 0.2f, -1.0f, "Point D", "#FFD60A", 0.30f),
                    Demo("p6", -2.0f, 0.8f, -1.5f, "Point E", "#FF9F0A", 0.30f),
                }
            };

            markerManager.ProcessCoordinates(response);
        }

        private static CoordinateData Demo(string id, float x, float y, float z, string label, string color, float scale)
        {
            return new CoordinateData
            {
                id = id,
                x = x,
                y = y,
                z = z,
                label = label,
                color = color,
                scale = scale
            };
        }

        private void OnGUI()
        {
            float w = Screen.width;
            float h = Screen.height;
            float pad = w * 0.02f;
            float btnH = Mathf.Max(64f, h * 0.08f);
            float btnW = (w - pad * 4f) / 3f;
            float y = h - btnH - pad;

            int prevBtnFont = GUI.skin.button.fontSize;
            int prevLblFont = GUI.skin.label.fontSize;
            GUI.skin.button.fontSize = Mathf.RoundToInt(btnH * 0.3f);
            GUI.skin.label.fontSize = Mathf.RoundToInt(btnH * 0.26f);

            if (GUI.Button(new Rect(pad, y, btnW, btnH), "Demo"))
                SpawnDemoMarkers();

            if (GUI.Button(new Rect(pad * 2f + btnW, y, btnW, btnH), "Clear"))
            {
                if (appController != null) appController.ClearAllMarkers();
                else if (markerManager != null) markerManager.ClearAllMarkers();
            }

            if (GUI.Button(new Rect(pad * 3f + btnW * 2f, y, btnW, btnH), showInfo ? "Hide" : "Info"))
                showInfo = !showInfo;

            if (showInfo)
            {
                int count = markerManager != null ? markerManager.ActiveMarkerCount : 0;
                string info =
                    "MOBILE PREVIEW (απλή 3D κάμερα)\n" +
                    "• 1 δάχτυλο: περιστροφή\n" +
                    "• 2 δάχτυλα: pan / pinch zoom\n" +
                    "• Grid: 1 κελί = 1 μέτρο\n" +
                    "Markers: " + count;

                GUI.Label(new Rect(pad, pad, w - pad * 2f, btnH * 2.4f), info);
            }

            GUI.skin.button.fontSize = prevBtnFont;
            GUI.skin.label.fontSize = prevLblFont;
        }
    }
}
