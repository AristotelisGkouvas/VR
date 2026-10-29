using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.Models
{
    /// <summary>
    /// Reads an ASCII DXF (R12 through 2018) into a <see cref="ModelData"/>: one group per layer,
    /// curve entities tessellated into line segments, face entities into triangles. Only the
    /// subset that survey / CAD exports actually contain is interpreted; everything else is
    /// counted and reported as a warning instead of failing the import, because a drawing with a
    /// few hatches or dimensions is still perfectly usable as an overlay. The parser streams the
    /// group-code / value pairs without building a document model so that a 100 MB file with
    /// hundreds of thousands of entities loads in seconds on the headset.
    /// </summary>
    public static class DxfImporter
    {
        private const string BinarySentinel = "AutoCAD Binary DXF";
        // Read on the import worker thread: L is cached on the main thread at startup (L.Init).
        private static string BinaryMessage => L.T("Binary DXF δεν υποστηρίζεται — αποθήκευσε ως ASCII DXF",
            "Binary DXF is not supported — save as ASCII DXF");
        private const int MaxInsertDepth = 8;
        private const int MaxInsertCopies = 10000;
        private const int CircleSegments = 32;
        private const double ArcStepDegrees = 10.0;
        private const int BulgeSegmentsPerQuarter = 8;
        private const int ProgressEveryEntities = 2048;

        /// <summary>
        /// Loads a DXF file from disk; <paramref name="progress"/> gets 0..1 by file position.
        /// <paramref name="cancel"/> is checked at each progress step: a superseded import stops.
        /// </summary>
        public static ModelData Load(string path, ImportOptions options, Action<float> progress = null,
            CancellationToken cancel = default)
        {
            Action<float> report = progress;
            if (cancel.CanBeCanceled)
                report = p => { cancel.ThrowIfCancellationRequested(); progress?.Invoke(p); };
            progress = report;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                ThrowIfBinary(fs);
                // UTF-8 with BOM detection covers 2007+ files; older code-page files still parse,
                // only non-ASCII layer names may come out garbled, which is harmless.
                using (var reader = new StreamReader(fs, Encoding.UTF8, true, 1 << 16))
                {
                    long length = fs.Length;
                    Func<float> position = null;
                    if (progress != null && length > 0) position = () => (float)((double)fs.Position / length);
                    progress?.Invoke(0f);
                    var model = new Parser(reader, Path.GetFileNameWithoutExtension(path), options, progress, position).Run();
                    progress?.Invoke(1f);
                    return model;
                }
            }
        }

        /// <summary>Parses DXF text from any reader (tests, in-memory uploads).</summary>
        public static ModelData Parse(TextReader reader, string name, ImportOptions options)
        {
            return new Parser(reader, name, options, null, null).Run();
        }

        private static void ThrowIfBinary(FileStream fs)
        {
            var head = new byte[BinarySentinel.Length];
            int read = fs.Read(head, 0, head.Length);
            fs.Position = 0;
            if (read == head.Length && Encoding.ASCII.GetString(head) == BinarySentinel)
                throw new InvalidDataException(BinaryMessage);
        }

        // ------------------------------------------------------------------ colours

        /// <summary>
        /// AutoCAD Color Index to RGB. The first nine and the greys are exact; 10..249 use the
        /// standard hue wheel (24 hues, five brightness steps, odd indices desaturated), which is
        /// close enough for telling layers apart.
        /// </summary>
        public static Color AciToColor(int aci)
        {
            switch (aci)
            {
                case 1: return new Color(1f, 0f, 0f);
                case 2: return new Color(1f, 1f, 0f);
                case 3: return new Color(0f, 1f, 0f);
                case 4: return new Color(0f, 1f, 1f);
                case 5: return new Color(0f, 0f, 1f);
                case 6: return new Color(1f, 0f, 1f);
                case 7: return new Color(0.85f, 0.85f, 0.85f);
                case 8: return new Color(0.5f, 0.5f, 0.5f);
                case 9: return new Color(0.75f, 0.75f, 0.75f);
            }
            if (aci >= 250 && aci <= 255)
            {
                float g = 0.2f + (aci - 250) * 0.16f;
                return new Color(g, g, g);
            }
            if (aci >= 10 && aci <= 249)
            {
                int hueIndex = (aci - 10) / 10;
                int shade = (aci % 10) / 2;
                bool pale = (aci % 2) == 1;
                float v;
                switch (shade)
                {
                    case 0: v = 1f; break;
                    case 1: v = 0.75f; break;
                    case 2: v = 0.55f; break;
                    case 3: v = 0.4f; break;
                    default: v = 0.3f; break;
                }
                return Hsv(hueIndex * 15f, pale ? 0.5f : 1f, v);
            }
            return new Color(0.85f, 0.88f, 0.9f);
        }

        private static Color Hsv(float hueDegrees, float s, float v)
        {
            float c = v * s;
            float hp = (hueDegrees % 360f) / 60f;
            float x = c * (1f - Math.Abs(hp % 2f - 1f));
            float r, g, b;
            if (hp < 1) { r = c; g = x; b = 0; }
            else if (hp < 2) { r = x; g = c; b = 0; }
            else if (hp < 3) { r = 0; g = c; b = x; }
            else if (hp < 4) { r = 0; g = x; b = c; }
            else if (hp < 5) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            float m = v - c;
            return new Color(r + m, g + m, b + m);
        }

        // ------------------------------------------------------------------ transforms

        /// <summary>
        /// Affine transform in doubles (3x3 + translation). Doubles matter because survey
        /// coordinates are in the millions and the origin shift happens only after the
        /// block / OCS transforms have been applied.
        /// </summary>
        private struct Xform
        {
            public double M00, M01, M02, M10, M11, M12, M20, M21, M22, Tx, Ty, Tz;

            public static Xform Identity => new Xform { M00 = 1, M11 = 1, M22 = 1 };

            public void Apply(double x, double y, double z, out double ox, out double oy, out double oz)
            {
                ox = M00 * x + M01 * y + M02 * z + Tx;
                oy = M10 * x + M11 * y + M12 * z + Ty;
                oz = M20 * x + M21 * y + M22 * z + Tz;
            }

            /// <summary>Returns a transform that applies <paramref name="b"/> first, then <paramref name="a"/>.</summary>
            public static Xform Mul(in Xform a, in Xform b)
            {
                Xform r;
                r.M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20;
                r.M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21;
                r.M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22;
                r.M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20;
                r.M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21;
                r.M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22;
                r.M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20;
                r.M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21;
                r.M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22;
                r.Tx = a.M00 * b.Tx + a.M01 * b.Ty + a.M02 * b.Tz + a.Tx;
                r.Ty = a.M10 * b.Tx + a.M11 * b.Ty + a.M12 * b.Tz + a.Ty;
                r.Tz = a.M20 * b.Tx + a.M21 * b.Ty + a.M22 * b.Tz + a.Tz;
                return r;
            }

            public static Xform Translation(double x, double y, double z)
            {
                var r = Identity; r.Tx = x; r.Ty = y; r.Tz = z; return r;
            }

            public static Xform Scale(double sx, double sy, double sz)
            {
                return new Xform { M00 = sx, M11 = sy, M22 = sz };
            }

            public static Xform RotationZ(double degrees)
            {
                double rad = degrees * Math.PI / 180.0;
                double c = Math.Cos(rad), s = Math.Sin(rad);
                return new Xform { M00 = c, M01 = -s, M10 = s, M11 = c, M22 = 1 };
            }

            /// <summary>
            /// AutoCAD's Arbitrary Axis Algorithm: the OCS of an entity whose extrusion is
            /// <paramref name="nx"/>,<paramref name="ny"/>,<paramref name="nz"/>. Ax = Wy x N when N is
            /// within 1/64 of the world Z axis, otherwise Ax = Wz x N; Ay = N x Ax. Columns are
            /// Ax, Ay, N so that OCS (x, y, z) maps to x*Ax + y*Ay + z*N in WCS.
            /// </summary>
            public static Xform Ocs(double nx, double ny, double nz)
            {
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-12) return Identity;
                nx /= len; ny /= len; nz /= len;
                double ax, ay, az;
                const double eps = 1.0 / 64.0;
                if (Math.Abs(nx) < eps && Math.Abs(ny) < eps)
                {
                    // Wy x N
                    ax = nz; ay = 0; az = -nx;
                }
                else
                {
                    // Wz x N
                    ax = -ny; ay = nx; az = 0;
                }
                double alen = Math.Sqrt(ax * ax + ay * ay + az * az);
                ax /= alen; ay /= alen; az /= alen;
                // Ay = N x Ax
                double bx = ny * az - nz * ay;
                double by = nz * ax - nx * az;
                double bz = nx * ay - ny * ax;
                return new Xform
                {
                    M00 = ax, M01 = bx, M02 = nx,
                    M10 = ay, M11 = by, M12 = ny,
                    M20 = az, M21 = bz, M22 = nz
                };
            }
        }

        // ------------------------------------------------------------------ records

        /// <summary>
        /// One parsed entity (or table / block header). Scalars are kept sparse (code, value)
        /// because an entity has a few dozen codes at most and block contents are retained in
        /// memory for INSERT expansion, where a dense 500-double table per entity would not fit.
        /// Point series 10..13 / 20..23 / 30..33 are collected positionally, since LWPOLYLINE,
        /// SPLINE and MESH repeat them.
        /// </summary>
        private sealed class Ent
        {
            public string Type = "";
            public string Layer = "0";
            public string Name = "";
            public int Color = 256;
            private int[] codes = new int[24];
            private double[] vals = new double[24];
            private int count;
            public readonly List<double> X0 = new List<double>(), Y0 = new List<double>(), Z0 = new List<double>(), B0 = new List<double>();
            public readonly List<double> X1 = new List<double>(), Y1 = new List<double>(), Z1 = new List<double>();
            public readonly List<double> X2 = new List<double>(), Y2 = new List<double>(), Z2 = new List<double>();
            public readonly List<double> X3 = new List<double>(), Y3 = new List<double>(), Z3 = new List<double>();
            /// <summary>All code-90 values in order (MESH face list, edge list).</summary>
            public readonly List<int> I90 = new List<int>();
            /// <summary>Index into <see cref="I90"/> where the MESH face list starts (set when code 93 is seen).</summary>
            public int FaceListStart = -1;
            /// <summary>POLYLINE vertices (VERTEX records up to SEQEND).</summary>
            public List<Ent> Children;

            public void Reset(string type)
            {
                Type = type; Layer = "0"; Name = ""; Color = 256; count = 0;
                X0.Clear(); Y0.Clear(); Z0.Clear(); B0.Clear();
                X1.Clear(); Y1.Clear(); Z1.Clear();
                X2.Clear(); Y2.Clear(); Z2.Clear();
                X3.Clear(); Y3.Clear(); Z3.Clear();
                I90.Clear(); FaceListStart = -1;
                Children?.Clear();
            }

            public void Set(int code, double v)
            {
                for (int i = 0; i < count; i++)
                {
                    if (codes[i] == code) { vals[i] = v; return; }
                }
                if (count == codes.Length)
                {
                    Array.Resize(ref codes, count * 2);
                    Array.Resize(ref vals, count * 2);
                }
                codes[count] = code; vals[count] = v; count++;
            }

            public bool Has(int code)
            {
                for (int i = 0; i < count; i++) if (codes[i] == code) return true;
                return false;
            }

            public double Get(int code, double def)
            {
                for (int i = 0; i < count; i++) if (codes[i] == code) return vals[i];
                return def;
            }

            public int GetInt(int code, int def)
            {
                for (int i = 0; i < count; i++) if (codes[i] == code) return (int)vals[i];
                return def;
            }

            public bool HasExtrusion => Has(210) || Has(220) || Has(230);

            public void Feed(int code, string value)
            {
                switch (code)
                {
                    case 8: Layer = value.Trim(); return;
                    case 2: Name = value.Trim(); return;
                    case 10: X0.Add(ParseD(value)); Y0.Add(0); Z0.Add(0); B0.Add(0); return;
                    case 20: if (Y0.Count > 0) Y0[Y0.Count - 1] = ParseD(value); return;
                    case 30: if (Z0.Count > 0) Z0[Z0.Count - 1] = ParseD(value); return;
                    case 11: X1.Add(ParseD(value)); Y1.Add(0); Z1.Add(0); return;
                    case 21: if (Y1.Count > 0) Y1[Y1.Count - 1] = ParseD(value); return;
                    case 31: if (Z1.Count > 0) Z1[Z1.Count - 1] = ParseD(value); return;
                    case 12: X2.Add(ParseD(value)); Y2.Add(0); Z2.Add(0); return;
                    case 22: if (Y2.Count > 0) Y2[Y2.Count - 1] = ParseD(value); return;
                    case 32: if (Z2.Count > 0) Z2[Z2.Count - 1] = ParseD(value); return;
                    case 13: X3.Add(ParseD(value)); Y3.Add(0); Z3.Add(0); return;
                    case 23: if (Y3.Count > 0) Y3[Y3.Count - 1] = ParseD(value); return;
                    case 33: if (Z3.Count > 0) Z3[Z3.Count - 1] = ParseD(value); return;
                    case 42:
                    {
                        double v = ParseD(value);
                        if (B0.Count > 0) B0[B0.Count - 1] = v;
                        Set(42, v);
                        return;
                    }
                    case 62: Color = (int)ParseD(value); return;
                    case 90: I90.Add((int)ParseD(value)); return;
                    case 93: FaceListStart = I90.Count; Set(93, ParseD(value)); return;
                }
                if (IsNumericCode(code)) Set(code, ParseD(value));
            }

            private static bool IsNumericCode(int code)
            {
                return (code >= 10 && code <= 99) || (code >= 140 && code <= 149) || (code >= 160 && code <= 179)
                    || (code >= 210 && code <= 239) || (code >= 270 && code <= 289) || (code >= 370 && code <= 389)
                    || (code >= 400 && code <= 409) || (code >= 420 && code <= 429) || (code >= 440 && code <= 469);
            }

            private static double ParseD(string s)
            {
                // "NaN" / "Infinity" / 1e400 parse too; one of them as the first coordinate became
                // the import origin and turned every vertex of the drawing into NaN.
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                       && !double.IsNaN(v) && !double.IsInfinity(v) ? v : 0.0;
            }
        }

        private sealed class LayerInfo
        {
            public int Color = 7;
            public bool Hidden;
        }

        private sealed class Block
        {
            public string Name = "";
            public double BaseX, BaseY, BaseZ;
            public readonly List<Ent> Entities = new List<Ent>();
        }

        // ------------------------------------------------------------------ pair reader

        /// <summary>
        /// Streams (group code, value) pairs. Lines that are not a valid code are skipped one at a
        /// time so a stray blank line resyncs instead of shifting every following pair.
        /// </summary>
        private sealed class PairReader
        {
            private readonly TextReader reader;
            private bool hasPushed;
            private int pushedCode;
            private string pushedValue;
            private long pairs;

            public PairReader(TextReader reader) { this.reader = reader; }

            public bool Next(out int code, out string value)
            {
                if (hasPushed)
                {
                    hasPushed = false; code = pushedCode; value = pushedValue; return true;
                }
                for (;;)
                {
                    string codeLine = reader.ReadLine();
                    if (codeLine == null) { code = 0; value = null; return false; }
                    if (TryParseCode(codeLine, out code))
                    {
                        value = reader.ReadLine();
                        if (value == null) return false;
                        pairs++;
                        return true;
                    }
                    if (pairs == 0 && codeLine.StartsWith(BinarySentinel, StringComparison.Ordinal))
                        throw new InvalidDataException(BinaryMessage);
                }
            }

            public void PushBack(int code, string value)
            {
                hasPushed = true; pushedCode = code; pushedValue = value;
            }

            private static bool TryParseCode(string s, out int code)
            {
                code = 0;
                int i = 0, n = s.Length;
                while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
                if (i == n) return false;
                bool neg = false;
                if (s[i] == '-') { neg = true; i++; }
                else if (s[i] == '+') i++;
                int digits = 0, v = 0;
                while (i < n && s[i] >= '0' && s[i] <= '9')
                {
                    v = v * 10 + (s[i] - '0');
                    i++; digits++;
                    if (digits > 5) return false;
                }
                if (digits == 0) return false;
                while (i < n && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r')) i++;
                if (i != n) return false;
                code = neg ? -v : v;
                return true;
            }
        }

        // ------------------------------------------------------------------ parser

        private sealed class Parser
        {
            private readonly PairReader pairs;
            private readonly ModelData model;
            private readonly ImportOptions options;
            private readonly Action<float> progress;
            private readonly Func<float> position;

            private double scale;
            private readonly bool zUp;
            private bool originSet;
            private double originE, originN, originH;
            // What is subtracted from each vertex: the picked origin, or nothing when the header
            // gave one (it is the map E,N,H of the file's (0,0,0), as send_model.py --origin means it).
            private double shiftE, shiftN, shiftH;

            private readonly Dictionary<string, ModelGroup> groups = new Dictionary<string, ModelGroup>(StringComparer.Ordinal);
            private readonly Dictionary<string, LayerInfo> layers = new Dictionary<string, LayerInfo>(StringComparer.Ordinal);
            private readonly Dictionary<string, Block> blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> skipped = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> missingBlocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> insertChain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private int splineCount, depthSkips, degenerateFaces;
            private string lastLayer;
            private ModelGroup lastGroup;

            private readonly Ent scratch = new Ent();
            private readonly Ent consume = new Ent();
            private readonly List<Ent> childPool = new List<Ent>();
            private readonly List<double> sx = new List<double>(), sy = new List<double>(), sz = new List<double>(), sb = new List<double>();
            private int entityCount;

            private bool hasPoints;
            private float minX, minY, minZ, maxX, maxY, maxZ;

            public Parser(TextReader reader, string name, ImportOptions options, Action<float> progress, Func<float> position)
            {
                pairs = new PairReader(reader);
                this.options = options;
                this.progress = progress;
                this.position = position;
                scale = options.UnitScale > 0f ? options.UnitScale : 1.0;
                zUp = options.ZUp;
                if (options.HasOrigin)
                {
                    originSet = true;
                    originE = options.OriginE; originN = options.OriginN; originH = options.OriginH;
                }
                model = new ModelData
                {
                    Name = name,
                    SourceFormat = "dxf",
                    Georeferenced = options.Georeferenced,
                };
                minX = minY = minZ = float.MaxValue;
                maxX = maxY = maxZ = float.MinValue;
            }

            public ModelData Run()
            {
                string section = null;
                string headerVar = null;
                while (pairs.Next(out int code, out string value))
                {
                    if (code == 0)
                    {
                        string type = value.Trim();
                        if (type == "SECTION")
                        {
                            if (pairs.Next(out int c2, out string v2))
                            {
                                if (c2 == 2) section = v2.Trim();
                                else pairs.PushBack(c2, v2);
                            }
                            continue;
                        }
                        if (type == "ENDSEC") { section = null; continue; }
                        if (type == "EOF") break;
                        if (section == null) continue;
                        switch (section)
                        {
                            case "TABLES":
                                if (type == "LAYER") { ReadEntity(scratch, type, true); RegisterLayer(scratch); }
                                break;
                            case "BLOCKS":
                                if (type == "BLOCK") ReadBlock();
                                break;
                            case "ENTITIES":
                                ReadEntity(scratch, type, true);
                                Process(scratch, Xform.Identity, null, 7, 0);
                                entityCount++;
                                if (progress != null && position != null && (entityCount % ProgressEveryEntities) == 0)
                                    progress(Mathf.Clamp01(position()));
                                break;
                        }
                    }
                    else if (section == "HEADER")
                    {
                        if (code == 9) headerVar = value.Trim();
                        else if (code == 70 && headerVar == "$INSUNITS"
                                 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int units))
                            ApplyInsUnits(units);
                    }
                }
                Finish();
                return model;
            }

            /// <summary>
            /// The caller's unit scale wins when it is explicit; only a default of 1 is overridden
            /// by what the file declares, so a mm drawing does not come in a thousand times too big.
            /// </summary>
            private void ApplyInsUnits(int units)
            {
                if (Math.Abs(options.UnitScale - 1f) > 1e-6f) return;
                string unitName; double factor;
                switch (units)
                {
                    case 1: unitName = L.T("ίντσες", "inches"); factor = 0.0254; break;
                    case 2: unitName = L.T("πόδια", "feet"); factor = 0.3048; break;
                    case 4: unitName = L.T("χιλιοστά", "millimetres"); factor = 0.001; break;
                    case 5: unitName = L.T("εκατοστά", "centimetres"); factor = 0.01; break;
                    default: return;
                }
                scale = factor;
                string scaleText = factor.ToString(CultureInfo.InvariantCulture);
                model.Warnings.Add(L.T($"Μονάδες από $INSUNITS: {unitName} ({units}) — εφαρμόστηκε κλίμακα {scaleText}",
                    $"Units from $INSUNITS: {unitName} ({units}) — scale {scaleText} applied"));
            }

            // -------------------------------------------------------------- reading

            /// <summary>Reads the pairs of one entity (the "0 TYPE" pair is already consumed); POLYLINE also swallows its VERTEX..SEQEND.</summary>
            private void ReadEntity(Ent e, string type, bool pooled)
            {
                e.Reset(type);
                while (pairs.Next(out int code, out string value))
                {
                    if (code == 0) { pairs.PushBack(code, value); break; }
                    e.Feed(code, value);
                }
                if (type != "POLYLINE") return;
                if (e.Children == null) e.Children = new List<Ent>();
                int poolUsed = 0;
                while (pairs.Next(out int code, out string value))
                {
                    if (code != 0) continue;
                    string t = value.Trim();
                    if (t == "VERTEX")
                    {
                        Ent child;
                        if (pooled)
                        {
                            if (poolUsed == childPool.Count) childPool.Add(new Ent());
                            child = childPool[poolUsed++];
                        }
                        else child = new Ent();
                        ReadEntity(child, t, pooled);
                        e.Children.Add(child);
                    }
                    else if (t == "SEQEND")
                    {
                        ReadEntity(consume, t, true);
                        break;
                    }
                    else
                    {
                        pairs.PushBack(code, value);
                        break;
                    }
                }
            }

            private void ReadBlock()
            {
                var header = new Ent();
                ReadEntity(header, "BLOCK", false);
                var block = new Block { Name = header.Name };
                if (header.X0.Count > 0) { block.BaseX = header.X0[0]; block.BaseY = header.Y0[0]; block.BaseZ = header.Z0[0]; }
                while (pairs.Next(out int code, out string value))
                {
                    if (code != 0) continue;
                    string t = value.Trim();
                    if (t == "ENDBLK") { ReadEntity(consume, t, true); break; }
                    if (t == "ENDSEC" || t == "EOF" || t == "BLOCK") { pairs.PushBack(code, value); break; }
                    var e = new Ent();
                    ReadEntity(e, t, false);
                    block.Entities.Add(e);
                }
                if (block.Name.Length > 0) blocks[block.Name] = block;
            }

            private void RegisterLayer(Ent e)
            {
                if (e.Name.Length == 0) return;
                var info = new LayerInfo();
                int color = e.Color;
                if (color < 0) { info.Hidden = true; color = -color; }
                if (color == 256 || color == 0) color = 7;
                info.Color = color;
                if ((e.GetInt(70, 0) & 1) != 0) info.Hidden = true;
                layers[e.Name] = info;
            }

            // -------------------------------------------------------------- processing

            private void Process(Ent e, in Xform parent, string layerOverride, int blockColor, int depth)
            {
                string layer = e.Layer;
                if (layerOverride != null && (layer.Length == 0 || layer == "0")) layer = layerOverride;
                int color = e.Color == 0 ? blockColor : e.Color;
                if (e.GetInt(60, 0) == 1) return; // invisible entity

                switch (e.Type)
                {
                    case "LINE":
                    {
                        if (e.X0.Count == 0 || e.X1.Count == 0) return;
                        var g = GetGroup(layer, color);
                        AddLine(g, parent, e.X0[0], e.Y0[0], e.Z0[0], e.X1[0], e.Y1[0], e.Z1[0]);
                        return;
                    }
                    case "LWPOLYLINE":
                    {
                        if (e.X0.Count < 2) return;
                        var g = GetGroup(layer, color);
                        Xform xf = WithOcs(e, parent);
                        bool closed = (e.GetInt(70, 0) & 1) != 0;
                        EmitPolyline(g, xf, e.X0, e.Y0, null, e.Get(38, 0), e.B0, closed);
                        return;
                    }
                    case "POLYLINE":
                        ProcessPolyline(e, parent, layer, color);
                        return;
                    case "CIRCLE":
                    {
                        if (e.X0.Count == 0) return;
                        double r = e.Get(40, 0);
                        if (r <= 0) return;
                        var g = GetGroup(layer, color);
                        Xform xf = WithOcs(e, parent);
                        EmitArc(g, xf, e.X0[0], e.Y0[0], e.Z0[0], r, 0, 360, CircleSegments);
                        return;
                    }
                    case "ARC":
                    {
                        if (e.X0.Count == 0) return;
                        double r = e.Get(40, 0);
                        if (r <= 0) return;
                        double a0 = e.Get(50, 0), a1 = e.Get(51, 360);
                        double sweep = a1 - a0;
                        while (sweep <= 0) sweep += 360;
                        while (sweep > 360) sweep -= 360;
                        var g = GetGroup(layer, color);
                        Xform xf = WithOcs(e, parent);
                        int segs = Math.Max(1, (int)Math.Ceiling(sweep / ArcStepDegrees));
                        EmitArc(g, xf, e.X0[0], e.Y0[0], e.Z0[0], r, a0, a0 + sweep, segs);
                        return;
                    }
                    case "ELLIPSE":
                        ProcessEllipse(e, parent, layer, color);
                        return;
                    case "3DFACE":
                    {
                        if (e.X0.Count == 0 || e.X1.Count == 0 || e.X2.Count == 0) return;
                        var g = GetGroup(layer, color);
                        AddTriangle(g, parent, e.X0[0], e.Y0[0], e.Z0[0], e.X1[0], e.Y1[0], e.Z1[0], e.X2[0], e.Y2[0], e.Z2[0]);
                        if (e.X3.Count > 0 && !Same(e.X2[0], e.Y2[0], e.Z2[0], e.X3[0], e.Y3[0], e.Z3[0]))
                            AddTriangle(g, parent, e.X0[0], e.Y0[0], e.Z0[0], e.X2[0], e.Y2[0], e.Z2[0], e.X3[0], e.Y3[0], e.Z3[0]);
                        return;
                    }
                    case "SOLID":
                    case "TRACE":
                    {
                        // SOLID corners are given in bow-tie order 1-2-4-3, hence the (1, 3, 2) second triangle.
                        if (e.X0.Count == 0 || e.X1.Count == 0 || e.X2.Count == 0) return;
                        var g = GetGroup(layer, color);
                        Xform xf = WithOcs(e, parent);
                        AddTriangle(g, xf, e.X0[0], e.Y0[0], e.Z0[0], e.X1[0], e.Y1[0], e.Z1[0], e.X2[0], e.Y2[0], e.Z2[0]);
                        if (e.X3.Count > 0 && !Same(e.X2[0], e.Y2[0], e.Z2[0], e.X3[0], e.Y3[0], e.Z3[0]))
                            AddTriangle(g, xf, e.X1[0], e.Y1[0], e.Z1[0], e.X3[0], e.Y3[0], e.Z3[0], e.X2[0], e.Y2[0], e.Z2[0]);
                        return;
                    }
                    case "MESH":
                        ProcessMesh(e, parent, layer, color);
                        return;
                    case "SPLINE":
                    {
                        // Control points only: a hull polyline, not the curve. Reported as a warning.
                        List<double> xs = e.X0, ys = e.Y0, zs = e.Z0;
                        if (xs.Count < 2 && e.X1.Count >= 2) { xs = e.X1; ys = e.Y1; zs = e.Z1; }
                        if (xs.Count < 2) return;
                        var g = GetGroup(layer, color);
                        bool closed = (e.GetInt(70, 0) & 1) != 0;
                        EmitPolyline(g, parent, xs, ys, zs, 0, null, closed);
                        splineCount++;
                        return;
                    }
                    case "INSERT":
                        ProcessInsert(e, parent, layer, blockColor, depth);
                        return;
                    default:
                        skipped.TryGetValue(e.Type, out int n);
                        skipped[e.Type] = n + 1;
                        return;
                }
            }

            private void ProcessPolyline(Ent e, in Xform parent, string layer, int color)
            {
                var children = e.Children;
                if (children == null || children.Count == 0) return;
                int flags = e.GetInt(70, 0);
                if ((flags & 64) != 0)
                {
                    // Polyface mesh: location vertices first, then face records with 1-based indices.
                    sx.Clear(); sy.Clear(); sz.Clear();
                    for (int i = 0; i < children.Count; i++)
                    {
                        var v = children[i];
                        bool faceRecord = v.Has(71) || v.Has(72) || v.Has(73) || v.Has(74);
                        if (faceRecord || v.X0.Count == 0) continue;
                        sx.Add(v.X0[0]); sy.Add(v.Y0[0]); sz.Add(v.Z0[0]);
                    }
                    var g = GetGroup(layer, color);
                    for (int i = 0; i < children.Count; i++)
                    {
                        var v = children[i];
                        int i1 = Math.Abs(v.GetInt(71, 0)), i2 = Math.Abs(v.GetInt(72, 0));
                        int i3 = Math.Abs(v.GetInt(73, 0)), i4 = Math.Abs(v.GetInt(74, 0));
                        if (i1 == 0 || i2 == 0 || i3 == 0) continue;
                        if (!ValidIndex(i1) || !ValidIndex(i2) || !ValidIndex(i3)) { degenerateFaces++; continue; }
                        AddTriangle(g, parent, sx[i1 - 1], sy[i1 - 1], sz[i1 - 1], sx[i2 - 1], sy[i2 - 1], sz[i2 - 1], sx[i3 - 1], sy[i3 - 1], sz[i3 - 1]);
                        if (i4 != 0 && ValidIndex(i4))
                            AddTriangle(g, parent, sx[i1 - 1], sy[i1 - 1], sz[i1 - 1], sx[i3 - 1], sy[i3 - 1], sz[i3 - 1], sx[i4 - 1], sy[i4 - 1], sz[i4 - 1]);
                    }
                    return;
                }
                if ((flags & 16) != 0)
                {
                    // Polygon mesh: M x N grid of vertices, closed in M (bit 1) and/or N (bit 32).
                    int m = e.GetInt(71, 0), n = e.GetInt(72, 0);
                    if (m < 2 || n < 2 || children.Count < m * n) return;
                    var g = GetGroup(layer, color);
                    bool closedM = (flags & 1) != 0, closedN = (flags & 32) != 0;
                    int rows = closedM ? m : m - 1, cols = closedN ? n : n - 1;
                    for (int i = 0; i < rows; i++)
                    {
                        int i2 = (i + 1) % m;
                        for (int j = 0; j < cols; j++)
                        {
                            int j2 = (j + 1) % n;
                            Ent a = children[i * n + j], b = children[i2 * n + j], c = children[i2 * n + j2], d = children[i * n + j2];
                            if (a.X0.Count == 0 || b.X0.Count == 0 || c.X0.Count == 0 || d.X0.Count == 0) continue;
                            AddTriangle(g, parent, a.X0[0], a.Y0[0], a.Z0[0], b.X0[0], b.Y0[0], b.Z0[0], c.X0[0], c.Y0[0], c.Z0[0]);
                            AddTriangle(g, parent, a.X0[0], a.Y0[0], a.Z0[0], c.X0[0], c.Y0[0], c.Z0[0], d.X0[0], d.Y0[0], d.Z0[0]);
                        }
                    }
                    return;
                }
                // 2D (OCS, elevation in the POLYLINE's own 30) or 3D (bit 8, WCS) polyline.
                bool is3D = (flags & 8) != 0;
                bool splineFit = (flags & 4) != 0;
                double elevation = (!is3D && e.Z0.Count > 0) ? e.Z0[0] : 0.0;
                sx.Clear(); sy.Clear(); sz.Clear(); sb.Clear();
                for (int i = 0; i < children.Count; i++)
                {
                    var v = children[i];
                    if (v.X0.Count == 0) continue;
                    int vf = v.GetInt(70, 0);
                    // A spline-fit polyline carries both the frame (bit 16) and the fitted curve (bit 8): draw the curve.
                    if (splineFit && (vf & 16) != 0) continue;
                    sx.Add(v.X0[0]); sy.Add(v.Y0[0]); sz.Add(v.Z0[0] + elevation); sb.Add(v.Get(42, 0));
                }
                if (sx.Count < 2) return;
                var group = GetGroup(layer, color);
                Xform xf = is3D ? parent : WithOcs(e, parent);
                EmitPolyline(group, xf, sx, sy, sz, 0, sb, (flags & 1) != 0);
            }

            private bool ValidIndex(int oneBased) => oneBased >= 1 && oneBased <= sx.Count;

            private void ProcessEllipse(Ent e, in Xform parent, string layer, int color)
            {
                if (e.X0.Count == 0 || e.X1.Count == 0) return;
                double cx = e.X0[0], cy = e.Y0[0], cz = e.Z0[0];
                double mx = e.X1[0], my = e.Y1[0], mz = e.Z1[0];
                double major = Math.Sqrt(mx * mx + my * my + mz * mz);
                if (major <= 0) return;
                double ratio = e.Get(40, 1);
                double u0 = e.Get(41, 0), u1 = e.Get(42, 2 * Math.PI);
                while (u1 <= u0) u1 += 2 * Math.PI;
                double sweep = u1 - u0;
                if (sweep > 2 * Math.PI) sweep = 2 * Math.PI;
                // Minor axis = (N x major) * ratio, with N the extrusion (default +Z). The ellipse itself is WCS.
                double nx = e.Get(210, 0), ny = e.Get(220, 0), nz = e.Has(230) ? e.Get(230, 1) : 1;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-12) { nx = 0; ny = 0; nz = 1; } else { nx /= len; ny /= len; nz /= len; }
                double ix = ny * mz - nz * my, iy = nz * mx - nx * mz, iz = nx * my - ny * mx;
                double ilen = Math.Sqrt(ix * ix + iy * iy + iz * iz);
                if (ilen < 1e-12) return;
                double k = major * ratio / ilen;
                ix *= k; iy *= k; iz *= k;
                int segs = Math.Max(1, (int)Math.Ceiling(sweep / (ArcStepDegrees * Math.PI / 180.0)));
                var g = GetGroup(layer, color);
                double px = 0, py = 0, pz = 0;
                for (int i = 0; i <= segs; i++)
                {
                    double u = u0 + sweep * i / segs;
                    double cu = Math.Cos(u), su = Math.Sin(u);
                    double x = cx + mx * cu + ix * su, y = cy + my * cu + iy * su, z = cz + mz * cu + iz * su;
                    if (i > 0) AddLine(g, parent, px, py, pz, x, y, z);
                    px = x; py = y; pz = z;
                }
            }

            private void ProcessMesh(Ent e, in Xform parent, string layer, int color)
            {
                int vcount = e.X0.Count;
                if (vcount < 3 || e.FaceListStart < 0) return;
                int start = e.FaceListStart;
                // long: a huge (or corrupt) code-93 size overflowed int and dropped every face.
                int end = (int)Math.Min(e.I90.Count, (long)start + Math.Max(0, e.GetInt(93, int.MaxValue)));
                var g = GetGroup(layer, color);
                int i = start;
                while (i < end)
                {
                    int n = e.I90[i++];
                    if (n < 0 || n > end - i) break; // i + n overflows for a corrupt count near int.MaxValue
                    if (n < 3) { i += n; continue; }
                    int a = e.I90[i];
                    for (int k = 1; k + 1 < n; k++)
                    {
                        int b = e.I90[i + k], c = e.I90[i + k + 1];
                        if (a < 0 || b < 0 || c < 0 || a >= vcount || b >= vcount || c >= vcount) { degenerateFaces++; continue; }
                        AddTriangle(g, parent, e.X0[a], e.Y0[a], e.Z0[a], e.X0[b], e.Y0[b], e.Z0[b], e.X0[c], e.Y0[c], e.Z0[c]);
                    }
                    i += n;
                }
            }

            private void ProcessInsert(Ent e, in Xform parent, string layer, int blockColor, int depth)
            {
                string name = e.Name;
                if (name.Length == 0 || !blocks.TryGetValue(name, out Block block))
                {
                    missingBlocks.TryGetValue(name, out int n);
                    missingBlocks[name] = n + 1;
                    return;
                }
                if (depth >= MaxInsertDepth || insertChain.Contains(name)) { depthSkips++; return; }
                if (block.Entities.Count == 0) return;

                double ix = 0, iy = 0, iz = 0;
                if (e.X0.Count > 0) { ix = e.X0[0]; iy = e.Y0[0]; iz = e.Z0[0]; }
                double sxv = e.Has(41) ? e.Get(41, 1) : 1, syv = e.Has(42) ? e.Get(42, 1) : 1, szv = e.Has(43) ? e.Get(43, 1) : 1;
                double rotation = e.Get(50, 0);
                int cols = Math.Max(1, e.GetInt(70, 1)), rows = Math.Max(1, e.GetInt(71, 1));
                double colSpacing = e.Get(44, 0), rowSpacing = e.Get(45, 0);
                // A corrupt array count (70/71 up to 32767 each) would expand a block a billion
                // times on the import thread; past this many copies the insert is skipped.
                if ((long)rows * cols > MaxInsertCopies) { depthSkips++; return; }

                // WCS = parent * OCS * T(insertion) * Rz * T(array offset) * S * T(-base)
                Xform outer = WithOcs(e, parent);
                outer = Xform.Mul(outer, Xform.Translation(ix, iy, iz));
                outer = Xform.Mul(outer, Xform.RotationZ(rotation));
                Xform inner = Xform.Mul(Xform.Scale(sxv, syv, szv), Xform.Translation(-block.BaseX, -block.BaseY, -block.BaseZ));

                int insertColor = e.Color == 0 ? blockColor : (e.Color == 256 ? LayerColor(layer) : e.Color);
                insertChain.Add(name);
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        Xform xf = (r == 0 && c == 0)
                            ? Xform.Mul(outer, inner)
                            : Xform.Mul(Xform.Mul(outer, Xform.Translation(c * colSpacing, r * rowSpacing, 0)), inner);
                        var list = block.Entities;
                        for (int i = 0; i < list.Count; i++) Process(list[i], xf, layer, insertColor, depth + 1);
                    }
                }
                insertChain.Remove(name);
            }

            // -------------------------------------------------------------- geometry helpers

            /// <summary>Parent transform combined with the entity's OCS when its extrusion is not world Z.</summary>
            private static Xform WithOcs(Ent e, in Xform parent)
            {
                if (!e.HasExtrusion) return parent;
                double nx = e.Get(210, 0), ny = e.Get(220, 0), nz = e.Get(230, 0);
                if (Math.Abs(nx) < 1e-12 && Math.Abs(ny) < 1e-12 && nz > 0) return parent;
                return Xform.Mul(parent, Xform.Ocs(nx, ny, nz));
            }

            private static bool Same(double ax, double ay, double az, double bx, double by, double bz)
            {
                return Math.Abs(ax - bx) < 1e-9 && Math.Abs(ay - by) < 1e-9 && Math.Abs(az - bz) < 1e-9;
            }

            private void EmitArc(ModelGroup g, in Xform xf, double cx, double cy, double cz, double r, double startDeg, double endDeg, int segments)
            {
                double a0 = startDeg * Math.PI / 180.0, a1 = endDeg * Math.PI / 180.0;
                double px = cx + r * Math.Cos(a0), py = cy + r * Math.Sin(a0);
                for (int i = 1; i <= segments; i++)
                {
                    double a = a0 + (a1 - a0) * i / segments;
                    double x = cx + r * Math.Cos(a), y = cy + r * Math.Sin(a);
                    AddLine(g, xf, px, py, cz, x, y, cz);
                    px = x; py = y;
                }
            }

            /// <summary>
            /// Walks consecutive vertices; a non-zero bulge turns the segment into an arc.
            /// <paramref name="zs"/> null means a constant <paramref name="elevation"/> (LWPOLYLINE).
            /// </summary>
            private void EmitPolyline(ModelGroup g, in Xform xf, List<double> xs, List<double> ys, List<double> zs, double elevation, List<double> bulges, bool closed)
            {
                int n = xs.Count;
                int segments = closed ? n : n - 1;
                if (closed && n == 2) segments = 1;
                for (int i = 0; i < segments; i++)
                {
                    int j = (i + 1) % n;
                    double ax = xs[i], ay = ys[i], az = zs != null ? zs[i] : elevation;
                    double bx = xs[j], by = ys[j], bz = zs != null ? zs[j] : elevation;
                    if (Same(ax, ay, az, bx, by, bz)) continue;
                    double bulge = bulges != null && i < bulges.Count ? bulges[i] : 0.0;
                    if (Math.Abs(bulge) < 1e-9) AddLine(g, xf, ax, ay, az, bx, by, bz);
                    else EmitBulge(g, xf, ax, ay, az, bx, by, bz, bulge);
                }
            }

            /// <summary>
            /// bulge = tan(theta/4) of the included angle, positive for a counter-clockwise arc.
            /// Signed radius r = chord / (2 sin(theta/2)) puts the centre on the correct side of the chord.
            /// </summary>
            private void EmitBulge(ModelGroup g, in Xform xf, double ax, double ay, double az, double bx, double by, double bz, double bulge)
            {
                double theta = 4.0 * Math.Atan(bulge);
                double dx = bx - ax, dy = by - ay;
                double chord = Math.Sqrt(dx * dx + dy * dy);
                if (chord < 1e-12) { AddLine(g, xf, ax, ay, az, bx, by, bz); return; }
                double r = chord / (2.0 * Math.Sin(theta / 2.0));
                double nx = -dy / chord, ny = dx / chord;
                double h = r * Math.Cos(theta / 2.0);
                double cx = (ax + bx) * 0.5 + nx * h, cy = (ay + by) * 0.5 + ny * h;
                double startAngle = Math.Atan2(ay - cy, ax - cx);
                double radius = Math.Abs(r);
                int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 2.0) * BulgeSegmentsPerQuarter));
                double px = ax, py = ay, pz = az;
                for (int i = 1; i <= segments; i++)
                {
                    double t = (double)i / segments;
                    double a = startAngle + theta * t;
                    double x = i == segments ? bx : cx + radius * Math.Cos(a);
                    double y = i == segments ? by : cy + radius * Math.Sin(a);
                    double z = az + (bz - az) * t;
                    AddLine(g, xf, px, py, pz, x, y, z);
                    px = x; py = y; pz = z;
                }
            }

            private void AddLine(ModelGroup g, in Xform xf, double ax, double ay, double az, double bx, double by, double bz)
            {
                xf.Apply(ax, ay, az, out double wx, out double wy, out double wz);
                Vector3 a = ToLocal(wx, wy, wz);
                xf.Apply(bx, by, bz, out wx, out wy, out wz);
                Vector3 b = ToLocal(wx, wy, wz);
                g.LineVertices.Add(a);
                g.LineVertices.Add(b);
            }

            private void AddTriangle(ModelGroup g, in Xform xf, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
            {
                if (Same(ax, ay, az, bx, by, bz) || Same(bx, by, bz, cx, cy, cz) || Same(ax, ay, az, cx, cy, cz)) { degenerateFaces++; return; }
                xf.Apply(ax, ay, az, out double wx, out double wy, out double wz);
                Vector3 a = ToLocal(wx, wy, wz);
                xf.Apply(bx, by, bz, out wx, out wy, out wz);
                Vector3 b = ToLocal(wx, wy, wz);
                xf.Apply(cx, cy, cz, out wx, out wy, out wz);
                Vector3 c = ToLocal(wx, wy, wz);
                int baseIndex = g.Vertices.Count;
                g.Vertices.Add(a); g.Vertices.Add(b); g.Vertices.Add(c);
                g.Triangles.Add(baseIndex); g.Triangles.Add(baseIndex + 1); g.Triangles.Add(baseIndex + 2);
            }

            /// <summary>
            /// WCS source units to Unity local metres: unit scale, then the origin (chosen from the
            /// first coordinate when the caller gave none, rounded down to 100 m so that it is a
            /// round number a surveyor recognises), then the axis swap.
            /// </summary>
            private Vector3 ToLocal(double x, double y, double z)
            {
                double e = x * scale, n, h;
                if (zUp) { n = y * scale; h = z * scale; }
                else { h = y * scale; n = -z * scale; }
                if (!originSet)
                {
                    originE = Math.Floor(e / 100.0) * 100.0;
                    originN = Math.Floor(n / 100.0) * 100.0;
                    originH = Math.Floor(h / 100.0) * 100.0;
                    shiftE = originE; shiftN = originN; shiftH = originH;
                    originSet = true;
                }
                float lx = (float)(e - shiftE), ly = (float)(h - shiftH), lz = (float)(n - shiftN);
                if (lx < minX) minX = lx; if (lx > maxX) maxX = lx;
                if (ly < minY) minY = ly; if (ly > maxY) maxY = ly;
                if (lz < minZ) minZ = lz; if (lz > maxZ) maxZ = lz;
                hasPoints = true;
                return new Vector3(lx, ly, lz);
            }

            private int LayerColor(string layer)
            {
                return layers.TryGetValue(layer, out LayerInfo info) ? info.Color : 7;
            }

            /// <summary>
            /// Groups are keyed by layer; the colour comes from the layer table, or, for a layer the
            /// table does not list, from the first entity that names an explicit colour.
            /// </summary>
            private ModelGroup GetGroup(string layer, int entityColor)
            {
                if (lastGroup != null && layer == lastLayer) return lastGroup;
                if (!groups.TryGetValue(layer, out ModelGroup g))
                {
                    g = new ModelGroup { Name = layer };
                    if (layers.TryGetValue(layer, out LayerInfo info))
                    {
                        g.Color = AciToColor(info.Color);
                        g.Visible = !info.Hidden;
                    }
                    else if (entityColor >= 1 && entityColor <= 255)
                    {
                        g.Color = AciToColor(entityColor);
                    }
                    groups[layer] = g;
                    model.Groups.Add(g);
                }
                lastLayer = layer;
                lastGroup = g;
                return g;
            }

            private void Finish()
            {
                int removed = 0;
                for (int i = model.Groups.Count - 1; i >= 0; i--)
                {
                    var g = model.Groups[i];
                    if (g.Triangles.Count == 0 && g.LineVertices.Count == 0) { model.Groups.RemoveAt(i); removed++; }
                }
                int tris = 0, lines = 0;
                for (int i = 0; i < model.Groups.Count; i++)
                {
                    tris += model.Groups[i].Triangles.Count / 3;
                    lines += model.Groups[i].LineVertices.Count / 2;
                }
                model.TriangleCount = tris;
                model.LineCount = lines;
                model.UnitScale = (float)scale;
                model.OriginE = originE; model.OriginN = originN; model.OriginH = originH;
                if (hasPoints)
                {
                    var b = new Bounds();
                    b.SetMinMax(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
                    model.LocalBounds = b;
                }
                else model.LocalBounds = new Bounds(Vector3.zero, Vector3.zero);

                if (skipped.Count > 0)
                {
                    var types = new List<string>(skipped.Keys);
                    types.Sort(StringComparer.Ordinal);
                    for (int i = 0; i < types.Count; i++)
                        model.Warnings.Add(L.T($"{types[i]}: {skipped[types[i]]} παραλείφθηκαν", $"{types[i]}: {skipped[types[i]]} skipped"));
                }
                if (splineCount > 0) model.Warnings.Add(L.T($"SPLINE: {splineCount} προσεγγίστηκαν από το πολύγωνο των σημείων ελέγχου",
                    $"SPLINE: {splineCount} approximated by their control polygon"));
                if (missingBlocks.Count > 0)
                {
                    int total = 0;
                    foreach (var kv in missingBlocks) total += kv.Value;
                    model.Warnings.Add(L.T($"INSERT: {total} αναφορές σε άγνωστα μπλοκ παραλείφθηκαν ({missingBlocks.Count} ονόματα)",
                        $"INSERT: {total} reference{(total == 1 ? "" : "s")} to unknown blocks skipped ({missingBlocks.Count} name{(missingBlocks.Count == 1 ? "" : "s")})"));
                }
                if (depthSkips > 0) model.Warnings.Add(L.T($"INSERT: {depthSkips} παραλείφθηκαν (βάθος φωλιάσματος πάνω από {MaxInsertDepth}, κυκλική αναφορά ή πίνακας πάνω από {MaxInsertCopies} αντιγράφων)",
                    $"INSERT: {depthSkips} skipped (nesting deeper than {MaxInsertDepth}, circular reference or array over {MaxInsertCopies} copies)"));
                if (degenerateFaces > 0) model.Warnings.Add(L.T($"{degenerateFaces} εκφυλισμένες όψεις παραλείφθηκαν",
                    $"{degenerateFaces} degenerate face{(degenerateFaces == 1 ? "" : "s")} skipped"));
                if (removed > 0) model.Warnings.Add(L.T($"{removed} επίπεδα χωρίς γεωμετρία παραλείφθηκαν",
                    $"{removed} layer{(removed == 1 ? "" : "s")} without geometry skipped"));
                if (model.Groups.Count == 0) model.Warnings.Add(L.T("Δεν βρέθηκε γεωμετρία στο DXF", "No geometry found in the DXF"));
            }
        }

        // ------------------------------------------------------------------ self-test

        /// <summary>
        /// A tiny DXF covering the common entities, used by <see cref="SelfTest"/> and the offline
        /// harness: one LAYER (WALLS, red), a LINE, a closed LWPOLYLINE with a semicircular bulge,
        /// a CIRCLE, a 3DFACE and a BLOCK with one LINE inserted twice (0 and 90 degrees).
        /// Expected: 4 groups, 53 line segments, 2 triangles, bounds (0,0,0)..(101,5,101).
        /// </summary>
        public static string BuildSelfTestDxf()
        {
            var sb = new StringBuilder();
            void P(int code, string v) { sb.Append(code.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("\r\n").Append(v).Append("\r\n"); }
            void Pt(int baseCode, double x, double y, double z)
            {
                P(baseCode, x.ToString(CultureInfo.InvariantCulture));
                P(baseCode + 10, y.ToString(CultureInfo.InvariantCulture));
                P(baseCode + 20, z.ToString(CultureInfo.InvariantCulture));
            }
            P(0, "SECTION"); P(2, "HEADER"); P(9, "$INSUNITS"); P(70, "6"); P(0, "ENDSEC");
            P(0, "SECTION"); P(2, "TABLES");
            P(0, "TABLE"); P(2, "LAYER"); P(70, "1");
            P(0, "LAYER"); P(2, "WALLS"); P(70, "0"); P(62, "1"); P(6, "CONTINUOUS");
            P(0, "ENDTAB");
            P(0, "ENDSEC");
            P(0, "SECTION"); P(2, "BLOCKS");
            P(0, "BLOCK"); P(8, "0"); P(2, "B1"); P(70, "0"); Pt(10, 0, 0, 0); P(3, "B1");
            P(0, "LINE"); P(8, "0"); Pt(10, 0, 0, 0); Pt(11, 1, 0, 0);
            P(0, "ENDBLK"); P(8, "0");
            P(0, "ENDSEC");
            P(0, "SECTION"); P(2, "ENTITIES");
            P(0, "LINE"); P(8, "WALLS"); Pt(10, 0, 0, 0); Pt(11, 10, 0, 0);
            P(0, "LWPOLYLINE"); P(8, "WALLS"); P(90, "3"); P(70, "1");
            P(10, "0"); P(20, "0");
            P(10, "10"); P(20, "0"); P(42, "1");
            P(10, "10"); P(20, "10");
            P(0, "CIRCLE"); P(8, "CIRC"); P(62, "3"); Pt(10, 50, 50, 0); P(40, "5");
            P(0, "3DFACE"); P(8, "ROOF"); Pt(10, 0, 0, 5); Pt(11, 10, 0, 5); Pt(12, 10, 10, 5); Pt(13, 0, 10, 5);
            P(0, "INSERT"); P(8, "INS"); P(2, "B1"); Pt(10, 100, 100, 0);
            P(0, "INSERT"); P(8, "INS"); P(2, "B1"); Pt(10, 100, 100, 0); P(50, "90");
            P(0, "ENDSEC");
            P(0, "EOF");
            return sb.ToString();
        }

#if UNITY_EDITOR
        /// <summary>Parses <see cref="BuildSelfTestDxf"/> and asserts counts, colours and bounds (Editor only).</summary>
        public static void SelfTest()
        {
            ModelData m;
            using (var reader = new StringReader(BuildSelfTestDxf()))
                m = Parse(reader, "selftest", ImportOptions.Default);
            Debug.Assert(m.Groups.Count == 4, $"DXF self-test: groups {m.Groups.Count} != 4");
            Debug.Assert(m.LineCount == 53, $"DXF self-test: lines {m.LineCount} != 53");
            Debug.Assert(m.TriangleCount == 2, $"DXF self-test: triangles {m.TriangleCount} != 2");
            Debug.Assert(m.OriginE == 0 && m.OriginN == 0 && m.OriginH == 0, "DXF self-test: origin should be 0");
            Debug.Assert(Vector3.Distance(m.LocalBounds.min, Vector3.zero) < 1e-3f, $"DXF self-test: min {m.LocalBounds.min}");
            Debug.Assert(Vector3.Distance(m.LocalBounds.max, new Vector3(101f, 5f, 101f)) < 1e-3f, $"DXF self-test: max {m.LocalBounds.max}");
            ModelGroup walls = m.Groups.Find(g => g.Name == "WALLS");
            Debug.Assert(walls != null && walls.Color == new Color(1f, 0f, 0f), "DXF self-test: WALLS should be red");
            Debug.Assert(walls != null && walls.LineVertices.Count == 2 * 19, "DXF self-test: WALLS should have 19 segments");
            ModelGroup ins = m.Groups.Find(g => g.Name == "INS");
            Debug.Assert(ins != null && ins.LineVertices.Count == 4, "DXF self-test: INS should have 2 segments");
            Debug.Assert(ins != null && Vector3.Distance(ins.LineVertices[3], new Vector3(100f, 0f, 101f)) < 1e-3f, "DXF self-test: rotated insert");
            Debug.Log($"DXF self-test OK: {m.Groups.Count} groups, {m.LineCount} lines, {m.TriangleCount} triangles, warnings: {string.Join(" | ", m.Warnings)}");
        }
#endif
    }
}
