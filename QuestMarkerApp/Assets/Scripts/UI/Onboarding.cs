using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// First-use guide (docs/proposals/onboarding.md, phase 1). The real first login, project
    /// and alignment are the lesson: a small coach card above the off-hand controller says what
    /// to do next, and each step completes from the app's own state (menu open, logged in,
    /// aligned, card open), so doing things in another order still counts.
    ///
    /// The card is not modal: it never enters UiState.AnyModal and only its own buttons take
    /// the pointer. It hides while the alignment panel is up (that panel has its own stepper)
    /// and while the user walks, and dims under the menu and lists. The only modal moments are
    /// the one-off safety screen and the alignment brief, both plain FlowPanel messages.
    ///
    /// Starts by itself on a first run only; Help › First-use guide starts it over; it can be
    /// skipped a step at a time or ended at any moment. Progress survives a restart.
    /// </summary>
    public class Onboarding : MonoBehaviour
    {
        public enum Step { Safety, Hand, Ray, Menu, Login, Project, Align, Card, Close, Done }
        private enum State { None = 0, Running = 1, Completed = 2, Dismissed = 3 }

        /// <summary>Steps counted on the card: Hand … Done (the safety screen is not a "step").</summary>
        public const int StepTotal = 9;

        private const float Width = 400f, Height = 196f;
        private const float MetersPerPixel = 0.00055f; // as the alignment panel
        // Card centre above the off-hand controller; its lower edge clears the controller labels.
        private const float CardLift = 0.15f, BadgeLift = 0.05f;
        // Without that controller: in the room, lower than the menu so the two never collide.
        private const float CardDistance = 0.7f, CardDrop = 0.25f;
        private const float BadgeDistance = 0.9f, BadgeDrop = 0.45f;
        private const float ConfirmWindow = 3f;
        // Hide while walking: horizontal head speed above this for WalkDelay, back after StillDelay.
        private const float WalkSpeed = 0.6f, WalkDelay = 0.5f, StillDelay = 1f;
        private const float AlignUsable = 0.15f; // ProjectPanel's "large error" line

        // ------------------------------------------------------------------
        //  Storage
        // ------------------------------------------------------------------

        /// <summary>
        /// Whose progress is kept. "device" for now: whether headsets are shared is undecided.
        /// A per-user profile (e.g. a hash of server + username after login) only has to set
        /// this before the guide reads its keys; every key below follows it.
        /// </summary>
        public static string ProfileId = "device";

        private static string Key(string field) => "onb.v1." + ProfileId + "." + field;
        // The safety screen is about the headset (boundary, sun on the lenses), not the person.
        private const string SafetyKey = "onb.v1.device.safety";

        public static Onboarding Instance { get; private set; }
        public static bool IsRunning => Instance != null && Instance.running;
        /// <summary>Until the guide's menu step, the launch-time automatic menu stands down (MarkerAppController).</summary>
        public static bool HoldsMenu => IsRunning && Instance.step <= Step.Menu;
        /// <summary>1 … StepTotal while running.</summary>
        public static int StepNumber => IsRunning ? Mathf.Clamp((int)Instance.step, 1, StepTotal) : 0;

        private ProjectAlignment project;
        private MarkerManager markers;
        private TerrawebClient terraweb;
        private FlowPanel flowPanel;

        private bool running, started;
        private Step step;
        private float stepStarted;
        private bool forceSafety, safetyShown, safetyDone, handChosen, rayClicked, rayHovered, briefShown;
        private string noteEl, noteEn;
        private float noteUntil;
        private float confirmSkipUntil, confirmEndUntil;

        // ---- UI ----
        private Transform cardRoot, badgeRoot;
        private RectTransform canvas, actions;
        private CanvasGroup group;
        private TextMeshProUGUI eyebrow, title, body, skipLabel;
        private readonly Image[] dots = new Image[StepTotal];
        private bool cardPlaced, cardGliding, badgePlaced, badgeGliding;
        private int actionsKey = -1;
        private float nextText;

        // ---- walking ----
        private Vector3 lastHead;
        private bool haveHead, walking;
        private float speed, fastSince = -1f, stillSince = -1f;

        // ---- nearest sensor (card step) ----
        private string nearestName;
        private float nearestDist, nextNearest;
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        public void Init(ProjectAlignment alignment, MarkerManager markerManager, TerrawebClient client, FlowPanel panel)
        {
            Instance = this;
            project = alignment;
            markers = markerManager;
            terraweb = client;
            flowPanel = panel;
            BuildCard();
            BuildBadge();
            L.Changed += Relayout;
            Hands.OnChanged += Relayout;

            var state = (State)PlayerPrefs.GetInt(Key("state"), (int)State.None);
            if (state == State.Running)
                StartCoroutine(Begin((Step)Mathf.Clamp(PlayerPrefs.GetInt(Key("step"), 0), 0, (int)Step.Done)));
            // Only a true first run: an install that has already been through the menu keeps quiet.
            else if (state == State.None && ControllerHints.FirstRun)
                StartCoroutine(Begin(Step.Safety));
        }

        private void OnDestroy()
        {
            L.Changed -= Relayout;
            Hands.OnChanged -= Relayout;
            if (Instance != this) return;
            Instance = null;
            ControllerHints.Spotlight = ControllerHints.Spot.None;
        }

        // Text and buttons name languages, sides and buttons: lay them out again.
        private void Relayout()
        {
            actionsKey = -1;
            nextText = 0f;
        }

        // As the launch menu: wait for tracking to settle so the first screen lands in front.
        private IEnumerator Begin(Step from)
        {
            running = true; // holds the launch menu back from the first frame
            step = from;
            started = false;
            yield return new WaitForSeconds(1.5f);
            if (running && !started) EnterStep(from);
        }

        /// <summary>Help › First-use guide: from the top, safety screen included. Keeps project, login and alignment.</summary>
        public void Restart()
        {
            StopAllCoroutines();
            running = true;
            forceSafety = true;
            handChosen = rayClicked = rayHovered = briefShown = false;
            noteEl = noteEn = null;
            EnterStep(Step.Safety);
        }

        // ------------------------------------------------------------------
        //  Steps
        // ------------------------------------------------------------------

        private bool HasSource => (terraweb != null && terraweb.IsLoggedIn) || (project != null && project.HasProject);

        // What the alignment panel itself would call usable: two points or more, no outlier,
        // not a "large error", not waiting for a re-check.
        private bool AlignedWell =>
            project != null && project.HasProject && project.IsAligned && !project.NeedsCheck
            && project.PlacedCount >= 2 && string.IsNullOrEmpty(project.WorstId)
            && Mathf.Max(project.RmsHorizontal, project.RmsVertical) <= AlignUsable;

        private bool Satisfied()
        {
            switch (step)
            {
                case Step.Safety: return safetyDone;
                case Step.Hand: return handChosen;
                case Step.Ray: return rayClicked;
                case Step.Menu: return MainMenu.IsOpen;
                case Step.Login: return HasSource;
                case Step.Project: return project != null && project.HasProject;
                case Step.Align: return AlignedWell;
                case Step.Card: return SensorCard.IsOpen;
                case Step.Close: return !SensorCard.IsOpen;
                default: return false; // Done: OK, or it fades by itself
            }
        }

        private static ControllerHints.Spot SpotFor(Step s)
        {
            switch (s)
            {
                case Step.Hand: return ControllerHints.Spot.BothTriggers;
                case Step.Ray:
                case Step.Card: return ControllerHints.Spot.Trigger;
                case Step.Menu: return ControllerHints.Spot.Menu;
                case Step.Close: return ControllerHints.Spot.Back;
                default: return ControllerHints.Spot.None;
            }
        }

        private void EnterStep(Step s, bool quiet = false)
        {
            step = s;
            started = true;
            stepStarted = Time.unscaledTime;
            confirmSkipUntil = confirmEndUntil = 0f;
            actionsKey = -1;
            nextText = 0f;
            if (s == Step.Safety) safetyShown = safetyDone = false;
            PlayerPrefs.SetInt(Key("state"), (int)State.Running);
            PlayerPrefs.SetInt(Key("step"), (int)s);
            Prefs.Save();
            ControllerHints.Spotlight = SpotFor(s);

            // Steps the app's state already satisfies pass silently on the next frame.
            if (quiet || s == Step.Safety || Satisfied()) return;
            Feedback.Play(Feedback.Sound.Ping);
#if !MOBILE_PREVIEW
            // The buzz says "this hand" faster than any text.
            if (s == Step.Hand)
            {
                Feedback.Buzz(OVRInput.Controller.LTouch, 0.3f, 0.05f);
                Feedback.Buzz(OVRInput.Controller.RTouch, 0.3f, 0.05f);
            }
            else if (s == Step.Menu) Feedback.Buzz(OVRInput.Controller.LTouch, 0.3f, 0.05f); // the Menu button is on the left
            else if (s != Step.Align && s != Step.Done) Feedback.Buzz(Hands.Dominant, 0.3f, 0.05f);
#endif
        }

        private void Advance(bool byUser)
        {
            if (step == Step.Done) { Finish(State.Completed); return; }
            Step done = step;
            if (done == Step.Safety)
            {
                PlayerPrefs.SetInt(SafetyKey, 1);
                forceSafety = false;
            }
            else if (done == Step.Menu)
                Flash($"Πίσω ή κλείσιμο: {Hands.SecondaryName}. Το Menu πάντα σε βγάζει έξω.",
                    $"Back or close: {Hands.SecondaryName}. Menu always gets you out.", 4f);
            else if (done == Step.Align && PracticeProject.IsActive)
                Flash($"Στην εξάσκηση η βάση είναι {PracticeProject.Baseline:F0} m. Στο εργοτάξιο θέλει τουλάχιστον 5 m.",
                    $"Practice uses a {PracticeProject.Baseline:F0} m baseline. On site, use at least 5 m.", 8f);

            // The alignment plays its own chord as it locks in; elsewhere a soft "done".
            bool success = byUser && done != Step.Align && done != Step.Safety;
            if (success) Feedback.Play(Feedback.Sound.Success, 0.5f);
            EnterStep(done + 1, quiet: success);
        }

        private void Finish(State state)
        {
            running = started = false;
            ControllerHints.Spotlight = ControllerHints.Spot.None;
            PlayerPrefs.SetInt(Key("state"), (int)state);
            Prefs.Save();
            if (cardRoot != null) cardRoot.gameObject.SetActive(false);
        }

        private void Flash(string el, string en, float seconds)
        {
            noteEl = el;
            noteEn = en;
            noteUntil = Time.unscaledTime + seconds;
            nextText = 0f;
        }

        private void Update()
        {
            if (cardRoot == null) return; // before Init
            TrackWalking();
            if (badgeRoot != null && badgeRoot.gameObject.activeSelf != PracticeProject.IsActive)
                badgeRoot.gameObject.SetActive(PracticeProject.IsActive);
            if (!running || !started) return;

            // Something undone underneath (logged out, project gone, points reset): step back.
            if (step > Step.Login && step < Step.Done && !HasSource) { EnterStep(Step.Login); return; }
            if (step > Step.Project && step < Step.Done && !project.HasProject) { EnterStep(Step.Project); return; }
            if (step == Step.Card && !AlignedWell) { EnterStep(Step.Align); return; }

            switch (step)
            {
                case Step.Safety:
                    if (!safetyShown)
                    {
                        if (!forceSafety && PlayerPrefs.GetInt(SafetyKey, 0) == 1) safetyDone = true;
                        else if (!UiState.AnyModal) ShowSafety();
                    }
                    else if (!FlowPanel.IsOpen) safetyDone = true;
                    break;
                case Step.Ray:
                    if (VrPointer.IsHovering) rayHovered = true;
                    break;
                case Step.Align:
                    // A short brief the first time the panel opens, before any touch.
                    if (!briefShown && project.IsModeActive && project.PlacedCount == 0 && !UiState.AnyModal)
                        ShowBrief();
                    break;
                case Step.Done:
                    if (Time.unscaledTime - stepStarted > 8f) { Finish(State.Completed); return; }
                    break;
            }

            if (Satisfied())
            {
                // Satisfied on arrival (already logged in, already aligned…): no fanfare.
                Advance(Time.unscaledTime - stepStarted > 0.4f);
                return;
            }

            RefreshCard();
        }

        // After every Update: the pointer has decided whether this trigger was a button click,
        // and the controllers have their final pose for the frame.
        private void LateUpdate()
        {
            if (cardRoot == null) return;
            if (running && started && step == Step.Hand && !handChosen && Time.unscaledTime - stepStarted > 0.4f
                && !VrPointer.ConsumedThisFrame && !UiState.AnyModal)
            {
#if !MOBILE_PREVIEW
                bool left = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
                bool right = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
#else
                bool left = false, right = Input.GetMouseButtonDown(0);
#endif
                if (left || right)
                {
                    Hands.LeftHanded = left && !right;
                    handChosen = true;
                    Flash($"{Hands.DominantSide}: δείχνει και πατάει. Το άλλο κρατάει λίστες και μενού.",
                        $"{Hands.DominantSide}: points and clicks. The other one holds lists and menus.", 4f);
                }
            }

            Camera cam = Headset.Camera;
            if (cam == null) return;
            if (cardRoot.gameObject.activeSelf)
                Place(cardRoot, cam.transform, CardLift, CardDistance, CardDrop, step != Step.Hand, ref cardPlaced, ref cardGliding);
            if (badgeRoot.gameObject.activeSelf)
                Place(badgeRoot, cam.transform, BadgeLift, BadgeDistance, BadgeDrop, true, ref badgePlaced, ref badgeGliding);
        }

        // ------------------------------------------------------------------
        //  Modal moments
        // ------------------------------------------------------------------

        private void ShowSafety()
        {
            safetyShown = true;
            bool off = BoundaryControl.Suppress;
            string text = L.En
                ? (off ? "The safety boundary is off so you can walk the site. You see the room through the cameras, a little less sharp."
                       : "The safety boundary is on. If you step outside it, the view stops.")
                  + "\n• Stop to read. Walk with your eyes on the ground."
                  + "\n• Keep away from trenches, edges and moving machines."
                  + "\n• Never leave the lenses facing the sun: it burns the display."
                  + "\n\nA: Got it"
                : (off ? "Το όριο ασφαλείας (boundary) είναι κλειστό, για να περπατάς στο εργοτάξιο. Βλέπεις τον χώρο μέσα από τις κάμερες, αλλά λίγο πιο θολά."
                       : "Το όριο ασφαλείας είναι ενεργό. Αν βγεις από αυτό, η εικόνα σταματά.")
                  + "\n• Σταμάτα για να διαβάσεις. Περπάτα με το βλέμμα στο έδαφος."
                  + "\n• Μακριά από σκάμματα, άκρες και μηχανήματα σε κίνηση."
                  + "\n• Μην αφήνεις τα γυαλιά με τους φακούς στον ήλιο: καίγεται η οθόνη."
                  + "\n\nA: Κατάλαβα";
            flowPanel.ShowMessage(L.T("ΑΣΦΑΛΕΙΑ", "SAFETY"), L.T("Οδηγός πρώτης χρήσης", "First-use guide"),
                L.T("Πρόσεχε πού πατάς", "Watch your step"), text, false, flowPanel.Close);
            Feedback.Play(Feedback.Sound.Ping);
        }

        private void ShowBrief()
        {
            briefShown = true;
            bool practice = PracticeProject.IsActive;
            string text = L.En
                ? $"1. Go to the point the panel shows{(practice ? " (target P1)" : "")}.\n"
                  + $"2. Rest the pink tip of the {Hands.DominantSideName} controller {(practice ? "on the centre of the cross" : "on it")}. Hold still.\n"
                  + $"3. Press the trigger. Wrong? {Hands.SecondaryName} undoes it.\n"
                  + (practice ? $"Then the same on P2. Practice uses {PracticeProject.Baseline:F2} m; on site, at least 5 m."
                              : "Two points are needed, at least 5 m apart.")
                  + "\n\nA: Let's go"
                : $"1. Πήγαινε στο σημείο που δείχνει το πάνελ{(practice ? " (στόχος P1)" : "")}.\n"
                  + $"2. Ακούμπα τη ροζ μύτη του {Hands.DominantSideName} χειριστηρίου {(practice ? "στο κέντρο του σταυρού" : "πάνω του")}. Κράτα σταθερά.\n"
                  + $"3. Πάτα Trigger. Λάθος; Το {Hands.SecondaryName} αναιρεί.\n"
                  + (practice ? $"Μετά το ίδιο στο P2. Εδώ η απόσταση είναι {PracticeProject.Baseline:F2} m· στο εργοτάξιο τουλάχιστον 5 m."
                              : "Χρειάζονται 2 σημεία, τουλάχιστον 5 m μακριά.")
                  + "\n\nA: Ξεκινάμε";
            flowPanel.ShowMessage(L.T("ΟΔΗΓΟΣ", "GUIDE"), L.T("Ευθυγράμμιση", "Alignment"),
                L.T("Ακούμπα, μη σημαδεύεις", "Touch, don't aim"), text, false, flowPanel.Close);
        }

        // ------------------------------------------------------------------
        //  Card content
        // ------------------------------------------------------------------

        private void RefreshCard()
        {
            bool show = step != Step.Safety && !project.IsModeActive && !walking;
            if (cardRoot.gameObject.activeSelf != show)
            {
                cardRoot.gameObject.SetActive(show);
                if (show) cardPlaced = false;
            }
            if (!show) return;
            // The user is reading a panel: stay, but step back.
            group.alpha = MainMenu.IsOpen || FlowPanel.IsOpen || VrKeyboard.IsOpen ? 0.4f : 1f;

            int key = (int)step * 4 + (L.En ? 2 : 0) + (HasSource ? 1 : 0);
            if (key != actionsKey)
            {
                actionsKey = key;
                BuildActions();
            }
            if (Time.unscaledTime < nextText) return;
            nextText = Time.unscaledTime + 0.25f;
            if (step == Step.Card) UpdateNearest();

            int n = Mathf.Clamp((int)step, 1, StepTotal);
            Set(eyebrow, Time.unscaledTime < confirmEndUntil
                ? L.T("ΠΑΤΑ ΞΑΝΑ × ΓΙΑ ΤΕΛΟΣ ΟΔΗΓΟΥ", "PRESS × AGAIN TO END THE GUIDE")
                : L.En ? $"GUIDE · {n} OF {StepTotal}" : $"ΟΔΗΓΟΣ · {n} ΑΠΟ {StepTotal}");
            eyebrow.color = Time.unscaledTime < confirmEndUntil ? Brand.Danger : Brand.FgSubtle;
            for (int i = 0; i < dots.Length; i++)
                dots[i].color = i < n - 1 ? Brand.Primary : i == n - 1 ? Brand.Fg : Brand.SurfaceSunken;
            if (skipLabel != null)
                Set(skipLabel, Time.unscaledTime < confirmSkipUntil ? L.T("Πάτα ξανά", "Press again") : L.T("Παράλειψη", "Skip"));

            Content(out string head, out string text);
            if (Time.unscaledTime < noteUntil && noteEl != null)
                text += "\n<color=#8fc93a>" + (L.En ? noteEn : noteEl) + "</color>";
            Set(title, head);
            Set(body, text);
        }

        private static void Set(TextMeshProUGUI t, string s)
        {
            if (t.text != s) t.text = s;
        }

        private void Content(out string head, out string text)
        {
            bool en = L.En;
            bool practice = PracticeProject.IsActive;
            string dom = Hands.DominantSideName;
            switch (step)
            {
                case Step.Hand:
                    head = en ? "Which hand will you point with?" : "Με ποιο χέρι θα δείχνεις;";
                    // Both languages: whoever put the headset on can read one of them.
                    text = en ? "Press the trigger on that hand.\nΓλώσσα / Language:" : "Πάτα το Trigger σε αυτό το χέρι.\nΓλώσσα / Language:";
                    break;
                case Step.Ray:
                    head = en ? "Aim and press" : "Σημάδεψε και πάτα";
                    text = en ? $"Aim at Continue with the ray of the {dom} controller and press the trigger."
                              : $"Στόχευσε το «Συνέχεια» με την ακτίνα του {dom} χειριστηρίου και πάτα Trigger.";
                    if (!rayHovered && Time.unscaledTime - stepStarted > 15f)
                        text += en ? " The ray comes out of its tip." : " Η ακτίνα βγαίνει από τη μύτη του.";
                    break;
                case Step.Menu:
                    head = en ? "Open the menu" : "Άνοιξε το μενού";
                    text = OneController()
                        ? (en ? "Click the stick. Everything starts there." : "Κλικ στο στικ. Από εκεί ξεκινούν όλα.")
                        : (en ? $"Press Menu or {Hands.OffHandPrimaryName}. Everything starts there."
                              : $"Πάτα Menu ή {Hands.OffHandPrimaryName}. Από εκεί ξεκινούν όλα.");
                    break;
                case Step.Login:
                    head = en ? "Log in or practise" : "Σύνδεση ή εξάσκηση";
                    text = FlowPanel.IsOpen
                        ? (en ? "Your Terraweb account. To type, aim at a key and press the trigger."
                              : "Ο λογαριασμός σου στο Terraweb. Γράφεις σημαδεύοντας πλήκτρα και πατώντας Trigger.")
                        : (en ? "Menu › Log in to Terraweb. In the office, with the two printed targets: Practice."
                              : "Μενού › Σύνδεση στο Terraweb. Στο γραφείο, με τους δύο τυπωμένους στόχους: Εξάσκηση.");
                    break;
                case Step.Project:
                    head = en ? "Pick a project" : "Διάλεξε έργο";
                    text = FlowPanel.IsOpen
                        ? (en ? $"Stick up/down to move through the list, {Hands.PrimaryName} to pick."
                              : $"Στικ πάνω/κάτω για τη λίστα, {Hands.PrimaryName} για επιλογή.")
                        : (en ? "Menu › Pick a project." : "Μενού › Διάλεξε έργο.");
                    break;
                case Step.Align:
                    head = en ? "Alignment" : "Ευθυγράμμιση";
                    if (project.IsAligned && !project.NeedsCheck)
                        text = en ? "The error is large. Menu › Alignment: the panel says which point to touch again."
                                  : "Μεγάλο σφάλμα. Μενού › Ευθυγράμμιση: το πάνελ λέει ποιο σημείο να ξαναγγίξεις.";
                    else if (practice)
                        text = en ? $"Lay targets P1 and P2 on the floor, {PracticeProject.Baseline:F2} m apart. Then Menu › Align."
                                  : $"Βάλε τους στόχους P1 και P2 στο πάτωμα, {PracticeProject.Baseline:F2} m μεταξύ τους. Μετά Μενού › Ευθυγράμμιση.";
                    else
                        text = en ? "Touch 2 sensors with the pink tip. Menu › Align."
                                  : "Ακούμπα 2 αισθητήρες με τη ροζ μύτη. Μενού › Ευθυγράμμιση.";
                    break;
                case Step.Card:
                    head = en ? "Look at a sensor" : "Δες έναν αισθητήρα";
                    text = en ? "Aim at an icon and press the trigger." : "Σημάδεψε ένα εικονίδιο και πάτα Trigger.";
                    if (nearestName != null && nearestDist > 8f)
                        text += en ? $" The nearest, {nearestName}, is {nearestDist:F0} m away: walk closer or aim from here."
                                   : $" Ο κοντινότερος, {nearestName}, είναι στα {nearestDist:F0} m: πλησίασε ή σημάδεψε από εδώ.";
                    break;
                case Step.Close:
                    head = en ? "Close the card" : "Κλείσε την καρτέλα";
                    text = en ? $"Press {Hands.SecondaryName}. Or aim at another sensor to switch."
                              : $"Πάτα {Hands.SecondaryName}. Ή σημάδεψε άλλον αισθητήρα για να αλλάξεις.";
                    break;
                default:
                    head = en ? "All set" : "Έτοιμος";
                    text = en ? "Align, open a card, close it: those are the basics. Everything else: Menu › Help."
                              : "Ευθυγράμμιση, καρτέλα, κλείσιμο: αυτά είναι τα βασικά. Τα υπόλοιπα: Μενού › Βοήθεια.";
                    if (practice)
                        text += en ? " Leave practice there too." : " Εκεί και η έξοδος από την εξάσκηση.";
                    break;
            }
        }

        private static bool OneController()
        {
#if !MOBILE_PREVIEW
            return !OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
#else
            return false;
#endif
        }

        private void UpdateNearest()
        {
            if (Time.unscaledTime < nextNearest || markers == null) return;
            nextNearest = Time.unscaledTime + 0.5f;
            Camera cam = Headset.Camera;
            nearestName = null;
            nearestDist = float.MaxValue;
            if (cam == null) return;
            foreach (ProjectPoint p in project.Points)
            {
                if (p == null || !markers.ActiveMarkers.TryGetValue(ProjectAlignment.MarkerPrefix + p.id, out Marker m) || m == null)
                    continue;
                Vector3 d = m.transform.position - cam.transform.position;
                float flat = new Vector2(d.x, d.z).magnitude;
                if (flat >= nearestDist) continue;
                nearestDist = flat;
                nearestName = string.IsNullOrEmpty(p.label) ? p.id : p.label;
            }
        }

        // ------------------------------------------------------------------
        //  Buttons
        // ------------------------------------------------------------------

        private void BuildActions()
        {
            if (actions != null) Destroy(actions.gameObject);
            actions = VrUi.Rect("Actions", canvas, 0, Height - 54f, Width, 46f);
            skipLabel = null;
            const float y = 2f, h = 40f;
            switch (step)
            {
                case Step.Hand:
                    // The current language is the filled one. Pointing hand clicks, so this is
                    // also the first taste of the ray; LateUpdate ignores that trigger.
                    VrUi.Button("Greek", actions, 24, y, 110, h, null, "Ελληνικά",
                        () => L.Current = L.Language.Greek, !L.En);
                    VrUi.Button("English", actions, 142, y, 110, h, null, "English",
                        () => L.Current = L.Language.English, L.En);
                    break;
                case Step.Ray:
                    VrUi.Button("Continue", actions, 24, y, 160, h, "check", L.T("Συνέχεια", "Continue"),
                        () => rayClicked = true, true);
                    break;
                case Step.Login:
                    if (!HasSource)
                        VrUi.Button("Practice", actions, 24, y, 160, h, "pin", L.T("Εξάσκηση", "Practice"),
                            () => PracticeProject.Enter(project));
                    break;
                case Step.Done:
                    VrUi.Button("Ok", actions, 24, y, 110, h, "check", "OK", () => Finish(State.Completed), true);
                    break;
            }
            if (step == Step.Done) return;
            RectTransform skip = VrUi.Button("Skip", actions, Width - 24 - 150, y, 150, h, "chevron-right",
                L.T("Παράλειψη", "Skip"), OnSkip);
            skipLabel = skip.Find("SkipLabel")?.GetComponent<TextMeshProUGUI>();
        }

        // Skip and end ask twice, like the menu's destructive rows.
        private void OnSkip()
        {
            if (Time.unscaledTime >= confirmSkipUntil)
            {
                confirmSkipUntil = Time.unscaledTime + ConfirmWindow;
                Feedback.Play(Feedback.Sound.Warn, 0.5f);
                nextText = 0f;
                return;
            }
            Feedback.Play(Feedback.Sound.Whoosh, 0.6f);
            if (step == Step.Ray) rayClicked = true;
            Advance(false);
        }

        private void OnEnd()
        {
            if (Time.unscaledTime >= confirmEndUntil)
            {
                confirmEndUntil = Time.unscaledTime + ConfirmWindow;
                Feedback.Play(Feedback.Sound.Warn, 0.5f);
                nextText = 0f;
                return;
            }
            Feedback.Play(Feedback.Sound.Whoosh, 0.6f);
            Finish(State.Dismissed);
        }

        // ------------------------------------------------------------------
        //  Build / place
        // ------------------------------------------------------------------

        private void BuildCard()
        {
            cardRoot = new GameObject("CoachCard").transform;
            cardRoot.SetParent(transform, false);
            canvas = VrUi.CreateCanvas("CoachCanvas", cardRoot, new Vector2(Width, Height), MetersPerPixel);
            group = canvas.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            VrUi.Card("Card", canvas, 0, 0, Width, Height, Brand.Surface.WithAlpha(0.97f), Brand.BorderStrong, 22);
            VrUi.Panel("Mark", canvas, 24, 22, 10, 10, Brand.Primary, 5);
            eyebrow = VrUi.Text("Eyebrow", canvas, 42, 14, 190, 26, "", 12f, Brand.FgSubtle, true);
            eyebrow.characterSpacing = 4f;
            for (int i = 0; i < dots.Length; i++)
                dots[i] = VrUi.Panel("Dot" + i, canvas, 236 + i * 12, 23, 7, 7, Brand.SurfaceSunken, 3);
            VrUi.Button("End", canvas, Width - 52, 10, 36, 36, "close", null, OnEnd);
            title = VrUi.Text("Title", canvas, 24, 42, Width - 48, 30, "", 21f, Brand.Fg, true);
            body = VrUi.Text("Body", canvas, 24, 76, Width - 48, 66, "", 14.5f, Brand.FgMuted);
            body.textWrappingMode = TextWrappingModes.Normal;
            body.alignment = TextAlignmentOptions.TopLeft;
            cardRoot.gameObject.SetActive(false);
        }

        // Yellow, both languages, always on while practising: nobody may take these numbers for site data.
        private void BuildBadge()
        {
            badgeRoot = new GameObject("PracticeBadge").transform;
            badgeRoot.SetParent(transform, false);
            RectTransform c = VrUi.CreateCanvas("BadgeCanvas", badgeRoot, new Vector2(300f, 40f), MetersPerPixel);
            VrUi.Panel("Pill", c, 0, 0, 300, 40, Brand.Control, 20);
            VrUi.Text("Text", c, 0, 0, 300, 40, "ΕΞΑΣΚΗΣΗ · PRACTICE", 17f, Brand.MapInk, true,
                TextAlignmentOptions.Center).characterSpacing = 4f;
            badgeRoot.gameObject.SetActive(false);
        }

        // Above the off-hand controller like a watch face; in the room (lazy follow) when that
        // controller is down or, for the handedness step, not yet known.
        private void Place(Transform t, Transform head, float lift, float distance, float drop, bool onHand,
            ref bool placed, ref bool gliding)
        {
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            if (onHand && rig != null && OVRInput.IsControllerConnected(Hands.OffHand)
                && OVRInput.GetControllerPositionTracked(Hands.OffHand))
            {
                t.position = Hands.OffHandAnchor(rig).position + Vector3.up * lift;
                VrUi.FaceViewer(t, head);
                placed = false;
                return;
            }
#endif
            if (!placed)
            {
                t.position = VrUi.InFront(head, distance, drop);
                VrUi.FaceViewer(t, head);
                placed = true;
                gliding = false;
            }
            else VrUi.LazyFollow(t, head, distance, drop, ref gliding);
        }

        // Never walk with the eyes on text: the card goes while the head moves at walking pace.
        private void TrackWalking()
        {
            Camera cam = Headset.Camera;
            float dt = Time.unscaledDeltaTime;
            if (cam == null || dt <= 0f) return;
            Vector3 p = cam.transform.position;
            if (haveHead)
            {
                Vector3 d = p - lastHead;
                d.y = 0f;
                speed = Mathf.Lerp(speed, d.magnitude / dt, 1f - Mathf.Exp(-10f * dt));
            }
            lastHead = p;
            haveHead = true;

            float now = Time.unscaledTime;
            if (speed > WalkSpeed)
            {
                stillSince = -1f;
                if (fastSince < 0f) fastSince = now;
                if (now - fastSince > WalkDelay) walking = true;
            }
            else
            {
                fastSince = -1f;
                if (!walking) return;
                if (stillSince < 0f) stillSince = now;
                if (now - stillSince > StillDelay) walking = false;
            }
        }
    }
}
