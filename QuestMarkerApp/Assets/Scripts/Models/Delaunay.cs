using System.Collections.Generic;
using UnityEngine;

namespace QuestMarkerApp.Models
{
    /// <summary>
    /// Plane Delaunay triangulation (Bowyer–Watson) of a few hundred points at most: the
    /// sensors of a project, for ModelHeatmap's linear interpolation. Returns index triples
    /// into the input; duplicate points are joined once; collinear input gives no triangles.
    /// </summary>
    public static class Delaunay
    {
        private struct Tri
        {
            public int A, B, C;
            public Vector2 Centre;
            public float R2;
        }

        public static List<int> Triangulate(Vector2[] input)
        {
            var result = new List<int>();
            int n = input.Length;
            if (n < 3) return result;

            // Work relative to the centre for float precision; a super-triangle enclosing all.
            Vector2 min = input[0], max = input[0];
            foreach (Vector2 p in input) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            Vector2 mid = (min + max) * 0.5f;
            float span = Mathf.Max(max.x - min.x, max.y - min.y, 1f);
            var pts = new List<Vector2>(n + 3);
            foreach (Vector2 p in input) pts.Add(p - mid);
            pts.Add(new Vector2(-20f * span, -20f * span));
            pts.Add(new Vector2(20f * span, -20f * span));
            pts.Add(new Vector2(0f, 20f * span));

            var tris = new List<Tri> { Make(pts, n, n + 1, n + 2) };
            var seen = new HashSet<Vector2>();
            for (int i = 0; i < n; i++)
            {
                Vector2 p = pts[i];
                if (!seen.Add(p)) continue; // the same spot twice: one vertex
                var edges = new List<(int, int)>();
                for (int t = tris.Count - 1; t >= 0; t--)
                {
                    Tri tri = tris[t];
                    if ((p - tri.Centre).sqrMagnitude > tri.R2) continue;
                    Add(edges, tri.A, tri.B);
                    Add(edges, tri.B, tri.C);
                    Add(edges, tri.C, tri.A);
                    tris.RemoveAt(t);
                }
                foreach ((int a, int b) in edges) tris.Add(Make(pts, a, b, i));
            }

            foreach (Tri t in tris)
            {
                if (t.A >= n || t.B >= n || t.C >= n) continue; // touches the super-triangle
                result.Add(t.A); result.Add(t.B); result.Add(t.C);
            }
            return result;
        }

        // The boundary of the cavity: an edge shared by two removed triangles cancels out.
        private static void Add(List<(int, int)> edges, int a, int b)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                (int x, int y) = edges[i];
                if ((x == a && y == b) || (x == b && y == a)) { edges.RemoveAt(i); return; }
            }
            edges.Add((a, b));
        }

        private static Tri Make(List<Vector2> pts, int a, int b, int c)
        {
            Vector2 p = pts[a], q = pts[b], r = pts[c];
            float d = 2f * (p.x * (q.y - r.y) + q.x * (r.y - p.y) + r.x * (p.y - q.y));
            Vector2 centre;
            if (Mathf.Abs(d) < 1e-12f) centre = (p + q + r) / 3f; // degenerate: never contains a point usefully
            else
            {
                float p2 = p.sqrMagnitude, q2 = q.sqrMagnitude, r2 = r.sqrMagnitude;
                centre = new Vector2(
                    (p2 * (q.y - r.y) + q2 * (r.y - p.y) + r2 * (p.y - q.y)) / d,
                    (p2 * (r.x - q.x) + q2 * (p.x - r.x) + r2 * (q.x - p.x)) / d);
            }
            return new Tri { A = a, B = b, C = c, Centre = centre, R2 = (p - centre).sqrMagnitude };
        }
    }
}
