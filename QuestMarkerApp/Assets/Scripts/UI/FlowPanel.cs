using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>One row of a <see cref="FlowPanel"/> list.</summary>
    public class FlowItem
    {
        public string Label;
        public string Sub;
        public string Value;
        /// <summary>Brand-kit icon name; null draws no chip.</summary>
        public string Icon;
        public Color Color = Brand.Primary;
        public object Tag;
    }

    /// <summary>One field of a <see cref="FlowPanel"/> form, edited with the in-app <see cref="VrKeyboard"/>.</summary>
    public class FlowField
    {
        public string Label;
        public string Value = "";
        public bool Secret;
        public string Placeholder = "";
    }

    /// <summary>
    /// A floating, modal step-by-step panel: a list to pick from, a form to fill, or a
    /// message (loading / error). Built once; pages swap in place. Stick moves, Trigger or A
    /// picks, B goes back. Text fields open the in-app keyboard.
    /// </summary>
    public class FlowPanel : MonoBehaviour
    {
        private const float Width = 560f;
        private const float RowH = 58f;
        private const int Rows = 8;
        private const float BodyTop = 136f;
        private const float MetersPerPixel = 0.0006f;
        private static readonly float Height = BodyTop + Rows * RowH + 22f;

        public static bool IsOpen { get; private set; }
        public static bool IsForm { get; private set; }

        private CanvasGroup walkFade; // WalkFade: faint while the user walks
        private RectTransform root, listPage, messagePage;
        private TextMeshProUGUI eyebrow, title, listCount, messageTitle, messageText;
        private Image spinner, scrollThumb;
        private RectTransform backButton, searchButton;
        private readonly List<Row> rows = new List<Row>();

        private readonly List<FlowItem> items = new List<FlowItem>();
        private List<FlowField> fields;
        private string submitLabel;
        private Action<FlowItem> onPick;
        private Action<List<FlowField>> onSubmit;
        private Action onBack;
        private int selected;
        private bool messageMode;
        private float nextStickMove;
        // Held stick speeds up: one step per 0.2 s, then ~16 per second after 0.8 s.
        private float stickHeldSince = -1f;

        private float StickRepeatDelay()
        {
            if (stickHeldSince < 0f) stickHeldSince = Time.unscaledTime;
            return Time.unscaledTime - stickHeldSince > 0.8f ? 0.06f : 0.2f;
        }
        private VrKeyboard keyboard;
        // The press that opened a page must not also act on it.
        private int openedFrame;
        private FlowField editing;

        private class Row
        {
            public RectTransform Root;
            public Image Bg, Accent, Chip, Glyph, Field;
            public TextMeshProUGUI Label, Sub, Value;
        }

        // ------------------------------------------------------------------
        //  Pages
        // ------------------------------------------------------------------

        /// <param name="initialSelection">Row to start on, e.g. the one just toggled when a list is re-shown.</param>
        public void ShowList(string eyebrowText, string titleText, List<FlowItem> list, Action<FlowItem> pick,
            Action back, int initialSelection = 0)
        {
            Build();
            items.Clear();
            items.AddRange(list);
            fields = null;
            onPick = pick;
            onSubmit = null;
            onBack = back;
            Open(eyebrowText, titleText, false, false);
            if (initialSelection > 0 && items.Count > 0)
            {
                selected = Mathf.Clamp(initialSelection, 0, items.Count - 1);
                Refresh();
            }
        }

        public void ShowForm(string eyebrowText, string titleText, List<FlowField> formFields, string submit,
            Action<List<FlowField>> submitAction, Action back)
        {
            Build();
            fields = formFields;
            submitLabel = submit;
            items.Clear();
            foreach (FlowField f in fields) items.Add(new FlowItem { Label = f.Label });
            items.Add(new FlowItem { Label = submit });
            onPick = null;
            onSubmit = submitAction;
            onBack = back;
            Open(eyebrowText, titleText, false, true);
        }

        public void ShowMessage(string eyebrowText, string titleText, string heading, string text, bool busy,
            Action back)
        {
            Build();
            onBack = back;
            messageTitle.text = heading;
            messageText.text = text;
            spinner.gameObject.SetActive(busy);
            Open(eyebrowText, titleText, true, false);
        }

        /// <summary>Raised whenever the panel closes, so pending network replies can be dropped.</summary>
        public event Action OnClosed;
        public static bool IsMessage { get; private set; }

        public void Close()
        {
            bool wasOpen = IsOpen;
            VrKeyboard.CancelOpen();
            IsOpen = false;
            IsForm = false;
            IsMessage = false;
            editing = null;
            if (wasOpen)
            {
                UiState.NoteClosed();
                OnClosed?.Invoke();
            }
            if (root != null) root.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            // Static flags outlive the object: never leave the app thinking a panel is up.
            if (!IsOpen) return;
            IsOpen = IsForm = IsMessage = false;
            UiState.NoteClosed();
        }

        private void Open(string eyebrowText, string titleText, bool message, bool form)
        {
            bool wasOpen = IsOpen;
            IsOpen = true;
            openedFrame = Time.frameCount;
            IsForm = form;
            IsMessage = message;
            messageMode = message;
            selected = 0;
            eyebrow.text = eyebrowText.ToUpperInvariant();
            title.text = titleText;
            listPage.gameObject.SetActive(!message);
            messagePage.gameObject.SetActive(message);
            if (!wasOpen && !PanelPin.IsPinned(this)) PlaceInFront();
            root.gameObject.SetActive(true);
            Refresh();
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private void Build()
        {
            if (root != null) return;
            root = VrUi.CreateCanvas("FlowCanvas", transform, new Vector2(Width, Height), MetersPerPixel);
            VrUi.Card("Card", root, 0, 0, Width, Height, Brand.Surface.WithAlpha(0.97f), Brand.BorderStrong, 24);
            VrPointer.RegisterDraggable(root, transform);
            VrUi.Panel("Mark", root, 28, 30, 12, 12, Brand.Primary, 6);
            eyebrow = VrUi.Text("Eyebrow", root, 48, 22, 420, 28, "", 14f, Brand.FgSubtle, true);
            eyebrow.characterSpacing = 8f;
            title = VrUi.Text("Title", root, 28, 54, Width - 140, 40, "", 28f, Brand.Fg, true);
            VrUi.Button("Close", root, Width - 64, 22, 40, 40, "close", null, Close, false, false,
                () => !VrKeyboard.IsOpen);
            backButton = VrUi.Button("Back", root, Width - 112, 22, 40, 40, "chevron-right", null, GoBack, false, true,
                () => !VrKeyboard.IsOpen);
            searchButton = VrUi.Button("Search", root, Width - 160, 22, 40, 40, "search", null, Search, false, false,
                () => !VrKeyboard.IsOpen);
            listCount = VrUi.Text("Count", root, Width - 228, 104, 200, 24, "", 13f, Brand.FgSubtle, false,
                TextAlignmentOptions.MidlineRight);

            listPage = VrUi.Rect("ListPage", root, 0, 0, Width, Height);
            for (int i = 0; i < Rows; i++) rows.Add(BuildRow(i));
            VrUi.Panel("Track", listPage, Width - 18, BodyTop, 4, Rows * RowH, Brand.SurfaceSunken, 2);
            scrollThumb = VrUi.Panel("Thumb", listPage, Width - 18, BodyTop, 4, 40, Brand.FgFaint, 2);

            messagePage = VrUi.Rect("MessagePage", root, 0, 0, Width, Height);
            spinner = VrUi.Icon("Spinner", messagePage, Width / 2f - 28, BodyTop + 70, 56, Brand.IconSprite("sensor-point"),
                Brand.Primary);
            messageTitle = VrUi.Text("Heading", messagePage, 40, BodyTop + 150, Width - 80, 40, "", 22f, Brand.Fg, true,
                TextAlignmentOptions.Center);
            messageText = VrUi.Text("Body", messagePage, 40, BodyTop + 196, Width - 80, 160, "", 16f, Brand.FgMuted,
                false, TextAlignmentOptions.Top);
            messageText.textWrappingMode = TextWrappingModes.Normal;

            root.gameObject.SetActive(false);
        }

        private Row BuildRow(int i)
        {
            float y = BodyTop + i * RowH;
            int slot = i;
            var r = new Row { Root = VrUi.Rect("Row" + i, listPage, 16, y, Width - 44, RowH - 6) };
            // Hover plate under the selection plate: the row the ray is on lights up softly
            // (the selected row's own plate covers it).
            Image hover = VrUi.Panel("Hover", r.Root, 0, 0, Width - 44, RowH - 6, Color.clear, 14);
            // Rows scroll, so a slot maps to whichever item it shows right now.
            // Click only: selecting on hover would re-scroll the list under the cursor.
            VrPointer.Register(r.Root,
                () => { selected = ItemInSlot(slot); Refresh(); Activate(); },
                null,
                () => !messageMode && !VrKeyboard.IsOpen && ItemInSlot(slot) < items.Count,
                hover, Brand.SurfaceSunken.WithAlpha(0.7f));
            r.Bg = VrUi.Panel("Bg", r.Root, 0, 0, Width - 44, RowH - 6, Brand.PrimarySoft, 14);
            r.Accent = VrUi.Panel("Accent", r.Root, 0, 10, 4, RowH - 26, Brand.Primary, 2);
            r.Field = VrUi.Panel("Field", r.Root, 196, 8, Width - 44 - 210, RowH - 22, Brand.SurfaceSunken, 10);
            r.Chip = VrUi.Icon("Chip", r.Root, 14, 8, 36, Brand.DiscSprite, Brand.Primary);
            r.Glyph = VrUi.Icon("Glyph", r.Root, 21, 15, 22, null, Color.white);
            r.Label = VrUi.Text("Label", r.Root, 62, 4, 300, 26, "", 18f, Brand.Fg, true);
            r.Sub = VrUi.Text("Sub", r.Root, 62, 28, 300, 20, "", 13f, Brand.FgSubtle);
            r.Value = VrUi.Text("Value", r.Root, 200, 0, Width - 44 - 214, RowH - 6, "", 15f, Brand.FgMuted, false,
                TextAlignmentOptions.MidlineRight);
            return r;
        }

        // Long lists (areas, sensor picker): jump to the next item containing the query.
        private void Search()
        {
            VrKeyboard.Shared.Open(L.T("Αναζήτηση", "Search"),"", false, q =>
            {
                if (string.IsNullOrWhiteSpace(q)) return;
                for (int step = 1; step <= items.Count; step++)
                {
                    int i = (selected + step) % items.Count;
                    if ((items[i].Label ?? "").IndexOf(q.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        selected = i;
                        break;
                    }
                }
                Refresh();
            }, null);
        }

        private int FirstVisible() => Mathf.Clamp(selected - Rows / 2, 0, Mathf.Max(0, items.Count - Rows));

        private int ItemInSlot(int slot) => FirstVisible() + slot;

        private void GoBack()
        {
            Action b = onBack;
            if (b != null) b(); else Close();
        }

        private const float Distance = 0.65f, Drop = 0.06f;
        private bool gliding;

        private void PlaceInFront()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            gliding = false;
            transform.position = VrUi.InFront(cam.transform, Distance, Drop);
            VrUi.FaceViewer(transform, cam.transform);
        }

        // ------------------------------------------------------------------
        //  Input
        // ------------------------------------------------------------------

        private void Update()
        {
            if (!IsOpen) return;

            // Turned away: glide back in front (not a jump), unless the user parked it.
            Camera cam = Camera.main;
            if (cam != null && !VrPointer.IsDragging && !PanelPin.IsPinned(this))
                VrUi.LazyFollow(transform, cam.transform, Distance, Drop, ref gliding);
            WalkFade.Apply(root, ref walkFade);
            if (spinner.gameObject.activeInHierarchy)
                spinner.rectTransform.Rotate(0f, 0f, -180f * Time.unscaledDeltaTime);

            // The keyboard owns input while it is up (it handles B / cancel itself).
            if (VrKeyboard.IsOpen || VrKeyboard.ClosedFrame == Time.frameCount) return;

            if (Time.frameCount != openedFrame) HandleInput();
        }

        private void HandleInput()
        {
            if (VrPointer.IsDragging) return;
            int move = 0;
            bool select = false, back = false;
#if !MOBILE_PREVIEW
            float stick = Hands.AnyStickY();
            if (Mathf.Abs(stick) > 0.6f && Time.unscaledTime >= nextStickMove)
            {
                move = stick > 0 ? -1 : 1;
                nextStickMove = Time.unscaledTime + StickRepeatDelay();
            }
            else if (Mathf.Abs(stick) < 0.3f) { nextStickMove = 0f; stickHeldSince = -1f; }
            select = (!VrPointer.ConsumedThisFrame && Hands.TriggerDown()) || Hands.AnySelectDown();
            back = Hands.AnyBackDown();
#else
            if (Input.GetKeyDown(KeyCode.UpArrow)) move = -1;
            if (Input.GetKeyDown(KeyCode.DownArrow)) move = 1;
            select = Input.GetKeyDown(KeyCode.Return);
            back = Input.GetKeyDown(KeyCode.Escape);
#endif
            if (back) { Feedback.Play(Feedback.Sound.Tick); GoBack(); return; }
            if (messageMode)
            {
                if (select && !spinner.gameObject.activeSelf) { Feedback.Play(Feedback.Sound.Click); GoBack(); }
                return;
            }

            if (move != 0 && items.Count > 0)
            {
                int before = selected;
                selected = Mathf.Clamp(selected + move, 0, items.Count - 1);
                if (selected != before) Feedback.Play(Feedback.Sound.Tick, 0.35f, 0.05f);
                Refresh();
            }
            // The pointer plays its own click; buttons get the same sound.
            if (select && items.Count > 0) { Feedback.Play(Feedback.Sound.Click); Activate(); }
        }

        private void Activate()
        {
            if (fields != null)
            {
                if (selected < fields.Count)
                {
                    editing = fields[selected];
                    if (keyboard == null) keyboard = VrKeyboard.Shared;
                    FlowField target = editing;
                    int next = selected + 1;
                    keyboard.Open(target.Label, target.Value, target.Secret,
                        v =>
                        {
                            target.Value = v;
                            editing = null;
                            selected = Mathf.Min(next, items.Count - 1); // on to the next field / submit
                            Refresh();
                        },
                        () => { editing = null; Refresh(); });
                }
                else
                {
                    onSubmit?.Invoke(fields);
                }
                return;
            }
            onPick?.Invoke(items[selected]);
        }

        // ------------------------------------------------------------------
        //  Refresh
        // ------------------------------------------------------------------

        private void Refresh()
        {
            backButton.gameObject.SetActive(onBack != null);
            searchButton.gameObject.SetActive(!messageMode && fields == null && items.Count > 12);
            if (messageMode) { listCount.text = ""; return; }

            int n = items.Count;
            listCount.text = fields != null || n == 0 ? "" : $"{selected + 1} / {n}";
            int first = FirstVisible();
            for (int i = 0; i < Rows; i++) RefreshRow(rows[i], first + i);

            float track = Rows * RowH;
            float thumb = n <= Rows ? track : Mathf.Max(28f, track * Rows / n);
            float t = n <= Rows ? 0f : (float)first / (n - Rows);
            scrollThumb.rectTransform.sizeDelta = new Vector2(4f, thumb);
            scrollThumb.rectTransform.anchoredPosition =
                new Vector2(scrollThumb.rectTransform.anchoredPosition.x, -(BodyTop + t * (track - thumb)));
            scrollThumb.enabled = n > Rows;
        }

        private void RefreshRow(Row r, int index)
        {
            bool visible = index < items.Count;
            r.Root.gameObject.SetActive(visible);
            if (!visible) return;

            FlowItem item = items[index];
            bool isSel = index == selected;
            r.Bg.enabled = isSel;
            r.Accent.enabled = isSel;

            bool isField = fields != null && index < fields.Count;
            bool isSubmit = fields != null && index == fields.Count;
            r.Field.enabled = isField;
            r.Chip.enabled = item.Icon != null;
            r.Glyph.enabled = item.Icon != null;
            if (item.Icon != null)
            {
                r.Chip.color = item.Color;
                r.Glyph.sprite = Brand.IconSprite(item.Icon);
            }

            float textX = item.Icon != null ? 62f : 20f;
            r.Label.rectTransform.anchoredPosition = new Vector2(textX, isField || string.IsNullOrEmpty(item.Sub) ? -14f : -4f);
            r.Label.text = item.Label;
            r.Label.color = isSubmit ? Brand.Primary : Brand.Fg;
            r.Sub.rectTransform.anchoredPosition = new Vector2(textX, -28f);
            r.Sub.text = isField ? "" : item.Sub ?? "";

            if (isField)
            {
                FlowField f = fields[index];
                bool empty = string.IsNullOrEmpty(f.Value);
                r.Value.text = empty ? f.Placeholder : f.Secret ? new string('•', Mathf.Min(f.Value.Length, 16)) : f.Value;
                r.Value.color = empty ? Brand.FgFaint : Brand.Fg;
                r.Value.alignment = TextAlignmentOptions.MidlineLeft;
                r.Value.rectTransform.anchoredPosition = new Vector2(212f, 0f);
            }
            else
            {
                r.Value.text = item.Value ?? "";
                r.Value.color = Brand.FgMuted;
                r.Value.alignment = TextAlignmentOptions.MidlineRight;
                r.Value.rectTransform.anchoredPosition = new Vector2(200f, 0f);
            }
        }
    }
}
