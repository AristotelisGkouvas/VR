using System;
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

        // Circumcircle in double: near-collinear sensors (a deck edge, a tunnel wall) overflow
        // float. A degenerate (collinear) triangle gets an infinite circle, so the next point
        // removes it instead of it sticking as a zero-area sliver.
        private static Tri Make(List<Vector2> pts, int a, int b, int c)
        {
            Vector2 p = pts[a], q = pts[b], r = pts[c];
            double px = p.x, py = p.y, qx = q.x, qy = q.y, rx = r.x, ry = r.y;
            double d = 2.0 * (px * (qy - ry) + qx * (ry - py) + rx * (py - qy));
            double scale = Math.Max(1e-9, Math.Max((q - p).sqrMagnitude, (r - p).sqrMagnitude));
            if (Math.Abs(d) < 1e-9 * scale)
                return new Tri { A = a, B = b, C = c, Centre = (p + q + r) / 3f, R2 = float.MaxValue };
            double p2 = px * px + py * py, q2 = qx * qx + qy * qy, r2 = rx * rx + ry * ry;
            double cx = (p2 * (qy - ry) + q2 * (ry - py) + r2 * (py - qy)) / d;
            double cy = (p2 * (rx - qx) + q2 * (px - rx) + r2 * (qx - px)) / d;
            double dx = px - cx, dy = py - cy;
            return new Tri { A = a, B = b, C = c, Centre = new Vector2((float)cx, (float)cy), R2 = (float)(dx * dx + dy * dy) };
        }
    }
}
