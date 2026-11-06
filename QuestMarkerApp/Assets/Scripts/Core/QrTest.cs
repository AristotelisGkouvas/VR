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
    /// A code shows only while it is being read: out of sight its pose freezes, and after the
    /// glasses come off it belongs to another tracking space.
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
            public bool HasPose;
            public Vector3 Center;
            public float Width, Height;
            public readonly Queue<Vector3> Recent = new Queue<Vector3>();
            public QrOutline Outline;
        }

        private readonly Dictionary<MRUKTrackable, Code> codes = new Dictionary<MRUKTrackable, Code>();
        private readonly List<MRUKTrackable> found = new List<MRUKTrackable>();
        private readonly List<MRUKTrackable> gone = new List<MRUKTrackable>();
        private bool asked, started, holding;
        private int epoch;
        private float startedAt, nextScan, nextLog, nextView;
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
            if (!holding)
            {
                holding = true;
                QrSource.Acquire(transform);
                epoch = QrSource.Epoch;
            }
            if (!started)
            {
                if (QrSource.Running)
                {
                    started = true;
                    Status = L.T("Ψάχνει QR…", "Looking for QR codes…");
                    Debug.Log("[QrTest] QR tracking on");
                }
                else if (Time.unscaledTime - startedAt > StartTimeoutSeconds)
                {
                    bool supported = QrSource.Supported;
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

        private void Scan()
        {
            QrSource.Get(found);
            foreach (MRUKTrackable t in found)
            {
                if (t == null || t.TrackableType != OVRAnchor.TrackableType.QRCode || codes.ContainsKey(t)) continue;
                var code = new Code { Trackable = t, Payload = string.IsNullOrEmpty(t.MarkerPayloadString) ? "?" : t.MarkerPayloadString };
                code.Outline = new QrOutline(transform, code.Payload);
                codes[t] = code;
                Debug.Log($"[QrTest] Found '{code.Payload}'");
            }
            gone.Clear();
            foreach (MRUKTrackable t in codes.Keys) if (t == null || !found.Contains(t)) gone.Add(t);
            foreach (MRUKTrackable t in gone)
            {
                codes[t].Outline.Destroy();
                codes.Remove(t);
            }
        }

        private void Sample()
        {
            Camera cam = Headset.Camera;
            if (cam == null) return;
            Vector3 head = cam.transform.position;
            QrSource.Tick(head);
            if (epoch != QrSource.Epoch)
            {
                // What was read before belongs to the old tracking space: no pairs across it.
                epoch = QrSource.Epoch;
                foreach (Code c in codes.Values)
                {
                    c.HasPose = false;
                    c.Recent.Clear();
                    c.Outline.Hide();
                }
            }
            bool log = Time.unscaledTime >= nextLog;
            if (log) nextLog = Time.unscaledTime + LogSeconds;

            foreach (Code c in codes.Values)
            {
                if (c.Trackable == null) continue;
                Transform t = c.Trackable.transform;
                Rect box = c.Trackable.PlaneRect ?? default;
                Vector3 center = t.TransformPoint(new Vector3(box.center.x, box.center.y, 0f));
                if (log) Append(c, center, box, t.rotation, head);
                if (!c.Trackable.IsTracked || QrSource.IsGhost(c.Payload, box.width))
                {
                    c.Outline.Hide();
                    continue;
                }
                c.HasPose = true;
                c.Center = center;
                c.Width = box.width;
                c.Height = box.height;
                if (log)
                {
                    c.Recent.Enqueue(c.Center);
                    while (c.Recent.Count > JitterSamples) c.Recent.Dequeue();
                }
                float dist = Vector3.Distance(head, c.Center);
                c.Outline.Show(t, box, head, Brand.Primary, $"{c.Payload}\n{dist:0.00} m · {c.Width * 100f:0.0} cm · ±{Jitter(c) * 1000f:0.0} mm");
            }

            if (Time.unscaledTime >= nextView)
            {
                nextView = Time.unscaledTime + ViewSeconds;
                RefreshHud();
            }
            PlaceHud(cam);
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
            int read = 0;
            foreach (Code c in codes.Values) if (c.HasPose) read++;
            text.Append(L.En ? $" · {read} codes\n" : $" · {read} κωδικοί\n");
            int lines = 0;
            var list = new List<Code>(codes.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.Payload, b.Payload));
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count && lines < 6; j++)
                {
                    if (!list[i].HasPose || !list[j].HasPose) continue;
                    text.Append(list[i].Payload).Append(" – ").Append(list[j].Payload).Append(": ")
                        .Append(Vector3.Distance(list[i].Center, list[j].Center).ToString("0.000", CultureInfo.InvariantCulture)).Append(" m\n");
                    lines++;
                }
            hudText.text = text.ToString();
            Status = read == 0 ? L.T("Ψάχνει QR…", "Looking for QR codes…")
                : L.En ? $"On · {read} codes" : $"Ναι · {read} κωδικοί";
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

        private void Append(Code c, Vector3 center, Rect box, Quaternion rotation, Vector3 head)
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
                float angle = Vector3.Angle(normal, head - center);
                if (angle > 90f) angle = 180f - angle; // which way the anchor's axis points is not the question
                Field(sb, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                Field(sb, FieldTestLog.SessionId);
                Field(sb, name);
                Field(sb, Application.version);
                Field(sb, WorkMode.Tag);
                Field(sb, c.Payload);
                Field(sb, c.Trackable.IsTracked ? "1" : "0");
                foreach (float v in new[] { center.x, center.y, center.z, rotation.x, rotation.y, rotation.z, rotation.w,
                             box.width, box.height, head.x, head.y, head.z, Vector3.Distance(head, center), angle })
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
            foreach (Code c in codes.Values) c.Outline.Destroy();
            codes.Clear();
            if (hud != null) hud.gameObject.SetActive(false);
            if (holding) QrSource.Release();
            holding = false;
            Debug.Log("[QrTest] QR tracking off");
        }

        private void OnDestroy() => Active = false;
    }
}
#endif
