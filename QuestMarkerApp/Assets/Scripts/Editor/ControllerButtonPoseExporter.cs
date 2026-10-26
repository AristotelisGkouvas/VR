using System.IO;
using UnityEditor;
using UnityEngine;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Editor
{
    /// <summary>
    /// Reads the Quest 3 (Touch Plus) controller models shipped in the Meta XR Core SDK and
    /// stores each button's position, as Unity imports it, in Resources for the runtime
    /// callouts. The FBX can't be loaded in a player build, and reading the imported
    /// hierarchy (instead of the raw FBX numbers) settles the handedness conversion.
    /// Runs automatically after a compile when the file is missing.
    /// </summary>
    [InitializeOnLoad]
    public static class ControllerButtonPoseExporter
    {
        private const string ModelDir = "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/";
        private const string OutputPath = "Assets/Resources/ControllerButtonPoses.json";
        private const string ModelOutDir = "Assets/Resources/ControllerModels/";

        static ControllerButtonPoseExporter()
        {
            if (!File.Exists(OutputPath))
                EditorApplication.delayCall += () => Export(false);
            if (!File.Exists(ModelOutDir + "TouchPlusLeft.prefab") || !File.Exists(ModelOutDir + "TouchPlusRight.prefab"))
                EditorApplication.delayCall += ExportModels;
        }

        /// <summary>
        /// Plain prefabs of the Touch Plus models in Resources, so the headset can draw the
        /// controllers under the button badges (Stencil-style). Package FBX can't be loaded at runtime.
        /// </summary>
        [MenuItem("Quest Marker App/Regenerate Controller Models")]
        public static void ExportModels()
        {
            Directory.CreateDirectory(ModelOutDir);
            SaveModel("MetaQuestTouchPlus_Left.fbx", "TouchPlusLeft");
            SaveModel("MetaQuestTouchPlus_Right.fbx", "TouchPlusRight");
            AssetDatabase.Refresh();
        }

        private static Material ShellMaterial()
        {
            string path = ModelOutDir + "ControllerShell.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            mat = new Material(Shader.Find("Standard")) { color = new Color(0.227f, 0.259f, 0.251f) };
            mat.SetFloat("_Glossiness", 0.35f);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static void SaveModel(string file, string name)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelDir + file);
            if (model == null)
            {
                Debug.LogWarning("[ControllerPoses] Model not found: " + ModelDir + file);
                return;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            // Only the meshes matter; animation rigs and the battery quad are noise here.
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);
            foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name.Contains("battery")) Object.DestroyImmediate(t.gameObject);
            // The SDK's MetaLit material UsePass-es "Universal Render Pipeline/Lit"; with URP not the
            // active pipeline nothing strips it and a player build stalls on ~72 billion variants.
            Material shell = ShellMaterial();
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = shell;
                r.sharedMaterials = mats;
            }
            instance.name = name;
            PrefabUtility.SaveAsPrefabAsset(instance, ModelOutDir + name + ".prefab");
            Object.DestroyImmediate(instance);
        }

        [MenuItem("Quest Marker App/Regenerate Controller Button Poses")]
        public static void ExportFromMenu() => Export(true);

        private static void Export(bool verbose)
        {
            var poses = new ControllerButtonPoses
            {
                left = Read("MetaQuestTouchPlus_Left.fbx", "b_button_x", "b_button_y", "left_b_button_oculus",
                    "left_b_thumbstick", "left_b_trigger_front", "left_b_trigger_grip", "left_laser_begin"),
                right = Read("MetaQuestTouchPlus_Right.fbx", "b_button_a", "b_button_b", "right_b_button_oculus",
                    "right_b_thumbstick", "right_b_trigger_front", "right_b_trigger_grip", "right_laser_begin")
            };
            if (poses.left == null || poses.right == null)
            {
                Debug.LogWarning("[ControllerPoses] Touch Plus models not found under " + ModelDir);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllText(OutputPath, JsonUtility.ToJson(poses, true));
            AssetDatabase.ImportAsset(OutputPath);
            if (verbose) Debug.Log("[ControllerPoses] Wrote " + OutputPath);
        }

        private static ControllerButtonPoses.Hand Read(string file, string primary, string secondary, string menu,
            string stick, string trigger, string grip, string tip)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelDir + file);
            if (model == null) return null;
            Transform root = model.transform;
            return new ControllerButtonPoses.Hand
            {
                primary = Local(root, primary),
                secondary = Local(root, secondary),
                menu = Local(root, menu),
                thumbstick = Local(root, stick),
                trigger = Local(root, trigger),
                grip = Local(root, grip),
                tip = Local(root, tip)
            };
        }

        // Position in the model root's space — the controller anchor's space, since
        // OVRControllerPrefab mounts these models at identity under the anchor.
        private static Vector3 Local(Transform root, string node)
        {
            Transform t = Find(root, node);
            if (t == null)
            {
                Debug.LogWarning($"[ControllerPoses] Node '{node}' not found in {root.name}");
                return Vector3.zero;
            }
            // Rotate into root space but keep world units: the imported root carries the
            // FBX's cm->m scale, which InverseTransformPoint would divide back out.
            return Quaternion.Inverse(root.rotation) * (t.position - root.position);
        }

        private static Transform Find(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            foreach (Transform child in parent)
            {
                Transform hit = Find(child, name);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
