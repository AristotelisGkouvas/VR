using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using QuestMarkerApp.Core;
using QuestMarkerApp.Models;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// "Πού δουλεύεις τώρα;" — Field or Office (<see cref="WorkMode"/>), asked at start and when
    /// the headset comes back after more than <see cref="WorkMode.RememberMinutes"/>. Office
    /// then asks how to see the model: tabletop miniature or life size. A third row starts the
    /// office practice with the printed targets. Built on the shared <see cref="FlowPanel"/>
    /// list, so it looks and handles like the project picker.
    ///
    /// The suggestion follows what the headset already knows: a project whose saved alignment
    /// was found again in this room means the user is on site, so Field starts selected.
    /// </summary>
    public class WorkModeChooser : MonoBehaviour
    {
        private const string OfficeViewKey = "work.officeView"; // 1 tabletop, 2 life size
        private const float TouchEverySeconds = 60f;
        // The reference anchor relocalises a few seconds after start: wait for it, so the
        // "found here" suggestion is right, but not for ever.
        private const float AnchorWaitSeconds = 10f;

        private FlowPanel flow;
        private ProjectAlignment alignment;
        private ModelOverlay model;
        private SpatialAnchorManager anchors;
        private bool asking, pendingAsk;
        private float nextTouch;
        private Action afterChoice;

        public static bool IsAsking { get; private set; }

        public void Init(FlowPanel flowPanel, ProjectAlignment projectAlignment)
        {
            flow = flowPanel;
            alignment = projectAlignment;
        }

        private void Start()
        {
            model = FindAnyObjectByType<ModelOverlay>();
            anchors = FindAnyObjectByType<SpatialAnchorManager>();
        }

        /// <summary>
        /// At start: the saved mode when the app was used less than 30 minutes ago, otherwise
        /// the question. <paramref name="then"/> runs once the mode is settled.
        /// </summary>
        public IEnumerator SettleAtStart(Action then)
        {
            if (WorkMode.TryRestore())
            {
                Apply(WorkMode.Current, null);
                then?.Invoke();
                yield break;
            }
            float until = Time.realtimeSinceStartup + AnchorWaitSeconds;
            while (anchors != null && !anchors.IsInitialized && Time.realtimeSinceStartup < until) yield return null;
            while (UiState.AnyModal || Onboarding.HoldsMenu) yield return null;
            Ask(then);
        }

        /// <summary>From the menu's "Λειτουργία" row, or on return after a long pause.</summary>
        public void Ask(Action then = null)
        {
            if (flow == null) { then?.Invoke(); return; }
            afterChoice = then;
            asking = true;
            IsAsking = true;
            ShowModes();
        }

        private void ShowModes()
        {
            bool found = alignment != null && alignment.HasProject && !alignment.IsPractice
                         && alignment.IsAligned && !alignment.NeedsCheck;
            string project = alignment != null && alignment.HasProject && !alignment.IsPractice ? alignment.ProjectName : null;
            var items = new List<FlowItem>
            {
                new FlowItem
                {
                    Label = L.T("Πεδίο", "Field"),
                    Sub = L.T("Ευθυγράμμιση · μοντέλο και heatmap πάνω στην κατασκευή · μετρήσεις",
                        "Alignment · model and heatmap on the structure · measurements"),
                    Value = found ? L.T("βρέθηκε εδώ", "found here") : null,
                    Icon = "target", Color = Brand.Primary, Tag = WorkModeKind.Field
                },
                new FlowItem
                {
                    Label = L.T("Γραφείο", "Office"),
                    Sub = L.T("Μακέτα ή 1:1 · χωρίς ευθυγράμμιση · χωρίς μετρήσεις",
                        "Tabletop or life size · no alignment · no measurements"),
                    Icon = "displacement", Color = Brand.Info, Tag = WorkModeKind.Office
                },
                new FlowItem
                {
                    Label = L.T("Εξάσκηση", "Practice"),
                    Sub = L.T("Τυπωμένοι στόχοι P1 και P2 · χωρίς Terraweb", "Printed targets P1 and P2 · no Terraweb"),
                    Icon = "sensor-point", Color = Brand.Control, Tag = "practice"
                }
            };
            // Field when the saved alignment was found in this room; else the mode last used.
            int initial = found ? 0
                : WorkMode.IsOffice || (WorkMode.Current == WorkModeKind.None && project == null) ? 1 : 0;
            string title = project != null
                ? L.T($"Πού δουλεύεις τώρα; · {project}", $"Where are you working? · {project}")
                : L.T("Πού δουλεύεις τώρα;", "Where are you working?");
            flow.ShowList(L.T("Λειτουργία", "Mode"), title, items, Picked,
                // B keeps the mode there is (or takes the suggestion): the app never runs without one.
                () => Picked(WorkMode.Current != WorkModeKind.None ? FindTag(items, WorkMode.Current) : items[initial]),
                initial);
        }

        private static FlowItem FindTag(List<FlowItem> items, object tag)
        {
            foreach (FlowItem i in items) if (Equals(i.Tag, tag)) return i;
            return items[0];
        }

        private void Picked(FlowItem item)
        {
            if (item == null) return;
            if (Equals(item.Tag, WorkModeKind.Office) && model != null && model.IsLoaded)
            {
                ShowOfficeViews();
                return;
            }
            if (Equals(item.Tag, "practice")) Finish(WorkModeKind.Office, null, practice: true);
            else Finish((WorkModeKind)item.Tag, null);
        }

        private void ShowOfficeViews()
        {
            int last = Mathf.Clamp(PlayerPrefs.GetInt(OfficeViewKey, 1), 1, 2);
            var items = new List<FlowItem>
            {
                new FlowItem
                {
                    Label = L.T("Μακέτα", "Tabletop"),
                    Sub = L.T("Μικρογραφία μπροστά σου · grip: μετακίνηση και περιστροφή", "Miniature in front of you · grip: move and turn"),
                    Value = last == 1 ? L.T("τελευταία φορά", "last time") : null,
                    Icon = "displacement", Color = Brand.Info, Tag = ModelOverlay.ViewMode.Tabletop
                },
                new FlowItem
                {
                    Label = L.T("1:1 γύρω μου", "Life size around me"),
                    Sub = L.T("Φυσικό μέγεθος · μένεις ακίνητος, κινείσαι με τα sticks", "Full size · stay put, move with the sticks"),
                    Value = last == 2 ? L.T("τελευταία φορά", "last time") : null,
                    Icon = "sensor-point", Color = Brand.Info, Tag = ModelOverlay.ViewMode.LifeSize
                }
            };
            string name = model != null && !string.IsNullOrEmpty(model.LoadedName) ? model.LoadedName : "";
            flow.ShowList(L.T("Γραφείο", "Office"), L.T($"Πώς να δεις το μοντέλο; {name}", $"How to see the model? {name}").Trim(),
                items, i => Finish(WorkModeKind.Office, (ModelOverlay.ViewMode)i.Tag), ShowModes, last - 1);
        }

        private void Finish(WorkModeKind mode, ModelOverlay.ViewMode? view, bool practice = false)
        {
            if (view.HasValue) PlayerPrefs.SetInt(OfficeViewKey, view == ModelOverlay.ViewMode.LifeSize ? 2 : 1);
            asking = false;
            IsAsking = false;
            flow.Close();
            WorkMode.Set(mode);
            Apply(mode, view);
            if (practice && alignment != null && !PracticeProject.IsActive) PracticeProject.Enter(alignment);
            Action then = afterChoice;
            afterChoice = null;
            then?.Invoke();
        }

        /// <summary>What a mode means for the rest of the app.</summary>
        private void Apply(WorkModeKind mode, ModelOverlay.ViewMode? view)
        {
            if (mode == WorkModeKind.Field)
            {
                if (PracticeProject.IsActive) PracticeProject.Leave();
                // The model stands on the real structure once aligned; the room stays visible.
                if (model != null) model.View = ModelOverlay.ViewMode.Auto;
                if (alignment != null) alignment.OpenIfNeeded();
            }
            else if (mode == WorkModeKind.Office)
            {
                bool practice = PracticeProject.IsActive;
                // Office touches must never be logged as site data.
                AnchorFieldTest.Enabled = false;
                if (!practice && alignment != null) AlignmentAccuracy.SetActive(false, alignment);
                if (model != null)
                {
                    ModelOverlay.ViewMode v = view
                        ?? (Mathf.Clamp(PlayerPrefs.GetInt(OfficeViewKey, 1), 1, 2) == 2 ? ModelOverlay.ViewMode.LifeSize : ModelOverlay.ViewMode.Tabletop);
                    model.View = v;
                }
                // No alignment is asked for in the office: put the panel away if it opened by itself.
                if (!practice && alignment != null && alignment.IsModeActive) alignment.SetMode(false);
            }
        }

        private void Update()
        {
            if (Time.unscaledTime >= nextTouch)
            {
                nextTouch = Time.unscaledTime + TouchEverySeconds;
                WorkMode.Touch();
            }
            if (pendingAsk && !asking && !UiState.AnyModal && !Onboarding.HoldsMenu)
            {
                pendingAsk = false;
                Ask();
            }
        }

        // Headset off: note the time. Back on after more than 30 minutes: ask again.
        private void OnApplicationPause(bool paused)
        {
            if (paused) { WorkMode.Touch(flush: true); return; }
            if (WorkMode.Current != WorkModeKind.None && !WorkMode.Recent()) pendingAsk = true;
            else WorkMode.Touch();
        }

        private void OnDestroy() => IsAsking = false;
    }
}
