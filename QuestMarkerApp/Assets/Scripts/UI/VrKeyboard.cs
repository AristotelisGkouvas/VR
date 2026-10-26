using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// In-app keyboard for the login form. The Quest system keyboard only appears for a
    /// focused Android text editor, which a Unity VR app doesn't have — it was hidden on open
    /// ("HIDE_SAME_WINDOW_FOCUSED_WITHOUT_EDITOR"). Keys are clicked with the controller
    /// pointer; a paired Bluetooth keyboard types too. B or Cancel always gets you out.
    /// </summary>
    public class VrKeyboard : MonoBehaviour
    {
        private const float KeyW = 52f, KeyH = 52f, Gap = 6f, Pad = 20f;
        private const float Width = Pad * 2 + 10 * KeyW + 9 * Gap;
        private const float FieldH = 64f;
        private const float MetersPerPixel = 0.0006f;

        public static bool IsOpen { get; private set; }
        private static VrKeyboard shared;

        /// <summary>The one keyboard, created on first use.</summary>
        public static VrKeyboard Shared
        {
            get
            {
                if (shared != null) return shared;
                shared = new GameObject("VrKeyboard").AddComponent<VrKeyboard>();
                return shared;
            }
        }
        /// <summary>Frame the keyboard last closed — the closing press must not act behind it.</summary>
        public static int ClosedFrame { get; private set; } = -1;
        private static VrKeyboard active;

        /// <summary>Cancel whatever is being typed (Menu button).</summary>
        public static void CancelOpen()
        {
            if (IsOpen && active != null) active.Cancel();
        }

        private static readonly string[][] Letters =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "@" },
            new[] { "z", "x", "c", "v", "b", "n", "m", ".", "-", "_" },
        };
        private static readonly string[][] Symbols =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "!", "#", "$", "%", "&", "*", "(", ")", "=", "+" },
            new[] { "/", "\\", ":", ";", "?", "'", "\"", ",", "~", "@" },
            new[] { "[", "]", "{", "}", "<", ">", "|", ".", "-", "_" },
        };

        private RectTransform root;
        private TextMeshProUGUI caption, field;
        private readonly List<TextMeshProUGUI> keyLabels = new List<TextMeshProUGUI>();
        private TextMeshProUGUI shiftLabel, modeLabel;
        private string value = "";
        private bool secret, shift, capsLock, symbols, reveal;
        private float lastShiftTap = -1f;
        private Action<string> onDone;
        private Action onCancel;
        private int openedFrame;

        public void Open(string label, string initial, bool isSecret, Action<string> done, Action cancel)
        {
            Build();
            value = initial ?? "";
            secret = isSecret;
            reveal = false;
            shift = capsLock = symbols = false;
            onDone = done;
            onCancel = cancel;
            caption.text = label;
            active = this;
            IsOpen = true;
            openedFrame = Time.frameCount;
            if (!PanelPin.IsPinned(this)) PlaceInFront();
            root.gameObject.SetActive(true);
            RefreshKeys();
            RefreshField();
        }

        private void Close()
        {
            if (IsOpen) UiState.NoteClosed();
            IsOpen = false;
            ClosedFrame = Time.frameCount;
            if (root != null) root.gameObject.SetActive(false);
        }

        private void Submit() { string v = value; Close(); onDone?.Invoke(v); }
        private void Cancel() { Close(); onCancel?.Invoke(); }

        private void OnDestroy()
        {
            if (active == this) { IsOpen = false; active = null; }
            if (shared == this) shared = null;
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private void Build()
        {
            if (root != null) return;
            float rows = Letters.Length + 1;
            float height = Pad + 22f + FieldH + 16f + rows * (KeyH + Gap) + Pad;
            root = VrUi.CreateCanvas("KeyboardCanvas", transform, new Vector2(Width, height), MetersPerPixel);
            VrUi.Card("Card", root, 0, 0, Width, height, Brand.Surface.WithAlpha(0.98f), Brand.BorderStrong, 22);
            VrPointer.RegisterDraggable(root, transform);

            caption = VrUi.Text("Caption", root, Pad, Pad - 4, Width - 2 * Pad, 22, "", 14f, Brand.FgSubtle, true);
            VrUi.Panel("FieldBg", root, Pad, Pad + 22, Width - 2 * Pad - 64, FieldH, Brand.SurfaceSunken, 14);
            field = VrUi.Text("Field", root, Pad + 16, Pad + 22, Width - 2 * Pad - 96, FieldH, "", 24f, Brand.Fg);
            field.overflowMode = TextOverflowModes.Masking;
            field.alignment = TextAlignmentOptions.MidlineRight; // keep the caret end in view
            VrUi.Button("Reveal", root, Width - Pad - 56, Pad + 22 + (FieldH - 56) / 2f, 56, 56, "eye", null,
                () => { reveal = !reveal; RefreshField(); }, false, false, () => secret);

            float top = Pad + 22 + FieldH + 16;
            for (int r = 0; r < Letters.Length; r++)
            {
                for (int c = 0; c < Letters[r].Length; c++)
                {
                    int row = r, col = c;
                    float x = Pad + c * (KeyW + Gap), y = top + r * (KeyH + Gap);
                    RectTransform key = VrUi.Button($"K{r}{c}", root, x, y, KeyW, KeyH, null, " ",
                        () => Type(KeyAt(row, col)));
                    keyLabels.Add(key.GetComponentInChildren<TextMeshProUGUI>());
                }
            }

            float by = top + Letters.Length * (KeyH + Gap);
            float x0 = Pad;
            RectTransform shiftKey = VrUi.Button("Shift", root, x0, by, 96, KeyH, null, "Shift", TapShift);
            shiftLabel = shiftKey.GetComponentInChildren<TextMeshProUGUI>();
            RectTransform modeKey = VrUi.Button("Mode", root, x0 + 102, by, 80, KeyH, null, "#+=", () => { symbols = !symbols; RefreshKeys(); });
            modeLabel = modeKey.GetComponentInChildren<TextMeshProUGUI>();
            VrUi.Button("Space", root, x0 + 188, by, 130, KeyH, null, "κενό", () => Type(" "));
            VrUi.Button("Back", root, x0 + 324, by, 86, KeyH, null, "Διαγρ.", Backspace);
            VrUi.Button("Cancel", root, Width - Pad - 2 * 76 - 6, by, 76, KeyH, null, "Άκυρο", Cancel);
            VrUi.Button("Done", root, Width - Pad - 76, by, 76, KeyH, null, "OK", Submit, true);
            root.gameObject.SetActive(false);
        }

        // Tap = next letter capital; double-tap = caps lock (passwords with several capitals).
        private void TapShift()
        {
            bool doubleTap = Time.unscaledTime - lastShiftTap < 0.4f;
            lastShiftTap = Time.unscaledTime;
            if (capsLock) { capsLock = shift = false; }
            else if (doubleTap) { capsLock = shift = true; }
            else shift = !shift;
            RefreshKeys();
        }

        private string KeyAt(int r, int c)
        {
            string k = (symbols ? Symbols : Letters)[r][c];
            return shift && !symbols ? k.ToUpperInvariant() : k;
        }

        private void Type(string s)
        {
            if (value.Length >= 128) return;
            value += s;
            lastShiftTap = -1f; // Shift, letter, Shift must not read as a double-tap
            if (shift && !capsLock) { shift = false; RefreshKeys(); } // one-shot shift, like a phone
            RefreshField();
        }

        private void Backspace()
        {
            if (value.Length > 0) value = value.Substring(0, value.Length - 1);
            RefreshField();
        }

        private void RefreshKeys()
        {
            int i = 0;
            for (int r = 0; r < Letters.Length; r++)
                for (int c = 0; c < Letters[r].Length; c++)
                    keyLabels[i++].text = KeyAt(r, c);
            shiftLabel.color = shift ? Brand.Primary : Brand.Fg;
            shiftLabel.text = capsLock ? "CAPS" : "Shift";
            modeLabel.text = symbols ? "abc" : "#+=";
        }

        private void RefreshField()
        {
            string shown = secret && !reveal ? new string('•', value.Length) : value;
            field.text = shown + "<color=#8fc93a>|</color>";
        }

        private void PlaceInFront()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            // A little lower and closer than the form, tilted up like a real keyboard.
            transform.position = cam.transform.position + forward * 0.5f + Vector3.down * 0.22f;
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, Vector3.up);
        }

        // ------------------------------------------------------------------
        //  Input: B cancels, A / Enter submits, a Bluetooth keyboard types.
        // ------------------------------------------------------------------

        private void Update()
        {
            if (!IsOpen || Time.frameCount == openedFrame) return;

#if !MOBILE_PREVIEW
            if (Hands.AnyBackDown()) { Cancel(); return; }
            if (Hands.AnySelectDown()) { Submit(); return; } // A / X = OK, as the hint says
#endif
            foreach (char ch in Input.inputString)
            {
                if (ch == '\b') Backspace();
                else if (ch == '\n' || ch == '\r') { Submit(); return; }
                else if (!char.IsControl(ch)) Type(ch.ToString());
            }
            if (Input.GetKeyDown(KeyCode.Escape)) Cancel();
        }
    }
}
