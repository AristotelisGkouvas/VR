using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Greek and English voice commands with push-to-talk: hold the off-hand thumbstick click,
    /// speak ("βρες Κ5", "δείξε συναγερμούς", "χρονομηχανή", "κλείσε", "find K5", "show alarms",
    /// "close"...), release, and the command runs. Both languages are accepted whatever the
    /// interface language (a crew may mix them); only the replies follow <see cref="L"/>.
    /// A small tag above the off-hand controller shows "Ακούω…" while listening and the
    /// reply for a moment afterwards. The grammar (<see cref="Parse"/>) and the sensor-name
    /// matching (<see cref="MatchSensor"/>) are pure and engine-independent: the speech engine
    /// is an <see cref="IVoiceRecognizer"/> — Wit.ai when the Meta Voice SDK is installed,
    /// else the in-app keyboard so the whole path can be tried today.
    /// </summary>
    public class VoiceCommands : MonoBehaviour
    {
        private const string PrefEnabled = "ui.voice";
        private const float TagW = 300f, TagH = 48f;
        private const float MetersPerPixel = 0.0006f;
        private const float TagLift = 0.12f;          // metres above the off-hand controller
        private const float ReplySeconds = 2.5f;
        private const float MaxHoldSeconds = 15f;     // a stuck button must not stream forever

        public enum Kind { None, Find, ShowAlarms, TimeMachine, Menu, Close, Labels, Stop, Align, Route, Undo, ClearAll }

        public struct Command
        {
            public Kind Kind;
            /// <summary>Sensor name for Find, free text for Route, else empty.</summary>
            public string Argument;
            public Command(Kind kind, string argument) { Kind = kind; Argument = argument ?? ""; }
        }

        private static bool? enabledPref;

        /// <summary>Master switch (main menu), persisted; default on.</summary>
        public static bool Enabled
        {
            get => enabledPref ?? (bool)(enabledPref = PlayerPrefs.GetInt(PrefEnabled, 1) == 1);
            set { enabledPref = value; PlayerPrefs.SetInt(PrefEnabled, value ? 1 : 0); Prefs.Save(); }
        }

        /// <summary>
        /// Hook for the inspection-route feature: receives the words after "διαδρομή" /
        /// "επιθεώρηση". Null until that feature is wired, in which case the command replies
        /// that it does not exist yet.
        /// </summary>
        public Action<string> OnRouteRequested;

        public static bool IsListening { get; private set; }

        private MarkerAppController app;
        private ProjectAlignment alignment;
        private MarkerManager markers;
        private SensorNavigator navigator;
        private DisplacementView displacement;
        private MainMenu menu;
        private FlowPanel flowPanel;
        private SensorCard sensorCard;
        // Not passed to Init (public signature kept): looked up on first use.
        private InspectionRoute route;
        private InspectionRoute Route => route != null ? route : (route = FindAnyObjectByType<InspectionRoute>());

        private IVoiceRecognizer recognizer;
        private bool usesKeyboard;
        private float listeningSince;

        // Indicator
        private RectTransform tag;
        private Image dot;
        private TextMeshProUGUI tagText;
        private float showUntil;          // reply visible until this time; 0 = hidden
        private int tagState = -1;        // 0 hidden, 1 listening, 2 reply — text set on change only
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        // Status for the menu row, rebuilt only when its inputs change.
        private string statusText = "";
        private int statusKey = int.MinValue;

        // ------------------------------------------------------------------
        //  Setup
        // ------------------------------------------------------------------

        public void Init(MarkerAppController controller, ProjectAlignment projectAlignment, MarkerManager markerManager,
            SensorNavigator sensorNavigator, DisplacementView displacementView, MainMenu mainMenu, FlowPanel flow)
        {
            app = controller;
            alignment = projectAlignment;
            markers = markerManager;
            navigator = sensorNavigator;
            displacement = displacementView;
            menu = mainMenu;
            flowPanel = flow;
            sensorCard = FindAnyObjectByType<SensorCard>();

            var wit = new WitRecognizer(transform);
            if (wit.Available) { recognizer = wit; usesKeyboard = false; }
            else
            {
                Debug.Log("[Voice] " + wit.Status + " — using the keyboard fallback");
                recognizer = new KeyboardRecognizer();
                usesKeyboard = true;
            }
            recognizer.OnTranscript += OnTranscript;
            recognizer.OnError += OnRecognizerError;
            Build();
        }

        private void OnDestroy()
        {
            if (recognizer != null)
            {
                recognizer.OnTranscript -= OnTranscript;
                recognizer.OnError -= OnRecognizerError;
            }
            if (IsListening) IsListening = false;
        }

        /// <summary>"Wit.ai" or "Πληκτρολόγιο" / "Keyboard".</summary>
        public string EngineName => usesKeyboard ? L.T("Πληκτρολόγιο", "Keyboard") : "Wit.ai";

        /// <summary>Menu row value: engine and the button that triggers it, or "Ανενεργό".</summary>
        public string Status
        {
            get
            {
                int key = (Enabled ? 1 : 0) | (Hands.LeftHanded ? 2 : 0) | (usesKeyboard ? 4 : 0) | (L.En ? 8 : 0);
                if (key != statusKey)
                {
                    statusKey = key;
                    // The off-hand is the left controller for a right-handed user.
                    if (L.En) statusText = !Enabled ? "Off" : $"{EngineName} · {(Hands.LeftHanded ? "right" : "left")} stick";
                    else statusText = !Enabled ? "Ανενεργό" : $"{EngineName} · stick {(Hands.LeftHanded ? "δεξ." : "αριστ.")}";
                }
                return statusText;
            }
        }

        // ------------------------------------------------------------------
        //  Push-to-talk
        // ------------------------------------------------------------------

        private void Update()
        {
            if (recognizer == null) return;
#if !MOBILE_PREVIEW
            bool bothConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch)
                                 && OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            if (IsListening && !usesKeyboard)
            {
                // Release ends the session whatever opened or closed meanwhile.
                if (Hands.OffHandStickClickUp() || !bothConnected
                    || Time.unscaledTime - listeningSince > MaxHoldSeconds)
                    EndListening();
            }
            else if (!IsListening)
            {
                // Allowed with the menu up (so "κλείσε" works) but not under a list, card or the
                // keyboard, and never while a panel is being dragged.
                bool menuOnly = MainMenu.IsOpen && !FlowPanel.IsOpen && !SensorCard.IsOpen && !VrKeyboard.IsOpen
                                && !VrPointer.IsDragging;
                bool allowed = Enabled && bothConnected && (!UiState.InputBlocked || menuOnly);
                if (allowed && Hands.OffHandStickClickDown()) BeginListening();
            }
#endif
            UpdateTag();
        }

        private void BeginListening()
        {
            IsListening = true;
            listeningSince = Time.unscaledTime;
            showUntil = 0f;
            Feedback.Play(Feedback.Sound.Tick);
            recognizer.StartListening();
        }

        private void EndListening()
        {
            if (!IsListening) return;
            recognizer.StopListening();
            // The keyboard ends its own session through OnTranscript / OnRecognizerError.
            if (usesKeyboard) return;
            IsListening = false;
            // Wit answers a moment after release: keep the tag up until then (or give up quietly).
            ShowReply("…");
            showUntil = Time.unscaledTime + 8f;
        }

        private void OnTranscript(string text)
        {
            IsListening = false;
            string reply = Execute(text ?? "");
            ShowReply(reply);
        }

        private void OnRecognizerError(string message)
        {
            IsListening = false;
            if (string.IsNullOrEmpty(message)) { showUntil = 0f; return; } // cancelled
            Feedback.Play(Feedback.Sound.Warn);
            ShowReply(message);
        }

        // ------------------------------------------------------------------
        //  Execution
        // ------------------------------------------------------------------

        /// <summary>Runs the utterance and returns the reply shown on the tag.</summary>
        private string Execute(string utterance)
        {
            Command cmd = Parse(utterance);
            // A bare sensor name ("Κ5") without a verb still means "find it".
            if (cmd.Kind == Kind.None && alignment != null && alignment.HasProject
                && MatchSensor(utterance, alignment.Points) != null)
                cmd = new Command(Kind.Find, utterance);

            switch (cmd.Kind)
            {
                case Kind.Find: return Find(cmd.Argument);
                case Kind.ShowAlarms: return ShowAlarms();
                case Kind.TimeMachine:
                    if (displacement == null) return L.T("Δεν υπάρχει χρονομηχανή", "No time machine");
                    string why = displacement.Unavailable();
                    if (why != null && !DisplacementView.IsActive) { Feedback.Play(Feedback.Sound.Warn); return why; }
                    displacement.Toggle();
                    return DisplacementView.IsActive ? L.T("Χρονομηχανή", "Time machine") : L.T("Χρονομηχανή: κλείσιμο", "Time machine: closed");
                case Kind.Menu:
                    if (menu == null) return L.T("Δεν υπάρχει μενού", "No menu");
                    Feedback.Play(Feedback.Sound.Click);
                    menu.Toggle();
                    return MainMenu.IsOpen ? L.T("Μενού", "Menu") : L.T("Μενού: κλείσιμο", "Menu: closed");
                case Kind.Close: return CloseTopmost();
                case Kind.Labels:
                    Marker.LabelsVisible = !Marker.LabelsVisible;
                    Feedback.Play(Feedback.Sound.Click);
                    return Marker.LabelsVisible ? L.T("Ετικέτες: ναι", "Labels: on") : L.T("Ετικέτες: όχι", "Labels: off");
                case Kind.Stop:
                {
                    Feedback.Play(Feedback.Sound.Click);
                    // Stop words outrank route words in Parse, so "σταμάτα τη διαδρομή" lands here,
                    // never in OnRouteRequested: naming the route stops it whatever is on top.
                    string said = (utterance ?? "").ToLowerInvariant();
                    bool namesRoute = said.Contains("διαδρομ") || said.Contains("επιθεώρ") || said.Contains("επιθεωρ")
                        || said.Contains("route") || said.Contains("inspect");
                    if (namesRoute && InspectionRoute.IsActive && Route != null) { Route.Stop(); return L.T("Διαδρομή: διακοπή", "Route: stopped"); }
                    // A bare "σταμάτα" ends only the topmost, in B's order (UiState.Context):
                    // stopping navigation must not also end the inspection with a report.
                    if (SensorNavigator.IsActive) { navigator?.Stop(); return L.T("Πλοήγηση: διακοπή", "Navigation: stopped"); }
                    if (DisplacementView.IsActive) { displacement?.Close(); return L.T("Χρονομηχανή: κλείσιμο", "Time machine: closed"); }
                    if (InspectionRoute.IsActive && Route != null) { Route.Stop(); return L.T("Διαδρομή: διακοπή", "Route: stopped"); }
                    return L.T("Διακοπή", "Stopped");
                }
                case Kind.Align:
                    if (alignment == null || !alignment.HasProject) { Feedback.Play(Feedback.Sound.Warn); return L.T("Φόρτωσε πρώτα έργο", "Load a project first"); }
                    Feedback.Play(Feedback.Sound.Click);
                    alignment.SetMode(true);
                    return L.T("Ευθυγράμμιση", "Alignment");
                case Kind.Route:
                    if (OnRouteRequested == null) { Feedback.Play(Feedback.Sound.Warn); return L.T("Δεν υπάρχει ακόμα", "Not available yet"); }
                    Feedback.Play(Feedback.Sound.Click);
                    OnRouteRequested(cmd.Argument);
                    return L.T("Διαδρομή επιθεώρησης", "Inspection route");
                case Kind.Undo:
                    if (app == null) return L.T("Δεν γίνεται", "Can't do that");
                    app.Undo();
                    return L.T("Αναίρεση", "Undo");
                case Kind.ClearAll:
                    // Too destructive for a misheard word: the menu asks for a second press.
                    Feedback.Play(Feedback.Sound.Warn);
                    return L.T("Μόνο από το μενού", "Only from the menu");
                default:
                    Feedback.Play(Feedback.Sound.Warn);
                    return L.En ? "Didn't understand: “" + utterance.Trim() + "”" : "Δεν κατάλαβα: «" + utterance.Trim() + "»";
            }
        }

        private string Find(string spoken)
        {
            if (alignment == null || !alignment.HasProject) { Feedback.Play(Feedback.Sound.Warn); return L.T("Φόρτωσε πρώτα έργο", "Load a project first"); }
            ProjectPoint p = MatchSensor(spoken, alignment.Points);
            if (p == null) { Feedback.Play(Feedback.Sound.Warn); return L.En ? "Not found: “" + spoken + "”" : "Δεν βρέθηκε: «" + spoken + "»"; }
            return NavigateTo(p);
        }

        private string NavigateTo(ProjectPoint p)
        {
            if (!alignment.IsAligned) { Feedback.Play(Feedback.Sound.Warn); return L.T("Πρώτα ευθυγράμμιση", "Align first"); }
            if (navigator == null) return L.T("Δεν υπάρχει πλοήγηση", "No navigation");
            navigator.Navigate(ProjectAlignment.MarkerPrefix + p.id, p);
            return L.T("Πλοήγηση: ", "Navigating: ") + NameOf(p);
        }

        private string ShowAlarms()
        {
            if (alignment == null || !alignment.HasProject || flowPanel == null) { Feedback.Play(Feedback.Sound.Warn); return L.T("Φόρτωσε πρώτα έργο", "Load a project first"); }
            var list = new List<FlowItem>();
            // Alarms first, then alerts, each in project order.
            for (int tier = 0; tier < 2; tier++)
            {
                bool wantAlarm = tier == 0;
                foreach (ProjectPoint p in alignment.Points)
                {
                    if (p == null) continue;
                    bool isAlarm = string.Equals(p.alertState, "alarm", StringComparison.OrdinalIgnoreCase);
                    bool isAlert = string.Equals(p.alertState, "alert", StringComparison.OrdinalIgnoreCase);
                    if (wantAlarm ? !isAlarm : !isAlert) continue;
                    list.Add(new FlowItem
                    {
                        Label = NameOf(p),
                        Sub = isAlarm ? L.T("Συναγερμός", "Alarm") : L.T("Προειδοποίηση", "Alert"),
                        Value = "",
                        Icon = string.IsNullOrEmpty(p.icon) ? null : p.icon,
                        Color = isAlarm ? Brand.Danger : Brand.Warning,
                        Tag = p,
                    });
                }
            }
            if (list.Count == 0) { Feedback.Play(Feedback.Sound.Ping); return L.T("Κανένας συναγερμός", "No alarms"); }

            Feedback.Play(Feedback.Sound.Click);
            if (MainMenu.IsOpen) menu?.Close();
            flowPanel.ShowList(L.T("ΦΩΝΗΤΙΚΗ ΕΝΤΟΛΗ", "VOICE COMMAND"), L.T("Συναγερμοί", "Alarms"), list,
                item =>
                {
                    flowPanel.Close();
                    if (item?.Tag is ProjectPoint picked) ShowReply(NavigateTo(picked));
                },
                () => flowPanel.Close());
            return L.En ? (list.Count == 1 ? "1 alarm" : list.Count + " alarms")
                : list.Count == 1 ? "1 συναγερμός" : list.Count + " συναγερμοί";
        }

        /// <summary>"κλείσε" / "close": the panel in front first; with none up, whatever runs in the world.</summary>
        private string CloseTopmost()
        {
            Feedback.Play(Feedback.Sound.Click);
            string closed = L.T("Κλείσιμο", "Closed");
            if (VrKeyboard.IsOpen) { VrKeyboard.CancelOpen(); return closed; }
            if (FlowPanel.IsOpen && flowPanel != null) { flowPanel.Close(); return closed; }
            if (SensorCard.IsOpen && sensorCard != null) { sensorCard.Close(); return closed; }
            if (MainMenu.IsOpen && menu != null) { menu.Close(); return L.T("Μενού: κλείσιμο", "Menu: closed"); }
            bool any = false;
            if (SensorNavigator.IsActive && navigator != null) { navigator.Stop(); any = true; }
            if (DisplacementView.IsActive && displacement != null) { displacement.Close(); any = true; }
            else if (!any && InspectionRoute.IsActive && Route != null) { Route.Stop(); any = true; }
            if (alignment != null && alignment.IsModeActive) { alignment.SetMode(false); any = true; }
            return any ? closed : L.T("Τίποτα ανοιχτό", "Nothing open");
        }

        private static string NameOf(ProjectPoint p) => string.IsNullOrEmpty(p.label) ? p.id : p.label;

        // ------------------------------------------------------------------
        //  Grammar
        // ------------------------------------------------------------------

        // Each keyword list is matched against the normalised utterance (see Normalize). Greek
        // stems first, then English, then greeklish; the Latin forms also let the keyboard
        // fallback (Latin keys only) drive it. English is always accepted, whatever L.Current:
        // a crew may mix languages. Not display text: never pass these through L.T.
        // Longer phrases before their prefixes ("navigate to" before "navigate"): on a tie the
        // first listed wins in ArgumentAfter.
        private static readonly string[] FindWords = { "βρεσ", "βρει", "πηγαινε", "πλοηγηση", "δειξε μου το", "δειξε μου", "που ειναι", "vres", "find", "go to", "goto", "navigate to", "navigate", "take me to", "show me", "show", "where is", "where's", "wheres", "locate", "pigaine" };
        private static readonly string[] AlarmWords = { "συναγερμ", "προειδοποι", "alarm", "alert", "warning", "synagerm" };
        private static readonly string[] TimeWords = { "χρονομηχαν", "μετακινησ", "time machine", "displacement", "movement", "xronomixan", "hronomihan" };
        private static readonly string[] MenuWords = { "μενου", "menu", "menou" };
        // No "hide" here: "hide labels" must reach LabelWords, and Close is tested first.
        private static readonly string[] CloseWords = { "κλεισε", "κλεισιμο", "close", "go back", "back", "exit", "dismiss", "kleise" };
        private static readonly string[] LabelWords = { "ετικετ", "label", "etiket" };
        private static readonly string[] StopWords = { "σταματα", "στοπ", "διακοπη", "stop", "cancel", "halt", "stamata", "diakopi" };
        private static readonly string[] AlignWords = { "ευθυγραμμ", "align", "calibrat", "efthygram", "eythygram" };
        // "inspection" before "inspect", so StripKeywords leaves no "ion" behind.
        private static readonly string[] RouteWords = { "διαδρομη", "επιθεωρηση", "route", "inspection", "inspect", "diadromi", "epitheorisi" };
        private static readonly string[] UndoWords = { "αναιρεση", "undo", "anairesi" };
        private static readonly string[] ClearWords = { "καθαρισ", "clear", "delete", "remove", "katharis" };
        private static readonly string[] AllWords = { "ολ", "all" };
        // Filler between the verb and the sensor name: "πήγαινε στον αισθητήρα Κ5", "go to sensor K5".
        private static readonly string[] Fillers = { "στο", "στη", "στην", "στον", "στουσ", "το", "τον", "την", "τη", "τα", "μου", "σε", "προσ", "αισθητηρα", "αισθητηρασ", "σημειο", "σταθμο", "to", "the", "sensor", "point", "station" };

        /// <summary>Turn one utterance into a command; <see cref="Kind.None"/> when nothing matched.</summary>
        public static Command Parse(string utterance)
        {
            string n = Normalize(utterance);
            if (n.Length == 0) return new Command(Kind.None, "");

            // Destructive and "exit" words first, so "κλείσε το μενού" closes instead of opening.
            if (ContainsAny(n, ClearWords) && ContainsAny(n, AllWords)) return new Command(Kind.ClearAll, "");
            if (ContainsAny(n, StopWords)) return new Command(Kind.Stop, "");
            if (ContainsAny(n, CloseWords)) return new Command(Kind.Close, "");
            if (ContainsAny(n, UndoWords)) return new Command(Kind.Undo, "");
            if (ContainsAny(n, MenuWords)) return new Command(Kind.Menu, "");
            if (ContainsAny(n, LabelWords)) return new Command(Kind.Labels, "");
            if (ContainsAny(n, AlignWords)) return new Command(Kind.Align, "");
            if (ContainsAny(n, TimeWords)) return new Command(Kind.TimeMachine, "");
            if (ContainsAny(n, RouteWords)) return new Command(Kind.Route, StripKeywords(n, RouteWords));

            string arg = ArgumentAfter(n, FindWords);
            bool alarms = ContainsAny(n, AlarmWords);
            // "δείξε συναγερμούς" / "συναγερμοί": alarms, unless a sensor name was actually asked for.
            if (alarms && (arg == null || arg.Length == 0 || ContainsAny(arg, AlarmWords)))
                return new Command(Kind.ShowAlarms, "");
            if (arg != null && arg.Length > 0) return new Command(Kind.Find, arg);
            return new Command(Kind.None, "");
        }

        /// <summary>
        /// Lower-case, accents and diaeresis stripped, final sigma folded, spaces collapsed —
        /// what a recogniser returns varies in all of these.
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool pendingSpace = false;
            foreach (char raw in s.ToLowerInvariant())
            {
                char c = FoldGreek(raw);
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static char FoldGreek(char c)
        {
            switch (c)
            {
                case 'ά': return 'α';
                case 'έ': return 'ε';
                case 'ή': return 'η';
                case 'ί': case 'ϊ': case 'ΐ': return 'ι';
                case 'ό': return 'ο';
                case 'ύ': case 'ϋ': case 'ΰ': return 'υ';
                case 'ώ': return 'ω';
                case 'ς': return 'σ';
                default: return c;
            }
        }

        private static bool ContainsAny(string n, string[] words)
        {
            foreach (string w in words) if (IndexOfKeyword(n, w) >= 0) return true;
            return false;
        }

        // Short English words that are also the start of other words ("backfill", "showroom",
        // "exiting"): these must stand alone. Other Latin keywords are stems ("calibrat",
        // "label" → "labels") and only need to start a word.
        private static readonly HashSet<string> WholeWords = new HashSet<string>
        {
            "back", "go back", "halt", "exit", "show", "show me", "stop", "all", "undo", "menu",
            "find", "close", "clear", "cancel", "route"
        };

        /// <summary>
        /// Where the keyword occurs, or -1. Greek stems match anywhere, as before; a Latin
        /// keyword must start a word, so "halt" does not fire inside "asphalt".
        /// </summary>
        private static int IndexOfKeyword(string n, string w)
        {
            if (w.Length == 0 || w[0] > 0x7f) return n.IndexOf(w, StringComparison.Ordinal);
            bool whole = WholeWords.Contains(w);
            for (int i = n.IndexOf(w, StringComparison.Ordinal); i >= 0; i = n.IndexOf(w, i + 1, StringComparison.Ordinal))
            {
                int end = i + w.Length;
                if (i > 0 && n[i - 1] != ' ') continue;
                if (whole && end < n.Length && n[end] != ' ') continue;
                return i;
            }
            return -1;
        }

        /// <summary>Text after the first matching verb, with filler words removed; null when no verb.</summary>
        private static string ArgumentAfter(string n, string[] words)
        {
            int best = -1, bestEnd = 0;
            foreach (string w in words)
            {
                int i = IndexOfKeyword(n, w);
                if (i < 0 || (best >= 0 && i >= best)) continue;
                best = i;
                bestEnd = i + w.Length;
            }
            if (best < 0) return null;
            return StripFillers(n.Substring(bestEnd));
        }

        private static string StripKeywords(string n, string[] words)
        {
            foreach (string w in words) n = n.Replace(w, " ");
            return StripFillers(n);
        }

        private static string StripFillers(string s)
        {
            string[] tokens = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder(s.Length);
            foreach (string t in tokens)
            {
                if (Array.IndexOf(Fillers, t) >= 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Sensor names
        // ------------------------------------------------------------------

        // Spelled-out letters and digits, as a recogniser writes them, to the characters used
        // in sensor ids ("κάπα πέντε" → "k5"). Whole tokens only, so "πι" never bites "πιεζόμετρο".
        private static readonly Dictionary<string, string> SpokenTokens = new Dictionary<string, string>
        {
            { "αλφα", "a" }, { "βητα", "b" }, { "γαμα", "g" }, { "γαμμα", "g" }, { "δελτα", "d" }, { "εψιλον", "e" },
            { "ζητα", "z" }, { "ητα", "h" }, { "θητα", "th" }, { "γιωτα", "i" }, { "καπα", "k" }, { "λαμδα", "l" },
            { "λαμβδα", "l" }, { "μι", "m" }, { "μυ", "m" }, { "νι", "n" }, { "νυ", "n" }, { "ξι", "x" }, { "ομικρον", "o" },
            { "πι", "p" }, { "ρο", "r" }, { "σιγμα", "s" }, { "ταυ", "t" }, { "υψιλον", "y" }, { "φι", "f" }, { "χι", "ch" },
            { "ψι", "ps" }, { "ωμεγα", "w" },
            { "μηδεν", "0" }, { "ενα", "1" }, { "δυο", "2" }, { "τρια", "3" }, { "τεσσερα", "4" }, { "πεντε", "5" },
            { "εξι", "6" }, { "εφτα", "7" }, { "επτα", "7" }, { "οχτω", "8" }, { "οκτω", "8" }, { "εννια", "9" },
            { "εννεα", "9" }, { "δεκα", "10" }, { "εντεκα", "11" }, { "δωδεκα", "12" },
        };

        /// <summary>
        /// The sensor the user named, or null. Both sides are folded to one key (lower-case,
        /// no accents, spelled letters and digits replaced, look-alike Greek letters
        /// transliterated, separators removed) so "κ5", "Κάπα πέντε" and "K-5" all meet "K5".
        /// </summary>
        public static ProjectPoint MatchSensor(string spoken, IReadOnlyList<ProjectPoint> points)
        {
            if (points == null || points.Count == 0) return null;
            string key = SensorKey(spoken);
            if (key.Length == 0) return null;

            ProjectPoint best = null;
            int bestScore = 0, bestLen = int.MaxValue;
            foreach (ProjectPoint p in points)
            {
                if (p == null) continue;
                Consider(p, SensorKey(p.id), key, ref best, ref bestScore, ref bestLen);
                Consider(p, SensorKey(p.label), key, ref best, ref bestScore, ref bestLen);
            }
            return best;
        }

        // Score 3 exact, 2 prefix, 1 close (Levenshtein <= 2 on short names); ties go to the shorter name.
        private static void Consider(ProjectPoint p, string candidate, string key,
            ref ProjectPoint best, ref int bestScore, ref int bestLen)
        {
            if (candidate.Length == 0) return;
            int score;
            if (candidate == key) score = 3;
            else if (candidate.StartsWith(key, StringComparison.Ordinal) && key.Length >= 2) score = 2;
            else if (candidate.Length <= 8 && key.Length <= 8 && Levenshtein(candidate, key) <= 2) score = 1;
            else return;
            if (score > bestScore || (score == bestScore && candidate.Length < bestLen))
            {
                best = p;
                bestScore = score;
                bestLen = candidate.Length;
            }
        }

        /// <summary>Normalised, spelled tokens replaced, Greek look-alikes transliterated, separators dropped.</summary>
        public static string SensorKey(string s)
        {
            string n = Normalize(s);
            if (n.Length == 0) return "";
            var sb = new StringBuilder(n.Length);
            foreach (string token in n.Split(' '))
            {
                if (token.Length == 0) continue;
                if (SpokenTokens.TryGetValue(token, out string mapped)) { sb.Append(mapped); continue; }
                foreach (char c in token)
                {
                    if (c == '.' || c == '-' || c == '_' || c == '/' || c == ',' || c == '\'' || c == '"') continue;
                    sb.Append(Transliterate(c));
                }
            }
            return sb.ToString();
        }

        // Greek letters a recogniser substitutes for the Latin ones in ids: "Κ5" for "K5".
        private static char Transliterate(char c)
        {
            switch (c)
            {
                case 'κ': return 'k';
                case 'α': return 'a';
                case 'β': return 'b';
                case 'τ': return 't';
                case 'π': return 'p';
                case 'ε': return 'e';
                case 'η': return 'h';
                case 'ι': return 'i';
                case 'μ': return 'm';
                case 'ν': return 'n';
                case 'ο': return 'o';
                case 'ρ': return 'r';
                case 'χ': return 'x';
                case 'ζ': return 'z';
                case 'υ': return 'y';
                default: return c;
            }
        }

        private static int Levenshtein(string a, string b)
        {
            int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Mathf.Min(Mathf.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                int[] t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        // ------------------------------------------------------------------
        //  Indicator
        // ------------------------------------------------------------------

        private void Build()
        {
            if (tag != null) return;
            tag = VrUi.CreateCanvas("VoiceTag", transform, new Vector2(TagW, TagH), MetersPerPixel);
            // Pivot at the centre: the canvas is positioned by its middle above the controller.
            tag.pivot = new Vector2(0.5f, 0.5f);
            VrUi.Card("Card", tag, 0, 0, TagW, TagH, Brand.Surface.WithAlpha(0.96f), Brand.BorderStrong, 24);
            dot = VrUi.Icon("Dot", tag, 16, (TagH - 14f) / 2f, 14f, Brand.DiscSprite, Brand.Danger);
            tagText = VrUi.Text("Text", tag, 40, 0, TagW - 52, TagH, "", 18f, Brand.Fg, true);
            tag.gameObject.SetActive(false);
        }

        private void ShowReply(string text)
        {
            if (tagText == null) return;
            tagText.text = text ?? "";
            tagText.color = Brand.Fg;
            showUntil = Time.unscaledTime + ReplySeconds;
            tagState = 2;
            dot.gameObject.SetActive(false);
            tag.gameObject.SetActive(true);
        }

        private void UpdateTag()
        {
            if (tag == null) return;
            int state = IsListening ? 1 : showUntil > Time.unscaledTime ? 2 : 0;
            if (state != tagState)
            {
                tagState = state;
                tag.gameObject.SetActive(state != 0);
                if (state == 1)
                {
                    tagText.text = usesKeyboard ? L.T("Πληκτρολόγηση…", "Typing…") : L.T("Ακούω…", "Listening…");
                    tagText.color = Brand.Fg;
                    dot.gameObject.SetActive(true);
                }
            }
            if (state == 0) return;

            if (state == 1)
            {
                float pulse = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 7f);
                dot.color = Brand.Danger.WithAlpha(pulse);
            }
            Place();
        }

        /// <summary>Above the off-hand controller, facing the viewer; in front of the head without one.</summary>
        private void Place()
        {
            Camera cam = Headset.Camera;
            if (cam == null) return;
            Transform t = tag.transform;
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && OVRInput.IsControllerConnected(Hands.OffHand))
            {
                Transform hand = Hands.OffHandAnchor(rig);
                if (hand != null)
                {
                    t.position = hand.position + Vector3.up * TagLift;
                    VrUi.FaceViewer(t, cam.transform);
                    return;
                }
            }
#endif
            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            t.position = cam.transform.position + forward * 0.6f + Vector3.down * 0.12f;
            VrUi.FaceViewer(t, cam.transform);
        }
    }
}
