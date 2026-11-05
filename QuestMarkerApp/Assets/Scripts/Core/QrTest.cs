#if !MOBILE_PREVIEW
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;
using UnityEngine.Android;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// «Δοκιμή QR»: a measuring tool, not part of any flow. Answers whether the headset's QR
    /// tracking is steady and true enough to align on stuck-on QR stickers: it outlines every
    /// code it sees, shows its jitter and the distance between pairs of codes (to compare with
    /// a tape) and logs each code twice a second to persistentDataPath/qrtest.
    /// Nothing is placed and the alignment is not touched. Session-only.
    /// Runs on MRUK, created only when the test first starts, with its world lock and scene
    /// loading off: left on, the lock would move the tracking space under the project's
    /// alignment. The Core SDK's own QR API reports "not supported" on the same headset.
    /// Protocol: thesis/data/protocols/δοκιμή_qr.md, analysis: thesis/analysis/qr_test.py.
    /// </summary>
    public class QrTest : MonoBehaviour
    {
        public const string FolderName = "qrtest";
        private const float ScanSeconds = 1f, LogSeconds = 0.5f, ViewSeconds = 0.2f, StartTimeoutSeconds = 8f;
        private const int JitterSamples = 60; // 30 s at the log rate

        private static readonly string[] Columns =
        {
            "timestamp", "session_id", "project", "app_version", "mode", "payload", "tracked",
            "x", "y", "z", "qx", "qy", "qz", "qw", "width_m", "height_m",
            "head_x", "head_y", "head_z", "head_dist_m", "view_angle_deg"
        };

        public static bool Active { get; private set; }

        /// <summary>
        /// One line for the menu; while off, why the last start failed (empty otherwise).
        /// </summary>
        public static string Status { get; private set; } = "";

        private static QrTest instance;

        public static void SetActive(bool on)
        {
            if (instance == null) return;
            Active = on;
            if (on)
            {
                Status = L.T("Εκκίνηση…", "Starting…");
                instance.startedAt = Time.unscaledTime;
                instance.started = false;
            }
            else
            {
                Status = "";
                instance.Stop();
            }
        }

        private sealed class Code
        {
            public MRUKTrackable Trackable;
            public string Payload;
            public Vector3 Center;
            public float Width, Height;
            public readonly Queue<Vector3> Recent = new Queue<Vector3>();
            public LineRenderer Frame;
            public TextMeshPro Label;
        }

        private readonly Dictionary<MRUKTrackable, Code> codes = new Dictionary<MRUKTrackable, Code>();
        private readonly List<MRUKTrackable> found = new List<MRUKTrackable>();
        private readonly List<MRUKTrackable> gone = new List<MRUKTrackable>();
        private MRUK mruk;
        private bool asked, started;
        private float startedAt, nextScan, nextLog, nextView;
        private Material lineMaterial;
        private ProjectAlignment project;
        private RectTransform hud;
        private TextMeshProUGUI hudText;
        private readonly StringBuilder text = new StringBuilder(256);

        private void Awake() => instance = this;

        private void Update()
        {
            if (!Active) return;
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            {
                if (!asked)
                {
                    asked = true;
                    Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission);
                }
                Status = L.T("Χρειάζεται άδεια χωρικών δεδομένων", "Needs the spatial data permission");
                startedAt = Time.unscaledTime;
                return;
            }
            EnsureMruk();
            if (!started)
            {
                if (mruk.TrackerConfiguration.QRCodeTrackingEnabled)
                {
                    started = true;
                    Status = L.T("Ψάχνει QR…", "Looking for QR codes…");
                    Debug.Log("[QrTest] QR tracking on");
                }
                else if (Time.unscaledTime - startedAt > StartTimeoutSeconds)
                {
                    bool supported = mruk.QRCodeTrackingSupported;
                    Debug.LogWarning($"[QrTest] QR tracking did not start (supported: {supported})");
                    Stop();
                    Active = false;
                    Status = supported ? L.T("Απέτυχε η εκκίνηση", "Could not start") : L.T("Δεν υποστηρίζεται", "Not supported");
                }
                return;
            }
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + ScanSeconds;
                Scan();
            }
            Sample();
        }

        private void EnsureMruk()
        {
            if (mruk == null)
            {
                // Inactive while it is set up: its Awake reads the settings.
                var go = new GameObject("MRUK");
                go.SetActive(false);
                go.transform.SetParent(transform, false);
                mruk = go.AddComponent<MRUK>();
                mruk.EnableWorldLock = false;
                mruk.SceneSettings = new MRUK.MRUKSettings { LoadSceneOnStartup = false };
                SetTracking(true);
                go.SetActive(true);
            }
            else if (!mruk.SceneSettings.TrackerConfiguration.QRCodeTrackingEnabled) SetTracking(true);
        }

        private void SetTracking(bool on) =>
            mruk.SceneSettings.TrackerConfiguration = new OVRAnchor.TrackerConfiguration { QRCodeTrackingEnabled = on };

        private void Scan()
        {
            mruk.GetTrackables(found);
            foreach (MRUKTrackable t in found)
            {
                if (t == null || t.TrackableType != OVRAnchor.TrackableType.QRCode || codes.ContainsKey(t)) continue;
                var code = new Code { Trackable = t, Payload = string.IsNullOrEmpty(t.MarkerPayloadString) ? "?" : t.MarkerPayloadString };
                BuildVisual(code);
                codes[t] = code;
                Debug.Log($"[QrTest] Found '{code.Payload}'");
            }
            gone.Clear();
            foreach (MRUKTrackable t in codes.Keys) if (t == null || !found.Contains(t)) gone.Add(t);
            foreach (MRUKTrackable t in gone)
            {
                if (codes[t].Frame != null) Destroy(codes[t].Frame.gameObject);
                codes.Remove(t);
            }
        }

        private void Sample()
        {
            Camera cam = Headset.Camera;
            if (cam == null) return;
            Vector3 head = cam.transform.position;
            bool log = Time.unscaledTime >= nextLog;
            if (log) nextLog = Time.unscaledTime + LogSeconds;

            foreach (Code c in codes.Values)
            {
                if (c.Trackable == null) continue;
                Transform t = c.Trackable.transform;
                Rect box = c.Trackable.PlaneRect ?? default;
                c.Center = t.TransformPoint(new Vector3(box.center.x, box.center.y, 0f));
                c.Width = box.width;
                c.Height = box.height;
                if (log)
                {
                    // A lost code keeps its last pose: that is not a measurement of jitter.
                    if (c.Trackable.IsTracked)
                    {
                        c.Recent.Enqueue(c.Center);
                        while (c.Recent.Count > JitterSamples) c.Recent.Dequeue();
                    }
                    Append(c, t.rotation, head);
                }
                Draw(c, t, box, head);
            }

            if (Time.unscaledTime >= nextView)
            {
                nextView = Time.unscaledTime + ViewSeconds;
                RefreshHud();
            }
            PlaceHud(cam);
        }

        private void Draw(Code c, Transform t, Rect box, Vector3 head)
        {
            c.Frame.SetPosition(0, t.TransformPoint(new Vector3(box.xMin, box.yMin, 0f)));
            c.Frame.SetPosition(1, t.TransformPoint(new Vector3(box.xMax, box.yMin, 0f)));
            c.Frame.SetPosition(2, t.TransformPoint(new Vector3(box.xMax, box.yMax, 0f)));
            c.Frame.SetPosition(3, t.TransformPoint(new Vector3(box.xMin, box.yMax, 0f)));
            Color color = c.Trackable.IsTracked ? Brand.Primary : Brand.FgFaint;
            c.Frame.startColor = c.Frame.endColor = color;
            float dist = Vector3.Distance(head, c.Center);
            Vector3 labelAt = c.Center + Vector3.up * (Mathf.Max(box.height * 0.5f, 0.02f) + 0.03f);
            c.Label.transform.SetPositionAndRotation(labelAt, Quaternion.LookRotation(labelAt - head));
            c.Label.transform.localScale = Vector3.one * Mathf.Max(0.004f, dist * 0.006f);
            c.Label.text = $"{c.Payload}\n{dist:0.00} m · {c.Width * 100f:0.0} cm · ±{Jitter(c) * 1000f:0.0} mm";
        }

        // Spread of the code's centre over the last 30 s tracked, 3D standard deviation, metres.
        private static float Jitter(Code c)
        {
            if (c.Recent.Count < 2) return 0f;
            Vector3 mean = Vector3.zero;
            foreach (Vector3 v in c.Recent) mean += v;
            mean /= c.Recent.Count;
            float sum = 0f;
            foreach (Vector3 v in c.Recent) sum += (v - mean).sqrMagnitude;
            return Mathf.Sqrt(sum / (c.Recent.Count - 1));
        }

        private void BuildVisual(Code c)
        {
            if (lineMaterial == null) lineMaterial = new Material(Shader.Find("Sprites/Default")) { name = "QrTestFrame" };
            var go = new GameObject("QrFrame_" + c.Payload);
            go.transform.SetParent(transform, false);
            c.Frame = go.AddComponent<LineRenderer>();
            c.Frame.sharedMaterial = lineMaterial;
            c.Frame.useWorldSpace = true;
            c.Frame.loop = true;
            c.Frame.positionCount = 4;
            c.Frame.startWidth = c.Frame.endWidth = 0.004f;
            c.Frame.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            c.Frame.receiveShadows = false;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            c.Label = labelGo.AddComponent<TextMeshPro>();
            Brand.Style(c.Label, true);
            c.Label.fontSize = 10f;
            c.Label.alignment = TextAlignmentOptions.Bottom;
            c.Label.rectTransform.pivot = new Vector2(0.5f, 0f);
            c.Label.rectTransform.sizeDelta = new Vector2(60f, 6f);
            c.Label.textWrappingMode = TextWrappingModes.NoWrap;
            c.Label.color = Color.white;
            Material outlined = Brand.LabelMaterial;
            if (outlined != null) c.Label.fontSharedMaterial = outlined;
        }

        // ------------------------------------------------------------------
        //  Panel: the distance between each pair, to hold against the tape
        // ------------------------------------------------------------------

        private void RefreshHud()
        {
            if (hud == null)
            {
                hud = VrUi.CreateCanvas("QrTestHud", transform, new Vector2(440f, 190f), 0.0006f);
                VrUi.Card("Bg", hud, 0, 0, 440, 190, Brand.Surface.WithAlpha(0.92f), Brand.BorderStrong, 16);
                hudText = VrUi.Text("Text", hud, 18, 10, 404, 170, "", 15f, Brand.Fg, false, TextAlignmentOptions.TopLeft);
                hudText.textWrappingMode = TextWrappingModes.Normal;
            }
            text.Clear();
            text.Append(L.T("<b>Δοκιμή QR</b>", "<b>QR test</b>"));
            text.Append(L.En ? $" · {codes.Count} codes\n" : $" · {codes.Count} κωδικοί\n");
            int lines = 0;
            var list = new List<Code>(codes.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.Payload, b.Payload));
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count && lines < 6; j++)
                {
                    text.Append(list[i].Payload).Append(" – ").Append(list[j].Payload).Append(": ")
                        .Append(Vector3.Distance(list[i].Center, list[j].Center).ToString("0.000", CultureInfo.InvariantCulture)).Append(" m\n");
                    lines++;
                }
            hudText.text = text.ToString();
            Status = codes.Count == 0 ? L.T("Ψάχνει QR…", "Looking for QR codes…")
                : L.En ? $"On · {codes.Count} codes" : $"Ναι · {codes.Count} κωδικοί";
        }

        private void PlaceHud(Camera cam)
        {
            if (hud == null) return;
            bool show = !UiState.AnyModal;
            if (hud.gameObject.activeSelf != show) hud.gameObject.SetActive(show);
            if (!show) return;
            Transform head = cam.transform;
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 target = head.position + flat * 0.7f + Vector3.down * 0.42f;
            hud.position = Vector3.Lerp(hud.position, target, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
            VrUi.FaceViewer(hud, head);
        }

        // ------------------------------------------------------------------
        //  CSV (same conventions as FieldTestLog: BOM on a new file, quoted fields, invariant numbers)
        // ------------------------------------------------------------------

        private void Append(Code c, Quaternion rotation, Vector3 head)
        {
            try
            {
                if (project == null) project = FindAnyObjectByType<ProjectAlignment>();
                string name = project != null && project.HasProject ? project.ProjectName : "no-project";
                string folder = Path.Combine(Application.persistentDataPath, FolderName);
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, $"qrtest-{DateTime.Now:yyyy-MM-dd}.csv");
                bool fresh = !File.Exists(file);
                var sb = new StringBuilder(256);
                if (fresh) sb.Append(string.Join(",", Columns)).Append('\n');
                Vector3 normal = rotation * Vector3.forward;
                float angle = Vector3.Angle(normal, head - c.Center);
                if (angle > 90f) angle = 180f - angle; // which way the anchor's axis points is not the question
                Field(sb, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                Field(sb, FieldTestLog.SessionId);
                Field(sb, name);
                Field(sb, Application.version);
                Field(sb, WorkMode.Tag);
                Field(sb, c.Payload);
                Field(sb, c.Trackable.IsTracked ? "1" : "0");
                foreach (float v in new[] { c.Center.x, c.Center.y, c.Center.z, rotation.x, rotation.y, rotation.z, rotation.w,
                             c.Width, c.Height, head.x, head.y, head.z, Vector3.Distance(head, c.Center), angle })
                    Field(sb, v.ToString("0.#####", CultureInfo.InvariantCulture));
                sb[sb.Length - 1] = '\n';
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(fresh));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QrTest] CSV write failed: {e.Message}");
            }
        }

        private static void Field(StringBuilder sb, string v) =>
            sb.Append('"').Append((v ?? "").Replace("\"", "\"\"")).Append("\",");

        // ------------------------------------------------------------------

        private void Stop()
        {
            started = false;
            asked = false;
            nextScan = nextLog = nextView = 0f;
            foreach (Code c in codes.Values)
                if (c.Frame != null) Destroy(c.Frame.gameObject);
            codes.Clear();
            if (hud != null) hud.gameObject.SetActive(false);
            // MRUK stays, idle: tracking off releases the resources and it holds nothing else.
            if (mruk != null) SetTracking(false);
            Debug.Log("[QrTest] QR tracking off");
        }

        private void OnDestroy() => Active = false;
    }
}
#endif
