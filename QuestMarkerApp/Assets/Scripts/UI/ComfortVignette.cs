using UnityEngine;
using UnityEngine.Rendering;
using QuestMarkerApp.Models;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Narrows the view while the sticks carry the user through the life-size model: dark
    /// edges cut the peripheral flow that makes artificial motion sickening. A quad just in
    /// front of the eyes with a clear centre; it fades in with the motion and out after it.
    /// </summary>
    public class ComfortVignette : MonoBehaviour
    {
        private const float Distance = 0.3f;
        private const float Size = 1.1f;          // covers the Quest 3 field of view at Distance
        private const float FadeSeconds = 0.2f;
        private const float MaxAlpha = 0.85f;

        private Transform quad;
        private Material material;
        private float alpha;

        private void Awake()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "ComfortVignette";
            Destroy(go.GetComponent<Collider>());
            quad = go.transform;
            quad.SetParent(transform, false);
            quad.localScale = Vector3.one * Size;
            material = new Material(Shader.Find("Sprites/Default")) { mainTexture = RingTexture(128) };
            material.renderQueue = (int)RenderQueue.Overlay;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
        }

        private void LateUpdate()
        {
            float target = ModelOverlay.LifeSizeShown && ModelOverlay.Moving ? MaxAlpha : 0f;
            alpha = Mathf.MoveTowards(alpha, target, MaxAlpha * Time.unscaledDeltaTime / FadeSeconds);
            bool show = alpha > 0.001f;
            if (quad.gameObject.activeSelf != show) quad.gameObject.SetActive(show);
            if (!show) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            quad.SetPositionAndRotation(cam.transform.position + cam.transform.forward * Distance, cam.transform.rotation);
            material.color = new Color(0f, 0f, 0f, alpha);
        }

        // White with alpha 0 in a central circle rising to 1 at the edges (tinted black above).
        private static Texture2D RingTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Vignette" };
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c; // 0 centre, 1 edge midpoints
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 0.85f, r));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private void OnDestroy()
        {
            if (material != null)
            {
                Destroy(material.mainTexture);
                Destroy(material);
            }
        }
    }
}
