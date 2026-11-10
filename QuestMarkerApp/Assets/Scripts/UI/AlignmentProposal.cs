using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// The approved mockup «Αυτόματη πρόταση ευθυγράμμισης» (10/11/2026), in three pieces:
    /// <list type="bullet">
    /// <item>a chip low in the view while the surveyed codes are read in the background
    /// («Θέση από κωδικούς · 1 από 2»);</item>
    /// <item>the proposal card once the alignment is found, from the codes or from the room's
    /// spatial anchor: «Αποδοχή · A» makes it final, the other button aligns in the panel;</item>
    /// <item>a short chip after «Αποδοχή» («Ευθυγραμμισμένο · QR · ± 1,2 cm», 4 s).</item>
    /// </list>
    /// Field only (and never in Practice): the office never asks for an alignment.
    /// </summary>
    public class AlignmentProposal : MonoBehaviour
    {
        private const float MetersPerPixel = 0.0006f;
        private const float ChipW = 560f, ChipH = 52f;
        private const float CardW = 420f, CardH = 320f;
        private const float AcceptedSeconds = 4f;

        private ProjectAlignment alignment;

        private RectTransform chip;
        private TextMeshProUGUI chipText;
        private Image chipIcon;

        private RectTransform card;
        private Image cardIcon;
        private TextMeshProUGUI title, body, tile1Caption, tile1Value, tile2Caption, tile2Value, checkText, note;
        private GameObject tiles, checkRow;
        private TextMeshProUGUI acceptLabel, declineLabel;
        private bool cardPlaced;

        private float acceptedUntil = -1f;
        private string acceptedText;

        public void Init(ProjectAlignment projectAlignment) => alignment = projectAlignment;

        private void Start()
        {
            BuildChip();
            BuildCard();
            L.Changed += Relabel;
            Relabel();
        }

        private void OnDestroy() => L.Changed -= Relabel;

        private bool InField => alignment != null && alignment.HasProject && !alignment.IsPractice && !WorkMode.IsOffice;

        private void Update()
        {
            if (alignment == null || chip == null) return;
            Camera cam = Headset.Camera;
            bool calm = !UiState.AnyModal && !alignment.IsModeActive;

            bool showCard = calm && InField && alignment.IsProvisional;
            if (showCard)
            {
                Fill();
                if (!card.gameObject.activeSelf) Show(cam);
                if (!UiState.InputBlocked && Hands.DominantPrimaryDown()) Accept();
            }
            else if (card.gameObject.activeSelf)
            {
                card.gameObject.SetActive(false);
                cardPlaced = false;
            }

            bool accepted = acceptedUntil > Time.unscaledTime;
            bool reading = calm && InField && alignment.ReadsCodesInBackground;
            bool showChip = calm && !showCard && (accepted || reading);
            if (chip.gameObject.activeSelf != showChip) chip.gameObject.SetActive(showChip);
            if (!showChip) return;
            if (accepted)
            {
                chipText.text = acceptedText;
                chipIcon.sprite = Brand.UiIconSprite("check");
                chipIcon.color = Brand.Primary;
            }
            else
            {
                int read = Mathf.Min(alignment.PlacedCount, ProjectAlignment.ProposalCodes);
                string doing = string.IsNullOrEmpty(alignment.QrStatus)
                    ? L.T("κοίτα έναν κωδικό QM", "look at a QM code")
                    : alignment.QrStatus.TrimEnd('.');
                // Restored from last time (no anchor): one code confirms it, the count says nothing.
                if (alignment.IsAligned && alignment.NeedsCheck)
                    chipText.text = L.En ? $"Checking the position   <color=#8ca09e>{doing}</color>"
                        : $"Έλεγχος θέσης   <color=#8ca09e>{doing}</color>";
                else chipText.text = L.En
                    ? $"Position from codes · <b><color=#8fc93a>{read}</color></b> of {ProjectAlignment.ProposalCodes}   <color=#8ca09e>{doing}</color>"
                    : $"Θέση από κωδικούς · <b><color=#8fc93a>{read}</color></b> από {ProjectAlignment.ProposalCodes}   <color=#8ca09e>{doing}</color>";
                chipIcon.sprite = Brand.UiIconSprite("search");
                chipIcon.color = Brand.Info;
            }
            if (cam != null) FollowLow(chip, cam);
        }

        // ------------------------------------------------------------------
        //  Actions
        // ------------------------------------------------------------------

        private void Accept()
        {
            if (!alignment.IsProvisional) return;
            bool qr = alignment.Proposal == ProjectAlignment.ProposalSource.Qr;
            float rms = alignment.RmsHorizontal;
            alignment.AcceptProposal();
            acceptedText = qr
                ? L.T($"Ευθυγραμμισμένο · QR · ± {Cm(rms)}", $"Aligned · QR · ± {Cm(rms)}")
                : L.T("Ευθυγραμμισμένο · από τον χώρο", "Aligned · from the room");
            acceptedUntil = Time.unscaledTime + AcceptedSeconds;
            Feedback.Play(Feedback.Sound.Success);
            Feedback.Buzz(Hands.Dominant, 0.8f, 0.2f);
        }

        private void Decline()
        {
            if (!alignment.IsProvisional) return;
            Feedback.Play(Feedback.Sound.Click);
            alignment.DeclineProposal();
        }

        // ------------------------------------------------------------------
        //  Content
        // ------------------------------------------------------------------

        private void Relabel()
        {
            if (acceptLabel != null) acceptLabel.text = L.T("Αποδοχή · A", "Accept · A");
        }

        private void Fill()
        {
            bool qr = alignment.Proposal == ProjectAlignment.ProposalSource.Qr;
            tiles.SetActive(qr);
            checkRow.SetActive(!qr);
            cardIcon.sprite = Brand.UiIconSprite(qr ? "check" : "location");
            cardIcon.color = qr ? Brand.Primary : Brand.Info;
            declineLabel.text = qr ? L.T("Όχι, με το χέρι", "No, by hand") : L.T("Νέα ευθυγράμμιση", "Align again");
            if (qr)
            {
                ProjectAlignment.PairCheck pair = alignment.Pair;
                title.text = L.T("Βρέθηκε η θέση σου στο έργο", "Found where you are");
                string d = pair.Valid ? Num(pair.Project, "0.0") : "—";
                body.text = pair.Valid
                    ? L.En ? $"From codes <b>{pair.A}</b> and <b>{pair.B}</b>, {d} m apart."
                        : $"Από τους κωδικούς <b>{pair.A}</b> και <b>{pair.B}</b>, {d} m μεταξύ τους."
                    : L.T("Από τους κωδικούς που διαβάστηκαν.", "From the codes read.");
                tile1Caption.text = L.T("Ακρίβεια", "Accuracy");
                tile1Value.text = "± " + Cm(alignment.RmsHorizontal);
                tile2Caption.text = L.T("Αισθητήρες", "Sensors");
                int n = alignment.Sensors.Count;
                tile2Value.text = L.En ? $"{n} in place" : $"{n} στη θέση τους";
                note.text = L.T("Οι αισθητήρες φαίνονται με διακεκομμένο κύκλο μέχρι να το επιβεβαιώσεις.",
                    "Sensors show with a dashed ring until you confirm.");
            }
            else
            {
                title.text = L.T("Αναγνωρίστηκε ο χώρος σου", "Your room was recognised");
                body.text = L.T("Η ευθυγράμμιση της προηγούμενης φοράς επανήλθε από το spatial anchor.",
                    "Last time's alignment came back from the spatial anchor.");
                string id = alignment.ProposalCheckId;
                if (id == null)
                {
                    checkText.text = L.T("Κοίτα έναν κωδικό για έλεγχο (προαιρετικό)", "Look at a code to check (optional)");
                    checkText.color = Brand.FgMuted;
                }
                else
                {
                    float off = alignment.ProposalCheckOff;
                    checkText.text = L.En ? $"Check with <b>{id}</b>: <b>{Cm(off)}</b> off"
                        : $"Έλεγχος με <b>{id}</b>: απέχει <b>{Cm(off)}</b>";
                    checkText.color = off <= 0.03f ? Brand.Primary : off <= ProjectAlignment.MovedTolerance ? Brand.Warning : Brand.Danger;
                }
                note.text = L.T("Οι αισθητήρες φαίνονται με διακεκομμένο κύκλο μέχρι να το επιβεβαιώσεις.",
                    "Sensors show with a dashed ring until you confirm.");
            }
        }

        private static string Cm(float metres) =>
            Num(metres * 100f, metres < 0.1f ? "0.0" : "0") + " cm";

        // Decimal comma in Greek, point in English.
        private static string Num(float v, string format)
        {
            string s = v.ToString(format, CultureInfo.InvariantCulture);
            return L.En ? s : s.Replace('.', ',');
        }

        // ------------------------------------------------------------------
        //  Placement
        // ------------------------------------------------------------------

        private void Show(Camera cam)
        {
            card.gameObject.SetActive(true);
            if (cam == null || cardPlaced) return;
            cardPlaced = true;
            card.position = VrUi.InFront(cam.transform, 0.9f, 0.05f);
            VrUi.FaceViewer(card, cam.transform);
            Feedback.Play(alignment.Proposal == ProjectAlignment.ProposalSource.Anchor ? Feedback.Sound.Ping : Feedback.Sound.Whoosh);
        }

        // Low in the view and lagging behind the head, like the QR test panel: read at a glance,
        // never in the way of the codes being read.
        private static void FollowLow(Transform t, Camera cam)
        {
            Transform head = cam.transform;
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 target = head.position + flat * 0.8f + Vector3.down * 0.38f;
            t.position = Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
            VrUi.FaceViewer(t, head);
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private void BuildChip()
        {
            chip = VrUi.CreateCanvas("AlignChip", transform, new Vector2(ChipW, ChipH), MetersPerPixel);
            VrUi.Card("Bg", chip, 0, 0, ChipW, ChipH, Brand.Surface.WithAlpha(0.92f), Brand.BorderStrong, 26);
            chipIcon = VrUi.Icon("Icon", chip, 18, 15, 22, Brand.UiIconSprite("search"), Brand.Info);
            chipText = VrUi.Text("Text", chip, 50, 0, ChipW - 66, ChipH, "", 16f, Brand.Fg);
            chipText.richText = true;
            chip.gameObject.SetActive(false);
        }

        private void BuildCard()
        {
            card = VrUi.CreateCanvas("AlignProposalCard", transform, new Vector2(CardW, CardH), MetersPerPixel);
            VrUi.Card("Bg", card, 0, 0, CardW, CardH, Brand.Surface.WithAlpha(0.97f), Brand.BorderStrong, 24);
            VrPointer.RegisterDraggable(card, card);

            cardIcon = VrUi.Icon("Icon", card, 24, 24, 24, Brand.UiIconSprite("check"), Brand.Primary);
            title = VrUi.Text("Title", card, 58, 20, CardW - 82, 32, "", 20f, Brand.Fg, true);
            body = VrUi.Text("Body", card, 24, 60, CardW - 48, 46, "", 15f, Brand.FgMuted);
            body.textWrappingMode = TextWrappingModes.Normal;
            body.alignment = TextAlignmentOptions.TopLeft;

            tiles = VrUi.Rect("Tiles", card, 24, 114, CardW - 48, 62).gameObject;
            float tw = (CardW - 48 - 10) / 2f;
            VrUi.Panel("Tile1", tiles.transform, 0, 0, tw, 62, Brand.SurfaceMuted, 10);
            tile1Caption = VrUi.Text("Cap1", tiles.transform, 12, 8, tw - 24, 18, "", 12f, Brand.FgSubtle);
            tile1Value = VrUi.Text("Val1", tiles.transform, 12, 28, tw - 24, 26, "", 18f, Brand.Primary, true);
            VrUi.Panel("Tile2", tiles.transform, tw + 10, 0, tw, 62, Brand.SurfaceMuted, 10);
            tile2Caption = VrUi.Text("Cap2", tiles.transform, tw + 22, 8, tw - 24, 18, "", 12f, Brand.FgSubtle);
            tile2Value = VrUi.Text("Val2", tiles.transform, tw + 22, 28, tw - 24, 26, "", 18f, Brand.Fg, true);

            checkRow = VrUi.Rect("Check", card, 24, 114, CardW - 48, 62).gameObject;
            VrUi.Panel("CheckBg", checkRow.transform, 0, 0, CardW - 48, 62, Brand.SurfaceMuted, 10);
            checkText = VrUi.Text("CheckText", checkRow.transform, 14, 0, CardW - 76, 62, "", 15f, Brand.FgMuted);
            checkText.textWrappingMode = TextWrappingModes.Normal;

            note = VrUi.Text("Note", card, 24, 186, CardW - 48, 40, "", 13f, Brand.FgSubtle);
            note.textWrappingMode = TextWrappingModes.Normal;
            note.alignment = TextAlignmentOptions.TopLeft;

            float bw = (CardW - 48 - 10) / 2f;
            acceptLabel = ButtonLabel(VrUi.Button("Accept", card, 24, CardH - 76, bw, 52, "check", "Αποδοχή · A", Accept, true));
            declineLabel = ButtonLabel(VrUi.Button("Decline", card, 24 + bw + 10, CardH - 76, bw, 52, null, "Όχι, με το χέρι", Decline));
            card.gameObject.SetActive(false);
        }

        private static TextMeshProUGUI ButtonLabel(RectTransform button) =>
            button.Find(button.name + "Label")?.GetComponent<TextMeshProUGUI>();
    }
}
