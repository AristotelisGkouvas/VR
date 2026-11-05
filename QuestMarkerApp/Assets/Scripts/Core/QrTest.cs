#if !MOBILE_PREVIEW
#pragma warning disable 0618 // the Core QR API is marked "moved to MRUK"; enough for this test (see below)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
    /// Uses OVRAnchor.Tracker from the Core SDK, which Meta now marks as moved to MRUK: no new
    /// package for a test. If the stickers go ahead, the real feature should move to MRUK.
    /// Protocol: thesis/data/protocols/δοκιμή_qr.md, analysis: thesis/analysis/qr_test.py.
    /// </summary>
    public class QrTest : MonoBehaviour
    {
        public const string FolderName = "qrtest";
        private const float FetchSeconds = 1f, LogSeconds = 0.5f, ViewSeconds = 0.2f;
        private const int JitterSamples = 60; // 30 s at the log rate

        private static readonly string[] Columns =
        {
            "timestamp", "session_id", "project", "app_version", "mode", "payload", "uuid",
            "x", "y", "z", "qx", "qy", "qz", "qw", "width_m", "height_m",
            "head_x", "head_y", "head_z", "head_dist_m", "view_angle_deg"
        };

        public static bool Active { get; private set; }

        /// <summary>One line for the menu.</summary>
        public static string Status { get; private set; } = "";

        private static QrTest instance;

        public static void SetActive(bool on)
        {
            if (instance == null) return;
            Active = on;
            if (on) Status = L.T("Εκκίνηση…", "Starting…");
            else instance.Stop();
        }

        private sealed class Code
        {
            public OVRAnchor Anchor;
            public OVRLocatable Locatable;
            public string Payload;
            public bool HasPose;
            public Vector3 Center;
            public Quaternion Rotation;
            public float Width, Height;
            public readonly Queue<Vector3> Recent = new Queue<Vector3>();
            public LineRenderer Frame;
            public TextMeshPro Label;
        }

        private readonly Dictionary<Guid, Code> codes = new Dictionary<Guid, Code>();
        private readonly List<OVRAnchor> fetched = new List<OVRAnchor>();
        private OVRAnchor.Tracker tracker;
        private bool configured, configuring, fetching, asked;
        private float nextFetch, nextLog, nextView;
        private Material lineMaterial;
        private OVRCameraRig rig;
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
                return;
            }
            if (!configured)
            {
                if (!configuring) Configure();
                return;
            }
            if (!fetching && Time.unscaledTime >= nextFetch)
            {
                nextFetch = Time.unscaledTime + FetchSeconds;
                Fetch();
            }
            Sample();
        }

        private async void Configure()
        {
            configuring = true;
            if (!OVRAnchor.TrackerConfiguration.QRCodeTrackingSupported)
            {
                Status = L.T("Δεν υποστηρίζεται", "Not supported");
                Debug.LogWarning("[QrTest] QR tracking not supported on this headset or OS");
                Active = false;
                configuring = false;
                return;
            }
            tracker ??= new OVRAnchor.Tracker();
            var result = await tracker.ConfigureAsync(new OVRAnchor.TrackerConfiguration { QRCodeTrackingEnabled = true });
            configuring = false;
            if (!result.Success)
            {
                Status = L.T("Απέτυχε η εκκίνηση", "Could not start");
                Debug.LogWarning($"[QrTest] Tracker configure failed: {result.Status}");
                Active = false;
                return;
            }
            configured = Active; // switched off meanwhile: Stop has already run
            if (!Active) { DisposeTracker(); return; }
            Status = L.T("Ψάχνει QR…", "Looking for QR codes…");
            Debug.Log("[QrTest] QR tracking on");
        }

        private async void Fetch()
        {
            fetching = true;
            fetched.Clear();
            var result = await tracker.FetchTrackablesAsync(fetched);
            if (Active && result.Success)
            {
                // Still "fetching" through the awaits below: a new fetch would clear this list.
                foreach (OVRAnchor anchor in fetched)
                {
                    if (codes.ContainsKey(anchor.Uuid)) continue;
                    if (!anchor.TryGetComponent(out OVRLocatable locatable)) continue;
                    var code = new Code { Anchor = anchor, Locatable = locatable, Payload = PayloadOf(anchor) };
                    codes[anchor.Uuid] = code;
                    await locatable.SetEnabledAsync(true);
                    if (!Active) break;
                    BuildVisual(code);
                    Debug.Log($"[QrTest] Found '{code.Payload}'");
                }
            }
            fetching = false;
        }

        private static string PayloadOf(OVRAnchor anchor)
        {
            if (anchor.TryGetComponent(out OVRMarkerPayload payload) && payload.PayloadType == OVRMarkerPayloadType.StringQRCode)
                return payload.AsString();
            return "?";
        }

        private void Sample()
        {
            Camera cam = Headset.Camera;
            if (cam == null) return;
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig == null) return;
            Vector3 head = cam.transform.position;
            bool log = Time.unscaledTime >= nextLog;
            if (log) nextLog = Time.unscaledTime + LogSeconds;

            foreach (KeyValuePair<Guid, Code> kv in codes)
            {
                Code c = kv.Value;
                if (c.Frame == null || !c.Locatable.IsEnabled || !c.Locatable.TryGetSceneAnchorPose(out OVRLocatable.TrackingSpacePose pose))
                    continue;
                Vector3? p = pose.ComputeWorldPosition(rig.trackingSpace);
                Quaternion? q = pose.ComputeWorldRotation(rig.trackingSpace);
                if (!p.HasValue || !q.HasValue) continue;
                Rect box = c.Anchor.TryGetComponent(out OVRBounded2D bounded) && bounded.IsEnabled ? bounded.BoundingBox : default;
                c.HasPose = true;
                c.Rotation = q.Value;
                c.Center = p.Value + q.Value * new Vector3(box.center.x, box.center.y, 0f);
                c.Width = box.width;
                c.Height = box.height;
                if (log)
                {
                    c.Recent.Enqueue(c.Center);
                    while (c.Recent.Count > JitterSamples) c.Recent.Dequeue();
                    Append(c, head, kv.Key);
                }
                Draw(c, box, p.Value, q.Value, head);
            }

            if (Time.unscaledTime >= nextView)
            {
                nextView = Time.unscaledTime + ViewSeconds;
                RefreshHud();
            }
            PlaceHud(cam);
        }

        private void Draw(Code c, Rect box, Vector3 p, Quaternion q, Vector3 head)
        {
            c.Frame.SetPosition(0, p + q * new Vector3(box.xMin, box.yMin, 0f));
            c.Frame.SetPosition(1, p + q * new Vector3(box.xMax, box.yMin, 0f));
            c.Frame.SetPosition(2, p + q * new Vector3(box.xMax, box.yMax, 0f));
            c.Frame.SetPosition(3, p + q * new Vector3(box.xMin, box.yMax, 0f));
            float dist = Vector3.Distance(head, c.Center);
            Vector3 labelAt = c.Center + Vector3.up * (Mathf.Max(box.height * 0.5f, 0.02f) + 0.03f);
            c.Label.transform.SetPositionAndRotation(labelAt, Quaternion.LookRotation(labelAt - head));
            c.Label.transform.localScale = Vector3.one * Mathf.Max(0.004f, dist * 0.006f);
            c.Label.text = $"{c.Payload}\n{dist:0.00} m · {c.Width * 100f:0.0} cm · ±{Jitter(c) * 1000f:0.0} mm";
        }

        // Spread of the code's centre over the last 30 s, 3D standard deviation, metres.
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
            c.Frame.startColor = c.Frame.endColor = Brand.Primary;
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
            int seen = 0;
            foreach (Code c in codes.Values) if (c.HasPose) seen++;
            text.Append(L.En ? $" · {seen} codes\n" : $" · {seen} κωδικοί\n");
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
            Status = L.En ? $"On · {seen} codes" : $"Ναι · {seen} κωδικοί";
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

        private void Append(Code c, Vector3 head, Guid uuid)
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
                Vector3 normal = c.Rotation * Vector3.forward;
                float angle = Vector3.Angle(normal, head - c.Center);
                if (angle > 90f) angle = 180f - angle; // which way the anchor's axis points is not the question
                Field(sb, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                Field(sb, FieldTestLog.SessionId);
                Field(sb, name);
                Field(sb, Application.version);
                Field(sb, WorkMode.Tag);
                Field(sb, c.Payload);
                Field(sb, uuid.ToString("N"));
                foreach (float v in new[] { c.Center.x, c.Center.y, c.Center.z, c.Rotation.x, c.Rotation.y, c.Rotation.z, c.Rotation.w,
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
            configured = false;
            asked = false;
            nextFetch = nextLog = nextView = 0f;
            foreach (Code c in codes.Values)
                if (c.Frame != null) Destroy(c.Frame.gameObject);
            codes.Clear();
            if (hud != null) hud.gameObject.SetActive(false);
            Status = "";
            if (!configuring) DisposeTracker();
            Debug.Log("[QrTest] QR tracking off");
        }

        private void DisposeTracker()
        {
            tracker?.Dispose();
            tracker = null;
        }

        private void OnDestroy()
        {
            Active = false;
            DisposeTracker();
        }
    }
}
#endif
