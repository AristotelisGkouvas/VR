using UnityEngine;

namespace QuestMarkerApp.Mobile
{
    /// <summary>
    /// Σχεδιάζει ένα grid στο επίπεδο του εδάφους (XZ, y=0) ως αναφορά κλίμακας.
    /// Κάθε κελί = 1 μέτρο (default). Οι άξονες X (κόκκινο) και Z (μπλε) τονίζονται,
    /// ώστε να ταιριάζουν με το σύστημα συντεταγμένων της εφαρμογής.
    /// Procedural line-mesh — δουλεύει σε build, χωρίς εξάρτηση από Meta XR SDK.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class GroundGrid : MonoBehaviour
    {
        [Header("Μέγεθος")]
        [Tooltip("Μισό πλάτος του grid σε μέτρα (π.χ. 10 = grid 20x20m).")]
        [SerializeField] private int halfSize = 10;
        [Tooltip("Απόσταση μεταξύ γραμμών σε μέτρα.")]
        [SerializeField] private float spacing = 1f;
        [Tooltip("Κάθε πόσες γραμμές τονίζεται μια «κύρια» γραμμή.")]
        [SerializeField] private int majorEvery = 5;

        [Header("Χρώματα")]
        [SerializeField] private Color minorColor = new Color(1f, 1f, 1f, 0.12f);
        [SerializeField] private Color majorColor = new Color(1f, 1f, 1f, 0.28f);
        [SerializeField] private Color xAxisColor = new Color(1f, 0.23f, 0.19f, 0.9f); // κόκκινο = X
        [SerializeField] private Color zAxisColor = new Color(0.04f, 0.52f, 1f, 0.9f); // μπλε = Z

        private Mesh mesh;
        private Material material;

        private void OnEnable()
        {
            Build();
        }

        private void OnValidate()
        {
            if (halfSize < 1) halfSize = 1;
            if (spacing < 0.05f) spacing = 0.05f;
            if (majorEvery < 1) majorEvery = 1;
            if (isActiveAndEnabled) Build();
        }

        private void OnDestroy()
        {
            if (Application.isPlaying)
            {
                if (mesh != null) Destroy(mesh);
                if (material != null) Destroy(material);
            }
            else
            {
                if (mesh != null) DestroyImmediate(mesh);
                if (material != null) DestroyImmediate(material);
            }
        }

        private void Build()
        {
            var mf = GetComponent<MeshFilter>();
            var mr = GetComponent<MeshRenderer>();

            if (material == null)
            {
                // Sprites/Default υποστηρίζει per-vertex colors και transparency.
                material = new Material(Shader.Find("Sprites/Default")) { name = "GroundGridMat" };
            }
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            if (mesh == null)
            {
                mesh = new Mesh { name = "GroundGridMesh" };
            }
            mesh.Clear();

            int lineCount = (halfSize * 2 + 1);
            int vertCount = lineCount * 4; // 2 κατευθύνσεις * 2 vertices ανά γραμμή

            var verts = new Vector3[vertCount];
            var colors = new Color[vertCount];
            var indices = new int[vertCount];

            float extent = halfSize * spacing;
            int v = 0;

            for (int i = -halfSize; i <= halfSize; i++)
            {
                float p = i * spacing;

                Color cParallelX = LineColor(i, isXAxis: i == 0); // γραμμή κατά μήκος του X (z=p)
                Color cParallelZ = LineColor(i, isZAxis: i == 0); // γραμμή κατά μήκος του Z (x=p)

                // Γραμμή παράλληλη στον X σε z = p
                verts[v] = new Vector3(-extent, 0f, p);
                verts[v + 1] = new Vector3(extent, 0f, p);
                colors[v] = colors[v + 1] = cParallelX;
                indices[v] = v; indices[v + 1] = v + 1;
                v += 2;

                // Γραμμή παράλληλη στον Z σε x = p
                verts[v] = new Vector3(p, 0f, -extent);
                verts[v + 1] = new Vector3(p, 0f, extent);
                colors[v] = colors[v + 1] = cParallelZ;
                indices[v] = v; indices[v + 1] = v + 1;
                v += 2;
            }

            mesh.vertices = verts;
            mesh.colors = colors;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            mf.sharedMesh = mesh;
        }

        private Color LineColor(int i, bool isXAxis = false, bool isZAxis = false)
        {
            if (isXAxis) return xAxisColor;
            if (isZAxis) return zAxisColor;
            return (i % majorEvery == 0) ? majorColor : minorColor;
        }
    }
}
