using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.Core;
using QuestMarkerApp.Models;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// A watch on the off-hand controller: what is running (so it is clear what B ends), how
    /// far to trust the alignment, how old the data is, and whether Terraweb is reachable.
    /// Glance at the hand to read it; it stays out of the view otherwise. Menu switch, on by default.
    /// </summary>
    public class StatusChip : MonoBehaviour
    {
        private const string EnabledKey = "ui.statusChip";
        private const float W = 300f, H = 98f, Scale = 0.0005f;
        /// <summary>Battery warnings, percent: a buzz and a sound once each per charge.</summary>
        private const int LowBattery = 20, CriticalBattery = 10;
        private const float RefreshSeconds = 0.5f;

        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(EnabledKey, 1) == 1;
            set { PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        private ProjectAlignment project;
        private TerrawebClient client;
        private TerrawebFlow flow;
        private ModelOverlay model;
        private RectTransform chip;
        private Image modeDot, alignDot, netDot, batteryDot;
        private int warnedAt = 101; // lowest threshold already warned about
        private TextMeshProUGUI modeText, alignText, dataText, batteryText;
        private float nextRefresh;
        private Transform watchHand;
        private bool lateHooked;

        // Again from the pose the rig sets just before rendering: placed only in LateUpdate,
        // the watch swam a frame behind the hand.
        private void LatePose()
        {
            Camera cam = Camera.main;
            if (chip == null || !chip.gameObject.activeSelf || watchHand == null || cam == null) return;
            chip.position = watchHand.position + Vector3.up * 0.07f;
            VrUi.FaceViewer(chip, cam.transform);
        }

        private void OnDestroy()
        {
            if (lateHooked) Application.onBeforeRender -= LatePose;
        }
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        public void Init(ProjectAlignment alignment, TerrawebClient terraweb, TerrawebFlow terrawebFlow, ModelOverlay overlay)
        {
            project = alignment;
            client = terraweb;
            flow = terrawebFlow;
            model = overlay;
        }

        private void Awake()
        {
            chip = VrUi.CreateCanvas("StatusChip", transform, new Vector2(W, H), Scale);
            VrUi.Card("Bg", chip, 0, 0, W, H, Brand.Surface.WithAlpha(0.94f), Brand.BorderStrong, 14);
            modeDot = VrUi.Icon("ModeDot", chip, 12, 10, 10, Brand.DiscSprite, Brand.Primary);
            modeText = VrUi.Text("Mode", chip, 28, 2, W - 36, 24, "", 15f, Brand.Fg, true);
            alignDot = VrUi.Icon("AlignDot", chip, 12, 32, 10, Brand.DiscSprite, Brand.FgFaint);
            alignText = VrUi.Text("Align", chip, 28, 24, W - 36, 24, "", 13f, Brand.FgMuted);
            netDot = VrUi.Icon("NetDot", chip, 12, 54, 10, Brand.DiscSprite, Brand.FgFaint);
            dataText = VrUi.Text("Data", chip, 28, 46, W - 36, 24, "", 13f, Brand.FgMuted);
            batteryDot = VrUi.Icon("BatteryDot", chip, 12, 76, 10, Brand.DiscSprite, Brand.FgFaint);
            batteryText = VrUi.Text("Battery", chip, 28, 68, W - 36, 24, "", 13f, Brand.FgMuted);
            foreach (TextMeshProUGUI t in new[] { modeText, alignText, dataText, batteryText })
            {
                t.enableAutoSizing = true; // long lines shrink rather than run off the watch
                t.fontSizeMin = 12f;
                t.fontSizeMax = t.fontSize;
            }
            chip.gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            Transform hand = null;
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && OVRInput.IsControllerConnected(Hands.OffHand)) hand = Hands.OffHandAnchor(rig);
#endif
            WatchBattery();
            // Panels own the view while open; the hand-held alignment panel sits on this hand.
            bool show = Enabled && cam != null && hand != null && !UiState.AnyModal
                        && (project == null || !project.IsModeActive);
            if (chip.gameObject.activeSelf != show) chip.gameObject.SetActive(show);
            if (!show) return;

            // Over the back of the hand, facing the eyes, like a watch.
            watchHand = hand;
            if (!lateHooked) { Application.onBeforeRender += LatePose; lateHooked = true; }
            chip.position = hand.position + Vector3.up * 0.07f;
            VrUi.FaceViewer(chip, cam.transform);
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            Fill();
        }

        private void Fill()
        {
            modeText.text = Mode(out Color modeColor);
            modeDot.color = modeColor;

            if (project == null || !project.HasProject)
            {
                alignText.text = L.T("Χωρίς έργο", "No project");
                alignDot.color = Brand.FgFaint;
            }
            else if (!project.IsAligned)
            {
                alignText.text = L.T("Χωρίς ευθυγράμμιση", "Not aligned");
                alignDot.color = Brand.Warning;
            }
            else if (project.NeedsCheck)
            {
                alignText.text = L.T("Ευθυγράμμιση: άγγιξε ένα σημείο για έλεγχο", "Alignment: touch a point to check");
                alignDot.color = Brand.Warning;
            }
            else
            {
                float cm = Mathf.Max(project.PredictedError, project.RmsHorizontal) * 100f;
                alignText.text = L.En ? $"Aligned · ±{cm:0} cm at the farthest sensor" : $"Ευθυγράμμιση · ±{cm:0} cm στον πιο μακρινό";
                alignDot.color = cm <= 5f ? Brand.Primary : cm <= 15f ? Brand.Warning : Brand.Danger;
            }

            bool online = Application.internetReachability != NetworkReachability.NotReachable;
            bool signedIn = client != null && client.IsLoggedIn;
            bool fromPack = client != null && client.ServingOffline;
            string net = fromPack ? L.T("Offline πακέτο", "Offline pack")
                : !online ? L.T("Χωρίς δίκτυο", "Offline")
                : signedIn ? "Terraweb" : L.T("Εκτός Terraweb", "Signed out");
            string age = "";
            if (fromPack && client.OfflineDataDate < DateTime.MaxValue)
                age = " · " + L.T("δεδομένα ", "data ") + Ago(DateTime.UtcNow - client.OfflineDataDate);
            else if (flow != null && flow.LastLoaded > DateTime.MinValue)
                age = " · " + L.T("δεδομένα ", "data ") + Ago(DateTime.UtcNow - flow.LastLoaded);
            // Offline the alert states are those of the copy: say they are not live.
            if (fromPack || !online) age += L.T(" · συναγερμοί όχι ζωντανοί", " · alarms not live");
            dataText.text = net + age;
            FillBattery();
            netDot.color = fromPack || !signedIn ? Brand.Warning : !online ? Brand.Danger : Brand.Primary;
        }

        // Low battery is told even with the watch hidden: a headset dying mid-route loses the
        // walk. Once per threshold per charge (a charger resets it).
        private float nextBatteryCheck;
        private void WatchBattery()
        {
            if (Time.unscaledTime < nextBatteryCheck) return;
            nextBatteryCheck = Time.unscaledTime + 10f;
            float level = SystemInfo.batteryLevel;
            if (level < 0f) return;
            int pct = Mathf.RoundToInt(level * 100f);
            if (SystemInfo.batteryStatus == BatteryStatus.Charging || pct > LowBattery) { warnedAt = 101; return; }
            int threshold = pct <= CriticalBattery ? CriticalBattery : LowBattery;
            if (threshold >= warnedAt) return;
            warnedAt = threshold;
            Feedback.Play(Feedback.Sound.Warn);
#if !MOBILE_PREVIEW
            Feedback.Buzz(Hands.OffHand, 0.6f, 0.3f);
#endif
            Debug.Log($"[Status] Battery {pct}%");
        }

        private void FillBattery()
        {
            float level = SystemInfo.batteryLevel;
            if (level < 0f) { batteryText.text = L.T("Μπαταρία: —", "Battery: —"); batteryDot.color = Brand.FgFaint; return; }
            int pct = Mathf.RoundToInt(level * 100f);
            bool charging = SystemInfo.batteryStatus == BatteryStatus.Charging;
            string hint = charging ? L.T(" · φορτίζει", " · charging")
                : pct <= CriticalBattery ? L.T(" · φόρτισε τώρα", " · charge now")
                : pct <= LowBattery ? L.T(" · χαμηλή, 72 Hz στις Ρυθμίσεις", " · low, 72 Hz in Settings") : "";
            batteryText.text = L.T($"Μπαταρία {pct}%", $"Battery {pct}%") + hint;
            batteryDot.color = pct <= CriticalBattery ? Brand.Danger : pct <= LowBattery ? Brand.Warning : Brand.Primary;
        }

        // What runs on top, which is also what B ends (UiState.Context's order).
        private string Mode(out Color color)
        {
            color = Brand.Info;
            string context = UiState.Context(project);
            switch (context)
            {
                case "nav": return L.T("Πλοήγηση · B: τέλος", "Navigation · B: end");
                case "measure": return L.T("Μετροταινία · B: έξοδος", "Tape measure · B: exit");
                case "time": return L.T("Time machine · B: έξοδος", "Time machine · B: exit");
                case "route": return L.T("Διαδρομή · κράτα Μενού: διακοπή", "Route · hold Menu: stop");
            }
            if (model != null && ModelOverlay.PreviewShown)
                return ModelOverlay.LifeSizeShown ? L.T("Μοντέλο 1:1", "Model 1:1") : L.T("Μακέτα", "Tabletop model");
            color = Brand.FgSubtle;
            if (project != null && project.IsPractice) return L.T("Έργο εξάσκησης", "Practice project");
            return project != null && project.HasProject ? project.ProjectName : L.T("Χωρίς έργο", "No project");
        }

        private static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return L.T("τώρα", "now");
            if (t.TotalHours < 1) return L.En ? $"{(int)t.TotalMinutes} min ago" : $"πριν {(int)t.TotalMinutes}′";
            if (t.TotalDays < 1) return L.En ? $"{(int)t.TotalHours} h ago" : $"πριν {(int)t.TotalHours} ώρ.";
            return L.En ? $"{(int)t.TotalDays} d ago" : $"πριν {(int)t.TotalDays} ημ.";
        }
    }
}
