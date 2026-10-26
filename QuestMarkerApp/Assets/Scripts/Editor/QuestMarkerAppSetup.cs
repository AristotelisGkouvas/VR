using UnityEngine;
using UnityEditor;
using System.IO;

namespace QuestMarkerApp.Editor
{
    /// <summary>
    /// Editor utility for quick setup of Quest Marker App scene
    /// </summary>
    public class QuestMarkerAppSetup : EditorWindow
    {
        [MenuItem("Quest Marker App/Setup Scene")]
        public static void SetupScene()
        {
            // Create main manager object
            GameObject manager = new GameObject("MarkerAppManager");
            
            // Add all required components
            manager.AddComponent<API.LocalAPIServer>();
            manager.AddComponent<API.NetworkDiscovery>();
            manager.AddComponent<Markers.MarkerManager>();
            manager.AddComponent<Core.SpatialAnchorManager>();
            manager.AddComponent<Core.CoordinateCalibration>();
            manager.AddComponent<Core.SessionManager>();
            manager.AddComponent<Core.MarkerAppController>();
            manager.AddComponent<UI.UIManager>();
            manager.AddComponent<UI.CalibrationHUD>();

            Selection.activeGameObject = manager;

            Debug.Log("[QuestMarkerApp] Scene setup complete! Don't forget to add OVR Camera Rig.");
            EditorUtility.DisplayDialog(
                "Quest Marker App Setup",
                "MarkerAppManager created (v1.2)!\n\nIncludes:\n" +
                "• LocalAPIServer + NetworkDiscovery\n" +
                "• Spatial anchors, calibration, sessions\n" +
                "• Calibration HUD\n\nNext steps:\n" +
                "1. Add OVR Camera Rig\n" +
                "2. Optional: set API key on LocalAPIServer\n" +
                "3. Build for Android/Quest\n" +
                "4. Use menu: Play Mode Tools while testing",
                "OK"
            );
        }

        [MenuItem("Quest Marker App/Android Build Settings (Quest)")]
        public static void ConfigureQuestAndroid()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            // Meta Quest: minimum API 32, Meta Store target API 34.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.bundleVersion = "1.2.0";
            Debug.Log("[QuestMarkerApp] Android/Quest build settings applied (min API 32, target API 34, IL2CPP, ARM64)");
            EditorUtility.DisplayDialog("Build Settings",
                "Applied:\n• Platform Android\n• Min API 32\n• Target API 34\n• IL2CPP + ARM64\n• Version 1.2.0\n\n" +
                "Still set XR Plug-in Management → Oculus and texture ASTC manually.",
                "OK");
        }
        
        [MenuItem("Quest Marker App/Create Marker Prefab")]
        public static void CreateMarkerPrefab()
        {
            // Create marker object
            GameObject marker = new GameObject("MarkerPrefab");
            
            // Main sphere
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Sphere";
            sphere.transform.SetParent(marker.transform);
            sphere.transform.localPosition = Vector3.zero;
            sphere.transform.localScale = Vector3.one * 0.1f;
            
            // Create emission material
            Material mat = new Material(Shader.Find("Standard"));
            mat.color = Color.cyan;
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", Color.cyan * 0.5f);
            sphere.GetComponent<Renderer>().sharedMaterial = mat;
            
            // Remove collider
            Object.DestroyImmediate(sphere.GetComponent<Collider>());
            
            // Add indicator line
            GameObject indicator = new GameObject("Indicator");
            indicator.transform.SetParent(marker.transform);
            indicator.transform.localPosition = Vector3.zero;
            
            LineRenderer line = indicator.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, Vector3.zero);
            line.SetPosition(1, Vector3.down * 0.15f);
            line.startWidth = 0.01f;
            line.endWidth = 0.005f;
            line.useWorldSpace = false;
            
            Material lineMat = new Material(Shader.Find("Sprites/Default"));
            lineMat.color = Color.cyan;
            line.material = lineMat;
            
            // Add Marker component
            marker.AddComponent<Markers.Marker>();
            
            // Save as prefab
            string prefabPath = "Assets/Prefabs";
            if (!Directory.Exists(prefabPath))
            {
                Directory.CreateDirectory(prefabPath);
            }
            
            string fullPath = $"{prefabPath}/MarkerPrefab.prefab";
            PrefabUtility.SaveAsPrefabAsset(marker, fullPath);
            Object.DestroyImmediate(marker);
            
            // Save materials
            string matPath = "Assets/Materials";
            if (!Directory.Exists(matPath))
            {
                Directory.CreateDirectory(matPath);
            }
            
            AssetDatabase.CreateAsset(mat, $"{matPath}/MarkerMaterial.mat");
            AssetDatabase.CreateAsset(lineMat, $"{matPath}/MarkerLineMaterial.mat");
            
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            Debug.Log($"[QuestMarkerApp] Marker prefab created at {fullPath}");
            EditorUtility.DisplayDialog(
                "Marker Prefab Created",
                $"Prefab saved to:\n{fullPath}\n\nAssign it to MarkerManager.markerPrefab",
                "OK"
            );
        }
        
        [MenuItem("Quest Marker App/Open Documentation")]
        public static void OpenDocs()
        {
            string readmePath = Path.Combine(Application.dataPath, "..", "README.md");
            if (File.Exists(readmePath))
            {
                Application.OpenURL(readmePath);
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Documentation",
                    "README.md not found.\n\nSee the project root folder.",
                    "OK"
                );
            }
        }
        
        [MenuItem("Quest Marker App/Check Quest Connection")]
        public static void CheckConnection()
        {
            EditorUtility.DisplayDialog(
                "Quest Connection",
                "To test Quest connection:\n\n" +
                "1. Build and run the app on Quest\n" +
                "2. Note the IP address shown on Quest UI\n" +
                "3. From terminal:\n" +
                "   curl http://<QUEST_IP>:8080/api/health\n\n" +
                "Expected response:\n" +
                "{\"status\": \"healthy\", ...}",
                "OK"
            );
        }
        
        [MenuItem("Quest Marker App/Build Settings")]
        public static void OpenBuildSettings()
        {
            bool switchToAndroid = EditorUtility.DisplayDialog(
                "Build Settings",
                "Quest apps require Android platform.\n\nSwitch to Android now?",
                "Switch to Android",
                "Cancel"
            );

            if (switchToAndroid)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Android,
                    BuildTarget.Android
                );
            }
        }

        // ---------------------------------------------------------------------
        //  MOBILE PREVIEW (απλή 3D κάμερα σε κανονικό Android κινητό)
        //  Δεν αγγίζει την Quest υλοποίηση — ενεργοποιείται μέσω του
        //  scripting define MOBILE_PREVIEW.
        // ---------------------------------------------------------------------

        private const string MOBILE_DEFINE = "MOBILE_PREVIEW";


        [MenuItem("Quest Marker App/Mobile Preview/1. Enable MOBILE_PREVIEW Define")]
        public static void EnableMobileDefine() => SetDefine(MOBILE_DEFINE, true, "MOBILE_PREVIEW");

        [MenuItem("Quest Marker App/Mobile Preview/Disable MOBILE_PREVIEW Define (back to Quest)")]
        public static void DisableMobileDefine() => SetDefine(MOBILE_DEFINE, false, "MOBILE_PREVIEW");

        private static void SetDefine(string define, bool enable, string label)
        {
            BuildTargetGroup[] groups = { BuildTargetGroup.Android, BuildTargetGroup.Standalone };
            foreach (var group in groups)
            {
                string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
                var list = new System.Collections.Generic.List<string>(
                    defines.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries));

                bool has = list.Contains(define);
                if (enable && !has) list.Add(define);
                if (!enable && has) list.Remove(define);

                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", list));
            }

            Debug.Log($"[QuestMarkerApp] {label} {(enable ? "ENABLED" : "DISABLED")}");
            EditorUtility.DisplayDialog(label,
                enable
                    ? $"{label} ΕΝΕΡΓΟ. Unity will recompile."
                    : $"{label} ΑΝΕΝΕΡΓΟ.",
                "OK");
        }

        [MenuItem("Quest Marker App/Mobile Preview/2. Setup Mobile Preview Scene")]
        public static void SetupMobilePreviewScene()
        {
            // Directional light (αν δεν υπάρχει ήδη φως στη σκηνή)
            if (Object.FindAnyObjectByType<Light>() == null)
            {
                GameObject lightObj = new GameObject("Directional Light");
                Light light = lightObj.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;
                lightObj.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }

            // Ground grid για αίσθηση κλίμακας (1 κελί = 1 μέτρο)
            if (Object.FindAnyObjectByType<Mobile.GroundGrid>() == null)
            {
                GameObject groundObj = new GameObject("GroundGrid");
                groundObj.transform.position = Vector3.zero;
                groundObj.AddComponent<Mobile.GroundGrid>();
            }

            // Απλή 3D κάμερα με orbit controller
            GameObject camObj = new GameObject("MobilePreviewCamera");
            Camera cam = camObj.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.10f, 0.12f);
            cam.nearClipPlane = 0.05f;
            camObj.AddComponent<AudioListener>();
            camObj.AddComponent<Mobile.MobileCameraController>();

            // Manager με τα ίδια core components (χωρίς OVR rig, χωρίς world-space UI)
            GameObject manager = new GameObject("MarkerAppManager");
            manager.AddComponent<API.LocalAPIServer>();
            manager.AddComponent<API.NetworkDiscovery>();
            manager.AddComponent<Markers.MarkerManager>();
            manager.AddComponent<Core.SpatialAnchorManager>();
            manager.AddComponent<Core.CoordinateCalibration>();
            manager.AddComponent<Core.SessionManager>();
            manager.AddComponent<Core.MarkerAppController>();
            manager.AddComponent<Mobile.MobilePreviewController>();
            manager.AddComponent<UI.CalibrationHUD>();

            Selection.activeGameObject = manager;

            Debug.Log("[QuestMarkerApp] Mobile preview scene ready. Πάτα Play ή Build & Run για Android.");
            EditorUtility.DisplayDialog(
                "Mobile Preview Scene",
                "Έτοιμο!\n\n" +
                "• MobilePreviewCamera (orbit/pan/zoom)\n" +
                "• GroundGrid (1 κελί = 1 μέτρο, X=κόκκινο / Z=μπλε)\n" +
                "• MarkerAppManager (+ demo markers)\n\n" +
                "Επόμενα βήματα:\n" +
                "1. File → Build Settings → Android (Switch Platform)\n" +
                "2. Project Settings → XR Plug-in Management: ΑΠΕΝΕΡΓΟΠΟΙΗΣΕ το Oculus/OpenXR στο tab Android\n" +
                "3. Σύνδεσε το κινητό (USB debugging) → Build & Run\n\n" +
                "Στον Editor: πάτα απλά Play. Ποντίκι = περιστροφή/pan, scroll = zoom.",
                "OK"
            );
        }
    }
}
