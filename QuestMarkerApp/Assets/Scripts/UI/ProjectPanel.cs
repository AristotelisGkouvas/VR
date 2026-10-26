using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// The alignment panel held above the off-hand controller while control points are
    /// touched. Two pages:
    /// <list type="bullet">
    /// <item>Guide: a 3-step stepper, the one sensor to touch next (big name, height, a
    /// distance gauge against an already placed point), a one-word verdict with three small
    /// metrics once aligned, the placed control points with their misfit, and the
    /// "Ολοκλήρωση" / "Λίστα" buttons.</item>
    /// <item>List: every sensor with the brand-kit icon, for picking the target. The stick
    /// opens it; placing a point returns to the guide.</item>
    /// </list>
    /// Pure view — <see cref="ProjectAlignment"/> owns the state and calls <see cref="Refresh"/>.
    /// </summary>
    public class ProjectPanel : MonoBehaviour
    {
        private const float Width = 440f;
        private const float Height = 700f;
        private const float MetersPerPixel = 0.00055f;
        private const float BodyTop = 108f;
        private const float BoxX = 20f;
        private const float BoxW = Width - 40f;
        private const float Inner = BoxW - 32f;
        private const int Rows = 9;
        private const float RowH = 48f;
        private const int MaxControlRows = 4;
        // Good / usable / redo, in metres of RMS residual.
        private const float GoodResidual = 0.05f;
        private const float UsableResidual = 0.15f;
        // Live gauge: full scale ±0.5 m, green zone ±0.10 m.
        private const float GaugeScale = 0.5f;
        private const float GaugeOk = 0.10f;

        private RectTransform root, guidePage, listPage;
        private TextMeshProUGUI title, subtitle;
        private ProjectAlignment owner;

        // Guide page blocks (stacked top to bottom; hidden ones take no room).
        private RectTransform stepperBlock, messageBlock, targetBlock, verdictBlock, controlBlock, actionsBlock;
        private readonly List<(Image bar, TextMeshProUGUI text)> steps = new List<(Image, TextMeshProUGUI)>();
        private TextMeshProUGUI messageText;
        private TextMeshProUGUI targetLabel, targetName, targetMeta, targetInstr, targetWhy, gaugeRead, gaugeDh;
        private Image targetChip, targetGlyph, gaugeBar, gaugeOkZone, gaugeNeedle;
        private RectTransform gaugeBlock;
        private Image verdictDot;
        private TextMeshProUGUI verdictWord, verdictHint, metricH, metricV, metricYaw;
        private TextMeshProUGUI controlCount, controlMore, controlUndo;
        private readonly List<ControlRow> controlRows = new List<ControlRow>();
        private Image doneButton;
        private TextMeshProUGUI doneLabel;

        // List page.
        private TextMeshProUGUI listCount;
        private Image scrollThumb;
        private readonly List<Row> rows = new List<Row>();
        private float listTop;

        private bool showingList;
        private int lastSelected = -1, lastPlaced = -1;

        private class Row
        {
            public RectTransform Root;
            public Image Background, Accent, Chip, Glyph, BadgeBg;
            public TextMeshProUGUI Name, Height, Badge;
        }

        private class ControlRow
        {
            public RectTransform Root;
            public Image Number, BadgeBg;
            public TextMeshProUGUI Index, Name, Badge;
        }

        public void Build(ProjectAlignment alignment)
        {
            owner = alignment;
            if (root != null) return;
            root = VrUi.CreateCanvas("ProjectPanelCanvas", transform, new Vector2(Width, Height), MetersPerPixel);

            VrUi.Card("Card", root, 0, 0, Width, Height, Brand.Surface.WithAlpha(0.96f), Brand.BorderStrong, 22);
            VrPointer.RegisterDraggable(root, transform);

            // Header
            VrUi.Panel("Mark", root, 24, 26, 10, 10, Brand.Primary, 5);
            VrUi.Text("Eyebrow", root, 42, 18, 300, 26, "ΕΥΘΥΓΡΑΜΜΙΣΗ", 13f, Brand.FgSubtle, true)
                .characterSpacing = 6f;
            title = VrUi.Text("Title", root, 24, 44, Width - 124, 36, "", 26f, Brand.Fg, true);
            VrUi.Button("Close", root, Width - 58, 18, 38, 38, "close", null, () => owner.SetMode(false));
            // 600-sensor areas: type part of a name instead of scrolling for a minute.
            VrUi.Button("Search", root, Width - 102, 18, 38, 38, "search", null,
                () => VrKeyboard.Shared.Open("Αναζήτηση αισθητήρα", "", false,
                    q => { if (owner.SelectMatch(q)) ShowList(true); }, null),
                enabled: () => !UiState.AnyModal);
            subtitle = VrUi.Text("Subtitle", root, 24, 80, Width - 48, 22, "", 14f, Brand.FgSubtle);

            guidePage = VrUi.Rect("Guide", root, 0, BodyTop, Width, Height - BodyTop);
            BuildStepper();
            BuildMessage();
            BuildTarget();
            BuildVerdict();
            BuildControlPoints();
            BuildActions();

            listPage = VrUi.Rect("List", root, 0, BodyTop, Width, Height - BodyTop);
            BuildList();
            listPage.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------
        //  Guide page blocks
        // ------------------------------------------------------------------

        private void BuildStepper()
        {
            stepperBlock = VrUi.Rect("Stepper", guidePage, BoxX, 0, BoxW, 34);
            string[] names = { "Σημείο 1", "Σημείο 2", "Έλεγχος (3ο)" };
            float segW = (BoxW - 12f) / 3f;
            for (int i = 0; i < 3; i++)
            {
                float x = i * (segW + 6f);
                Image bar = VrUi.Panel("Bar" + i, stepperBlock, x, 0, segW, 6, Brand.SurfaceSunken, 3);
                var text = VrUi.Text("Step" + i, stepperBlock, x, 10, segW, 20, names[i], 12f, Brand.FgSubtle, true);
                steps.Add((bar, text));
            }
        }

        private void BuildMessage()
        {
            messageBlock = VrUi.Rect("Message", guidePage, BoxX, 0, BoxW, 40);
            messageText = VrUi.Text("Text", messageBlock, 4, 0, BoxW - 8, 40, "", 13f, Brand.Warning);
            messageText.textWrappingMode = TextWrappingModes.Normal;
        }

        private void BuildTarget()
        {
            targetBlock = VrUi.Rect("Target", guidePage, BoxX, 0, BoxW, 196);
            VrUi.Panel("Bg", targetBlock, 0, 0, BoxW, 196, Brand.SurfaceMuted, 14);
            targetLabel = VrUi.Text("Label", targetBlock, 16, 10, Inner, 20, "", 12f, Brand.FgSubtle, true);
            targetLabel.characterSpacing = 5f;

            targetChip = VrUi.Icon("Chip", targetBlock, 16, 36, 56, Brand.DiscSprite, Brand.Primary);
            targetGlyph = VrUi.Icon("Glyph", targetBlock, 28, 48, 32, null, Color.white);
            targetName = VrUi.Text("Name", targetBlock, 86, 34, Inner - 70, 34, "", 28f, Brand.Fg, true);
            targetMeta = VrUi.Text("Meta", targetBlock, 86, 68, Inner - 70, 20, "", 13f, Brand.FgSubtle);

            VrUi.Panel("TipDot", targetBlock, 18, 106, 14, 14, new Color(1f, 0.2f, 0.9f), 7);
            targetInstr = VrUi.Text("Instr", targetBlock, 42, 100, Inner - 26, 26, "", 14f, Brand.FgMuted);

            // Gauge: walk until the needle sits in the green zone, then pull the trigger.
            gaugeBlock = VrUi.Rect("Gauge", targetBlock, 16, 130, Inner, 60);
            gaugeRead = VrUi.Text("Read", gaugeBlock, 0, 0, Inner, 22, "", 14f, Brand.Fg);
            gaugeBar = VrUi.Panel("Bar", gaugeBlock, 0, 26, Inner, 14, Brand.SurfaceSunken, 7);
            float okW = Inner * (GaugeOk / GaugeScale);
            gaugeOkZone = VrUi.Panel("Ok", gaugeBlock, Inner / 2f - okW / 2f, 26, okW, 14, Brand.Primary.WithAlpha(0.25f), 7);
            VrUi.Panel("Mid", gaugeBlock, Inner / 2f - 1f, 24, 2, 18, Brand.Primary, 1);
            gaugeNeedle = VrUi.Panel("Needle", gaugeBlock, Inner / 2f - 6f, 27, 12, 12, Brand.Primary, 6);
            gaugeDh = VrUi.Text("Dh", gaugeBlock, 0, 42, Inner, 18, "", 12f, Brand.FgSubtle);

            targetWhy = VrUi.Text("Why", targetBlock, 16, 130, Inner, 44, "", 13f, Brand.FgMuted);
            targetWhy.textWrappingMode = TextWrappingModes.Normal;
        }

        private void BuildVerdict()
        {
            verdictBlock = VrUi.Rect("Verdict", guidePage, BoxX, 0, BoxW, 112);
            VrUi.Panel("Bg", verdictBlock, 0, 0, BoxW, 112, Brand.SurfaceMuted, 14);
            verdictDot = VrUi.Panel("Dot", verdictBlock, 16, 18, 10, 10, Brand.Primary, 5);
            verdictWord = VrUi.Text("Word", verdictBlock, 34, 10, Inner - 18, 26, "", 17f, Brand.Primary, true);
            verdictHint = VrUi.Text("Hint", verdictBlock, 16, 36, Inner, 20, "", 12.5f, Brand.FgMuted);
            metricH = Metric(verdictBlock, 16, 62, "Οριζόντιο");
            metricV = Metric(verdictBlock, 16 + Inner / 3f, 62, "Κατακόρυφο");
            metricYaw = Metric(verdictBlock, 16 + Inner * 2f / 3f, 62, "Γωνία");
        }

        private static TextMeshProUGUI Metric(Transform parent, float x, float y, string caption)
        {
            VrUi.Text("Caption", parent, x, y, 120, 16, caption, 11.5f, Brand.FgSubtle);
            return VrUi.Text("Value", parent, x, y + 16, 120, 24, "", 17f, Brand.Fg, true);
        }

        private void BuildControlPoints()
        {
            float h = 36f + 16f + MaxControlRows * 28f + 20f + 26f + 12f;
            controlBlock = VrUi.Rect("Control", guidePage, BoxX, 0, BoxW, h);
            VrUi.Text("Header", controlBlock, 4, 4, 240, 24, "ΣΗΜΕΙΑ ΕΛΕΓΧΟΥ", 12f, Brand.FgSubtle, true)
                .characterSpacing = 5f;
            controlCount = VrUi.Text("Count", controlBlock, BoxW - 104, 4, 100, 24, "", 12f, Brand.FgSubtle, false,
                TextAlignmentOptions.MidlineRight);
            // The box itself is resized per refresh to the rows it shows.
            var bg = VrUi.Panel("Bg", controlBlock, 0, 32, BoxW, h - 32, Brand.SurfaceMuted, 14);
            bg.name = "ControlBg";
            for (int i = 0; i < MaxControlRows; i++)
            {
                var r = new ControlRow { Root = VrUi.Rect("Cp" + i, controlBlock, 16, 44 + i * 28, Inner, 26) };
                r.Number = VrUi.Panel("N", r.Root, 0, 1, 24, 24, Brand.Control, 12);
                r.Index = VrUi.Text("Idx", r.Root, 0, 1, 24, 24, (i + 1).ToString(), 12f, Brand.PrimaryFg, true,
                    TextAlignmentOptions.Center);
                r.Name = VrUi.Text("Name", r.Root, 34, 0, Inner - 34 - 110, 26, "", 14.5f, Brand.Fg, true);
                r.BadgeBg = VrUi.Panel("BadgeBg", r.Root, Inner - 104, 2, 104, 22, Brand.Control.WithAlpha(0.16f), 11);
                r.Badge = VrUi.Text("Badge", r.Root, Inner - 104, 2, 104, 22, "", 10.5f, Brand.Control, true,
                    TextAlignmentOptions.Center);
                controlRows.Add(r);
            }
            controlMore = VrUi.Text("More", controlBlock, 16, 44 + MaxControlRows * 28, Inner, 20, "", 12f, Brand.FgSubtle);
            controlUndo = VrUi.Text("Undo", controlBlock, 16, 44 + MaxControlRows * 28 + 20, Inner, 24, "", 12.5f, Brand.FgSubtle);
        }

        private void BuildActions()
        {
            actionsBlock = VrUi.Rect("Actions", guidePage, BoxX, 0, BoxW, 46);
            RectTransform done = VrUi.Button("Done", actionsBlock, 0, 0, BoxW - 118, 46, null, "Ολοκλήρωση",
                () => owner.SetMode(false), true, false, () => CanFinish(owner));
            doneButton = done.GetComponent<Image>();
            doneLabel = done.GetComponentInChildren<TextMeshProUGUI>();
            VrUi.Button("ListBtn", actionsBlock, BoxW - 110, 0, 110, 46, "list", "Λίστα", () => ShowList(true));
        }

        private static bool CanFinish(ProjectAlignment a) =>
            a != null && a.IsAligned && !a.NeedsCheck && string.IsNullOrEmpty(a.WorstId);

        // ------------------------------------------------------------------
        //  List page
        // ------------------------------------------------------------------

        private void BuildList()
        {
            listTop = 30f;
            VrUi.Text("ListHeader", listPage, 24, 0, 200, 24, "ΑΙΣΘΗΤΗΡΕΣ", 13f, Brand.FgSubtle, true)
                .characterSpacing = 6f;
            listCount = VrUi.Text("ListCount", listPage, Width - 224, 0, 200, 24, "", 13f, Brand.FgSubtle,
                false, TextAlignmentOptions.MidlineRight);
            for (int i = 0; i < Rows; i++) rows.Add(BuildRow(i));
            VrUi.Panel("ScrollTrack", listPage, Width - 18, listTop, 4, Rows * RowH, Brand.SurfaceSunken, 2);
            scrollThumb = VrUi.Panel("ScrollThumb", listPage, Width - 18, listTop, 4, 40, Brand.FgFaint, 2);
            VrUi.Button("Guide", listPage, BoxX, Height - BodyTop - 62, 150, 46, "chevron-right", "Οδηγός",
                () => ShowList(false), false, true);
        }

        private Row BuildRow(int i)
        {
            var r = new Row { Root = VrUi.Rect("Row" + i, listPage, 16, listTop + i * RowH, Width - 44, RowH - 4) };
            int slot = i;
            // Hover plate under the selection plate, as in FlowPanel: the row under the ray lights up.
            Image hover = VrUi.Panel("Hover", r.Root, 0, 0, Width - 44, RowH - 4, Color.clear, 12);
            VrPointer.Register(r.Root, () => owner.Select(SlotIndex(slot)), null,
                () => SlotIndex(slot) < owner.Points.Count, hover, Brand.SurfaceSunken.WithAlpha(0.7f));
            r.Background = VrUi.Panel("Bg", r.Root, 0, 0, Width - 44, RowH - 4, Brand.PrimarySoft, 12);
            r.Accent = VrUi.Panel("Accent", r.Root, 0, 8, 4, RowH - 20, Brand.Primary, 2);
            r.Chip = VrUi.Icon("Chip", r.Root, 14, 6, 32, Brand.DiscSprite, Brand.FgFaint);
            r.Glyph = VrUi.Icon("Glyph", r.Root, 20, 12, 20, null, Color.white);
            r.Name = VrUi.Text("Name", r.Root, 58, 0, 160, RowH - 4, "", 17f, Brand.Fg, true);
            r.BadgeBg = VrUi.Panel("BadgeBg", r.Root, 214, 11, 96, 22, Brand.Control.WithAlpha(0.16f), 11);
            r.Badge = VrUi.Text("Badge", r.Root, 214, 11, 96, 22, "", 11f, Brand.Control, true,
                TextAlignmentOptions.Center);
            r.Height = VrUi.Text("H", r.Root, 312, 0, Width - 44 - 324, RowH - 4, "", 15f, Brand.FgMuted,
                false, TextAlignmentOptions.MidlineRight);
            return r;
        }

        private int SlotIndex(int slot)
        {
            int n = owner.Points.Count;
            return Mathf.Clamp(owner.SelectedIndex - Rows / 2, 0, Mathf.Max(0, n - Rows)) + slot;
        }

        // Every opening starts on the guide; the browsing heuristic below needs a fresh baseline.
        private void OnEnable()
        {
            lastSelected = lastPlaced = -1;
            if (guidePage != null) ShowListSilently(false);
        }

        private void ShowList(bool show)
        {
            showingList = show;
            guidePage.gameObject.SetActive(!show);
            listPage.gameObject.SetActive(show);
            if (owner != null) Refresh(owner);
        }

        // ------------------------------------------------------------------
        //  Refresh
        // ------------------------------------------------------------------

        public void Refresh(ProjectAlignment a)
        {
            if (root == null || a == null || !a.HasProject) return;

            IReadOnlyList<ProjectPoint> points = a.Points;
            title.text = a.ProjectName;
            int n = a.PlacedCount;
            subtitle.text = a.IsAligned
                ? $"{Plural(n)} ελέγχου · ευθυγραμμισμένο"
                : $"{points.Count} αισθητήρες · {Plural(n)} ελέγχου";

            // Browsing (the selection moved, nothing placed) shows the list; a touch or undo
            // (the placed count changed) brings the guide back.
            if (lastPlaced >= 0 && n != lastPlaced && showingList) ShowListSilently(false);
            else if (lastSelected >= 0 && a.SelectedIndex != lastSelected && n == lastPlaced && !showingList)
                ShowListSilently(true);
            lastSelected = a.SelectedIndex;
            lastPlaced = n;

            if (showingList) RefreshList(a, points);
            else RefreshGuide(a);
        }

        private void ShowListSilently(bool show)
        {
            showingList = show;
            guidePage.gameObject.SetActive(!show);
            listPage.gameObject.SetActive(show);
        }

        private static string Plural(int n) => n == 1 ? "1 σημείο" : $"{n} σημεία";

        private void RefreshGuide(ProjectAlignment a)
        {
            int n = a.PlacedCount;
            IReadOnlyList<string> order = a.PlacementOrder;

            // Stepper
            for (int i = 0; i < 3; i++)
            {
                bool done = i < n;
                bool current = i == n;
                steps[i].bar.color = done ? Brand.Control : current ? Brand.Primary : Brand.SurfaceSunken;
                steps[i].text.color = current ? Brand.Fg : Brand.FgSubtle;
                steps[i].text.text = done && i < order.Count ? DisplayName(a, order[i])
                    : i == 0 ? "Σημείο 1" : i == 1 ? "Σημείο 2" : "Έλεγχος (3ο)";
            }

            bool worst = !string.IsNullOrEmpty(a.WorstId);
            bool showTarget = a.NeedsCheck || !a.IsAligned || n < 3 || worst;
            bool showVerdict = a.IsAligned && !a.NeedsCheck;
            // With both the target and the verdict up there is no room for a third note, and the
            // verdict's hint already carries the detail.
            bool showMessage = !string.IsNullOrEmpty(a.LastMessage) && !(showTarget && showVerdict);
            messageText.text = a.LastMessage ?? "";
            messageText.color = a.LastMessage != null && (a.LastMessage.StartsWith("Έλεγχος OK") || a.LastMessage.StartsWith("Η θέση"))
                ? Brand.Primary : Brand.Warning;

            if (showTarget) RefreshTarget(a);
            if (showVerdict) RefreshVerdict(a);
            RefreshControlPoints(a);

            // Stack the visible blocks.
            float y = 0f;
            y = Place(stepperBlock, true, y, 34f, 14f);
            y = Place(messageBlock, showMessage, y, 40f, 8f);
            y = Place(targetBlock, showTarget, y, targetBlock.sizeDelta.y, 10f);
            y = Place(verdictBlock, showVerdict, y, 112f, 10f);
            y = Place(controlBlock, n > 0, y, controlBlock.sizeDelta.y, 10f);
            // Actions sit at the bottom unless the content runs past them.
            float actionsY = Mathf.Max(y, Height - BodyTop - 62f);
            Place(actionsBlock, true, actionsY, 46f, 0f);

            bool canFinish = CanFinish(a);
            doneButton.color = canFinish ? Brand.Primary : Brand.Primary.WithAlpha(0.35f);
            doneLabel.color = canFinish ? Brand.PrimaryFg : Brand.PrimaryFg.WithAlpha(0.6f);
            doneLabel.text = canFinish && n == 2 ? "Ολοκλήρωση χωρίς έλεγχο" : "Ολοκλήρωση";
        }

        private static float Place(RectTransform block, bool show, float y, float h, float gap)
        {
            block.gameObject.SetActive(show);
            if (!show) return y;
            block.anchoredPosition = new Vector2(block.anchoredPosition.x, -y);
            return y + h + gap;
        }

        private void RefreshTarget(ProjectAlignment a)
        {
            int n = a.PlacedCount;
            bool worst = !string.IsNullOrEmpty(a.WorstId);
            ProjectPoint target = null;
            if (worst) a.TryGetPoint(ProjectAlignment.MarkerPrefix + a.WorstId, out target);
            if (target == null && a.SelectedIndex >= 0 && a.SelectedIndex < a.Points.Count) target = a.Points[a.SelectedIndex];

            string label = a.NeedsCheck ? "ΕΠΙΒΕΒΑΙΩΣΗ ΘΕΣΗΣ"
                : worst ? "ΞΑΝΑ-ΑΓΓΙΞΕ"
                : n == 0 ? "ΠΡΩΤΟ ΣΗΜΕΙΟ"
                : n == 1 ? "ΕΠΟΜΕΝΟ ΣΗΜΕΙΟ" : "ΣΗΜΕΙΟ ΕΛΕΓΧΟΥ (ΠΡΟΑΙΡΕΤΙΚΟ)";
            targetLabel.text = label;
            targetLabel.color = worst ? Brand.Danger : a.NeedsCheck ? Brand.Warning : Brand.FgSubtle;

            if (target != null)
            {
                bool placed = a.IsPlaced(target.id);
                targetChip.color = placed ? Brand.Control
                    : ColorUtility.TryParseHtmlString(target.color ?? "", out Color c) ? c : Brand.Primary;
                targetGlyph.sprite = Brand.IconSprite(target.icon);
                targetGlyph.enabled = targetGlyph.sprite != null;
                targetGlyph.color = placed ? Brand.PrimaryFg : Color.white;
                targetName.text = string.IsNullOrEmpty(target.label) ? target.id : target.label;
                string meta = $"H {target.h:F2}";
                if (placed) meta += " · σημείο ελέγχου";
                else if (target.id == a.SuggestedId) meta += " · <color=#5bcce5>προτεινόμενο</color>";
                targetMeta.text = meta;
            }
            else
            {
                targetName.text = "—";
                targetMeta.text = "";
            }

            targetInstr.text = a.NeedsCheck
                ? "Ακούμπησε ένα γνωστό σημείο και πάτα Trigger"
                : "Ροζ μύτη στο σημείο και Trigger";

            // Gauge, or a short reason, under the instruction.
            ProjectAlignment.LiveGuide g = a.Live;
            bool gauge = g.Valid && !a.NeedsCheck && !worst && target != null && !a.IsPlaced(target.id);
            gaugeBlock.gameObject.SetActive(gauge);
            float extra = 0f;
            if (gauge)
            {
                RefreshGauge(g);
                extra = 60f;
            }

            string why = a.NeedsCheck
                ? "Η ευθυγράμμιση είναι από προηγούμενη φορά. Κατά προτίμηση ένα από τα σημεία ελέγχου."
                : worst ? $"<color=#db5a46>Απέχει {Centimetres(a.Residual(a.WorstId) ?? 0f)} από τα άλλα.</color> {Hands.SecondaryName}: αναίρεση."
                : n == 2 ? "Με 2 σημεία το σφάλμα δεν ελέγχεται."
                : "";
            bool showWhy = !gauge && !string.IsNullOrEmpty(why);
            targetWhy.gameObject.SetActive(showWhy);
            targetWhy.text = why;
            if (showWhy) extra = 44f;

            float h = 130f + extra + (extra > 0f ? 8f : 0f);
            targetBlock.sizeDelta = new Vector2(BoxW, h);
            targetBlock.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(BoxW, h);
        }

        /// <summary>
        /// The fast-changing part: distance from the tip to an already-placed point against the
        /// project's distance, so the user can walk until they match before placing.
        /// </summary>
        public void RefreshLive(ProjectAlignment a)
        {
            if (root == null || a == null || showingList || !a.HasProject) return;
            ProjectAlignment.LiveGuide g = a.Live;
            // The guide becomes valid (or not) between state refreshes: re-lay the target box then.
            bool shown = targetBlock.gameObject.activeSelf && gaugeBlock.gameObject.activeSelf;
            if (g.Valid != shown) { Refresh(a); return; }
            if (g.Valid) RefreshGauge(g);
        }

        // What the gauge texts show, so they are only re-formatted when a printed digit changes:
        // RefreshLive runs every frame while aligning (two strings and a hex lookup per frame).
        private string gaugeRef;
        private long gaugeReadKey = long.MinValue, gaugeDhKey = long.MinValue;

        private void RefreshGauge(ProjectAlignment.LiveGuide g)
        {
            float diff = g.Measured - g.Expected;
            int tone = Mathf.Abs(diff) <= GaugeOk ? 0 : Mathf.Abs(diff) <= GaugeScale ? 1 : 2;
            Color col = tone == 0 ? Brand.Primary : tone == 1 ? Brand.Warning : Brand.Danger;
            long readKey = (Mathf.RoundToInt(g.Measured * 100f) * 1_000_000L + Mathf.RoundToInt(g.Expected * 100f)) * 4 + tone;
            if (readKey != gaugeReadKey || g.RefId != gaugeRef)
            {
                gaugeReadKey = readKey;
                gaugeRef = g.RefId;
                string hex = ColorUtility.ToHtmlStringRGB(col);
                gaugeRead.text = $"Από {g.RefId}: <color=#{hex}><b>{g.Measured:F2} m</b></color> <color=#8ca09e>/ πρέπει {g.Expected:F2} m</color>";
            }
            float t = Mathf.Clamp(0.5f + diff / (2f * GaugeScale), 0.02f, 0.98f);
            gaugeNeedle.color = col;
            gaugeNeedle.rectTransform.anchoredPosition = new Vector2(t * Inner - 6f, -27f);
            // ΔH only when the height is clearly off; otherwise it is noise here.
            float dhOff = g.MeasuredDh - g.ExpectedDh;
            long dhKey = Mathf.Abs(dhOff) > 0.3f
                ? Mathf.RoundToInt(g.MeasuredDh * 100f) * 1_000_000L + Mathf.RoundToInt(g.ExpectedDh * 100f)
                : long.MaxValue;
            if (dhKey == gaugeDhKey) return;
            gaugeDhKey = dhKey;
            gaugeDh.text = dhKey != long.MaxValue
                ? $"ΔH {Signed(g.MeasuredDh)} / πρέπει {Signed(g.ExpectedDh)}" : "";
        }

        private void RefreshVerdict(ProjectAlignment a)
        {
            int n = a.PlacedCount;
            float worst = Mathf.Max(a.RmsHorizontal, a.RmsVertical);
            ProjectAlignment.PairCheck pair = a.Pair;
            Color q;
            string word, hint;

            if (!string.IsNullOrEmpty(a.WorstId))
            {
                q = Brand.Danger;
                word = "Ένα σημείο διαφωνεί";
                hint = $"Ξανατοποθέτησε το {a.WorstId} · απόκλιση {Centimetres(a.Residual(a.WorstId) ?? 0f)}";
            }
            else if (n == 2)
            {
                // Two points always fit their rotation exactly: only their distance is checked.
                q = worst > UsableResidual ? Brand.Danger : Brand.Warning;
                word = worst > UsableResidual ? "Μεγάλο σφάλμα" : "Χωρίς έλεγχο";
                hint = pair.Valid && pair.Project < 5f
                    ? $"Βάση {pair.Project:F1} m, χρειάζονται τουλάχιστον 5 m"
                    : pair.Valid
                        ? $"Εκτίμηση ±{Centimetres(a.PredictedError)} στα {a.FarthestDistance:F0} m · {Signed(pair.Measured - pair.Project)} στη βάση"
                        : "Πρόσθεσε ένα 3ο σημείο για έλεγχο";
                if (!string.IsNullOrEmpty(a.SuggestedId) && (!pair.Valid || pair.Project >= 5f))
                    hint += $" · πρόταση {a.SuggestedId}";
            }
            else
            {
                q = worst <= GoodResidual ? Brand.Primary : worst <= UsableResidual ? Brand.Warning : Brand.Danger;
                word = worst <= GoodResidual ? "Καλή ακρίβεια" : worst <= UsableResidual ? "Μέτρια ακρίβεια" : "Μεγάλο σφάλμα";
                hint = worst <= GoodResidual
                    ? $"{Plural(n)} ελέγχου συμφωνούν"
                    : "Ξανατοποθέτησε το σημείο με το μεγαλύτερο σφάλμα";
            }

            verdictDot.color = q;
            verdictWord.color = q;
            verdictWord.text = word;
            verdictHint.text = hint;
            metricH.text = Centimetres(a.RmsHorizontal);
            metricV.text = Centimetres(a.RmsVertical);
            metricYaw.text = $"{a.YawDeg:F1}°";
        }

        private void RefreshControlPoints(ProjectAlignment a)
        {
            IReadOnlyList<string> order = a.PlacementOrder;
            int n = order.Count;
            controlCount.text = n.ToString();
            int shown = Mathf.Min(n, MaxControlRows);
            for (int i = 0; i < MaxControlRows; i++)
            {
                ControlRow r = controlRows[i];
                bool visible = i < shown;
                r.Root.gameObject.SetActive(visible);
                if (!visible) continue;
                string id = order[i];
                r.Name.text = DisplayName(a, id);
                float? res = a.Residual(id);
                bool suspect = id == a.WorstId;
                Color tone = suspect ? Brand.Danger
                    : res == null || res <= GoodResidual ? Brand.Control
                    : res <= UsableResidual ? Brand.Warning : Brand.Danger;
                r.Number.color = suspect ? Brand.Danger : Brand.Control;
                r.Badge.text = res == null ? "τοποθετήθηκε" : Centimetres(res.Value);
                r.Badge.color = tone;
                r.BadgeBg.color = tone.WithAlpha(0.16f);
            }
            bool more = n > MaxControlRows;
            controlMore.gameObject.SetActive(more);
            controlMore.text = more ? $"+{n - MaxControlRows} ακόμη" : "";
            controlUndo.text = $"{Hands.SecondaryName}: αναίρεση του τελευταίου";

            float rowsH = shown * 28f + (more ? 20f : 0f);
            controlMore.rectTransform.anchoredPosition = new Vector2(16f, -(44f + shown * 28f));
            controlUndo.rectTransform.anchoredPosition = new Vector2(16f, -(44f + rowsH));
            float h = 32f + 12f + rowsH + 26f + 10f;
            controlBlock.sizeDelta = new Vector2(BoxW, h);
            controlBlock.Find("ControlBg").GetComponent<RectTransform>().sizeDelta = new Vector2(BoxW, h - 32f);
        }

        private static string DisplayName(ProjectAlignment a, string id)
        {
            return a.TryGetPoint(ProjectAlignment.MarkerPrefix + id, out ProjectPoint p) && !string.IsNullOrEmpty(p.label)
                ? p.label : id;
        }

        private static string Signed(float v) => (v >= 0 ? "+" : "−") + Mathf.Abs(v).ToString("F2") + " m";

        private static string Centimetres(float metres) =>
            metres < 1f ? $"{metres * 100f:F1} cm" : $"{metres:F2} m";

        // ---- list page ----

        private void RefreshList(ProjectAlignment a, IReadOnlyList<ProjectPoint> points)
        {
            int n = points.Count;
            int first = Mathf.Clamp(a.SelectedIndex - Rows / 2, 0, Mathf.Max(0, n - Rows));
            listCount.text = $"{a.SelectedIndex + 1} / {n}";
            for (int i = 0; i < Rows; i++) RefreshRow(rows[i], a, first + i);

            // Scroll thumb proportional to the visible window.
            float track = Rows * RowH;
            float thumb = n <= Rows ? track : Mathf.Max(28f, track * Rows / n);
            float t = n <= Rows ? 0f : (float)first / (n - Rows);
            var thumbRt = scrollThumb.rectTransform;
            thumbRt.sizeDelta = new Vector2(4f, thumb);
            thumbRt.anchoredPosition = new Vector2(thumbRt.anchoredPosition.x, -(listTop + t * (track - thumb)));
        }

        private static void RefreshRow(Row r, ProjectAlignment a, int index)
        {
            IReadOnlyList<ProjectPoint> points = a.Points;
            bool visible = index < points.Count;
            r.Root.gameObject.SetActive(visible);
            if (!visible) return;

            ProjectPoint p = points[index];
            bool selected = index == a.SelectedIndex;
            bool placed = a.IsPlaced(p.id);
            bool suggested = !placed && p.id == a.SuggestedId;

            r.Background.enabled = selected;
            r.Accent.enabled = selected;
            r.Chip.color = placed ? Brand.Control
                : ColorUtility.TryParseHtmlString(p.color ?? "", out Color c) ? c : Brand.Primary;
            r.Glyph.sprite = Brand.IconSprite(p.icon);
            r.Glyph.enabled = r.Glyph.sprite != null;
            r.Glyph.color = placed ? Brand.PrimaryFg : Color.white;
            r.Name.text = string.IsNullOrEmpty(p.label) ? p.id : p.label;
            r.Name.color = selected ? Brand.Fg : Brand.FgMuted;

            // Badge: control point (with its own misfit once aligned), or the suggested next one.
            bool badge = placed || suggested;
            r.BadgeBg.enabled = badge;
            r.Badge.enabled = badge;
            if (placed)
            {
                float? res = a.Residual(p.id);
                bool suspect = p.id == a.WorstId;
                Color tone = suspect ? Brand.Danger
                    : res == null || res <= GoodResidual ? Brand.Control
                    : res <= UsableResidual ? Brand.Warning : Brand.Danger;
                r.Badge.text = res == null ? "ΕΛΕΓΧΟΣ" : "ΕΛΕΓΧ · " + Centimetres(res.Value);
                r.Badge.color = tone;
                r.BadgeBg.color = tone.WithAlpha(0.16f);
            }
            else if (suggested)
            {
                r.Badge.text = "ΠΡΟΤΑΣΗ";
                r.Badge.color = Brand.Info;
                r.BadgeBg.color = Brand.Info.WithAlpha(0.16f);
            }
            r.Height.text = $"H {p.h:F2}";
        }
    }
}
