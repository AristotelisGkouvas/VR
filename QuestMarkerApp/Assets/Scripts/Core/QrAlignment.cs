#if !MOBILE_PREVIEW
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Android;
using QuestMarkerApp.API;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Alignment on surveyed QR codes. While the alignment panel is open and QR was the method
    /// chosen (points named QM-…, whose centres were measured), a code that holds still in view becomes
    /// a control point by itself: no row to pick, nothing to touch. The solve, the outlier check,
    /// the suggestion and "Finish" stay the ones the touches use, and so does undo: a withdrawn
    /// code waits until it has left the view, or it would be read again at once.
    /// The limits come from the survey comparison in thesis/data/qrtest/.
    /// </summary>
    public class QrAlignment : MonoBehaviour
    {
        public const string FolderName = "qralign";
        private const float ScanSeconds = 1f, SampleSeconds = 0.2f;
        private const int Samples = 10; // 2 s
        private const float MaxDistance = 2.3f, MaxAngle = 60f, MaxSpread = 0.015f;

        private sealed class Reading
        {
            public MRUKTrackable Trackable;
            public string Payload;
            public QrOutline Outline;
            public readonly List<Vector3> Centres = new List<Vector3>();
            public bool PlacedHere, WaitingToLeave;
        }

        private ProjectAlignment alignment;
        private readonly Dictionary<MRUKTrackable, Reading> readings = new Dictionary<MRUKTrackable, Reading>();
        private readonly List<MRUKTrackable> found = new List<MRUKTrackable>();
        private readonly List<MRUKTrackable> gone = new List<MRUKTrackable>();
        // Codes placed since the panel opened: all from the tracking space of this sitting.
        private readonly HashSet<string> placedNow = new HashSet<string>();
        private bool holding, asked;
        private int epoch;
        private float nextScan, nextSample;

        public void Init(ProjectAlignment projectAlignment) => alignment = projectAlignment;

        private void Update()
        {
            bool want = alignment != null && alignment.IsModeActive && alignment.QrOnly
                && !alignment.ChoosingMethod && !alignment.IsPractice && !QrTest.Active;
            if (!want)
            {
                if (holding) Stop();
                return;
            }
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            {
                if (!asked)
                {
                    asked = true;
                    Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission);
                }
                alignment.SetQrStatus(L.T("Για τα QR χρειάζεται άδεια χωρικών δεδομένων.", "QR codes need the spatial data permission."));
                return;
            }
            if (!holding)
            {
                holding = true;
                QrSource.Acquire(transform);
                epoch = QrSource.Epoch;
                Debug.Log("[QrAlign] Reading codes");
            }
            Camera cam = Headset.Camera;
            if (cam == null || !QrSource.Running) return;

            Vector3 head = cam.transform.position;
            QrSource.Tick(head);
            if (epoch != QrSource.Epoch) OnTrackingChanged();
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + ScanSeconds;
                Scan();
            }
            bool sample = Time.unscaledTime >= nextSample;
            if (sample) nextSample = Time.unscaledTime + SampleSeconds;
            // Beside an open menu or list the panel has stepped aside: nothing lands unseen.
            bool paused = MainMenu.IsOpen || FlowPanel.IsOpen || VrKeyboard.IsOpen;
            string status = "";
            foreach (Reading r in readings.Values)
                Follow(r, head, sample, paused, ref status);
            alignment.SetQrStatus(status);
        }

        private void Scan()
        {
            QrSource.Get(found);
            foreach (MRUKTrackable t in found)
            {
                if (t == null || t.TrackableType != OVRAnchor.TrackableType.QRCode || readings.ContainsKey(t)) continue;
                string payload = t.MarkerPayloadString;
                if (string.IsNullOrEmpty(payload) || !ProjectPoint.IsCodeId(payload)) continue; // somebody else's code
                readings[t] = new Reading { Trackable = t, Payload = payload.Trim(), Outline = new QrOutline(transform, payload) };
            }
            gone.Clear();
            foreach (MRUKTrackable t in readings.Keys) if (t == null || !found.Contains(t)) gone.Add(t);
            foreach (MRUKTrackable t in gone)
            {
                readings[t].Outline.Destroy();
                readings.Remove(t);
            }
        }

        private void Follow(Reading r, Vector3 head, bool sample, bool paused, ref string status)
        {
            if (r.Trackable == null) return;
            Transform t = r.Trackable.transform;
            Rect box = r.Trackable.PlaneRect ?? default;
            if (!r.Trackable.IsTracked || QrSource.IsGhost(r.Payload, box.width))
            {
                r.Outline.Hide();
                r.Centres.Clear();
                if (!r.Trackable.IsTracked) r.WaitingToLeave = false;
                return;
            }
            if (paused)
            {
                r.Outline.Hide();
                r.Centres.Clear();
                return;
            }

            if (!alignment.HasCode(r.Payload))
            {
                r.Outline.Show(t, box, head, Brand.FgFaint, r.Payload + L.T("\nόχι σε αυτό το έργο", "\nnot in this project"));
                return;
            }

            bool placed = alignment.IsPlaced(r.Payload);
            if (r.PlacedHere && !placed)
            {
                r.PlacedHere = false;
                r.WaitingToLeave = true;
            }
            if (placed && !alignment.NeedsCheck)
            {
                r.Centres.Clear();
                r.Outline.Show(t, box, head, Brand.Primary, r.Payload + " · OK");
                return;
            }
            if (r.WaitingToLeave)
            {
                r.Outline.Show(t, box, head, Brand.FgFaint, r.Payload + L.T("\nκοίτα αλλού για να ξαναδιαβαστεί", "\nlook away to read it again"));
                return;
            }

            Vector3 centre = t.TransformPoint(new Vector3(box.center.x, box.center.y, 0f));
            float distance = Vector3.Distance(head, centre);
            float angle = Vector3.Angle(t.rotation * Vector3.forward, head - centre);
            if (angle > 90f) angle = 180f - angle;
            if (distance > MaxDistance || angle > MaxAngle)
            {
                r.Centres.Clear();
                string hint = distance > MaxDistance ? L.T("πλησίασε", "come closer") : L.T("πήγαινε πιο μπροστά από τον κωδικό", "stand more in front of it");
                r.Outline.Show(t, box, head, Brand.Warning, $"{r.Payload}\n{hint}");
                status = $"{r.Payload}: {hint}.";
                return;
            }

            r.Outline.Show(t, box, head, Brand.Info, r.Payload + L.T("\nμείνε ακίνητος", "\nhold still"));
            status = L.En ? $"Reading {r.Payload}. Hold still." : $"Διαβάζει το {r.Payload}. Μείνε ακίνητος.";
            if (!sample) return;
            r.Centres.Add(centre);
            if (r.Centres.Count > Samples) r.Centres.RemoveAt(0);
            if (r.Centres.Count < Samples) return;

            Vector3 median = Median(r.Centres);
            float spread = 0f;
            foreach (Vector3 c in r.Centres) spread = Mathf.Max(spread, Vector3.Distance(c, median));
            if (spread > MaxSpread) return;

            r.Centres.Clear();
            if (!alignment.PlaceCode(r.Payload, median)) return;
            r.PlacedHere = true;
            placedNow.Add(r.Payload);
            Debug.Log($"[QrAlign] {r.Payload} placed: {distance:F2} m away, spread {spread * 1000f:F1} mm");
            Log(r.Payload, median, distance, box.width, spread);
        }

        private static Vector3 Median(List<Vector3> values)
        {
            int n = values.Count;
            var x = new float[n];
            var y = new float[n];
            var z = new float[n];
            for (int i = 0; i < n; i++)
            {
                x[i] = values[i].x;
                y[i] = values[i].y;
                z[i] = values[i].z;
            }
            Array.Sort(x);
            Array.Sort(y);
            Array.Sort(z);
            return new Vector3(x[n / 2], y[n / 2], z[n / 2]);
        }

        // Readings from two tracking spaces fit metres apart: start over rather than mix them.
        private void OnTrackingChanged()
        {
            epoch = QrSource.Epoch;
            foreach (Reading r in readings.Values)
            {
                r.Centres.Clear();
                r.PlacedHere = false;
                r.WaitingToLeave = false;
                r.Outline.Hide();
            }
            if (placedNow.Count == 0) return;
            placedNow.Clear();
            alignment.DropAfterTrackingChange();
            Feedback.Play(Feedback.Sound.Warn);
            Debug.LogWarning("[QrAlign] Tracking space changed: codes read so far dropped");
        }

        private void Stop()
        {
            holding = false;
            foreach (Reading r in readings.Values) r.Outline.Destroy();
            readings.Clear();
            placedNow.Clear();
            nextScan = nextSample = 0f;
            if (alignment != null) alignment.SetQrStatus("");
            QrSource.Release();
            Debug.Log("[QrAlign] Reading stopped");
        }

        private void OnDisable()
        {
            if (holding) Stop();
        }

        // ------------------------------------------------------------------
        //  CSV: each placement with the fit it produced (thesis/data/qrtest)
        // ------------------------------------------------------------------

        private void Log(string payload, Vector3 centre, float distance, float width, float spread)
        {
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, FolderName);
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, $"qralign-{DateTime.Now:yyyy-MM-dd}.csv");
                bool fresh = !File.Exists(file);
                var sb = new StringBuilder(160);
                if (fresh) sb.Append("timestamp,session_id,project,app_version,payload,x,y,z,head_dist_m,width_m,spread_m,placed,aligned,yaw_deg,rms_h_m,rms_v_m\n");
                sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(FieldTestLog.SessionId).Append(",\"").Append(alignment.ProjectName.Replace("\"", "\"\"")).Append("\",");
                sb.Append(Application.version).Append(',').Append(payload).Append(',');
                foreach (float v in new[] { centre.x, centre.y, centre.z, distance, width, spread })
                    sb.Append(v.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(alignment.PlacedCount).Append(',').Append(alignment.IsAligned ? '1' : '0').Append(',');
                sb.Append(alignment.YawDeg.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(alignment.RmsHorizontal.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(alignment.RmsVertical.ToString("0.#####", CultureInfo.InvariantCulture)).Append('\n');
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(fresh));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QrAlign] CSV write failed: {e.Message}");
            }
        }
    }
}
#endif
