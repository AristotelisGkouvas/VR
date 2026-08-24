using UnityEngine;
using UnityEditor;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.Editor
{
    /// <summary>
    /// Play-mode helper window: send demo markers, clear, calibrate, session export.
    /// </summary>
    public class QuestMarkerPlayModeTools : EditorWindow
    {
        private float x = 1f, y = 0.5f, z = 2f;
        private string label = "Demo";
        private string color = "#00FFFF";
        private string markerType = "sphere";
        private string apiKey = "";
        private Vector2 scroll;

        [MenuItem("Quest Marker App/Play Mode Tools")]
        public static void Open()
        {
            GetWindow<QuestMarkerPlayModeTools>("Quest Marker Tools");
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Quest Marker — Play Mode Tools", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to use these actions.", MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Add Marker", EditorStyles.boldLabel);
            x = EditorGUILayout.FloatField("X", x);
            y = EditorGUILayout.FloatField("Y", y);
            z = EditorGUILayout.FloatField("Z", z);
            label = EditorGUILayout.TextField("Label", label);
            color = EditorGUILayout.TextField("Color", color);
            markerType = EditorGUILayout.TextField("Type (sphere/pin/arrow/zone)", markerType);

            GUI.enabled = Application.isPlaying;
            if (GUILayout.Button("Add Marker"))
            {
                var ctrl = Object.FindObjectOfType<MarkerAppController>();
                if (ctrl != null)
                {
                    // Use API path for full pipeline
                    var mm = Object.FindObjectOfType<MarkerManager>();
                    mm?.ProcessCoordinates(new CoordinateResponse
                    {
                        coordinates = new[]
                        {
                            new CoordinateData
                            {
                                id = System.Guid.NewGuid().ToString("N"),
                                x = x, y = y, z = z,
                                label = label, color = color,
                                markerType = markerType, scale = 0.15f
                            }
                        }
                    });
                }
            }

            if (GUILayout.Button("Spawn Demo Set"))
            {
                var mobile = Object.FindObjectOfType<Mobile.MobilePreviewController>();
                if (mobile != null) mobile.SpawnDemoMarkers();
                else
                {
                    var mm = Object.FindObjectOfType<MarkerManager>();
                    mm?.ProcessCoordinates(new CoordinateResponse
                    {
                        clearExisting = true,
                        coordinates = new[]
                        {
                            Demo("o", 0, 0, 0, "Origin", "#FFFFFF", "sphere"),
                            Demo("a", 1.5f, 0.5f, 1, "A", "#FF3B30", "pin"),
                            Demo("b", -1.5f, 1, 1.5f, "B", "#34C759", "arrow"),
                            Demo("c", 0, 0.2f, 2.5f, "Zone", "#0A84FF", "zone", 0.8f),
                        }
                    });
                }
            }

            if (GUILayout.Button("Clear Markers"))
                Object.FindObjectOfType<MarkerAppController>()?.ClearAllMarkers();

            if (GUILayout.Button("Undo"))
                Object.FindObjectOfType<MarkerAppController>()?.Undo();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Calibration", EditorStyles.boldLabel);
            if (GUILayout.Button("Start Calibration Mode"))
                Object.FindObjectOfType<MarkerAppController>()?.StartCalibrationMode();
            if (GUILayout.Button("Quick Calibrate Origin (0,0,0)"))
                Object.FindObjectOfType<CoordinateCalibration>()?.QuickCalibrate(Vector3.zero, 0f);
            if (GUILayout.Button("Reset Calibration"))
                Object.FindObjectOfType<CoordinateCalibration>()?.ResetCalibration();

            var cal = Object.FindObjectOfType<CoordinateCalibration>();
            if (cal != null)
            {
                EditorGUILayout.HelpBox(
                    $"Calibrated={cal.IsCalibrated}  Calibrating={cal.IsCalibrating}\n" +
                    $"Step: {cal.CurrentStepMessage}\nResidual RMS: {cal.LastResidualRms:F3} m",
                    MessageType.None);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Session", EditorStyles.boldLabel);
            if (GUILayout.Button("Save Session JSON"))
                Object.FindObjectOfType<SessionManager>()?.SaveToFile();
            if (GUILayout.Button("Save Session CSV"))
                Object.FindObjectOfType<SessionManager>()?.SaveToFile(csv: true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            var server = Object.FindObjectOfType<LocalAPIServer>();
            if (server != null)
            {
                EditorGUILayout.LabelField("Running", server.IsRunning.ToString());
                foreach (var u in server.GetServerUrls())
                    EditorGUILayout.SelectableLabel(u + "/api/coordinates", GUILayout.Height(18));
            }
            apiKey = EditorGUILayout.TextField("API Key (for curl)", apiKey);

            GUI.enabled = true;
            EditorGUILayout.EndScrollView();
        }

        private static CoordinateData Demo(string id, float x, float y, float z, string label, string color, string type, float radius = 0)
        {
            return new CoordinateData
            {
                id = id, x = x, y = y, z = z, label = label, color = color,
                markerType = type, scale = 0.2f, radius = radius
            };
        }
    }
}
