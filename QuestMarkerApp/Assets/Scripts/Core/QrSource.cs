#if !MOBILE_PREVIEW
using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// The headset's QR tracking, shared by «Δοκιμή QR» and the QR alignment. Runs on MRUK,
    /// created at first use, with its world lock and scene loading off: left on, the lock would
    /// move the tracking space under the project's alignment. The Core SDK's own QR API reports
    /// "not supported" on the same headset.
    /// </summary>
    public static class QrSource
    {
        // A head that moves this far between two frames has not walked there.
        private const float JumpMetres = 1f, JumpSeconds = 2f, WalkSpeed = 3f; // a hitch while walking is not a jump

        private static MRUK mruk;
        private static int users;
        private static bool hooked;
        private static Vector3 lastHead;
        private static float lastHeadAt = -1f;
        private static int lastHeadFrame = -1;

        /// <summary>
        /// Goes up whenever the tracking space may have changed: glasses taken off, a recenter or
        /// a jump of the head. The pose of a code out of sight stays in the old space (measured on
        /// 2026-11-06: frozen, while the head moved by metres), so a reading from before cannot
        /// be compared with one from after.
        /// </summary>
        public static int Epoch { get; private set; }

        public static bool Running => mruk != null && mruk.TrackerConfiguration.QRCodeTrackingEnabled;
        public static bool Supported => mruk != null && mruk.QRCodeTrackingSupported;

        public static void Acquire(Transform parent)
        {
            users++;
            if (!hooked && OVRManager.display != null)
            {
                hooked = true;
                OVRManager.HMDUnmounted += () => NewEpoch("glasses off");
                // The resets of 6/11/2026 (13:54, 14:00) came with none of the others.
                OVRManager.TrackingLost += () => NewEpoch("tracking lost");
                OVRManager.display.RecenteredPose += () => NewEpoch("recenter");
            }
            if (mruk == null)
            {
                // Inactive while it is set up: its Awake reads the settings.
                var go = new GameObject("MRUK");
                go.SetActive(false);
                go.transform.SetParent(parent, false);
                mruk = go.AddComponent<MRUK>();
                mruk.EnableWorldLock = false;
                mruk.SceneSettings = new MRUK.MRUKSettings { LoadSceneOnStartup = false };
                SetTracking(true);
                go.SetActive(true);
            }
            else SetTracking(true);
        }

        public static void Release()
        {
            users = Mathf.Max(0, users - 1);
            // MRUK stays, idle: tracking off releases the resources and it holds nothing else.
            if (users == 0 && mruk != null) SetTracking(false);
        }

        public static void Get(List<MRUKTrackable> into)
        {
            into.Clear();
            if (mruk != null) mruk.GetTrackables(into);
        }

        /// <summary>Call every frame while reading codes: watches the head for a jump.</summary>
        public static void Tick(Vector3 head)
        {
            if (Time.frameCount == lastHeadFrame) return;
            lastHeadFrame = Time.frameCount;
            float now = Time.unscaledTime;
            if (lastHeadAt >= 0f && now - lastHeadAt <= JumpSeconds && Vector3.Distance(head, lastHead) > JumpMetres + WalkSpeed * (now - lastHeadAt))
                NewEpoch("head jump");
            lastHead = head;
            lastHeadAt = now;
        }

        /// <summary>
        /// The side the name promises in metres (QM-07Y: 0.07), or 0 for a name without a size.
        /// </summary>
        public static float NominalSize(string payload)
        {
            string[] parts = payload.Split('-');
            if (parts.Length < 2 || parts[1].Length < 2 || !int.TryParse(parts[1].Substring(0, 2), out int cm) || cm <= 0)
                return 0f;
            return cm * 0.01f;
        }

        /// <summary>
        /// The size the name promises against what was read; ±30 % leaves room for a printer's
        /// scaling. Catches the misreads near the head. A name without a size is taken at its word.
        /// </summary>
        public static bool IsGhost(string payload, float width)
        {
            float nominal = NominalSize(payload);
            if (nominal <= 0f) return width < 0.02f;
            return width < nominal * 0.7f || width > nominal * 1.3f;
        }

        private static void SetTracking(bool on) =>
            mruk.SceneSettings.TrackerConfiguration = new OVRAnchor.TrackerConfiguration { QRCodeTrackingEnabled = on };

        private static void NewEpoch(string why)
        {
            Epoch++;
            Debug.Log($"[QrSource] Tracking space may have changed: {why}");
        }
    }

    /// <summary>The frame and label drawn on a code that is being read.</summary>
    public sealed class QrOutline
    {
        private static Material material;
        private readonly LineRenderer line;
        private readonly TextMeshPro label;

        public QrOutline(Transform parent, string payload)
        {
            if (material == null) material = new Material(Shader.Find("Sprites/Default")) { name = "QrFrame" };
            var go = new GameObject("QrFrame_" + payload);
            go.transform.SetParent(parent, false);
            line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = true;
            line.loop = true;
            line.positionCount = 4;
            line.startWidth = line.endWidth = 0.004f;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            label = labelGo.AddComponent<TextMeshPro>();
            Brand.Style(label, true);
            label.fontSize = 10f;
            label.alignment = TextAlignmentOptions.Bottom;
            label.rectTransform.pivot = new Vector2(0.5f, 0f);
            label.rectTransform.sizeDelta = new Vector2(60f, 6f);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.color = Color.white;
            Material outlined = Brand.LabelMaterial;
            if (outlined != null) label.fontSharedMaterial = outlined;
            Hide();
        }

        public void Show(Transform code, Rect box, Vector3 head, Color color, string text)
        {
            if (!line.gameObject.activeSelf) line.gameObject.SetActive(true);
            line.SetPosition(0, code.TransformPoint(new Vector3(box.xMin, box.yMin, 0f)));
            line.SetPosition(1, code.TransformPoint(new Vector3(box.xMax, box.yMin, 0f)));
            line.SetPosition(2, code.TransformPoint(new Vector3(box.xMax, box.yMax, 0f)));
            line.SetPosition(3, code.TransformPoint(new Vector3(box.xMin, box.yMax, 0f)));
            line.startColor = line.endColor = color;
            Vector3 centre = code.TransformPoint(new Vector3(box.center.x, box.center.y, 0f));
            float dist = Vector3.Distance(head, centre);
            Vector3 labelAt = centre + Vector3.up * (Mathf.Max(box.height * 0.5f, 0.02f) + 0.03f);
            label.transform.SetPositionAndRotation(labelAt, Quaternion.LookRotation(labelAt - head));
            label.transform.localScale = Vector3.one * Mathf.Max(0.004f, dist * 0.006f);
            label.text = text;
        }

        public void Hide()
        {
            if (line.gameObject.activeSelf) line.gameObject.SetActive(false);
        }

        public void Destroy() => UnityEngine.Object.Destroy(line.gameObject);
    }
}
#endif
