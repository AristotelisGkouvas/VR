using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>Everything the plan draws, in one mesh: a site's 600 dots are one draw call, rebuilt only on change.</summary>
    public class PlanMesh : MaskableGraphic
    {
        public Action<VertexHelper> Draw;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Draw?.Invoke(vh);
        }
    }

    /// <summary>
    /// The alignment panel's plan of the points, drawn from their coordinates with no map under
    /// them, and the line about the selected one below it. The ray picks a point and drags the
    /// plan, + and − or the stick (with the ray on the plan) zoom. "All" leaves out what lies far
    /// from the rest: one sensor at 1.2 km would crush the site into a dot, so it stays on the
    /// edge as an arrow with its distance. A strip on the right gives the heights, since a plan
    /// cannot tell a building's floors apart.
    /// </summary>
    public class PlanView : MonoBehaviour
    {
        public const float W = 400f, MapH = 270f, InfoY = 278f, InfoH = 78f, TotalH = InfoY + InfoH;
        private const float PlotW = 356f, StripW = W - PlotW;
        private const float MaxScale = 45f, MinZoom = 0.6f; // pixels per metre at the closest; of "all" at the widest
        private const float PickPixels = 24f, DragPixels = 6f, SamePlanMetres = 1.5f;
        private const float TweenSeconds = 0.26f, InfoSeconds = 0.25f, StickZoomPerSecond = 2.2f;
        private const int MaxFarPills = 5, SameChips = 3;

        private enum Mode { All, Near, Free }

        private struct View
        {
            public float X, Z, S;
        }

        private struct Pt
        {
            public float X, Z, H;
            public bool Code;
            public ProjectPoint Point;
        }

        private sealed class Pill
        {
            public Image Plate;
            public TextMeshProUGUI Text;
        }

        private ProjectAlignment owner;
        private RectTransform map, plateAll, plateNear, segPlate;
        private PlanMesh mesh;
        private TextMeshProUGUI north, scaleText, stripTop, stripBottom, allLabel, nearLabel, infoName, infoKind, infoH, infoSub, sameLabel;
        private Pill selectedPill, hoverPill;
        private readonly List<Pill> farPills = new List<Pill>();
        private readonly List<(RectTransform rect, TextMeshProUGUI text, int index)> sameChips = new List<(RectTransform, TextMeshProUGUI, int)>();
        private readonly List<int> same = new List<int>();

        private object source;
        private readonly List<Pt> points = new List<Pt>();
        private readonly List<int> far = new List<int>();
        private View allView, view, from, to;
        private float centreX, centreZ, hMin, hMax, tweenStart = -1f, nextInfo;
        private Mode mode = Mode.All;
        private int lastSelected = -1, hover = -1;
        private bool dirty;
        private bool hasHead;
        private Vector2 head;
        private float headHeading;

        // Trigger held on the plan: a press without a drag picks, a drag moves the plan.
        private bool pressed, panning;
        private Vector2 pressAt, dragLast;

        public void Build(ProjectAlignment alignment)
        {
            owner = alignment;
            map = VrUi.Rect("Map", transform, 0, 0, W, MapH);
            VrUi.Card("MapBg", map, 0, 0, W, MapH, Brand.Canvas, Brand.Border, 14);

            var meshGo = VrUi.Rect("Mesh", map, 0, 0, W, MapH).gameObject;
            mesh = meshGo.AddComponent<PlanMesh>();
            mesh.raycastTarget = false;
            mesh.Draw = Draw;

            north = VrUi.Text("North", map, 8, 22, 20, 18, "", 12f, Brand.FgSubtle, true, TextAlignmentOptions.Center);
            scaleText = VrUi.Text("Scale", map, 12, MapH - 38, 110, 18, "", 12f, Brand.Fg);
            stripTop = VrUi.Text("StripTop", map, PlotW, 5, StripW, 18, "", 11f, Brand.FgSubtle, false, TextAlignmentOptions.Center);
            stripBottom = VrUi.Text("StripBottom", map, PlotW, MapH - 23, StripW, 18, "", 11f, Brand.FgSubtle, false, TextAlignmentOptions.Center);

            for (int i = 0; i < MaxFarPills; i++) farPills.Add(MakePill("Far" + i));
            hoverPill = MakePill("Hover");
            selectedPill = MakePill("Selected");

            BuildSegment();
            VrUi.Button("ZoomOut", map, PlotW - 46, MapH - 48, 40, 40, null, "−", () => ZoomBy(1f / 1.6f)).GetComponentInChildren<TextMeshProUGUI>().fontSize = 22f;
            VrUi.Button("ZoomIn", map, PlotW - 46, MapH - 92, 40, 40, null, "+", () => ZoomBy(1.6f)).GetComponentInChildren<TextMeshProUGUI>().fontSize = 22f;

            // Last: at equal distance the pointer keeps the first registered, and these buttons sit on it.
            VrPointer.Register(meshGo.GetComponent<RectTransform>(), OnPress);

            BuildInfo();
        }

        private void BuildSegment()
        {
            const float x = PlotW - 136f, y = 8f;
            segPlate = VrUi.Panel("Seg", map, x, y, 130, 36, Brand.Surface, 10).rectTransform;
            plateAll = VrUi.Panel("Current", segPlate, 2, 2, 62, 32, Brand.SurfaceSunken, 8).rectTransform;
            allLabel = VrUi.Text("All", segPlate, 2, 2, 62, 32, "", 13f, Brand.Fg, true, TextAlignmentOptions.Center);
            nearLabel = VrUi.Text("Near", segPlate, 66, 2, 62, 32, "", 13f, Brand.FgSubtle, true, TextAlignmentOptions.Center);
            plateNear = nearLabel.rectTransform;
            VrPointer.Register(allLabel.rectTransform, () => SetMode(Mode.All));
            VrPointer.Register(nearLabel.rectTransform, () => SetMode(Mode.Near));
        }

        private void BuildInfo()
        {
            RectTransform info = VrUi.Panel("Info", transform, 0, InfoY, W, InfoH, Brand.SurfaceSunken, 12).rectTransform;
            infoName = VrUi.Text("Name", info, 14, 6, 170, 28, "", 19f, Brand.Fg, true);
            infoKind = VrUi.Text("Kind", info, 186, 8, 110, 26, "", 12f, Brand.FgSubtle);
            infoH = VrUi.Text("H", info, 276, 6, 110, 28, "", 15f, Brand.Fg, true, TextAlignmentOptions.MidlineRight);
            infoSub = VrUi.Text("Sub", info, 14, 40, W - 28, 28, "", 13f, Brand.FgMuted);
            sameLabel = VrUi.Text("Same", info, 14, 40, 72, 28, "", 12f, Brand.FgMuted);
            for (int i = 0; i < SameChips; i++)
            {
                int chip = i;
                RectTransform rt = VrUi.Button("SameChip" + i, info, 88 + i * 100, 40, 96, 30, null, " ", () => PickSame(chip));
                TextMeshProUGUI label = rt.GetComponentInChildren<TextMeshProUGUI>();
                label.fontSize = 12f;
                sameChips.Add((rt, label, i));
                rt.gameObject.SetActive(false);
            }
        }

        private Pill MakePill(string name)
        {
            Image plate = VrUi.Panel("Pill" + name, map, 0, 0, 40, 22, Brand.Canvas.WithAlpha(0.92f), 6);
            TextMeshProUGUI text = VrUi.Text("Text", plate.rectTransform, 6, 0, 200, 22, "", 12f, Brand.Fg, true);
            plate.gameObject.SetActive(false);
            return new Pill { Plate = plate, Text = text };
        }

        // ------------------------------------------------------------------
        //  Source
        // ------------------------------------------------------------------

        /// <summary>Called on every panel refresh: the points and the selection may have changed.</summary>
        public void Refresh(ProjectAlignment a)
        {
            owner = a;
            if (!ReferenceEquals(source, a.Points)) Rebuild(a);
            if (a.SelectedIndex != lastSelected)
            {
                lastSelected = a.SelectedIndex;
                OnSelected();
            }
            allLabel.text = L.T("Όλα", "All");
            nearLabel.text = L.T("Κοντά", "Near");
            north.text = L.T("Β", "N");
            RefreshInfo();
            dirty = true;
        }

        private void Rebuild(ProjectAlignment a)
        {
            source = a.Points;
            points.Clear();
            foreach (ProjectPoint p in a.Points)
            {
                Vector3 local = p != null ? a.PointLocal(p) : Vector3.zero;
                points.Add(new Pt { X = local.x, Z = local.z, H = p != null ? (float)p.h : 0f, Code = p != null && p.IsCode(), Point = p });
            }
            far.Clear();
            lastSelected = -1;
            hover = -1;
            pressed = panning = false;
            if (points.Count == 0) return;

            var xs = new List<float>();
            var zs = new List<float>();
            foreach (Pt p in points)
            {
                xs.Add(p.X);
                zs.Add(p.Z);
            }
            xs.Sort();
            zs.Sort();
            centreX = xs[xs.Count / 2];
            centreZ = zs[zs.Count / 2];

            var radii = new List<float>();
            foreach (Pt p in points) radii.Add(FromCentre(p));
            radii.Sort();
            float reach = Mathf.Max(radii[Mathf.Min(radii.Count - 1, (int)(radii.Count * 0.9f))] * 3f, 10f);

            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            hMin = float.MaxValue;
            hMax = float.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                Pt p = points[i];
                if (FromCentre(p) > reach)
                {
                    far.Add(i);
                    continue;
                }
                x0 = Mathf.Min(x0, p.X);
                x1 = Mathf.Max(x1, p.X);
                z0 = Mathf.Min(z0, p.Z);
                z1 = Mathf.Max(z1, p.Z);
                hMin = Mathf.Min(hMin, p.H);
                hMax = Mathf.Max(hMax, p.H);
            }
            hMin = Mathf.Floor(hMin);
            hMax = Mathf.Max(hMin + 1f, Mathf.Ceil(hMax));
            stripTop.text = hMax.ToString("F0");
            stripBottom.text = hMin.ToString("F0");
            allView = new View
            {
                X = (x0 + x1) * 0.5f,
                Z = (z0 + z1) * 0.5f,
                S = Mathf.Min((PlotW - 44f) / Mathf.Max(x1 - x0, 5f), (MapH - 70f) / Mathf.Max(z1 - z0, 5f))
            };
            view = allView;
            tweenStart = -1f;
            SetModeState(Mode.All);
        }

        private float FromCentre(Pt p) => Mathf.Sqrt((p.X - centreX) * (p.X - centreX) + (p.Z - centreZ) * (p.Z - centreZ));

        private static float Flat(Pt a, Pt b) => Mathf.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        private bool HasSelection => lastSelected >= 0 && lastSelected < points.Count;

        // ------------------------------------------------------------------
        //  View
        // ------------------------------------------------------------------

        private float ToX(float x) => PlotW * 0.5f + (x - view.X) * view.S;
        private float ToY(float z) => MapH * 0.5f - (z - view.Z) * view.S;
        private static bool Inside(float x, float y) => x >= 6f && x <= PlotW - 6f && y >= 6f && y <= MapH - 6f;

        private void GoTo(View target)
        {
            from = view;
            to = target;
            tweenStart = Time.unscaledTime;
        }

        private void SetModeState(Mode next)
        {
            mode = next;
            plateAll.gameObject.SetActive(next != Mode.Free);
            plateAll.anchoredPosition = new Vector2(next == Mode.Near ? 66f : 2f, -2f);
            allLabel.color = next == Mode.All ? Brand.Fg : Brand.FgSubtle;
            nearLabel.color = next == Mode.Near ? Brand.Fg : Brand.FgSubtle;
        }

        private void SetMode(Mode next)
        {
            if (points.Count == 0) return;
            SetModeState(next);
            if (next == Mode.All) GoTo(allView);
            else if (HasSelection) GoTo(NearView(points[lastSelected]));
        }

        // The ten nearest points fill the plan, however crowded the spot.
        private View NearView(Pt p)
        {
            var d = new List<float>(points.Count);
            foreach (Pt q in points) d.Add(Flat(p, q));
            d.Sort();
            float r = Mathf.Max(8f, d[Mathf.Min(10, d.Count - 1)]);
            return new View { X = p.X, Z = p.Z, S = Mathf.Min(MaxScale, (Mathf.Min(PlotW, MapH) * 0.5f - 24f) / r) };
        }

        private void OnSelected()
        {
            if (!HasSelection) return;
            Pt p = points[lastSelected];
            if (mode == Mode.Near) GoTo(NearView(p));
            else if (mode == Mode.Free && !Inside(ToX(p.X), ToY(p.Z))) GoTo(new View { X = p.X, Z = p.Z, S = view.S });
        }

        // The spot under (x, y) stays where it is.
        private void ZoomAround(float factor, float x, float y)
        {
            float s = Mathf.Clamp(view.S * factor, allView.S * MinZoom, Mathf.Max(MaxScale, allView.S));
            float k = 1f / view.S - 1f / s;
            view = new View { S = s, X = view.X + (x - PlotW * 0.5f) * k, Z = view.Z - (y - MapH * 0.5f) * k };
            tweenStart = -1f;
            SetModeState(Mode.Free);
            dirty = true;
        }

        private void ZoomBy(float factor)
        {
            if (points.Count == 0) return;
            float s = Mathf.Clamp(view.S * factor, allView.S * MinZoom, Mathf.Max(MaxScale, allView.S));
            SetModeState(Mode.Free);
            GoTo(new View { X = view.X, Z = view.Z, S = s });
        }

        // ------------------------------------------------------------------
        //  Pointer
        // ------------------------------------------------------------------

        private void OnPress()
        {
            if (!VrPointer.TryHit(map, out Vector2 at)) return;
            pressed = true;
            panning = false;
            pressAt = dragLast = at;
        }

        private int Pick(Vector2 at)
        {
            int best = -1, n = Mathf.Min(owner.ListCount, points.Count);
            float dist = PickPixels;
            for (int i = 0; i < n; i++)
            {
                float x = ToX(points[i].X), y = ToY(points[i].Z);
                if (!Inside(x, y)) continue;
                float d = Vector2.Distance(at, new Vector2(x, y));
                if (d < dist)
                {
                    dist = d;
                    best = i;
                }
            }
            return best;
        }

        private void PickSame(int chip)
        {
            if (chip < same.Count) owner.Select(same[chip]);
        }

        private void Update()
        {
            if (owner == null || points.Count == 0) return;

            bool onMap = VrPointer.TryHit(map, out Vector2 at) && at.x < PlotW;
            if (pressed)
            {
                if (!VrPointer.TriggerHeld)
                {
                    pressed = false;
                    if (!panning)
                    {
                        int picked = Pick(pressAt);
                        if (picked >= 0) owner.Select(picked);
                    }
                    panning = false;
                }
                else if (onMap)
                {
                    if (!panning && Vector2.Distance(at, pressAt) > DragPixels)
                    {
                        panning = true;
                        tweenStart = -1f;
                        SetModeState(Mode.Free);
                    }
                    if (panning)
                    {
                        view.X -= (at.x - dragLast.x) / view.S;
                        view.Z += (at.y - dragLast.y) / view.S;
                        dragLast = at;
                        dirty = true;
                    }
                }
            }
            else if (onMap)
            {
#if !MOBILE_PREVIEW
                float stick = Hands.DominantStickY();
                if (Mathf.Abs(stick) > 0.3f) ZoomAround(Mathf.Exp(stick * StickZoomPerSecond * Time.unscaledDeltaTime), at.x, at.y);
#endif
            }

            int over = onMap && !pressed ? Pick(at) : -1;
            if (over != hover)
            {
                hover = over;
                dirty = true;
            }

            if (tweenStart >= 0f)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - tweenStart) / TweenSeconds);
                float q = 1f - (1f - t) * (1f - t) * (1f - t);
                view = new View
                {
                    X = Mathf.Lerp(from.X, to.X, q),
                    Z = Mathf.Lerp(from.Z, to.Z, q),
                    S = Mathf.Exp(Mathf.Lerp(Mathf.Log(from.S), Mathf.Log(to.S), q))
                };
                if (t >= 1f) tweenStart = -1f;
                dirty = true;
            }

            if (Time.unscaledTime >= nextInfo)
            {
                nextInfo = Time.unscaledTime + InfoSeconds;
                hasHead = owner.TryHeadInProject(out head, out headHeading);
                if (hasHead) dirty = true;
                RefreshInfo();
            }

            if (dirty)
            {
                dirty = false;
                LayoutLabels();
                mesh.SetVerticesDirty();
            }
        }

        // ------------------------------------------------------------------
        //  Text
        // ------------------------------------------------------------------

        private static string Metres(float m) =>
            m >= 1000f ? (m / 1000f).ToString("0.0") + " km" : m >= 10f ? m.ToString("0") + " m" : m.ToString("0.0") + " m";

        private static string Direction(float dx, float dz)
        {
            float a = Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 360f);
            string[] names = L.En
                ? new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }
                : new[] { "Β", "ΒΑ", "Α", "ΝΑ", "Ν", "ΝΔ", "Δ", "ΒΔ" };
            return names[Mathf.RoundToInt(a / 45f) % 8];
        }

        private void RefreshInfo()
        {
            if (!HasSelection || points[lastSelected].Point == null) return;
            Pt p = points[lastSelected];
            ProjectPoint point = p.Point;
            infoName.text = string.IsNullOrEmpty(point.label) ? point.id : point.label;
            infoKind.text = owner.IsPlaced(point.id) ? L.T("σημείο ελέγχου", "control point")
                : p.Code ? L.T("κωδικός QR", "QR code") : L.T("αισθητήρας", "sensor");
            infoH.text = $"H {point.h:F2}";

            same.Clear();
            int n = Mathf.Min(owner.ListCount, points.Count);
            for (int i = 0; i < n && same.Count < SameChips; i++)
                if (i != lastSelected && Flat(points[i], p) <= SamePlanMetres) same.Add(i);
            bool stacked = same.Count > 0;
            sameLabel.gameObject.SetActive(stacked);
            infoSub.gameObject.SetActive(!stacked);
            foreach (var (rect, text, index) in sameChips)
            {
                bool on = index < same.Count;
                if (rect.gameObject.activeSelf != on) rect.gameObject.SetActive(on);
                if (on) text.text = $"{points[same[index]].Point.id} · {points[same[index]].H:F1}";
            }
            if (stacked)
            {
                sameLabel.text = L.T("Ίδια θέση:", "Same spot:");
                return;
            }
            if (hasHead)
            {
                float d = Vector2.Distance(head, new Vector2(p.X, p.Z));
                string to = Direction(p.X - head.x, p.Z - head.y);
                infoSub.text = L.En ? $"{Metres(d)} from you, to the {to}" : $"{Metres(d)} από εσένα, προς {to}";
                return;
            }
            float c = FromCentre(p);
            string side = Direction(p.X - centreX, p.Z - centreZ);
            infoSub.text = c < 5f ? $"E {point.e:F2} · N {point.n:F2}"
                : L.En ? $"{Metres(c)} {side} of the project's centre" : $"{Metres(c)} {side} από το κέντρο του έργου";
        }

        private void ShowPill(Pill pill, string text, float x, float y, Color color)
        {
            if (!pill.Plate.gameObject.activeSelf) pill.Plate.gameObject.SetActive(true);
            pill.Text.text = text;
            pill.Text.color = color;
            float w = pill.Text.GetPreferredValues(text).x + 12f;
            float px = x + 12f;
            if (px + w > PlotW - 4f) px = x - 12f - w;
            px = Mathf.Max(4f, px);
            float py = Mathf.Clamp(y - 11f, 4f, MapH - 26f);
            RectTransform rt = pill.Plate.rectTransform;
            rt.anchoredPosition = new Vector2(px, -py);
            rt.sizeDelta = new Vector2(w, 22f);
        }

        private static void HidePill(Pill pill)
        {
            if (pill.Plate.gameObject.activeSelf) pill.Plate.gameObject.SetActive(false);
        }

        private Vector2 EdgePoint(Pt p)
        {
            float dx = p.X - view.X, dz = p.Z - view.Z;
            float k = Mathf.Min((PlotW * 0.5f - 14f) / Mathf.Max(Mathf.Abs(dx), 1e-4f), (MapH * 0.5f - 14f) / Mathf.Max(Mathf.Abs(dz), 1e-4f));
            return new Vector2(PlotW * 0.5f + dx * k, MapH * 0.5f - dz * k);
        }

        private bool ShowsFar => mode == Mode.All && !owner.QrOnly;

        private void LayoutLabels()
        {
            scaleText.text = Metres(GridStep());
            int used = 0;
            if (ShowsFar)
                foreach (int i in far)
                {
                    if (i == lastSelected || used >= MaxFarPills) continue;
                    Pt p = points[i];
                    Vector2 at = EdgePoint(p);
                    ShowPill(farPills[used++], $"{p.Point.id} · {Metres(FromCentre(p))}", at.x, at.y + (at.y < 60f ? 16f : 0f), Brand.FgSubtle);
                }
            for (int i = used; i < farPills.Count; i++) HidePill(farPills[i]);

            if (HasSelection && points[lastSelected].Point != null)
            {
                Pt p = points[lastSelected];
                float x = ToX(p.X), y = ToY(p.Z);
                if (Inside(x, y)) ShowPill(selectedPill, p.Point.id, x + 2f, y - 18f, Brand.Fg);
                else
                {
                    Vector2 at = EdgePoint(p);
                    ShowPill(selectedPill, $"{p.Point.id} · {Metres(FromCentre(p))}", at.x, at.y + (at.y < 60f ? 16f : 0f), Color.white);
                }
            }
            else HidePill(selectedPill);

            if (hover >= 0 && hover != lastSelected && points[hover].Point != null)
                ShowPill(hoverPill, points[hover].Point.id, ToX(points[hover].X), ToY(points[hover].Z) - 14f, Brand.FgMuted);
            else HidePill(hoverPill);
        }

        private float GridStep()
        {
            float raw = 80f / view.S, pow = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw))), f = raw / pow;
            return (f < 1.5f ? 1f : f < 3.5f ? 2f : f < 7.5f ? 5f : 10f) * pow;
        }

        // ------------------------------------------------------------------
        //  Mesh. x right, y down, in panel pixels from the map's top-left corner.
        // ------------------------------------------------------------------

        private static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(new Vector3(a.x, -a.y), color, Vector2.zero);
            vh.AddVert(new Vector3(b.x, -b.y), color, Vector2.zero);
            vh.AddVert(new Vector3(c.x, -c.y), color, Vector2.zero);
            vh.AddVert(new Vector3(d.x, -d.y), color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }

        private static void Box(VertexHelper vh, float x0, float y0, float x1, float y1, Color32 color) =>
            Quad(vh, new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1), color);

        private static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color32 color)
        {
            Vector2 n = new Vector2(a.y - b.y, b.x - a.x).normalized * (width * 0.5f);
            Quad(vh, a + n, b + n, b - n, a - n, color);
        }

        private static void Tri(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color32 color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(new Vector3(a.x, -a.y), color, Vector2.zero);
            vh.AddVert(new Vector3(b.x, -b.y), color, Vector2.zero);
            vh.AddVert(new Vector3(c.x, -c.y), color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
        }

        private static void Disc(VertexHelper vh, float x, float y, float r, Color32 color)
        {
            const int sides = 14;
            int centre = vh.currentVertCount;
            vh.AddVert(new Vector3(x, -y), color, Vector2.zero);
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                vh.AddVert(new Vector3(x + Mathf.Cos(a) * r, -y + Mathf.Sin(a) * r), color, Vector2.zero);
                vh.AddTriangle(centre, centre + 1 + i, centre + 1 + (i + 1) % sides);
            }
        }

        private static void Ring(VertexHelper vh, float x, float y, float r, float width, Color32 color, bool dashed = false)
        {
            const int sides = 24;
            for (int i = 0; i < sides; i++)
            {
                if (dashed && i % 2 == 1) continue;
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                Line(vh, new Vector2(x + Mathf.Cos(a0) * r, y + Mathf.Sin(a0) * r), new Vector2(x + Mathf.Cos(a1) * r, y + Mathf.Sin(a1) * r), width, color);
            }
        }

        private void Dot(VertexHelper vh, Pt p, float x, float y, bool faint)
        {
            bool placed = owner.IsPlaced(p.Point.id);
            Color color = placed ? Brand.Control : p.Code ? Brand.Info : Brand.Primary;
            float alpha = faint ? 0.28f : 1f;
            Color ink = Brand.Canvas.WithAlpha(alpha);
            color = color.WithAlpha(alpha);
            float r = placed ? 7f : p.Code ? 5.5f : 5f;
            if (p.Code)
            {
                Box(vh, x - r - 1.2f, y - r - 1.2f, x + r + 1.2f, y + r + 1.2f, ink);
                Box(vh, x - r, y - r, x + r, y + r, color);
            }
            else
            {
                Disc(vh, x, y, r + 1.2f, ink);
                Disc(vh, x, y, r, color);
            }
        }

        private void Arrow(VertexHelper vh, Pt p, Color32 color)
        {
            Vector2 at = EdgePoint(p);
            Vector2 d = new Vector2(p.X - view.X, view.Z - p.Z).normalized;
            Vector2 side = new Vector2(-d.y, d.x);
            Tri(vh, at + d * 9f, at - d * 6f + side * 7f, at - d * 6f - side * 7f, color);
        }

        private void Draw(VertexHelper vh)
        {
            if (owner == null || points.Count == 0 || view.S <= 0f) return;
            int n = owner.ListCount;
            bool qr = owner.QrOnly;

            // Grid, at the scale bar's step.
            Color32 grid = new Color(1f, 1f, 1f, 0.06f);
            float step = GridStep();
            int lines = 0;
            for (float x = Mathf.Floor((view.X - PlotW * 0.5f / view.S) / step) * step; ToX(x) < PlotW && lines < 40; x += step, lines++)
                if (ToX(x) > 3f) Box(vh, ToX(x), 3f, ToX(x) + 1.5f, MapH - 3f, grid);
            lines = 0;
            for (float z = Mathf.Floor((view.Z - MapH * 0.5f / view.S) / step) * step; ToY(z) > 3f && lines < 40; z += step, lines++)
                if (ToY(z) < MapH - 3f) Box(vh, 3f, ToY(z), PlotW, ToY(z) + 1.5f, grid);

            bool hasSelected = HasSelection && points[lastSelected].Point != null;
            Pt sel = hasSelected ? points[lastSelected] : default;
            float sx = hasSelected ? ToX(sel.X) : 0f, sy = hasSelected ? ToY(sel.Z) : 0f;
            bool selInside = hasSelected && Inside(sx, sy);
            if (selInside)
            {
                Color32 cross = new Color(1f, 1f, 1f, 0.22f);
                Box(vh, sx - 0.75f, 3f, sx + 0.75f, MapH - 3f, cross);
                Box(vh, 3f, sy - 0.75f, PlotW, sy + 0.75f, cross);
            }

            // Sensors under codes under control points: what matters most stays on top.
            for (int pass = 0; pass < 3; pass++)
                for (int i = 0; i < points.Count; i++)
                {
                    Pt p = points[i];
                    if (p.Point == null) continue;
                    int layer = owner.IsPlaced(p.Point.id) ? 2 : p.Code ? 1 : 0;
                    if (layer != pass) continue;
                    float x = ToX(p.X), y = ToY(p.Z);
                    if (Inside(x, y)) Dot(vh, p, x, y, i >= n);
                }

            string next = owner.SuggestedId;
            if (!string.IsNullOrEmpty(next))
                foreach (Pt p in points)
                {
                    if (p.Point == null || p.Point.id != next) continue;
                    if (Inside(ToX(p.X), ToY(p.Z))) Ring(vh, ToX(p.X), ToY(p.Z), 10f, 2f, Brand.Info, true);
                    break;
                }

            if (hasHead)
            {
                float x = ToX(head.x), y = ToY(head.y);
                if (Inside(x, y))
                {
                    float a = headHeading * Mathf.Deg2Rad;
                    Vector2 f = new Vector2(Mathf.Sin(a), -Mathf.Cos(a)), s = new Vector2(-f.y, f.x), c = new Vector2(x, y);
                    Color32 white = Color.white;
                    Tri(vh, c + f * 10f, c - f * 7f + s * 7f, c - f * 3.5f, white);
                    Tri(vh, c + f * 10f, c - f * 3.5f, c - f * 7f - s * 7f, white);
                }
            }

            if (!qr && mode == Mode.All)
                foreach (int i in far)
                    if (i != lastSelected) Arrow(vh, points[i], Brand.Primary);

            if (selInside)
            {
                Dot(vh, sel, sx, sy, false);
                Ring(vh, sx, sy, 12f, 2.5f, Color.white);
            }
            else if (hasSelected) Arrow(vh, sel, Color.white);

            // North and the scale bar.
            Color32 subtle = Brand.FgSubtle;
            Tri(vh, new Vector2(18f, 9f), new Vector2(23f, 20f), new Vector2(13f, 20f), subtle);
            float bar = step * view.S;
            Box(vh, 12f, MapH - 15f, 12f + bar, MapH - 13f, Brand.Fg);

            // Heights.
            Box(vh, PlotW, 2f, W - 2f, MapH - 2f, Brand.Surface);
            Box(vh, PlotW, 2f, PlotW + 1f, MapH - 2f, Brand.BorderStrong);
            const float top = 28f;
            float bottom = MapH - 26f, cx = PlotW + StripW * 0.5f;
            Box(vh, cx - 0.5f, top, cx + 0.5f, bottom, Brand.BorderStrong);
            Color sensorTick = Brand.Primary.WithAlpha(0.55f);
            foreach (Pt p in points)
            {
                if (p.Point == null) continue;
                bool placed = owner.IsPlaced(p.Point.id);
                float y = HeightY(p.H, top, bottom);
                Box(vh, cx - 7f, y, cx + 7f, y + 1.5f, placed ? Brand.Control : p.Code ? Brand.Info : sensorTick);
            }
            if (hasSelected)
            {
                float y = HeightY(sel.H, top, bottom);
                Box(vh, cx - 13f, y - 1f, cx + 13f, y + 2f, Color.white);
            }
        }

        private float HeightY(float h, float top, float bottom) =>
            bottom - (Mathf.Clamp(h, hMin, hMax) - hMin) / (hMax - hMin) * (bottom - top);
    }
}
