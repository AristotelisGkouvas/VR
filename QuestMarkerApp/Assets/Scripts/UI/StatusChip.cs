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
        private const float W = 300f, H = 76f, Scale = 0.0005f;
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
        private Image modeDot, alignDot, netDot;
        private TextMeshProUGUI modeText, alignText, dataText;
        private float nextRefresh;
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
            // Panels own the view while open; the hand-held alignment panel sits on this hand.
            bool show = Enabled && cam != null && hand != null && !UiState.AnyModal
                        && (project == null || !project.IsModeActive);
            if (chip.gameObject.activeSelf != show) chip.gameObject.SetActive(show);
            if (!show) return;

            // Over the back of the hand, facing the eyes, like a watch.
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
            string net = !online ? L.T("Χωρίς δίκτυο", "Offline")
                : signedIn ? "Terraweb" : L.T("Εκτός Terraweb", "Signed out");
            string age = "";
            if (flow != null && flow.LastLoaded > DateTime.MinValue)
                age = " · " + L.T("δεδομένα ", "data ") + Ago(DateTime.UtcNow - flow.LastLoaded);
            dataText.text = net + age;
            netDot.color = !online ? Brand.Danger : signedIn ? Brand.Primary : Brand.Warning;
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
