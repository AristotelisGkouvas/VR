using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;
using QuestMarkerApp.Models;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// The app's main menu, opened with the left controller's Menu button (or X).
    /// The home page follows the app's state: one big call to action for the next step
    /// (log in → pick a project → align), quick tiles for the field tools once aligned, and
    /// five categories (project, alignment, view, settings, help) that each open a short
    /// page of rows. Stick up/down selects, trigger or A activates, B goes back / closes.
    /// Destructive rows ask for a second press. While open it owns the sticks and buttons
    /// (<see cref="IsOpen"/>).
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        private const float Width = 520f;
        private const float Height = 780f;
        private const float RowH = 42f;
        private const float NavRowH = 50f;
        private const float BodyTop = 100f;
        private const float MetersPerPixel = 0.00052f;
        private const float ConfirmWindow = 3f;

        public static bool IsOpen { get; private set; }

        private MarkerAppController app;
        private ProjectAlignment project;
        private MarkerManager markers;
        private LocalAPIServer server;
        private TerrawebClient terraweb;
        private SpatialAnchorManager anchors;
        private DisplacementView displacement;
        private TerrawebFlow flow;
        private SensorNavigator navigator;
        private HistoryRibbons ribbons;
        private LiveAlerts live;
        private InspectionRoute route;
        private FlowPanel flowPanel;
        private ModelOverlay model;
        private ModelSensors modelSensors;
        // Created after the menu: looked up on first use.
        private VoiceCommands voice;
        private VoiceCommands Voice => voice != null ? voice : (voice = FindAnyObjectByType<VoiceCommands>());
        private LimitBars limitBars;
        private LimitBars Limits => limitBars != null ? limitBars : (limitBars = FindAnyObjectByType<LimitBars>());
        private AnchorFieldTest fieldTest;
        private AnchorFieldTest FieldTest => fieldTest != null ? fieldTest : (fieldTest = FindAnyObjectByType<AnchorFieldTest>());

        private RectTransform root, body;
        private TextMeshProUGUI crumb, title;
        // GetServerUrls enumerates the network interfaces: cache its text and refresh it once a second.
        private string serverText = "";
        private float nextServerRefresh;

        // ---- pages ----
        private enum Page { Home, Project, Align, View, Model, Settings, Help, Controls }
        private Page page = Page.Home;
        // What the home page was built for; it is rebuilt when any of this changes.
        private int builtKey;
        // Row values are formatted strings: refresh them a few times a second, not every frame
        // (dozens of small allocations per frame add up to GC hitches on the Quest).
        private const float ValueRefreshInterval = 0.25f;
        private float nextValueRefresh;
        // Header text as last laid out (crumb.preferredWidth is a full text layout).
        private Page headerPage = (Page)(-1);
        private string headerHome;
        // Re-centring glide in progress (VrUi.LazyFollow).
        private bool gliding;

        // Home state: which call to action (if any) the home page leads with.
        private enum Stage { Login, NoProject, Unaligned, Ready }

        // Everything selectable on the current page, in stick order: tiles, then rows.
        private readonly List<Item> items = new List<Item>();
        private readonly List<ItemView> views = new List<ItemView>();
        private int selected;
        private int confirmIndex = -1;
        private float confirmUntil;
        private float nextStickMove;
        private int openedFrame = -1;

        private class Item
        {
            public string Label;
            public Func<string> Value = () => "";
            public Func<bool> Enabled = () => true;
            public bool Destructive;
            public Action Run;
            public bool Nav;      // category row: chevron, taller, opens a page
            public bool Tile;     // quick tile on the home page
            public bool Cta;      // the big call-to-action button
        }

        private class ItemView
        {
            public Image Background, Accent;
            public TextMeshProUGUI Label, Value;
            public Color NormalBg;
            public bool ShowsConfirm; // Value shows "press again" instead of the row's value
        }

        public void Init(MarkerAppController controller, ProjectAlignment alignment, MarkerManager markerManager,
            LocalAPIServer localServer, TerrawebClient terrawebClient, TerrawebFlow terrawebFlow,
            SensorNavigator sensorNavigator)
        {
            app = controller;
            project = alignment;
            markers = markerManager;
            server = localServer;
            terraweb = terrawebClient;
            flow = terrawebFlow;
            navigator = sensorNavigator;
            anchors = FindAnyObjectByType<SpatialAnchorManager>();
            displacement = FindAnyObjectByType<DisplacementView>();
            ribbons = FindAnyObjectByType<HistoryRibbons>();
            live = FindAnyObjectByType<LiveAlerts>();
            route = FindAnyObjectByType<InspectionRoute>();
            flowPanel = FindAnyObjectByType<FlowPanel>();
            model = FindAnyObjectByType<ModelOverlay>();
            modelSensors = FindAnyObjectByType<ModelSensors>();
            Build();
            root.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------
        //  State
        // ------------------------------------------------------------------

        private Stage CurrentStage()
        {
            // Practice needs no login: it goes straight to aligning the printed targets.
            if ((terraweb == null || !terraweb.IsLoggedIn) && !PracticeProject.IsActive) return Stage.Login;
            if (project == null || !project.HasProject) return Stage.NoProject;
            return project.NeedsAlignment ? Stage.Unaligned : Stage.Ready;
        }

        private string ProjectTitle() =>
            project != null && project.HasProject ? project.ProjectName : L.T("Χωρίς έργο", "No project");

        // ------------------------------------------------------------------
        //  Frame
        // ------------------------------------------------------------------

        private void Build()
        {
            root = VrUi.CreateCanvas("MainMenuCanvas", transform, new Vector2(Width, Height), MetersPerPixel);
            VrUi.Card("Card", root, 0, 0, Width, Height, Brand.Surface.WithAlpha(0.97f), Brand.BorderStrong, 24);
            VrPointer.RegisterDraggable(root, transform);

            VrUi.Panel("Mark", root, 28, 30, 12, 12, Brand.Primary, 6);
            VrUi.Text("Eyebrow", root, 48, 22, 300, 28, "TERRAWEB · QUEST", 14f, Brand.FgSubtle, true)
                .characterSpacing = 8f;
            // Breadcrumb: "Κόνιτσα ›" in subtle ink, then the page name; on the home page just the name.
            crumb = VrUi.Text("Crumb", root, 28, 52, Width - 92, 40, "", 20f, Brand.FgSubtle, true);
            title = VrUi.Text("Title", root, 28, 52, Width - 92, 40, "", 27f, Brand.Fg, true);
            VrUi.Button("Close", root, Width - 64, 22, 40, 40, "close", null, Close);
        }

        private void ClearBody()
        {
            if (body != null) Destroy(body.gameObject);
            body = VrUi.Rect("Body", root, 0, BodyTop, Width, Height - BodyTop);
            items.Clear();
            views.Clear();
            confirmIndex = -1;
        }

        private void ShowPage(Page p, bool keepSelection = false)
        {
            int keep = selected;
            page = p;
            ClearBody();
            switch (p)
            {
                case Page.Home: BuildHome(); break;
                case Page.Project: BuildProject(); break;
                case Page.Align: BuildAlign(); break;
                case Page.View: BuildView(); break;
                case Page.Settings: BuildSettings(); break;
                case Page.Help: BuildHelp(); break;
                case Page.Model: BuildModel(); break;
                case Page.Controls: BuildControls(); break;
            }
            selected = keepSelection && keep < items.Count ? keep : FirstEnabled(0, 1);
            nextValueRefresh = 0f; // new rows: fill their values now
            headerPage = (Page)(-1);
            Refresh();
        }

        // ------------------------------------------------------------------
        //  Home
        // ------------------------------------------------------------------

        // Read every frame while the home page is up: an int, not a formatted string.
        private int HomeKey()
        {
            int s = (int)CurrentStage();
            int placed = project != null ? Mathf.Min(project.PlacedCount, 1000) : 0;
            return s | (placed << 3) | (Hands.LeftHanded ? 1 << 14 : 0) | (PracticeProject.IsActive ? 1 << 15 : 0);
        }

        private void BuildHome()
        {
            Stage stage = CurrentStage();
            builtKey = HomeKey();
            float y = 0f;

            y = BuildChips(y, stage);

            switch (stage)
            {
                case Stage.Login:
                    y = BuildCta(y,
                        L.T("Συνδέσου στο Terraweb για να φορτώσεις τους αισθητήρες ενός έργου στον χώρο.", "Log in to Terraweb to load a project's sensors on site."),
                        L.T("Σύνδεση στο Terraweb", "Log in to Terraweb"),
                        "Server: " + Host() + L.T(" · αλλάζει στη φόρμα", " · change it in the form"),
                        () => { Close(); flow.Begin(); }, null);
                    y = PracticeRow(y);
                    break;
                case Stage.NoProject:
                    y = BuildCta(y,
                        L.T("Διάλεξε έργο και περιοχή. Οι αισθητήρες θα εμφανιστούν στον χώρο.", "Pick a project and area. Its sensors will appear around you."),
                        L.T("Διάλεξε έργο", "Pick a project"), null,
                        () => { Close(); flow.Begin(); }, null);
                    break;
                case Stage.Unaligned:
                {
                    int placed = project.PlacedCount;
                    bool recheck = project.IsAligned && project.NeedsCheck;
                    string text = recheck
                        ? L.T("Η ευθυγράμμιση είναι από προηγούμενη φορά. Ακούμπησε ένα σημείο ελέγχου για επιβεβαίωση.", "This alignment is from an earlier session. Touch a control point to confirm it.")
                        : L.T($"Ακούμπησε 2 αισθητήρες με τη μύτη του {Hands.DominantSideName} χειριστηρίου για να ταιριάξει το έργο με τον χώρο.",
                            $"Touch 2 sensors with the tip of the {Hands.DominantSideName} controller to match the project to the site.");
                    string button = recheck ? L.T("Επιβεβαίωση θέσης", "Confirm position") : L.T($"Ευθυγράμμιση · {Mathf.Min(placed, 2)} από 2", $"Align · {Mathf.Min(placed, 2)} of 2");
                    y = BuildCta(y, text, button, null, () => { Close(); project.SetMode(true); },
                        recheck ? null : (int?)Mathf.Min(placed, 2));
                    y = BuildTiles(y, false);
                    break;
                }
                case Stage.Ready:
                    y = BuildTiles(y, true);
                    break;
            }

            y = Section(y, L.T("ΚΑΤΗΓΟΡΙΕΣ", "CATEGORIES"));
            if (stage != Stage.Login)
                y = NavRow(y, L.T("Έργο & σύνδεση", "Project & login"), L.T("Αλλαγή έργου, ανανέωση, λογαριασμός", "Change project, refresh, account"), Page.Project);
            if (stage == Stage.Unaligned || stage == Stage.Ready)
                y = NavRow(y, L.T("Ευθυγράμμιση", "Alignment"), AlignSub(), Page.Align);
            y = NavRow(y, L.T("Εμφάνιση", "Display"), L.T("Αισθητήρες, ετικέτες, χειριστήρια", "Sensors, labels, controllers"), Page.View);
            if (stage == Stage.Unaligned || stage == Stage.Ready)
                y = NavRow(y, L.T("Μοντέλο 3D", "3D model"), L.T("Σχέδιο πάνω στην κατασκευή · OBJ, DXF, IFC", "Design over the structure · OBJ, DXF, IFC"), Page.Model);
            y = NavRow(y, L.T("Ρυθμίσεις", "Settings"), L.T("Ήχος, χέρι, boundary, PC", "Sound, hand, boundary, PC"), Page.Settings);
            y = NavRow(y, L.T("Βοήθεια", "Help"), L.T("Χειρισμός χειριστηρίων", "Using the controllers"), Page.Help);

            if (y + 34f <= Height - BodyTop - 40f)
                VrUi.Text("Hint", body, 28, Height - BodyTop - 40, Width - 56, 24, L.T("Menu ή × για κλείσιμο", "Menu or × to close"), 13f,
                    Brand.FgFaint);
        }

        private string AlignSub()
        {
            if (project == null) return "";
            int n = project.PlacedCount;
            string pts = L.En ? Count(n, "point", "points") : n == 1 ? "1 σημείο" : $"{n} σημεία";
            string anchor = anchors != null && anchors.IsPersistentAnchor ? L.T(" · Μόνιμο anchor", " · Persistent anchor") : "";
            return pts + anchor;
        }

        private string Host()
        {
            string s = terraweb != null ? terraweb.Server ?? "" : "";
            return s.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        }

        // Office practice with two printed targets (docs/practice-targets.html), no Terraweb.
        // Replacing a loaded real project asks twice; leaving brings it back.
        private float PracticeRow(float y) => Row(y, new Item
        {
            Label = PracticeProject.IsActive ? L.T("Έξοδος από εξάσκηση", "Leave practice") : L.T("Εξάσκηση · χωρίς Terraweb", "Practice · no Terraweb"),
            Value = () => PracticeProject.IsActive ? L.T("Πίσω στο έργο", "Back to project") : L.T("Τυπωμένοι στόχοι", "Printed targets"),
            Enabled = () => project != null,
            Destructive = PracticeProject.IsActive || (project != null && project.HasProject),
            Run = () =>
            {
                if (PracticeProject.IsActive) { PracticeProject.Leave(); ShowPage(page, true); return; }
                Close();
                PracticeProject.Enter(project);
            }
        });

        private float BuildChips(float y, Stage stage)
        {
            float x = 28f;
            // Unmistakable while practising: nobody may take these sensors for site data.
            if (PracticeProject.IsActive)
            {
                Chip(x, y, 300, Brand.Control, L.T("ΕΞΑΣΚΗΣΗ · όχι πραγματικά δεδομένα", "PRACTICE · not real data"));
                y += 40f;
            }
            if (stage == Stage.Login)
            {
                Chip(x, y, 150, Brand.FgFaint, L.T("Εκτός σύνδεσης", "Not logged in"));
                return y + 44f;
            }
            // No project: nothing to count. Unaligned: the call to action already shows the progress.
            if (stage == Stage.NoProject || stage == Stage.Unaligned) return y + 8f;

            int count = markers != null ? markers.ActiveMarkerCount : 0;
            x = Chip(x, y, 130, Brand.FgSubtle, L.En ? Count(count, "sensor", "sensors") : $"{count} αισθητήρες") + 8f;
            if (stage == Stage.Ready) Chip(x, y, 130, Brand.Primary, L.T("Ευθυγραμμ.", "Aligned"));
            else Chip(x, y, 190, Brand.Warning, L.T($"{Mathf.Min(project.PlacedCount, 2)} / 2 σημεία ελέγχου", $"{Mathf.Min(project.PlacedCount, 2)} / 2 control points"));
            return y + 44f;
        }

        // Returns the x just past the chip.
        private float Chip(float x, float y, float w, Color dot, string text)
        {
            VrUi.Panel("Chip", body, x, y, w, 30, Brand.SurfaceSunken, 15);
            VrUi.Panel("Dot", body, x + 12, y + 11, 8, 8, dot, 4);
            VrUi.Text("ChipText", body, x + 26, y, w - 34, 30, text, 13f, Brand.FgMuted, true);
            return x + w;
        }

        // The big next-step card: optional progress bars, a sentence, a filled button, a footnote.
        private float BuildCta(float y, string text, string button, string footnote, Action run, int? stepsDone)
        {
            float x = 20f, w = Width - 40f;
            float h = 16f + (stepsDone.HasValue ? 14f : 0f) + 44f + 52f + (footnote != null ? 26f : 0f) + 16f;
            VrUi.Card("CtaCard", body, x, y, w, h, Brand.SurfaceMuted, Brand.Border, 18);
            float cy = y + 16f;
            if (stepsDone.HasValue)
            {
                float segW = (w - 32f - 6f) / 2f;
                for (int i = 0; i < 2; i++)
                    VrUi.Panel("Step" + i, body, x + 16f + i * (segW + 6f), cy, segW, 4,
                        i < stepsDone.Value ? Brand.Primary : Brand.SurfaceSunken, 2);
                cy += 14f;
            }
            var t = VrUi.Text("CtaText", body, x + 16f, cy, w - 32f, 44, text, 14.5f, Brand.FgMuted);
            t.textWrappingMode = TextWrappingModes.Normal;
            cy += 50f;

            var item = new Item { Label = button, Cta = true, Run = run };
            Image bg = VrUi.Panel("Cta", body, x + 16f, cy, w - 32f, 52, Brand.Primary, 26);
            var label = VrUi.Text("CtaLabel", bg.rectTransform, 0, 0, w - 32f, 52, button, 17f, Brand.PrimaryFg, true,
                TextAlignmentOptions.Center);
            AddItem(item, new ItemView { Background = bg, Label = label, NormalBg = Brand.Primary });
            cy += 52f;
            if (footnote != null)
            {
                VrUi.Text("CtaSub", body, x + 16f, cy + 4f, w - 32f, 22, footnote, 13f, Brand.FgSubtle, false,
                    TextAlignmentOptions.Center);
                cy += 26f;
            }
            return y + h + 10f;
        }

        // Three (or two, before alignment) quick tiles: navigation, time machine, alignment.
        private float BuildTiles(float y, bool ready)
        {
            y = Section(y, ready ? L.T("ΓΡΗΓΟΡΑ", "QUICK") : L.T("ΕΡΓΑΛΕΙΑ", "TOOLS"));
            var tiles = new List<(string icon, string label, Func<string> sub, Func<bool> enabled, Action run)>
            {
                ("compass", L.T("Πλοήγηση", "Navigation"),
                    () => !ready ? L.T("μετά την ευθυγρ.", "after alignment") : SensorNavigator.IsActive ? L.T("διακοπή", "stop") : L.T("σε αισθητήρα", "to a sensor"),
                    () => SensorNavigator.IsActive || (project != null && project.IsAligned && !project.NeedsCheck),
                    () =>
                    {
                        if (SensorNavigator.IsActive) { navigator.Stop(); Close(); }
                        else { Close(); app.OpenSensorPicker(); }
                    }),
                ("clock", L.T("Χρονομηχανή", "Time machine"),
                    () => !ready ? L.T("μετά την ευθυγρ.", "after alignment")
                        : displacement == null ? ""
                        : DisplacementView.IsActive ? L.T("κλείσιμο", "close")
                        : displacement.Unavailable() ?? L.T("μετακινήσεις", "displacements"),
                    () => ready && displacement != null && (DisplacementView.IsActive || displacement.Unavailable() == null),
                    () => { Close(); displacement.Toggle(); }),
            };
            if (ready)
            {
                tiles.Add(("pin", L.T("Ευθυγράμμιση", "Alignment"),
                    () => L.En ? $"{Count(project.PlacedCount, "point", "points")} · {Verdict()}"
                        : $"{project.PlacedCount} σημεία · {Verdict()}",
                    () => true,
                    () => { Close(); project.SetMode(true); }));
                if (route != null)
                    tiles.Add(("compass", L.T("Επιθεώρηση", "Inspection"),
                        () => InspectionRoute.IsActive ? L.T($"{route.Index}/{route.Count} · διακοπή", $"{route.Index}/{route.Count} · stop")
                            : route.Unavailable() ?? L.T("διαδρομή ελέγχου", "inspection route"),
                        () => InspectionRoute.IsActive || route.Unavailable() == null,
                        () =>
                        {
                            if (InspectionRoute.IsActive) { route.Stop(); Close(); }
                            else { Close(); PickRoute(); }
                        }));
            }

            // Up to three in a row; four go two by two.
            int cols = tiles.Count <= 3 ? Mathf.Max(1, tiles.Count) : 2;
            int rows = (tiles.Count + cols - 1) / cols;
            float gap = 8f, x0 = 20f, w = (Width - 40f - gap * (cols - 1)) / cols, h = 92f;
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                float x = x0 + (i % cols) * (w + gap);
                float ty = y + (i / cols) * (h + gap);
                Image border = VrUi.Panel("TileBorder", body, x, ty, w, h, Brand.Border, 16);
                Image bg = VrUi.Panel("Tile", body, x + 1, ty + 1, w - 2, h - 2, Brand.SurfaceMuted, 16);
                VrUi.Panel("IconBg", body, x + 12, ty + 12, 34, 34, Brand.SurfaceSunken, 17);
                VrUi.Icon("Icon", body, x + 20, ty + 20, 18, Brand.UiIconSprite(t.icon), Brand.Primary);
                var label = VrUi.Text("TileLabel", body, x + 12, ty + 50, w - 24, 22, t.label, 15f, Brand.Fg, true);
                var sub = VrUi.Text("TileSub", body, x + 12, ty + 70, w - 24, 18, "", 13f, Brand.FgSubtle, true);
                var item = new Item { Label = t.label, Tile = true, Value = t.sub, Enabled = t.enabled, Run = t.run };
                AddItem(item, new ItemView { Background = bg, Accent = border, Label = label, Value = sub, NormalBg = Brand.SurfaceMuted });
            }
            return y + rows * (h + gap) - gap + 6f;
        }

        // Which sensors the inspection walk covers: the ones in alarm/alert, or all of them.
        private void PickRoute()
        {
            if (route == null || flowPanel == null || project == null) return;
            int alerts = 0, all = 0;
            foreach (ProjectPoint p in project.Points)
            {
                all++;
                string s = (p.alertState ?? "").ToLowerInvariant();
                if (s == "alarm" || s == "alert") alerts++;
            }
            var items = new List<FlowItem>
            {
                new FlowItem { Label = L.T("Συναγερμοί και προειδοποιήσεις", "Alarms and alerts"), Sub = L.En ? Count(alerts, "sensor", "sensors") : alerts == 1 ? "1 αισθητήρας" : $"{alerts} αισθητήρες",
                    Icon = "compass", Color = Brand.Danger, Tag = "alerts" },
                new FlowItem { Label = L.T("Όλοι οι αισθητήρες", "All sensors"), Sub = L.T($"{all} αισθητήρες, πιο κοντινός πρώτα", $"{Count(all, "sensor", "sensors")}, nearest first"),
                    Icon = "compass", Color = Brand.Primary, Tag = "all" }
            };
            flowPanel.ShowList(L.T("ΕΠΙΘΕΩΡΗΣΗ", "INSPECTION"), L.T("Διαδρομή επιθεώρησης", "Inspection route"), items, item =>
            {
                flowPanel.Close();
                if (item.Tag as string == "alerts")
                {
                    if (alerts == 0) { Feedback.Play(Feedback.Sound.Warn); return; }
                    route.StartAlerts();
                }
                else route.StartAll();
            }, flowPanel.Close);
        }

        // One word on the alignment, for the tile and the alignment page.
        private string Verdict()
        {
            if (project == null || !project.IsAligned) return L.T("εκκρεμεί", "pending");
            if (project.NeedsCheck) return L.T("έλεγχος", "check");
            if (!string.IsNullOrEmpty(project.WorstId)) return L.T("διαφωνία", "mismatch");
            if (project.PlacedCount == 2) return L.T("χωρίς έλεγχο", "unchecked");
            float worst = Mathf.Max(project.RmsHorizontal, project.RmsVertical);
            return worst <= 0.05f ? "OK" : worst <= 0.15f ? L.T("μέτρια", "fair") : L.T("σφάλμα", "error");
        }

        // ------------------------------------------------------------------
        //  Category pages
        // ------------------------------------------------------------------

        private void BuildProject()
        {
            float y = Section(0f, L.T("ΕΡΓΟ & ΣΥΝΔΕΣΗ", "PROJECT & LOGIN"));
            if (PracticeProject.IsActive) y = PracticeRow(y);
            y = Row(y, new Item
            {
                Label = L.T("Αλλαγή έργου / περιοχής", "Change project / area"),
                Value = () => project != null && project.HasProject ? project.ProjectName : L.T("Επιλογή", "Choose"),
                Enabled = () => flow != null,
                Run = () => { Close(); flow.Begin(); }
            });
            y = Row(y, new Item
            {
                // Reload the same project/area: alert states and history, without the lists.
                Label = L.T("Ανανέωση δεδομένων", "Refresh data"),
                Value = () => flow != null && flow.CanReload ? flow.LoadedAgo() : L.T("Χωρίς έργο", "No project"),
                Enabled = () => flow != null && flow.CanReload,
                Run = () => { Close(); flow.Reload(); }
            });
            y = Row(y, new Item
            {
                // Polls the backend's active alert events; a change shows up on the icons at once.
                Label = L.T("Ζωντανοί συναγερμοί", "Live alarms"),
                Value = () => live == null ? "—"
                    : !LiveAlerts.Enabled ? L.T("Ανενεργό", "Off")
                    : live.ActiveAlarms + live.ActiveAlerts > 0
                        ? L.T($"{live.ActiveAlarms} συν. · {live.ActiveAlerts} προειδ. · {live.Status}",
                            $"{Count(live.ActiveAlarms, "alarm", "alarms")} · {Count(live.ActiveAlerts, "alert", "alerts")} · {live.Status}")
                        : live.Status,
                Enabled = () => live != null,
                Run = () => { LiveAlerts.Enabled = !LiveAlerts.Enabled; if (LiveAlerts.Enabled) live.PollNow(); }
            });
            y = Row(y, new Item
            {
                Label = L.T("Καθαρισμός όλων των αισθητήρων", "Clear all sensors"),
                Value = () => markers != null ? markers.ActiveMarkerCount.ToString() : "",
                Enabled = () => markers != null && markers.ActiveMarkerCount > 0,
                Destructive = true,
                Run = () => app.ClearAllMarkers()
            });
            y = Row(y, new Item
            {
                Label = L.T("Λογαριασμός", "Account"),
                Value = () => terraweb == null || !terraweb.IsLoggedIn ? L.T("Εκτός σύνδεσης", "Not logged in")
                    : $"{terraweb.Username} · {Host()}",
                Enabled = () => false
            });
            y = Row(y, new Item
            {
                Label = L.T("Αποσύνδεση", "Log out"),
                Enabled = () => terraweb != null && terraweb.IsLoggedIn,
                Destructive = true,
                Run = () => { terraweb.Logout(); ShowPage(Page.Home); }
            });
            BackButton();
        }

        private void BuildAlign()
        {
            float y = Section(0f, L.T("ΕΥΘΥΓΡΑΜΜΙΣΗ", "ALIGNMENT"));
            y = Row(y, new Item
            {
                Label = L.T("Λίστα ευθυγράμμισης", "Alignment list"),
                Value = () => project != null && project.IsAligned ? L.T("Ευθυγραμμ. · άνοιγμα", "Aligned · open") : L.T("Άνοιγμα", "Open"),
                Enabled = () => project != null && project.HasProject,
                Run = () => { Close(); project.SetMode(true); }
            });
            y = Row(y, new Item
            {
                Label = L.T("Επαναφορά σημείων ελέγχου", "Reset control points"),
                Value = () => project != null ? L.T($"{project.PlacedCount} τοποθετημένα", $"{project.PlacedCount} placed") : "",
                Enabled = () => project != null && project.PlacedCount > 0,
                Destructive = true,
                Run = () => project.ResetPlacements()
            });
            y = Row(y, new Item
            {
                Label = L.T("Ακρίβεια", "Accuracy"),
                Value = () => project == null || !project.IsAligned ? "—"
                    : $"{Verdict()} · ±{Cm(Mathf.Sqrt(project.RmsHorizontal * project.RmsHorizontal + project.RmsVertical * project.RmsVertical))}",
                Enabled = () => false
            });
            y = Row(y, new Item
            {
                // Whether placed control points survive a restart without a re-check.
                Label = L.T("Spatial anchor χώρου", "Room spatial anchor"),
                Value = () => anchors == null ? "—"
                    : anchors.IsPersistentAnchor ? L.T("Μόνιμο · θυμάται τη θέση", "Persistent · remembered")
                    : anchors.IsInitialized ? L.T("Προσωρινό · έλεγχος", "Temporary · check") : L.T("Φόρτωση…", "Loading…"),
                Enabled = () => false
            });

            // Phase 0 measurement (README › Δοκιμή πεδίου anchors): logs only, never aligns.
            y = Section(y + 8f, L.T("ΔΟΚΙΜΗ ΠΕΔΙΟΥ", "FIELD TEST"));
            y = Row(y, new Item
            {
                Label = L.T("Δοκιμή πεδίου anchors", "Anchor field test"),
                Value = () => AnchorFieldTest.Enabled ? L.T("Ενεργή", "On") : L.T("Ανενεργή", "Off"),
                Enabled = () => FieldTest != null,
                Run = () => AnchorFieldTest.Enabled = !AnchorFieldTest.Enabled
            });
            y = Row(y, new Item
            {
                // The app cannot see the light: set by hand, written into every CSV row.
                Label = L.T("Συνθήκες", "Conditions"),
                Value = () => AnchorFieldTest.ConditionName,
                Enabled = () => AnchorFieldTest.Enabled,
                Run = () => AnchorFieldTest.Condition++
            });
            y = Row(y, new Item
            {
                Label = L.T("Αλλαγές στο εργοτάξιο", "Site changed"),
                Value = () => AnchorFieldTest.SiteChanged ? L.T("Ναι", "Yes") : L.T("Όχι", "No"),
                Enabled = () => AnchorFieldTest.Enabled,
                Run = () => AnchorFieldTest.SiteChanged = !AnchorFieldTest.SiteChanged
            });
            y = Row(y, new Item
            {
                Label = L.T("Μέτρηση τώρα", "Measure now"),
                Value = () => FieldTest != null ? FieldTest.Summary : "—",
                Enabled = () => AnchorFieldTest.Enabled && FieldTest != null && !FieldTest.Busy && project != null && project.HasProject && !project.IsPractice,
                Run = () => FieldTest.MeasureNow()
            });
            y = Row(y, new Item
            {
                Label = L.T("Διαγραφή anchors δοκιμής", "Erase test anchors"),
                Value = () => L.T("Έργο · τα CSV μένουν", "Project · CSVs kept"),
                Enabled = () => AnchorFieldTest.Enabled && FieldTest != null && !FieldTest.Busy && (project == null || !project.IsPractice),
                Destructive = true,
                Run = () => FieldTest.EraseAll()
            });
            BackButton();
        }

        private void BuildView()
        {
            float y = Section(0f, L.T("ΕΜΦΑΝΙΣΗ", "DISPLAY"));
            y = Row(y, new Item
            {
                Label = L.T("Αισθητήρες στον χώρο", "Sensors on site"),
                Value = () => markers == null || markers.MarkersVisible ? L.T("Εμφάνιση", "Shown") : L.T("Απόκρυψη", "Hidden"),
                Enabled = () => markers != null,
                Run = () => markers.MarkersVisible = !markers.MarkersVisible
            });
            y = Row(y, new Item
            {
                Label = L.T("Ετικέτες", "Labels"),
                Value = () => Marker.LabelsVisible ? L.T("Εμφάνιση", "Shown") : L.T("Απόκρυψη", "Hidden"),
                Run = () => Marker.LabelsVisible = !Marker.LabelsVisible
            });
            y = Row(y, new Item
            {
                // A 30-day mini-chart of each sensor's displacement beside its icon.
                Label = L.T("Κορδέλες ιστορικού", "History ribbons"),
                Value = () => ribbons == null ? "—"
                    : HistoryRibbons.Enabled ? L.T($"Εμφάνιση · {ribbons.RibbonCount}", $"Shown · {ribbons.RibbonCount}")
                    : ribbons.Unavailable() ?? L.T("Απόκρυψη", "Hidden"),
                Enabled = () => ribbons != null && (HistoryRibbons.Enabled || ribbons.Unavailable() == null),
                Run = () => HistoryRibbons.Enabled = !HistoryRibbons.Enabled
            });
            y = Row(y, new Item
            {
                // Feature 10: a bar left of each icon, its length the "% of limit". Without the
                // CanViewLimits permission the value says so and the row stays off.
                Label = L.T("Μπάρες ορίων", "Limit bars"),
                Value = () => Limits == null ? "—"
                    : Limits.Unavailable() ?? (LimitBars.Enabled ? L.T($"Εμφάνιση · {Limits.BarCount}", $"Shown · {Limits.BarCount}")
                        : L.T("Απόκρυψη", "Hidden")),
                Enabled = () => Limits != null && (LimitBars.Enabled || Limits.Unavailable() == null),
                Run = () => LimitBars.Enabled = !LimitBars.Enabled
            });
            y = Row(y, new Item
            {
                // The 15 sensors closest to their limits; picking one navigates to it.
                Label = L.T("Κοντά στα όρια", "Near limits"),
                Value = () => Limits == null ? "—"
                    : Limits.Unavailable() ?? (L.En ? $"{Limits.MeasuredCount} with limits" : $"{Limits.MeasuredCount} με όρια"),
                Enabled = () => Limits != null && Limits.Unavailable() == null && Limits.MeasuredCount > 0,
                Run = () => { Close(); Limits.ShowNearLimits(flowPanel, navigator); }
            });
            y = Row(y, new Item
            {
                Label = L.T("Χειριστήρια", "Controllers"),
                Value = ControllerStyleName,
                Run = CycleControllerStyle
            });
            y = Row(y, new Item
            {
                // Try both in the field: the clipboard follows the off hand, the other stays in
                // the room and glides back in front like this menu.
                Label = L.T("Panel ευθυγράμμισης", "Alignment panel"),
                Value = () => project == null || project.PanelOnHand ? L.T("Στο χέρι", "On hand") : L.T("Στον χώρο", "In the room"),
                Enabled = () => project != null,
                Run = () => project.PanelOnHand = !project.PanelOnHand
            });
            y = Row(y, new Item
            {
                Label = L.T("Επαναφορά θέσης παραθύρων", "Reset window positions"),
                Value = () => L.T("Grip: μετακίνηση", "Grip: move"),
                Run = () => { PanelPin.ResetAll(); PlaceInFront(); }
            });
            BackButton();
        }

        private void BuildSettings()
        {
            float y = Section(0f, L.T("ΡΥΘΜΙΣΕΙΣ", "SETTINGS"));
            y = Row(y, new Item
            {
                // Shown in both languages, so it can be found whichever is on.
                Label = "Γλώσσα / Language",
                Value = () => L.Name(L.Current),
                Run = () =>
                {
                    L.Current = L.En ? L.Language.Greek : L.Language.English;
                    ShowPage(Page.Settings, true); // row labels are laid out once per page
                }
            });
            y = Row(y, new Item
            {
                Label = L.T("Κατάσταση στο χέρι", "Status on the hand"),
                Value = () => StatusChip.Enabled ? L.T("Ναι", "On") : L.T("Όχι", "Off"),
                Run = () => StatusChip.Enabled = !StatusChip.Enabled
            });
            y = Row(y, new Item
            {
                Label = L.T("Υψηλή αντίθεση (ήλιος)", "High contrast (sunlight)"),
                Value = () => (Brand.HighContrastNext ? L.T("Ναι", "On") : L.T("Όχι", "Off"))
                    + (Brand.HighContrastNext != Brand.HighContrast ? L.T(" · μετά από επανεκκίνηση", " · after a restart") : ""),
                Run = () => Brand.HighContrastNext = !Brand.HighContrastNext
            });
            y = Row(y, new Item
            {
                Label = L.T("Ήχοι", "Sounds"),
                Value = () => Feedback.SoundOn ? L.T("Ενεργοί", "On") : L.T("Σίγαση", "Muted"),
                Run = () => { Feedback.SoundOn = !Feedback.SoundOn; Feedback.Play(Feedback.Sound.Click); }
            });
            y = Row(y, new Item
            {
                // Push-to-talk on the off-hand stick click; typed until the Wit.ai engine is installed.
                Label = L.T("Φωνητικές εντολές", "Voice commands"),
                Value = () => Voice == null ? "—" : Voice.Status,
                Enabled = () => Voice != null,
                Run = () => VoiceCommands.Enabled = !VoiceCommands.Enabled
            });
            y = Row(y, new Item
            {
                Label = L.T("Κύριο χέρι", "Main hand"),
                Value = () => Hands.LeftHanded ? L.T("Αριστερό", "Left") : L.T("Δεξί", "Right"),
                Run = () => Hands.LeftHanded = !Hands.LeftHanded
            });
            y = Row(y, new Item
            {
                // 90 Hz: steadier overlays and stick walking, more battery and heat. Only worth
                // it if the app holds the frame rate (check with OVR Metrics Tool).
                Label = L.T("Ρυθμός ανανέωσης", "Refresh rate"),
                Value = () =>
                {
                    float want = DisplayPerformance.RefreshRate, now = DisplayPerformance.ActualRate;
                    return now > 0f && Mathf.Abs(now - want) > 0.5f
                        ? L.T($"{want:0} Hz · τώρα {now:0}", $"{want:0} Hz · now {now:0}")
                        : $"{want:0} Hz";
                },
                Run = () => DisplayPerformance.RefreshRate = DisplayPerformance.NextRate(DisplayPerformance.RefreshRate)
            });
            y = Row(y, new Item
            {
                // Lower resolution at the edges of the view, where the eye does not resolve it;
                // frees GPU time for 90 Hz and large see-through models.
                Label = "Foveated rendering",
                Value = () => DisplayPerformance.FoveationNames[DisplayPerformance.Foveation],
                Run = () => DisplayPerformance.Foveation = (DisplayPerformance.Foveation + 1) % DisplayPerformance.FoveationNames.Length
            });
            y = Row(y, new Item
            {
                Label = L.T("Όριο ασφαλείας (boundary)", "Safety boundary"),
                Value = () => ModelOverlay.LifeSizeShown ? L.T("Ενεργό στο 1:1", "On at 1:1")
                    : BoundaryControl.Suppress
                    ? (BoundaryControl.IsSuppressed ? L.T("Ανενεργό", "Off") : L.T("Ανενεργό · αναμονή", "Off · waiting"))
                    : L.T("Ενεργό", "On"),
                Destructive = true, // turning the safety boundary off/on should never be a stray click
                Run = () => BoundaryControl.Suppress = !BoundaryControl.Suppress
            });
            y = Row(y, new Item
            {
                Label = "Server PC (LAN)",
                Value = () => serverText,
                Enabled = () => false
            });
            y = Row(y, new Item
            {
                // The LAN API needs this key (PC bridge: --quest-key, send_project: --api-key).
                Label = L.T("Κλειδί API για PC", "API key for PC"),
                Value = () => server != null ? server.ApiKey : "",
                Enabled = () => false
            });
            // Only for the free-form (PC bridge) mode: a loaded project owns the origin.
            if (project == null || !project.HasProject)
                y = Row(y, new Item
                {
                    Label = L.T("Ορισμός αρχής (0,0,0) εδώ", "Set origin (0,0,0) here"),
                    Destructive = true,
                    Run = () => app.CalibrateReferencePoint()
                });
            BackButton();
        }

        // The design model: which file, how see-through, edges, layers. Files arrive from the PC
        // (send_model.py) into the headset's models folder.
        private void BuildModel()
        {
            float y = Section(0f, L.T("ΜΟΝΤΕΛΟ 3D", "3D MODEL"));
            y = Row(y, new Item
            {
                Label = L.T("Μοντέλο", "Model"),
                Value = () => model == null ? "—" : model.Status,
                Enabled = () => model != null && flowPanel != null,
                Run = PickModel
            });
            y = Row(y, new Item
            {
                Label = L.T("Εμφάνιση στον χώρο", "Show on site"),
                Value = () => !ModelOverlay.Enabled ? L.T("Απόκρυψη", "Hidden")
                    : model != null && model.Unavailable() != null ? model.Unavailable() : L.T("Εμφάνιση", "Shown"),
                Enabled = () => model != null && model.IsLoaded,
                Run = () => ModelOverlay.Enabled = !ModelOverlay.Enabled
            });
            y = Row(y, new Item
            {
                // Auto: on the site once aligned, a miniature until then. The other two always preview.
                Label = L.T("Προβολή", "View"),
                Value = () => model == null ? ""
                    : model.View == ModelOverlay.ViewMode.Tabletop ? L.T("Μακέτα", "Tabletop")
                    : model.View == ModelOverlay.ViewMode.LifeSize ? L.T("1:1 γύρω μου", "1:1 around me")
                    : model.CanPlaceInProject ? L.T("Αυτόματα · στο έργο", "Auto · on site") : L.T("Αυτόματα · μακέτα", "Auto · tabletop"),
                Enabled = () => model != null,
                Run = () => model.View = (ModelOverlay.ViewMode)(((int)model.View + 1) % 3)
            });
            y = Row(y, new Item
            {
                Label = L.T("Νέα θέση μπροστά μου", "Move in front of me"),
                Enabled = () => model != null && model.IsLoaded && model.CurrentPlacement != ModelOverlay.Placement.Project,
                Run = () => { model.ResetPreview(); Close(); }
            });
            y = Row(y, new Item
            {
                // Four steps: barely there, x-ray, solid-ish, solid.
                Label = L.T("Διαφάνεια", "Transparency"),
                Value = () => model == null ? "" : $"{Mathf.RoundToInt((1f - model.Opacity) * 100f)}%",
                Enabled = () => model != null && model.IsLoaded,
                Run = () =>
                {
                    float o = model.Opacity;
                    model.Opacity = o < 0.2f ? 0.35f : o < 0.5f ? 0.6f : o < 0.9f ? 1f : 0.15f;
                }
            });
            y = Row(y, new Item
            {
                // Stick walking through the 1:1 copy; rising / sinking follows at 0.7x.
                Label = L.T("Ταχύτητα πλοήγησης", "Walk speed"),
                Value = () => ModelOverlay.WalkSpeed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " m/s",
                Run = ModelOverlay.CycleWalkSpeed
            });
            y = Row(y, new Item
            {
                // Dots per sensor on the tabletop / 1:1 preview; on the site the real markers stand.
                Label = L.T("Αισθητήρες στη μακέτα", "Sensors on model"),
                Value = () => modelSensors == null ? "—" : modelSensors.Status,
                Enabled = () => modelSensors != null,
                Run = () => ModelSensors.Enabled = !ModelSensors.Enabled
            });
            y = Row(y, new Item
            {
                // Only for a model in another height datum than the sensors' x/y/z (Konitsa's
                // ΕΓΣΑ87 model: Terraweb z + 426 m). Empty entry: none.
                Label = L.T("Υψομετρική διαφορά", "Height offset"),
                Value = () =>
                {
                    if (modelSensors == null) return "—";
                    float? v = modelSensors.ManualOffset;
                    return v.HasValue ? v.Value.ToString("+0.0;-0.0;0", System.Globalization.CultureInfo.InvariantCulture) + " m"
                        : L.T("Καμία", "None");
                },
                Enabled = () => modelSensors != null && project != null && project.HasProject && !project.IsPractice,
                Run = () =>
                {
                    float? cur = modelSensors.ManualOffset;
                    string initial = cur.HasValue ? cur.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";
                    VrKeyboard.Shared.Open(L.T("Διαφορά υψομέτρου (m), κενό: καμία", "Height offset (m), empty: none"), initial, false,
                        text =>
                        {
                            text = (text ?? "").Trim().Replace(',', '.');
                            if (text.Length == 0) modelSensors.ManualOffset = null;
                            else if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out float f)
                                     && !float.IsNaN(f) && !float.IsInfinity(f))
                                modelSensors.ManualOffset = f;
                            else Feedback.Play(Feedback.Sound.Warn);
                        }, null);
                }
            });
            y = Row(y, new Item
            {
                Label = L.T("Ακμές", "Edges"),
                Value = () => model != null && model.ShowEdges ? L.T("Εμφάνιση", "Shown") : L.T("Απόκρυψη", "Hidden"),
                Enabled = () => model != null && model.IsLoaded,
                Run = () => model.ShowEdges = !model.ShowEdges
            });
            y = Row(y, new Item
            {
                // OBJ groups, DXF layers, IFC storeys.
                Label = L.T("Ομάδες / layers", "Groups / layers"),
                Value = () =>
                {
                    if (model == null || !model.IsLoaded) return "";
                    int on = 0;
                    foreach (ModelGroup g in model.Groups) if (g.Visible) on++;
                    return L.T($"{on} από {model.Groups.Count}", $"{on} of {model.Groups.Count}");
                },
                Enabled = () => model != null && model.IsLoaded && model.Groups.Count > 1 && flowPanel != null,
                Run = PickGroups
            });
            y = Row(y, new Item
            {
                Label = L.T("Αφαίρεση μοντέλου", "Remove model"),
                Enabled = () => model != null && (model.IsLoaded || model.IsLoading),
                Destructive = true,
                Run = () => model.Unload()
            });
            string ip = serverText.Split(':')[0];
            var note = VrUi.Text("ModelNote", body, 28, y + 8, Width - 56, 96,
                L.T($"Μακέτα / 1:1: grip του {Hands.OffHandSideName}: μετακίνηση και περιστροφή · "
                    + "και τα δύο grip ή το άλλο stick: ζουμ.\n"
                    + $"Αποστολή από το PC: python send_model.py αρχείο.obj|dxf|ifc --quest-ip {ip} --api-key ",
                    $"Tabletop / 1:1: {Hands.OffHandSideName} grip: move and rotate · "
                    + "both grips or the other stick: zoom.\n"
                    + $"Send from the PC: python send_model.py file.obj|dxf|ifc --quest-ip {ip} --api-key ")
                + (server != null ? server.ApiKey : ""),
                13f, Brand.FgSubtle);
            note.textWrappingMode = TextWrappingModes.Normal;
            BackButton();
        }

        private void PickModel()
        {
            if (model == null || flowPanel == null) return;
            var items = new List<FlowItem>();
            foreach (ModelStore.ModelInfo m in ModelStore.List())
            {
                items.Add(new FlowItem
                {
                    Label = m.Name,
                    Sub = $"{(m.Format ?? "").ToUpperInvariant()} · {SizeText(m.Bytes)} · {m.Modified:dd/MM HH:mm}",
                    Value = m.Name == model.LoadedName ? L.T("τρέχον", "current") : "",
                    Icon = "displacement",
                    Tag = m
                });
            }
            Close();
            if (items.Count == 0)
            {
                flowPanel.ShowMessage(L.T("ΜΟΝΤΕΛΟ 3D", "3D MODEL"), L.T("Κανένα μοντέλο", "No models"), L.T("Δεν υπάρχουν αρχεία στα γυαλιά", "No files on the headset"),
                    L.T("Στείλε ένα από το PC με το send_model.py (OBJ, DXF ή IFC). Φορτώνεται αμέσως μόλις φτάσει.", "Send one from the PC with send_model.py (OBJ, DXF or IFC). It loads as soon as it arrives."),
                    false, flowPanel.Close);
                return;
            }
            flowPanel.ShowList(L.T("ΜΟΝΤΕΛΟ 3D", "3D MODEL"), L.T("Διάλεξε μοντέλο", "Pick a model"), items, item =>
            {
                flowPanel.Close();
                if (item.Tag is ModelStore.ModelInfo info) model.Load(info);
            }, flowPanel.Close);
        }

        private void PickGroups()
        {
            if (model == null || flowPanel == null || !model.IsLoaded) return;
            Close();
            ShowGroupList();
        }

        // Picking a row toggles it and shows the list again, so several layers can be switched in a row
        // (staying on that row: it used to jump back to the top after every toggle).
        private void ShowGroupList(int keep = 0)
        {
            var items = new List<FlowItem>();
            IReadOnlyList<ModelGroup> groups = model.Groups;
            for (int i = 0; i < groups.Count; i++)
            {
                ModelGroup g = groups[i];
                int tris = g.Triangles.Count / 3, segs = g.LineVertices.Count / 2;
                items.Add(new FlowItem
                {
                    Label = string.IsNullOrEmpty(g.Name) ? L.T("(χωρίς όνομα)", "(unnamed)") : g.Name,
                    Sub = L.En
                        ? (tris > 0 && segs > 0 ? $"{Count(tris, "triangle", "triangles")} · {Count(segs, "line", "lines")}"
                            : tris > 0 ? Count(tris, "triangle", "triangles") : Count(segs, "line", "lines"))
                        : tris > 0 && segs > 0 ? $"{tris} τρίγωνα · {segs} γραμμές"
                        : tris > 0 ? $"{tris} τρίγωνα" : $"{segs} γραμμές",
                    Value = g.Visible ? L.T("Εμφάνιση", "Shown") : L.T("Απόκρυψη", "Hidden"),
                    Icon = "eye",
                    Color = new Color(g.Color.r, g.Color.g, g.Color.b, 1f),
                    Tag = i
                });
            }
            flowPanel.ShowList(L.T("ΜΟΝΤΕΛΟ 3D", "3D MODEL"), L.T("Ομάδες / layers", "Groups / layers"), items, item =>
            {
                int i = (int)item.Tag;
                model.SetGroupVisible(i, !model.Groups[i].Visible);
                ShowGroupList(i);
            }, flowPanel.Close, keep);
        }

        private static string SizeText(long bytes) =>
            bytes >= 1 << 20 ? $"{bytes / (float)(1 << 20):F1} MB" : $"{Mathf.Max(1, bytes / 1024)} KB";

        private void BuildHelp()
        {
            float y = Section(0f, L.T("ΒΟΗΘΕΙΑ", "HELP"));
            y = Row(y, new Item
            {
                Label = L.T("Χειρισμός χειριστηρίων", "Using the controllers"),
                Value = () => L.T("Χάρτης κουμπιών", "Button map"),
                Run = () => ShowPage(Page.Controls)
            });
            y = Row(y, new Item
            {
                // The coach card from the top (Onboarding). Keeps project, login and alignment:
                // steps already done pass by themselves.
                Label = L.T("Οδηγός πρώτης χρήσης", "First-use guide"),
                Value = () => Onboarding.IsRunning
                    ? L.T($"Βήμα {Onboarding.StepNumber}/{Onboarding.StepTotal} · ξανά",
                        $"Step {Onboarding.StepNumber}/{Onboarding.StepTotal} · restart")
                    : L.T("Ξεκίνα από την αρχή", "Start over"),
                Enabled = () => Onboarding.Instance != null,
                Run = () =>
                {
                    ControllerHints.Enabled = true;
                    ControllerHints.ShowModel = true;
                    Close();
                    Onboarding.Instance.Restart();
                }
            });
            y = PracticeRow(y);
            y = Row(y, new Item
            {
                // What the guide row used to toggle: every button's label, not only the touched one.
                Label = L.T("Ετικέτες σε όλα τα κουμπιά", "Labels on every button"),
                Value = () => ControllerHints.AlwaysShowLabels ? L.T("Ναι", "Yes") : L.T("Όχι", "No"),
                Run = () =>
                {
                    ControllerHints.Enabled = true;
                    ControllerHints.AlwaysShowLabels = !ControllerHints.AlwaysShowLabels;
                }
            });
            BackButton();
        }

        // Model + icons → icons only → icons with every label → hidden → back.
        private static string ControllerStyleName()
        {
            if (!ControllerHints.Enabled) return L.T("Κρυφά", "Hidden");
            if (ControllerHints.AlwaysShowLabels) return L.T("Εικονίδια + ετικέτες", "Icons + labels");
            return ControllerHints.ShowModel ? L.T("Μοντέλο + εικονίδια", "Model + icons") : L.T("Μόνο εικονίδια", "Icons only");
        }

        private static void CycleControllerStyle()
        {
            if (!ControllerHints.Enabled)
            {
                ControllerHints.Enabled = true;
                ControllerHints.ShowModel = true;
                ControllerHints.AlwaysShowLabels = false;
            }
            else if (ControllerHints.AlwaysShowLabels) ControllerHints.Enabled = false;
            else if (ControllerHints.ShowModel) ControllerHints.ShowModel = false;
            else ControllerHints.AlwaysShowLabels = true;
        }

        // ------------------------------------------------------------------
        //  Controls page
        // ------------------------------------------------------------------

        // Columns are the physical controllers; their contents follow the dominant hand. The
        // Menu button is on the left controller whichever hand points.
        private void BuildControls()
        {
            float y = Section(0f, L.T("ΧΕΙΡΙΣΜΟΣ ΧΕΙΡΙΣΤΗΡΙΩΝ", "USING THE CONTROLLERS"));

            // Keep in step with ControllerHints and the input code (MarkerAppController, panels,
            // ProjectAlignment, VrPointer, VoiceCommands, ModelOverlay).
            var dominant = new List<(string, string)>
            {
                ("Trigger", L.T("Κλικ · καρτέλα · σημείο", "Click · card · point")),
                (Hands.SecondaryName, L.T("Πίσω · αναίρεση σημείου", "Back · undo point")),
                (Hands.PrimaryName, L.T("Επιλογή στο μενού", "Select in menu")),
                ("Stick ↑↓", L.T("Πλοήγηση μενού", "Menu navigation")),
                ("Grip", L.T("Μετακίνηση παραθύρου", "Move window")),
            };
            var offHand = new List<(string, string)>
            {
                (Hands.OffHandPrimaryName, L.T("Μενού / κλείσιμο λίστας", "Menu / close list")),
                ("Stick ↑↓", L.T("Επιλογή στη λίστα", "Select in list")),
                (Hands.OffHandSecondaryName, L.T("Αναίρεση*", "Undo*")),
                ("Grip", L.T("Μετακίνηση μοντέλου", "Move model")),
            };
            if (VoiceCommands.Enabled) offHand.Insert(2, ("Stick", L.T("Κράτα πατημένο: φωνή", "Hold: voice")));
            List<(string, string)> leftRows = Hands.LeftHanded ? dominant : offHand;
            List<(string, string)> rightRows = Hands.LeftHanded ? offHand : dominant;
            leftRows.Insert(0, ("Menu", L.T("Κύριο μενού", "Main menu")));

            float colW = (Width - 72f) / 2f;
            float leftH = Column(28, y, L.T("Αριστερό", "Left"), leftRows.ToArray(), colW);
            float rightH = Column(28 + colW + 16, y, L.T("Δεξί", "Right"), rightRows.ToArray(), colW);

            VrUi.Text("Note", body, 28, y + Mathf.Max(leftH, rightH) + 12, Width - 56, 60,
                L.T($"Η ροζ μύτη στην άκρη του {Hands.DominantSideName} χειριστηρίου είναι το σημείο που καταγράφεται. " +
                    $"* Τα {Hands.PrimaryName} / {Hands.SecondaryName} / {Hands.OffHandSecondaryName} λειτουργούν ως συντομεύσεις μόνο χωρίς φορτωμένο έργο.",
                    $"The pink tip at the end of the {Hands.DominantSideName} controller is the point that gets recorded. " +
                    $"* {Hands.PrimaryName} / {Hands.SecondaryName} / {Hands.OffHandSecondaryName} work as shortcuts only with no project loaded."),
                13f, Brand.FgSubtle).textWrappingMode = TextWrappingModes.Normal;
            BackButton(Page.Help);
        }

        // Returns the column's height, which follows its row count.
        private float Column(float x, float y, string heading, (string key, string action)[] rows, float w)
        {
            float h = 54f + rows.Length * 48f + 4f;
            VrUi.Card("Col", body, x, y, w, h, Brand.SurfaceMuted, Brand.Border, 16);
            VrUi.Text("Heading", body, x + 16, y + 10, w - 32, 30, heading, 17f, Brand.Fg, true);
            for (int i = 0; i < rows.Length; i++)
                // Bounded by the column: long actions used to run into the other column.
                VrUi.KeyCap(body, x + 16, y + 54 + i * 48, rows[i].key, rows[i].action, 32f, x + w - 12f);
            return h;
        }

        // ------------------------------------------------------------------
        //  Building blocks
        // ------------------------------------------------------------------

        private float Section(float y, string text)
        {
            VrUi.Text("Section", body, 28, y + 4, 400, 24, text, 13f, Brand.FgSubtle, true).characterSpacing = 6f;
            return y + 32f;
        }

        private void AddItem(Item item, ItemView view)
        {
            int index = items.Count;
            items.Add(item);
            views.Add(view);
            // Pointing at an item selects it; the trigger runs it (destructive ones ask twice).
            VrPointer.Register(view.Background.rectTransform,
                () => { selected = index; Activate(); },
                () => { if (selected != index) confirmIndex = -1; selected = index; },
                () => items[index].Enabled());
        }

        private float Row(float y, Item item)
        {
            var v = new ItemView
            {
                Background = VrUi.Panel("RowBg", body, 16, y, Width - 32, RowH - 6, Brand.PrimarySoft, 14),
                Accent = VrUi.Panel("Accent", body, 16, y + 10, 4, RowH - 26, Brand.Primary, 2),
                Label = VrUi.Text("Label", body, 36, y, 260, RowH - 6, item.Label, 17f, Brand.Fg, true),
                Value = VrUi.Text("Value", body, 296, y, 196, RowH - 6, "", 14f, Brand.FgSubtle, false,
                    TextAlignmentOptions.MidlineRight),
                NormalBg = Brand.PrimarySoft
            };
            AddItem(item, v);
            return y + RowH;
        }

        private float NavRow(float y, string label, string sub, Page target)
        {
            var item = new Item { Label = label, Nav = true, Run = () => ShowPage(target) };
            var v = new ItemView
            {
                Background = VrUi.Panel("NavBg", body, 16, y, Width - 32, NavRowH - 6, Brand.PrimarySoft, 14),
                Accent = VrUi.Panel("Accent", body, 16, y + 12, 4, NavRowH - 30, Brand.Primary, 2),
                Label = VrUi.Text("Label", body, 36, y + 4, Width - 110, 26, label, 17f, Brand.Fg, true),
                Value = VrUi.Text("Sub", body, 36, y + 26, Width - 110, 20, sub, 13f, Brand.FgSubtle, true),
                NormalBg = Brand.PrimarySoft
            };
            VrUi.Icon("Chev", body, Width - 52, y + (NavRowH - 6) / 2f - 8, 16, Brand.UiIconSprite("chevron-right"), Brand.FgSubtle);
            item.Value = () => sub;
            AddItem(item, v);
            return y + NavRowH;
        }

        private void BackButton(Page target = Page.Home)
        {
            VrUi.Button("Back", body, 28, Height - BodyTop - 66, 150, 44, "chevron-right",
                L.T($"Πίσω ({Hands.SecondaryName})", $"Back ({Hands.SecondaryName})"), () => ShowPage(target), false, true);
        }

        // English count with its noun: "1 sensor", "2 sensors".
        private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

        private static string Cm(float metres) => metres < 1f ? $"{metres * 100f:F1} cm" : $"{metres:F2} m";

        // ------------------------------------------------------------------
        //  Open / close / input
        // ------------------------------------------------------------------

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        /// <param name="userInitiated">
        /// False for the automatic open on launch: only a user who reached the menu themselves
        /// has seen the controller labels, so only then does the first-run state end.
        /// </param>
        public void Open(bool userInitiated = true)
        {
            if (root == null) return;
            IsOpen = true;
            openedFrame = Time.frameCount;
            if (userInitiated && ControllerHints.FirstRun) ControllerHints.FirstRun = false;
            if (!PanelPin.IsPinned(this)) PlaceInFront();
            root.gameObject.SetActive(true);
            RefreshServerText();
            ShowPage(Page.Home);
        }

        public void Close()
        {
            if (IsOpen) UiState.NoteClosed();
            IsOpen = false;
            if (root != null) root.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            // Static flag outlives the object: never leave the app thinking a menu is up.
            if (IsOpen) { IsOpen = false; UiState.NoteClosed(); }
        }

        // 0.6 m ahead, a little below the eyes: inside arm's reach for the ray, the top edge
        // just above eye level.
        private const float Distance = 0.6f, Drop = 0.08f;

        private void PlaceInFront()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            gliding = false;
            transform.position = VrUi.InFront(cam.transform, Distance, Drop);
            VrUi.FaceViewer(transform, cam.transform);
        }

        private void Update()
        {
            if (!IsOpen) return;

            // Re-centre if the user has turned away, so the menu is never lost behind them —
            // gliding there, not jumping.
            Camera cam = Camera.main;
            if (cam != null && !VrPointer.IsDragging && !PanelPin.IsPinned(this))
                VrUi.LazyFollow(transform, cam.transform, Distance, Drop, ref gliding);

            if (confirmIndex >= 0 && Time.unscaledTime > confirmUntil) confirmIndex = -1;
            // The home page's layout follows the app state (login / project / alignment): rebuild
            // when that changes under an open menu (e.g. a reload finished).
            if (page == Page.Home && HomeKey() != builtKey) ShowPage(Page.Home, true);
            HandleInput();
            Refresh();
        }

        private void HandleInput()
        {
            if (VrPointer.IsDragging) return;
            // The X that opened the menu must not also pick its first row; and while a
            // keyboard floats above, A / B belong to it alone.
            if (Time.frameCount == openedFrame) return;
            if (VrKeyboard.IsOpen || VrKeyboard.ClosedFrame == Time.frameCount) return;
#if !MOBILE_PREVIEW
            float stick = Hands.AnyStickY();
            if (Mathf.Abs(stick) > 0.6f && Time.unscaledTime >= nextStickMove)
            {
                Move(stick > 0 ? -1 : 1);
                nextStickMove = Time.unscaledTime + 0.22f;
            }
            else if (Mathf.Abs(stick) < 0.3f)
            {
                nextStickMove = 0f;
            }

            bool select = (!VrPointer.ConsumedThisFrame && Hands.TriggerDown()) || Hands.AnySelectDown();
            bool back = Hands.AnyBackDown();

            if (back) { Feedback.Play(Feedback.Sound.Tick); Back(); return; }
            // The pointer plays its own click; buttons get the same sound.
            if (select) { Feedback.Play(Feedback.Sound.Click); Activate(); }
#else
            if (Input.GetKeyDown(KeyCode.UpArrow)) Move(-1);
            if (Input.GetKeyDown(KeyCode.DownArrow)) Move(1);
            if (Input.GetKeyDown(KeyCode.Return)) Activate();
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) Back();
#endif
        }

        // B: up one level; from the home page it closes the menu.
        private void Back()
        {
            if (page == Page.Home) Close();
            else if (page == Page.Controls) ShowPage(Page.Help);
            else ShowPage(Page.Home);
        }

        private void Move(int delta)
        {
            confirmIndex = -1;
            int before = selected;
            selected = FirstEnabled(selected + delta, delta);
            if (selected != before) Feedback.Play(Feedback.Sound.Tick, 0.35f);
        }

        // Skip disabled items, wrapping round; stays put if nothing else is enabled.
        private int FirstEnabled(int start, int step)
        {
            int n = items.Count;
            if (n == 0) return 0;
            for (int i = 0; i < n; i++)
            {
                int idx = ((start + i * step) % n + n) % n;
                if (items[idx].Enabled()) return idx;
            }
            return Mathf.Clamp(selected, 0, n - 1);
        }

        private void Activate()
        {
            if (selected < 0 || selected >= items.Count) return;
            Item item = items[selected];
            if (!item.Enabled()) return;
            if (item.Destructive && confirmIndex != selected)
            {
                confirmIndex = selected;
                confirmUntil = Time.unscaledTime + ConfirmWindow;
                Feedback.Play(Feedback.Sound.Warn, 0.5f); // "press again": heard, not only seen
                return;
            }
            confirmIndex = -1;
            item.Run();
            nextValueRefresh = 0f; // a toggle shows its new value on this very frame
        }

        // ------------------------------------------------------------------
        //  Refresh
        // ------------------------------------------------------------------

        private void RefreshServerText()
        {
            string[] urls = server != null ? server.GetServerUrls() : Array.Empty<string>();
            serverText = urls.Length > 0 ? urls[0].Replace("http://", "") : L.T("Server εκτός", "Server off");
            nextServerRefresh = Time.unscaledTime + 1f;
        }

        private static string PageName(Page p)
        {
            switch (p)
            {
                case Page.Project: return L.T("Έργο & σύνδεση", "Project & login");
                case Page.Align: return L.T("Ευθυγράμμιση", "Alignment");
                case Page.View: return L.T("Εμφάνιση", "Display");
                case Page.Settings: return L.T("Ρυθμίσεις", "Settings");
                case Page.Help: return L.T("Βοήθεια", "Help");
                case Page.Model: return L.T("Μοντέλο 3D", "3D model");
                case Page.Controls: return L.T("Χειριστήρια", "Controllers");
                default: return "";
            }
        }

        private void Refresh()
        {
            if (Time.unscaledTime >= nextServerRefresh) RefreshServerText();

            // Header: only when the page or project name changed (the crumb concatenation and its
            // preferredWidth layout ran every frame).
            string home = CurrentStage() == Stage.Login ? L.T("Καλώς ήρθες", "Welcome") : ProjectTitle();
            if (page != headerPage || home != headerHome)
            {
                headerPage = page;
                headerHome = home;
                if (page == Page.Home)
                {
                    crumb.text = "";
                    title.text = home;
                    title.rectTransform.anchoredPosition = new Vector2(28f, -52f);
                }
                else
                {
                    crumb.text = home + " ›";
                    // Place the page name right after the breadcrumb, which may be long: clamp it.
                    float crumbW = Mathf.Min(crumb.preferredWidth, 220f);
                    crumb.rectTransform.sizeDelta = new Vector2(crumbW, 40f);
                    title.text = PageName(page);
                    title.rectTransform.anchoredPosition = new Vector2(28f + crumbW + 8f, -52f);
                }
            }

            bool values = Time.unscaledTime >= nextValueRefresh;
            if (values) nextValueRefresh = Time.unscaledTime + ValueRefreshInterval;

            for (int i = 0; i < items.Count; i++)
            {
                ItemView v = views[i];
                Item item = items[i];
                bool enabled = item.Enabled();
                bool isSel = i == selected;
                bool confirming = i == confirmIndex;

                if (item.Cta)
                {
                    v.Background.color = isSel ? Brand.Hex(0xa0d048) : Brand.Primary;
                    continue;
                }
                if (item.Tile)
                {
                    v.Background.color = isSel ? Brand.PrimarySoft : Brand.SurfaceMuted;
                    v.Accent.color = isSel ? Brand.Primary : Brand.Border;
                    v.Label.color = enabled ? Brand.Fg : Brand.FgFaint;
                    if (values) v.Value.text = item.Value();
                    v.Value.color = enabled ? Brand.FgSubtle : Brand.FgFaint;
                    continue;
                }

                v.Background.enabled = isSel;
                v.Background.color = confirming ? Brand.Danger.WithAlpha(0.22f) : Brand.PrimarySoft;
                v.Accent.enabled = isSel;
                v.Accent.color = confirming ? Brand.Danger : Brand.Primary;
                v.Label.color = !enabled ? Brand.FgFaint : item.Destructive ? Brand.Hex(0xf0a497) : Brand.Fg;
                if (item.Nav) continue;
                // A flag, not a text compare: the prompt's words follow the language.
                if (confirming) { v.Value.text = L.T("Πάτα ξανά", "Press again"); v.ShowsConfirm = true; }
                else if (values || v.ShowsConfirm) { v.Value.text = item.Value(); v.ShowsConfirm = false; }
                v.Value.color = confirming ? Brand.Danger : !enabled ? Brand.FgFaint : Brand.FgSubtle;
            }
        }
    }
}
