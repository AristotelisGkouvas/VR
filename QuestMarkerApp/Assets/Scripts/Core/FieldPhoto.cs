#if !MOBILE_PREVIEW
using System;
using System.Collections;
using System.IO;
using Meta.XR;
using UnityEngine;
using UnityEngine.Android;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// One photo of the real scene from the headset's passthrough camera (left), for a field
    /// note: "crack beside K5" is worth more with the crack in it. The camera runs only for the
    /// shot and is released after; virtual content (cards, markers) is not in the image.
    /// Photos go to persistentDataPath/field_photos as JPEG and are served to the PC at
    /// GET /api/fieldnotes/photo/&lt;file&gt;.
    /// </summary>
    public static class FieldPhoto
    {
        public const string FolderName = "field_photos";
        private const float StartTimeout = 5f;
        private const int Quality = 85;

        public static string Folder => Path.Combine(Application.persistentDataPath, FolderName);

        /// <summary>The headset has the camera API (Quest 3 / 3S, Horizon OS v74+).</summary>
        public static bool Supported => !Application.isEditor && PassthroughCameraAccess.IsSupported;

        public static bool HasPermission =>
            Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission);

        public static void RequestPermission() =>
            Permission.RequestUserPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission);

        /// <summary>
        /// Takes the photo; <paramref name="done"/> gets the file name (in <see cref="Folder"/>)
        /// or null with the reason in <paramref name="failed"/>.
        /// </summary>
        public static IEnumerator Take(MonoBehaviour host, string stem, Action<string> done, Action<string> failed)
        {
            if (!HasPermission)
            {
                failed(L.T("Χρειάζεται άδεια κάμερας.", "Camera permission needed."));
                yield break;
            }
            var go = new GameObject("FieldPhotoCamera");
            go.SetActive(false);
            go.transform.SetParent(host.transform, false);
            var cam = go.AddComponent<PassthroughCameraAccess>();
            cam.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            cam.RequestedResolution = new Vector2Int(1280, 960);
            go.SetActive(true);

            float until = Time.unscaledTime + StartTimeout;
            while (!cam.IsPlaying && Time.unscaledTime < until) yield return null;
            // The first frames after start can be dark while the exposure settles.
            for (int i = 0; i < 10 && cam.IsPlaying; i++) yield return null;
            if (!cam.IsPlaying)
            {
                UnityEngine.Object.Destroy(go);
                failed(L.T("Η κάμερα δεν ξεκίνησε.", "The camera did not start."));
                yield break;
            }

            string file = null, error = null;
            RenderTexture rt = null;
            Texture2D image = null;
            try
            {
                Texture source = cam.GetTexture();
                Vector2Int size = cam.CurrentResolution;
                rt = RenderTexture.GetTemporary(size.x, size.y, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, rt);
                RenderTexture before = RenderTexture.active;
                RenderTexture.active = rt;
                image = new Texture2D(size.x, size.y, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0);
                image.Apply(false);
                RenderTexture.active = before;

                Directory.CreateDirectory(Folder);
                file = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Safe(stem)}.jpg";
                File.WriteAllBytes(Path.Combine(Folder, file), image.EncodeToJPG(Quality));
                Debug.Log($"[Photo] {file} saved, {size.x}×{size.y}");
            }
            catch (Exception e)
            {
                error = e.Message;
                file = null;
                Debug.LogWarning("[Photo] Failed: " + e.Message);
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (image != null) UnityEngine.Object.Destroy(image);
                UnityEngine.Object.Destroy(go);
            }
            if (file != null) done(file);
            else failed(L.T("Η φωτογραφία δεν αποθηκεύτηκε: ", "The photo was not saved: ") + error);
        }

        /// <summary>A file name safe on the headset and in a URL.</summary>
        public static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "point";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) && c < 128 || c == '-' ? c : '_');
            return sb.ToString();
        }
    }
}
#endif
