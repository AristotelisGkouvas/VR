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
    /// Wavefront OBJ to <see cref="ModelData"/>, on a background thread. Handles what the
    /// survey/BIM/photogrammetry exporters actually write: v (3 or 4 numbers), vt, f in all
    /// four index forms with negative (relative) indices and n-gons, l polylines, g/o groups,
    /// usemtl + mtllib for group colours and diffuse maps (map_Kd), '#' comments, CRLF and '\'
    /// continuations. Numbers are read by hand from the line string rather than Split +
    /// double.Parse so a 400 MB / 4 M triangle file does not allocate a string per token.
    /// Vertices are not de-duplicated by position; a group may well exceed 65k vertices, so
    /// the overlay builds its meshes with 32-bit indices.
    /// </summary>
    public static class ObjImporter
    {
        private const int ProgressEveryLines = 20000;
        private static readonly Color DefaultColor = new Color(0.85f, 0.88f, 0.9f, 1f);

        /// <summary>
        /// Parse the file at <paramref name="path"/>; the MTL files it names are looked up next
        /// to it. <paramref name="progress"/> (0..1, by bytes) is called from the parsing thread.
        /// </summary>
        public static ModelData Load(string path, ImportOptions options, Action<float> progress = null,
            CancellationToken cancel = default)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            long bytes = 0;
            try { bytes = new FileInfo(path).Length; } catch { /* progress is then 0 until done */ }
            using (var reader = new StreamReader(path, Encoding.UTF8, true, 1 << 16))
            {
                return Parse(reader, Path.GetFileNameWithoutExtension(path), options, OpenBeside(dir), dir, bytes, progress, cancel);
            }
        }

        /// <summary>
        /// Parse OBJ text. Without a file there is no MTL lookup, so groups keep the default
        /// colour and no texture; UVs are still read.
        /// </summary>
        public static ModelData Parse(TextReader reader, string name, ImportOptions options)
        {
            return Parse(reader, name, options, null, null, 0, null, default);
        }

        // Opens "<dir>/<mtl name>" for mtllib; null when the file is not there.
        private static Func<string, TextReader> OpenBeside(string dir)
        {
            return file =>
            {
                try
                {
                    string full = Path.Combine(dir, file);
                    return IsInside(dir, full) && File.Exists(full) ? new StreamReader(full, Encoding.UTF8, true) : null;
                }
                catch { return null; }
            };
        }

        // ------------------------------------------------------------------
        //  Parser state
        // ------------------------------------------------------------------

        private sealed class State
        {
            public ImportOptions Options;
            public ModelData Model;
            // Global OBJ vertices, already in Unity local metres (origin subtracted).
            public List<Vector3> Vertices;
            // Global OBJ texture coordinates ("vt"), in file order.
            public List<Vector2> Uvs;
            // Per global vertex: which group it was last copied into, with which uv index (-1 =
            // none) and its index there, so a face that reuses a corner inside the same group
            // does not copy it again. This one-entry cache answers almost every lookup; the
            // per-group dictionaries below only hold the pairs it had to evict (UV seams).
            public List<int> RemapGroup, RemapUv, RemapLocal;
            // Per group index: (position << 32 | uv) to local vertex, created on the first
            // textured face of the group; null for untextured groups (today's cheap path).
            public List<Dictionary<long, int>> PairMaps = new List<Dictionary<long, int>>();
            public bool OriginChosen;
            public double OriginE, OriginN, OriginH;
            // Header origin (X-Model-Origin / sidecar) = map E,N,H of the file's (0,0,0): the
            // vertices are already relative to it (send_model.py / model_prep.py write them so).
            public double ShiftE, ShiftN, ShiftH;

            public Dictionary<string, MaterialDef> Materials = new Dictionary<string, MaterialDef>();
            // Folder of the OBJ, for mtllib paths; null when parsing text without a file.
            public string BaseDir;
            public string BaseGroup;          // null until the first g/o; faces before that go to "default"
            public string Material = "";
            public int CurrentGroup = -1;     // index into Model.Groups, -1 = resolve on next face
            public Dictionary<string, int> GroupIndex = new Dictionary<string, int>();
            public Dictionary<string, string> FirstMaterialOfBase = new Dictionary<string, string>();

            public List<int> FaceIndices = new List<int>(16);
            public List<int> FaceUvs = new List<int>(16);
            public int UnknownIndexFaces, Degenerate, BadLines, MissingMtl, BadUvFaces;
            public Func<string, TextReader> OpenMtl;
            public HashSet<string> MtlSeen = new HashSet<string>();
        }

        /// <summary>What the overlay uses of an MTL material: the tint and the diffuse map.</summary>
        private struct MaterialDef
        {
            public Color Color;
            /// <summary>Absolute path, null when the material has no map_Kd.</summary>
            public string Texture;
        }

        private static ModelData Parse(TextReader reader, string name, ImportOptions options,
            Func<string, TextReader> openMtl, string baseDir, long totalBytes, Action<float> progress, CancellationToken cancel)
        {
            var st = new State
            {
                Options = options,
                Model = new ModelData
                {
                    Name = name,
                    SourceFormat = "obj",
                    UnitScale = options.UnitScale <= 0f ? 1f : options.UnitScale,
                    Georeferenced = options.Georeferenced
                },
                OpenMtl = openMtl,
                BaseDir = baseDir,
                OriginChosen = options.HasOrigin,
                OriginE = options.OriginE, OriginN = options.OriginN, OriginH = options.OriginH
            };
            // ~40 bytes per "v x y z" line is a fair guess for capacity; avoids a dozen regrows.
            int hint = totalBytes > 0 ? (int)Math.Min(totalBytes / 40, 4_000_000) : 1024;
            st.Vertices = new List<Vector3>(hint);
            st.RemapGroup = new List<int>(hint);
            st.RemapUv = new List<int>(hint);
            st.RemapLocal = new List<int>(hint);
            st.Uvs = new List<Vector2>(1024);

            long consumed = 0;
            int lineNo = 0;
            StringBuilder joined = null;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                consumed += line.Length + 1;
                lineNo++;
                // Trailing backslash: the statement continues on the next line.
                if (line.Length > 0 && line[line.Length - 1] == '\\')
                {
                    if (joined == null) joined = new StringBuilder();
                    joined.Append(line, 0, line.Length - 1).Append(' ');
                    continue;
                }
                if (joined != null)
                {
                    joined.Append(line);
                    line = joined.ToString();
                    joined = null;
                }

                ParseLine(st, line);

                if ((lineNo % ProgressEveryLines) == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (progress != null && totalBytes > 0)
                        progress(Mathf.Clamp01((float)((double)consumed / totalBytes)));
                }
            }
            if (joined != null) ParseLine(st, joined.ToString());
            cancel.ThrowIfCancellationRequested();

            Finish(st);
            progress?.Invoke(1f);
            return st.Model;
        }

        // ------------------------------------------------------------------
        //  Lines
        // ------------------------------------------------------------------

        private static void ParseLine(State st, string s)
        {
            int len = s.Length;
            int i = SkipWs(s, 0, len);
            if (i >= len || s[i] == '#') return;
            int kStart = i;
            while (i < len && !IsWs(s[i])) i++;
            int kLen = i - kStart;
            char k0 = s[kStart];

            if (kLen == 1)
            {
                switch (k0)
                {
                    case 'v': ParseVertex(st, s, i, len); return;
                    case 'f': ParseFace(st, s, i, len); return;
                    case 'l': ParsePolyline(st, s, i, len); return;
                    case 'g':
                    case 'o':
                        string gname = Rest(s, i, len);
                        st.BaseGroup = gname.Length == 0 ? "default" : gname;
                        st.CurrentGroup = -1;
                        return;
                    case 's': case 'p': return; // smoothing groups / points: nothing to draw
                }
            }
            else if (kLen == 2 && k0 == 'v')
            {
                if (s[kStart + 1] == 't') ParseTexCoord(st, s, i, len);
                return; // vn / vp carry nothing the unlit overlay uses
            }
            else if (kLen == 6 && string.CompareOrdinal(s, kStart, "usemtl", 0, 6) == 0)
            {
                st.Material = Rest(s, i, len);
                st.CurrentGroup = -1;
                return;
            }
            else if (kLen == 6 && string.CompareOrdinal(s, kStart, "mtllib", 0, 6) == 0)
            {
                LoadMaterialLibs(st, Rest(s, i, len));
                return;
            }
            // Anything else (curves, surfaces, exporter extensions) is silently ignored.
        }

        private static void ParseVertex(State st, string s, int i, int len)
        {
            // "nan" / "inf" parse (double.TryParse fallback) but would poison the origin, and with
            // it every vertex of the model, when they come first: treat them as a bad line.
            if (!NextNumber(s, ref i, len, out double x) || !NextNumber(s, ref i, len, out double y)
                || !NextNumber(s, ref i, len, out double z) || !IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            {
                st.BadLines++;
                // Keep the index space consistent: a face pointing here is then "unknown".
                st.Vertices.Add(new Vector3(float.NaN, float.NaN, float.NaN));
                st.RemapGroup.Add(-1);
                st.RemapUv.Add(-1);
                st.RemapLocal.Add(0);
                return;
            }
            double sc = st.Model.UnitScale;
            double e = x * sc, h, n;
            if (st.Options.ZUp) { h = z * sc; n = y * sc; }
            else { h = y * sc; n = -z * sc; }   // right-handed Y-up to Unity's left-handed frame

            if (!st.OriginChosen)
            {
                // No header: absolute map coordinates in the file, shifted by an origin picked here.
                // Whole hundreds of metres: EGSA87 245 318.42 becomes 245 300, so every local
                // coordinate is a few hundred metres at most and float keeps millimetres.
                st.OriginE = Math.Floor(e / 100.0) * 100.0;
                st.OriginH = Math.Floor(h / 100.0) * 100.0;
                st.OriginN = Math.Floor(n / 100.0) * 100.0;
                st.ShiftE = st.OriginE; st.ShiftN = st.OriginN; st.ShiftH = st.OriginH;
                st.OriginChosen = true;
            }
            st.Vertices.Add(new Vector3((float)(e - st.ShiftE), (float)(h - st.ShiftH), (float)(n - st.ShiftN)));
            st.RemapGroup.Add(-1);
            st.RemapUv.Add(-1);
            st.RemapLocal.Add(0);
        }

        /// <summary>"vt u [v [w]]": w is dropped; a bad line still takes its index so later faces line up.</summary>
        private static void ParseTexCoord(State st, string s, int i, int len)
        {
            if (!NextNumber(s, ref i, len, out double u))
            {
                st.BadLines++;
                st.Uvs.Add(Vector2.zero);
                return;
            }
            // A missing v is legal (1D textures) and means 0.
            if (!NextNumber(s, ref i, len, out double v)) v = 0;
            st.Uvs.Add(new Vector2((float)u, (float)v));
        }

        private static void ParseFace(State st, string s, int i, int len)
        {
            List<int> idx = st.FaceIndices;
            List<int> uvs = st.FaceUvs;
            idx.Clear();
            uvs.Clear();
            int count = st.Vertices.Count;
            int uvCount = st.Uvs.Count;
            int withUv = 0;
            bool badUv = false;
            while (true)
            {
                i = SkipWs(s, i, len);
                if (i >= len || s[i] == '#') break;
                if (!TryParseInt(s, ref i, len, out int v)) { st.UnknownIndexFaces++; return; }
                int abs = v > 0 ? v - 1 : (v < 0 ? count + v : -1);
                if (abs < 0 || abs >= count || float.IsNaN(st.Vertices[abs].x)) { st.UnknownIndexFaces++; return; }
                idx.Add(abs);
                // "v/vt", "v/vt/vn", "v//vn" or plain "v": only the vt part matters here.
                int uv = -1;
                if (i < len && s[i] == '/')
                {
                    i++;
                    if (i < len && s[i] != '/' && !IsWs(s[i]))
                    {
                        if (TryParseInt(s, ref i, len, out int t))
                        {
                            int tAbs = t > 0 ? t - 1 : (t < 0 ? uvCount + t : -1);
                            if (tAbs >= 0 && tAbs < uvCount) uv = tAbs;
                            else badUv = true;
                        }
                        else badUv = true;
                    }
                }
                if (uv >= 0) withUv++;
                uvs.Add(uv);
                while (i < len && !IsWs(s[i])) i++; // skip /vn
            }
            if (idx.Count < 3) { st.UnknownIndexFaces++; return; }
            // A face is textured only when every corner names a valid vt; anything in between
            // would stretch one texel over the face, so it is drawn untextured instead.
            bool textured = withUv == idx.Count;
            // A file without any vt that still writes "v/vt" indices is a plain mesh, as before.
            if (!textured && uvCount > 0 && (badUv || withUv > 0)) st.BadUvFaces++;

            ModelGroup g = CurrentGroup(st);
            int gi = st.CurrentGroup;
            List<Vector3> gv = g.Vertices;
            List<int> tris = g.Triangles;
            int first = LocalIndex(st, gi, g, idx[0], textured ? uvs[0] : -1);
            int prev = LocalIndex(st, gi, g, idx[1], textured ? uvs[1] : -1);
            for (int k = 2; k < idx.Count; k++)
            {
                int cur = LocalIndex(st, gi, g, idx[k], textured ? uvs[k] : -1);
                if (first == prev || prev == cur || cur == first || IsDegenerate(gv[first], gv[prev], gv[cur]))
                    st.Degenerate++;
                else
                {
                    tris.Add(first); tris.Add(prev); tris.Add(cur);
                }
                prev = cur;
            }
        }

        private static void ParsePolyline(State st, string s, int i, int len)
        {
            int count = st.Vertices.Count;
            ModelGroup g = null;
            bool havePrev = false;
            Vector3 prev = default;
            while (true)
            {
                i = SkipWs(s, i, len);
                if (i >= len || s[i] == '#') break;
                if (!TryParseInt(s, ref i, len, out int v)) { st.BadLines++; return; }
                int abs = v > 0 ? v - 1 : (v < 0 ? count + v : -1);
                if (abs < 0 || abs >= count || float.IsNaN(st.Vertices[abs].x)) { st.UnknownIndexFaces++; return; }
                while (i < len && !IsWs(s[i])) i++;
                Vector3 p = st.Vertices[abs];
                if (havePrev)
                {
                    if (g == null) g = CurrentGroup(st);
                    if ((p - prev).sqrMagnitude > 1e-12f) { g.LineVertices.Add(prev); g.LineVertices.Add(p); }
                }
                prev = p;
                havePrev = true;
            }
        }

        /// <summary>
        /// The group-local vertex for corner (position <paramref name="abs"/>, texture
        /// coordinate <paramref name="uv"/>, -1 = none), copied into the group on first use.
        /// OBJ indexes positions and UVs separately, so one position on a UV seam becomes two
        /// vertices. The per-position cache remembers the last (group, uv) it was copied as,
        /// which is every corner of a photogrammetry mesh except the seams; the pair it
        /// replaces goes into the group's dictionary so a seam vertex is still reused.
        /// Untextured groups never get a dictionary: a vertex shared again after a group
        /// switch is simply copied once more, exactly as before textures were supported.
        /// </summary>
        private static int LocalIndex(State st, int groupIndex, ModelGroup g, int abs, int uv)
        {
            int cachedGroup = st.RemapGroup[abs];
            // An untextured corner does not care which UV the shared vertex carries.
            if (cachedGroup == groupIndex && (uv < 0 || st.RemapUv[abs] == uv)) return st.RemapLocal[abs];

            while (st.PairMaps.Count <= groupIndex) st.PairMaps.Add(null);
            Dictionary<long, int> map = st.PairMaps[groupIndex];
            if (map == null && uv >= 0) st.PairMaps[groupIndex] = map = new Dictionary<long, int>(1024);

            int local;
            if (map != null)
            {
                if (cachedGroup == groupIndex) map[PairKey(abs, st.RemapUv[abs])] = st.RemapLocal[abs];
                if (map.TryGetValue(PairKey(abs, uv), out local))
                {
                    Remember(st, abs, groupIndex, uv, local);
                    return local;
                }
            }

            List<Vector3> gv = g.Vertices;
            local = gv.Count;
            gv.Add(st.Vertices[abs]);
            List<Vector2> guv = g.UVs;
            if (uv >= 0 || guv.Count > 0)
            {
                // The group's first textured corner: earlier untextured vertices get (0,0) so
                // the UV list stays parallel to the vertex list.
                while (guv.Count < local) guv.Add(Vector2.zero);
                guv.Add(uv >= 0 ? st.Uvs[uv] : Vector2.zero);
            }
            Remember(st, abs, groupIndex, uv, local);
            return local;
        }

        private static long PairKey(int abs, int uv) => ((long)abs << 32) | (uint)uv;

        private static void Remember(State st, int abs, int groupIndex, int uv, int local)
        {
            st.RemapGroup[abs] = groupIndex;
            st.RemapUv[abs] = uv;
            st.RemapLocal[abs] = local;
        }

        private static bool IsDegenerate(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            // Twice the area below 1 mm²: a sliver the renderer would only z-fight with.
            return n.sqrMagnitude < 1e-12f;
        }

        // ------------------------------------------------------------------
        //  Groups + materials
        // ------------------------------------------------------------------

        private static ModelGroup CurrentGroup(State st)
        {
            if (st.CurrentGroup >= 0) return st.Model.Groups[st.CurrentGroup];
            string baseName = st.BaseGroup ?? "default";
            string key = baseName + "\u0001" + st.Material;
            if (!st.GroupIndex.TryGetValue(key, out int index))
            {
                // The first material a group uses keeps the plain group name; a change of
                // material inside the same group becomes its own "group · material" entry so
                // the user can still hide / recolour it on its own.
                string display;
                if (!st.FirstMaterialOfBase.TryGetValue(baseName, out string firstMat))
                {
                    st.FirstMaterialOfBase[baseName] = st.Material;
                    display = baseName;
                }
                else display = firstMat == st.Material ? baseName : baseName + " · " + st.Material;

                var g = new ModelGroup { Name = display };
                if (st.Material.Length > 0 && st.Materials.TryGetValue(st.Material, out MaterialDef mat))
                {
                    g.TexturePath = mat.Texture;
                    // Photogrammetry exporters (Geomagic Wrap) write Kd 0.5 grey next to the
                    // map; multiplying by it would halve the texture's brightness, so a
                    // textured group is tinted white and keeps only the material's opacity.
                    g.Color = mat.Texture != null ? new Color(1f, 1f, 1f, mat.Color.a) : mat.Color;
                }
                else g.Color = DefaultColor;
                index = st.Model.Groups.Count;
                st.Model.Groups.Add(g);
                st.GroupIndex[key] = index;
            }
            st.CurrentGroup = index;
            return st.Model.Groups[index];
        }

        private static void LoadMaterialLibs(State st, string names)
        {
            if (names.Length == 0) return;
            // Usually one file; some exporters list several. File names with spaces are rare
            // enough that splitting on whitespace is the better default.
            foreach (string raw in names.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                string file = raw.Trim();
                if (file.Length == 0 || !st.MtlSeen.Add(file)) continue;
                TextReader r = st.OpenMtl?.Invoke(file);
                if (r == null)
                {
                    if (st.OpenMtl != null) { st.MissingMtl++; st.Model.Warnings.Add(L.T($"Το αρχείο υλικών {file} δεν βρέθηκε", $"Material file {file} not found")); }
                    continue;
                }
                // Texture names in the MTL are relative to the MTL itself, which mtllib may
                // place in a subfolder of the OBJ's.
                string mtlDir = null;
                if (st.BaseDir != null)
                {
                    try { mtlDir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(st.BaseDir, file.Replace('\\', '/')))); }
                    catch { mtlDir = st.BaseDir; }
                }
                using (r) { ParseMtl(st, r, mtlDir); }
            }
        }

        /// <summary>
        /// newmtl / Kd / d / Tr / map_Kd only: the overlay draws an unlit colour or diffuse map,
        /// nothing else matters. <paramref name="mtlDir"/> null = no file system (text parse),
        /// so map_Kd is ignored.
        /// </summary>
        private static void ParseMtl(State st, TextReader r, string mtlDir)
        {
            string current = null;
            Color color = DefaultColor;
            string texture = null;
            string line;
            while ((line = r.ReadLine()) != null)
            {
                int len = line.Length;
                int i = SkipWs(line, 0, len);
                if (i >= len || line[i] == '#') continue;
                int kStart = i;
                while (i < len && !IsWs(line[i])) i++;
                int kLen = i - kStart;
                if (kLen == 6 && string.CompareOrdinal(line, kStart, "newmtl", 0, 6) == 0)
                {
                    if (current != null) st.Materials[current] = new MaterialDef { Color = color, Texture = texture };
                    current = Rest(line, i, len);
                    color = DefaultColor;
                    texture = null;
                }
                else if (current == null) continue;
                else if (kLen == 6 && string.Compare(line, kStart, "map_Kd", 0, 6, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    if (mtlDir != null)
                    {
                        texture = ResolveTexture(mtlDir, Rest(line, i, len));
                        // An uploaded MTL must not reach other files in the app's storage.
                        if (texture != null && st.BaseDir != null && !IsInside(st.BaseDir, texture)) texture = null;
                    }
                }
                else if (kLen == 2 && line[kStart] == 'K' && line[kStart + 1] == 'd')
                {
                    if (NextNumber(line, ref i, len, out double cr) && NextNumber(line, ref i, len, out double cg)
                        && NextNumber(line, ref i, len, out double cb))
                        color = new Color(Mathf.Clamp01((float)cr), Mathf.Clamp01((float)cg), Mathf.Clamp01((float)cb), color.a);
                }
                else if (kLen == 1 && line[kStart] == 'd')
                {
                    if (NextNumber(line, ref i, len, out double d)) color.a = Mathf.Clamp01((float)d);
                }
                else if (kLen == 2 && line[kStart] == 'T' && line[kStart + 1] == 'r')
                {
                    if (NextNumber(line, ref i, len, out double tr)) color.a = Mathf.Clamp01(1f - (float)tr);
                }
            }
            if (current != null) st.Materials[current] = new MaterialDef { Color = color, Texture = texture };
        }

        private static readonly string[] ImageFallbackExtensions = { ".jpg", ".jpeg", ".png" };

        /// <summary>
        /// The image a map_Kd statement names, as an absolute path. Options such as
        /// "-s 1 1 1" or "-bm 0.5" come before the file name, so the last token is taken.
        /// The PC tool converts textures to JPG and flattens folders before sending, so when
        /// the named file is missing, the same name with another image extension, and then
        /// the bare file name next to the MTL, are tried. Returns the as-written path when
        /// nothing matches, so the overlay can name the missing image in its warning.
        /// </summary>
        // mtllib / map_Kd names come from the uploaded file: "../../x" must stay in the model's folder.
        private static bool IsInside(string dir, string path)
        {
            try
            {
                string root = Path.GetFullPath(dir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string ResolveTexture(string mtlDir, string statement)
        {
            if (statement.Length == 0) return null;
            int end = statement.Length;
            int start = end;
            while (start > 0 && !IsWs(statement[start - 1])) start--;
            string name = statement.Substring(start, end - start).Replace('\\', '/');
            if (name.Length == 0) return null;
            try
            {
                string full = Path.GetFullPath(Path.Combine(mtlDir, name));
                if (File.Exists(full)) return full;
                foreach (string ext in ImageFallbackExtensions)
                {
                    string alt = Path.ChangeExtension(full, ext);
                    if (File.Exists(alt)) return alt;
                }
                string flat = Path.Combine(mtlDir, Path.GetFileName(name));
                if (File.Exists(flat)) return flat;
                foreach (string ext in ImageFallbackExtensions)
                {
                    string alt = Path.ChangeExtension(flat, ext);
                    if (File.Exists(alt)) return alt;
                }
                return full;
            }
            catch
            {
                return null; // illegal characters in the path: treat as untextured
            }
        }

        // ------------------------------------------------------------------
        //  Finish: counts, bounds, warnings
        // ------------------------------------------------------------------

        private static void Finish(State st)
        {
            ModelData m = st.Model;
            m.OriginE = st.OriginE; m.OriginN = st.OriginN; m.OriginH = st.OriginH;

            int removed = m.Groups.RemoveAll(g => g.Triangles.Count == 0 && g.LineVertices.Count == 0);
            int tris = 0, lines = 0;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (ModelGroup g in m.Groups)
            {
                tris += g.Triangles.Count / 3;
                lines += g.LineVertices.Count / 2;
                Extend(g.Vertices, ref min, ref max);
                Extend(g.LineVertices, ref min, ref max);
            }
            m.TriangleCount = tris;
            m.LineCount = lines;
            m.LocalBounds = min.x <= max.x ? new Bounds((min + max) * 0.5f, max - min) : new Bounds(Vector3.zero, Vector3.zero);

            // Worker thread: L is cached on the main thread at startup (L.Init).
            if (tris == 0 && lines == 0) m.Warnings.Add(L.T("Το αρχείο δεν περιέχει γεωμετρία", "The file has no geometry"));
            if (removed > 0) m.Warnings.Add(L.T($"{removed} ομάδες χωρίς γεωμετρία παραλείφθηκαν",
                $"{removed} group{(removed == 1 ? "" : "s")} without geometry skipped"));
            if (st.UnknownIndexFaces > 0) m.Warnings.Add(L.T($"{st.UnknownIndexFaces} όψεις με άγνωστους δείκτες κορυφών παραλείφθηκαν",
                $"{st.UnknownIndexFaces} face{(st.UnknownIndexFaces == 1 ? "" : "s")} with unknown vertex indices skipped"));
            if (st.Degenerate > 0) m.Warnings.Add(L.T($"{st.Degenerate} εκφυλισμένα τρίγωνα απορρίφθηκαν",
                $"{st.Degenerate} degenerate triangle{(st.Degenerate == 1 ? "" : "s")} dropped"));
            if (st.BadLines > 0) m.Warnings.Add(L.T($"{st.BadLines} γραμμές δεν αναγνωρίστηκαν",
                $"{st.BadLines} line{(st.BadLines == 1 ? "" : "s")} not recognised"));
            if (st.BadUvFaces > 0) m.Warnings.Add(L.T($"{st.BadUvFaces} όψεις με ελλιπείς συντεταγμένες υφής σχεδιάστηκαν χωρίς υφή",
                $"{st.BadUvFaces} face{(st.BadUvFaces == 1 ? "" : "s")} with incomplete texture coordinates drawn untextured"));
            int noUv = 0;
            foreach (ModelGroup g in m.Groups)
            {
                // A map without vt indices cannot be placed on the faces: draw the group plain.
                if (g.TexturePath != null && g.UVs.Count == 0) { g.TexturePath = null; g.Color = DefaultColor; noUv++; }
            }
            if (noUv > 0) m.Warnings.Add(L.T($"{noUv} ομάδες έχουν υφή αλλά όχι συντεταγμένες υφής: χωρίς υφή",
                $"{noUv} group{(noUv == 1 ? " has" : "s have")} a texture but no texture coordinates: untextured"));
        }

        private static void Extend(List<Vector3> pts, ref Vector3 min, ref Vector3 max)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 p = pts[i];
                if (p.x < min.x) min.x = p.x; if (p.x > max.x) max.x = p.x;
                if (p.y < min.y) min.y = p.y; if (p.y > max.y) max.y = p.y;
                if (p.z < min.z) min.z = p.z; if (p.z > max.z) max.z = p.z;
            }
        }

        // ------------------------------------------------------------------
        //  Tokenizer (no allocations on the hot path)
        // ------------------------------------------------------------------

        private static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\r';

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static int SkipWs(string s, int i, int len)
        {
            while (i < len && IsWs(s[i])) i++;
            return i;
        }

        private static string Rest(string s, int i, int len)
        {
            i = SkipWs(s, i, len);
            int end = len;
            // Strip a trailing comment and trailing whitespace.
            int hash = s.IndexOf('#', i);
            if (hash >= 0) end = hash;
            while (end > i && IsWs(s[end - 1])) end--;
            return end > i ? s.Substring(i, end - i) : "";
        }

        private static bool NextNumber(string s, ref int i, int len, out double value)
        {
            i = SkipWs(s, i, len);
            int start = i;
            while (i < len && !IsWs(s[i])) i++;
            return TryParseDouble(s, start, i, out value);
        }

        // 1e0 .. 1e22 are exact in double, so dividing by them rounds correctly.
        private static readonly double[] Pow10 =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
            1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22
        };

        /// <summary>
        /// Invariant-culture decimal / scientific number in s[start..end). Up to 18 significant
        /// digits are gathered exactly in a long and scaled once, which keeps survey
        /// coordinates (7 digits before the point, 3-4 after) to the millimetre. Anything
        /// unusual falls back to double.TryParse.
        /// </summary>
        private static bool TryParseDouble(string s, int start, int end, out double value)
        {
            value = 0;
            int i = start;
            if (i >= end) return false;
            bool neg = false;
            if (s[i] == '-') { neg = true; i++; }
            else if (s[i] == '+') i++;

            long mant = 0;
            int digits = 0, exp = 0;
            bool any = false;
            // Leading zeros are not significant digits: counting them ("0.0000…0123", zero-padded
            // exporters) used up the 18-digit budget and silently truncated the value to 0.
            while (i < end && s[i] >= '0' && s[i] <= '9')
            {
                if (digits < 18) { mant = mant * 10 + (s[i] - '0'); if (mant != 0) digits++; }
                else exp++;
                i++; any = true;
            }
            if (i < end && s[i] == '.')
            {
                i++;
                while (i < end && s[i] >= '0' && s[i] <= '9')
                {
                    if (digits < 18) { mant = mant * 10 + (s[i] - '0'); if (mant != 0) digits++; exp--; }
                    i++; any = true;
                }
            }
            if (!any) return false;
            if (i < end && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                bool eneg = false;
                if (i < end && (s[i] == '-' || s[i] == '+')) { eneg = s[i] == '-'; i++; }
                int e = 0;
                bool anyE = false;
                while (i < end && s[i] >= '0' && s[i] <= '9') { if (e < 1000) e = e * 10 + (s[i] - '0'); i++; anyE = true; }
                if (!anyE) return false;
                exp += eneg ? -e : e;
            }
            if (i != end)
                return double.TryParse(s.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

            value = mant;
            if (exp < 0) value = -exp < Pow10.Length ? value / Pow10[-exp] : value * Math.Pow(10, exp);
            else if (exp > 0) value = exp < Pow10.Length ? value * Pow10[exp] : value * Math.Pow(10, exp);
            if (neg) value = -value;
            return true;
        }

        /// <summary>Signed integer at s[i..]; stops at the first non-digit (the '/' of a face index).</summary>
        private static bool TryParseInt(string s, ref int i, int len, out int value)
        {
            value = 0;
            bool neg = false;
            if (i < len && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }
            bool any = false;
            long v = 0;
            while (i < len && s[i] >= '0' && s[i] <= '9')
            {
                v = v * 10 + (s[i] - '0');
                if (v > int.MaxValue) return false;
                i++; any = true;
            }
            if (!any) return false;
            value = neg ? -(int)v : (int)v;
            return true;
        }

        // ------------------------------------------------------------------
        //  Self-test
        // ------------------------------------------------------------------

#if UNITY_EDITOR
        /// <summary>
        /// Parses a 10 m cube at EGSA87-like coordinates with two groups, a material change and
        /// a polyline, through a temp file like the real path. Run from a menu / test to catch
        /// tokenizer regressions; prints the warnings so an unexpected one is visible.
        /// </summary>
        public static void SelfTest()
        {
            const string obj =
                "# cube 245310..245320 E, 4190010..4190020 N, 150..160 H (Z up)\r\n" +
                "mtllib cube.mtl\r\n" +
                "v 245310.0 4190010.0 150.0\r\n" +
                "v 245320.0 4190010.0 150.0\r\n" +
                "v 245320.0 4190020.0 150.0\r\n" +
                "v 245310.0 4190020.0 150.0\r\n" +
                "v 245310.0 4190010.0 160.0\r\n" +
                "v 245320.0 4190010.0 160.0\r\n" +
                "v 245320.0 4190020.0 \\\r\n" +
                "  160.0\r\n" +
                "v 245310.0 4190020.0 160.0\r\n" +
                "vn 0 0 1\r\n" +
                "vt 0 0\r\n" +
                "g walls\r\n" +
                "usemtl concrete\r\n" +
                "f 1/1/1 2/1/1 6/1/1 5/1/1\r\n" +
                "f 2//1 3//1 7//1 6//1\r\n" +
                "f 3 4 8 7\r\n" +
                "f -5 -8 -4 -1\r\n" +   // 4 1 5 8
                "g roof\r\n" +
                "f 5 6 7 8 # top\r\n" +
                "f 4 3 2 1\r\n" +
                "f 1 1 2\r\n" +          // degenerate: dropped
                "l 1 2 3\r\n" +          // two segments, in "roof"
                "g empty\r\n";           // no geometry: removed with a warning
            const string mtl = "newmtl concrete\nKd 0.6 0.6 0.65\nd 0.8\n";

            string dir = Path.Combine(Application.temporaryCachePath, "objtest");
            System.IO.Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "cube.obj");
            File.WriteAllText(path, obj);
            File.WriteAllText(Path.Combine(dir, "cube.mtl"), mtl);

            ModelData m = Load(path, ImportOptions.Default);
            Debug.Assert(m.Groups.Count == 2, $"groups {m.Groups.Count}");
            Debug.Assert(m.TriangleCount == 12, $"triangles {m.TriangleCount}");
            Debug.Assert(m.LineCount == 2, $"lines {m.LineCount}");
            Debug.Assert(Math.Abs(m.OriginE - 245300) < 1e-9 && Math.Abs(m.OriginN - 4190000) < 1e-9 && Math.Abs(m.OriginH - 100) < 1e-9,
                $"origin {m.OriginE} {m.OriginN} {m.OriginH}");
            Debug.Assert((m.LocalBounds.min - new Vector3(10f, 50f, 10f)).magnitude < 1e-3f, $"min {m.LocalBounds.min}");
            Debug.Assert((m.LocalBounds.size - new Vector3(10f, 10f, 10f)).magnitude < 1e-3f, $"size {m.LocalBounds.size}");
            Debug.Assert(m.Groups[0].Name == "walls" && m.Groups[0].Vertices.Count == 8, $"walls {m.Groups[0].Vertices.Count}");
            Debug.Assert(Mathf.Abs(m.Groups[0].Color.r - 0.6f) < 1e-3f && Mathf.Abs(m.Groups[0].Color.a - 0.8f) < 1e-3f, "mtl colour");
            // The degenerate face and the empty-group removal must both be reported.
            Debug.Assert(m.Groups[1].Name == "roof" && m.Groups[1].LineVertices.Count == 4, m.Groups[1].Name);
            Debug.Assert(m.Warnings.Count == 2, $"warnings {m.Warnings.Count}");
            Debug.Log($"[ObjImporter] SelfTest OK: {m.Groups.Count} groups, {m.TriangleCount} tris, {m.LineCount} lines, " +
                      $"warnings: {string.Join(" | ", m.Warnings)}");
            SelfTestTextured(dir);
        }

        /// <summary>
        /// A textured quad like a Geomagic Wrap export: "f v/vt", a negative vt index, a UV
        /// seam (position 4 used with two different UVs), map_Kd with options naming a .png
        /// that only exists as .jpg (what the PC tool sends), Kd 0.5 grey that must not
        /// darken the texture, and a second, untextured material that must stay without UVs.
        /// </summary>
        private static void SelfTestTextured(string dir)
        {
            const string obj =
                "mtllib quad.mtl\n" +
                "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\n" +
                "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1 0\nvt 0.5 0.5\n" +
                "usemtl photo\n" +
                "f 1/1 2/2 3/3\n" +
                "f 1/-5 3/-3 4/-2\n" +   // vt 1 3 4: reuses corners 1 and 3
                "f 4/5 3/3 2/2\n" +      // position 4 again, other UV: a seam vertex
                "usemtl plain\n" +
                "f 1 2 4\n";
            const string mtl =
                "newmtl photo\nKd 0.5 0.5 0.5\nd 1\nmap_Kd -s 1 1 1 quadImage.png\n" +
                "newmtl plain\nKd 0.2 0.4 0.6\n";

            string path = Path.Combine(dir, "quad.obj");
            File.WriteAllText(path, obj);
            File.WriteAllText(Path.Combine(dir, "quad.mtl"), mtl);
            string jpg = Path.Combine(dir, "quadImage.jpg");
            File.WriteAllBytes(jpg, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }); // only its existence matters here
            string png = Path.Combine(dir, "quadImage.png");
            if (File.Exists(png)) File.Delete(png);

            ModelData m = Load(path, ImportOptions.Default);
            Debug.Assert(m.Groups.Count == 2, $"textured groups {m.Groups.Count}");
            ModelGroup photo = m.Groups[0], plain = m.Groups[1];
            Debug.Assert(photo.Triangles.Count == 9 && photo.Vertices.Count == 5, $"photo v {photo.Vertices.Count} t {photo.Triangles.Count}");
            Debug.Assert(photo.UVs.Count == photo.Vertices.Count && photo.IsTextured, $"photo uvs {photo.UVs.Count}");
            Debug.Assert((photo.UVs[3] - new Vector2(0f, 1f)).sqrMagnitude < 1e-9f, $"uv3 {photo.UVs[3]}");
            Debug.Assert((photo.UVs[4] - new Vector2(0.5f, 0.5f)).sqrMagnitude < 1e-9f, $"uv4 {photo.UVs[4]}");
            Debug.Assert(photo.TexturePath != null && string.Equals(Path.GetFullPath(photo.TexturePath), Path.GetFullPath(jpg),
                StringComparison.OrdinalIgnoreCase), $"texture {photo.TexturePath}");
            Debug.Assert(photo.Color == Color.white, $"tint {photo.Color}");
            Debug.Assert(plain.UVs.Count == 0 && plain.TexturePath == null && !plain.IsTextured, "plain group got UVs");
            Debug.Assert(Mathf.Abs(plain.Color.b - 0.6f) < 1e-3f, "plain colour");
            Debug.Assert(m.Warnings.Count == 0, $"textured warnings: {string.Join(" | ", m.Warnings)}");
            Debug.Log($"[ObjImporter] SelfTest textured OK: {photo.Vertices.Count} vertices, texture {Path.GetFileName(photo.TexturePath)}");
        }
#endif
    }
}
