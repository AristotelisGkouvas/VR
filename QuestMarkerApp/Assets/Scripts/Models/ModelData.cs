using System.Collections.Generic;
using UnityEngine;

namespace QuestMarkerApp.Models
{
    /// <summary>
    /// A design model (building, dam, pipework) as the importers hand it over and the overlay
    /// renders it: groups of triangles and line segments in "project local" metres — the
    /// project's E/N/H minus <see cref="OriginE"/>/<see cref="OriginN"/>/<see cref="OriginH"/>,
    /// mapped to Unity axes x = East, y = Up, z = North, exactly like ProjectAlignment places
    /// the sensors. The alignment then carries the model into the room with the same rotation
    /// and translation as the sensors, so a georeferenced model lands on the real structure
    /// with no extra step.
    /// </summary>
    public class ModelData
    {
        public string Name;
        /// <summary>"obj" | "dxf" | "ifc" (IFC arrives converted to OBJ by the PC tool).</summary>
        public string SourceFormat;
        public readonly List<ModelGroup> Groups = new List<ModelGroup>();
        /// <summary>Subtracted from every coordinate at import, so the floats stay small.</summary>
        public double OriginE, OriginN, OriginH;
        /// <summary>Coordinates are in the project's survey system (E, N, H), not a local CAD frame.</summary>
        public bool Georeferenced = true;
        /// <summary>Source unit to metres factor that was applied (1 = metres, 0.001 = mm).</summary>
        public float UnitScale = 1f;
        /// <summary>Bounds in local metres after the origin shift, Unity axes.</summary>
        public Bounds LocalBounds;
        public int TriangleCount;
        public int LineCount;
        /// <summary>Notes for the user, e.g. "2 ομάδες χωρίς γεωμετρία παραλείφθηκαν".</summary>
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>One OBJ group / DXF layer / IFC storey: its own mesh and visibility toggle.</summary>
    public class ModelGroup
    {
        public string Name = "";
        /// <summary>Flat colour, or the tint multiplied into <see cref="TexturePath"/> (white for textured groups).</summary>
        public Color Color = new Color(0.85f, 0.88f, 0.9f, 1f);
        public readonly List<Vector3> Vertices = new List<Vector3>();
        /// <summary>Indices into <see cref="Vertices"/>, three per triangle.</summary>
        public readonly List<int> Triangles = new List<int>();
        /// <summary>
        /// Texture coordinate per vertex (same length as <see cref="Vertices"/>) when the faces
        /// carry OBJ "vt" indices, empty otherwise. A vertex is a (position, uv) pair, so a
        /// photogrammetry seam splits one position into two vertices.
        /// </summary>
        public readonly List<Vector2> UVs = new List<Vector2>();
        /// <summary>
        /// Absolute path of the diffuse map (MTL map_Kd), null when the group has none. The
        /// path may not exist on disk; the overlay then warns and draws the group untextured.
        /// </summary>
        public string TexturePath;
        /// <summary>Line segments as consecutive point pairs (wireframe entities, DXF lines).</summary>
        public readonly List<Vector3> LineVertices = new List<Vector3>();
        public bool Visible = true;

        /// <summary>True when the group carries UVs for every vertex and names a diffuse map.</summary>
        public bool IsTextured => TexturePath != null && UVs.Count > 0 && UVs.Count == Vertices.Count;
    }

    /// <summary>What an importer needs to know besides the file: units and axes of the source.</summary>
    public struct ImportOptions
    {
        /// <summary>Source unit to metres (1 = m, 0.001 = mm, 0.01 = cm).</summary>
        public float UnitScale;
        /// <summary>True when the file's Z is up (survey / CAD exports); false for Y-up exports (Blender, many OBJs).</summary>
        public bool ZUp;
        /// <summary>
        /// Map E,N,H of the file's (0,0,0) (X-Model-Origin, send_model.py --origin): the vertices
        /// are already relative to it, so nothing is subtracted. When <see cref="HasOrigin"/> is
        /// false the file holds absolute map coordinates; the importer picks an origin (the first
        /// vertex rounded to 100 m), subtracts it and reports it in the ModelData.
        /// </summary>
        public bool HasOrigin;
        public double OriginE, OriginN, OriginH;
        /// <summary>False for a local CAD frame; the overlay then needs a manual placement.</summary>
        public bool Georeferenced;

        public static ImportOptions Default => new ImportOptions { UnitScale = 1f, ZUp = true, Georeferenced = true };
    }
}
