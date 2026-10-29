using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Terraweb brand kit for the headset: the frontend's dark-theme tokens
    /// (terraweb2-frontend src/app/index.css, :root[data-theme='dark']), Manrope as
    /// --font-sans, and the procedural shapes/icons every panel and marker shares.
    /// Dark rather than the light theme: a pale card glares against passthrough.
    /// </summary>
    public static class Brand
    {
        private const string ContrastKey = "ui.highContrast";

        /// <summary>
        /// Outdoor theme for bright sun on passthrough: near-black cards, every grey text a step
        /// lighter, bold throughout. Read once at start (every panel takes its colours when it
        /// is built), so a change applies on the next launch.
        /// </summary>
        public static readonly bool HighContrast = PlayerPrefs.GetInt(ContrastKey, 0) == 1;

        /// <summary>The menu switch: what the next launch will use.</summary>
        public static bool HighContrastNext
        {
            get => PlayerPrefs.GetInt(ContrastKey, 0) == 1;
            set { PlayerPrefs.SetInt(ContrastKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static readonly Color Canvas = Hex(HighContrast ? 0x050807 : 0x0f1716);
        public static readonly Color Surface = Hex(HighContrast ? 0x070b0a : 0x172221);
        public static readonly Color SurfaceMuted = Hex(HighContrast ? 0x111a19 : 0x1e2b2a);
        public static readonly Color SurfaceSunken = Hex(HighContrast ? 0x1c2928 : 0x263433);
        public static readonly Color Border = Hex(HighContrast ? 0x4a5b57 : 0x232f2e);
        public static readonly Color BorderStrong = Hex(HighContrast ? 0x8a9c98 : 0x35443f);
        public static readonly Color Fg = Hex(HighContrast ? 0xffffff : 0xebefea);
        public static readonly Color FgMuted = Hex(HighContrast ? 0xf2f5f1 : 0xc4d0ce);
        public static readonly Color FgSubtle = Hex(HighContrast ? 0xd6dfdd : 0x8ca09e);
        public static readonly Color FgFaint = Hex(HighContrast ? 0xb4c2c0 : 0x6f817f);
        public static readonly Color Primary = Hex(0x8fc93a);
        public static readonly Color PrimarySoft = Hex(0x1f2e14);
        public static readonly Color PrimaryFg = Hex(0x10201f);
        public static readonly Color Info = Hex(0x5bcce5);
        public static readonly Color Warning = Hex(0xe4a62b);
        public static readonly Color Danger = Hex(0xdb5a46);
        /// <summary>Control points — the frontend's reference-tier yellow.</summary>
        public static readonly Color Control = Hex(0xffd400);
        /// <summary>Light-theme pair used for labels drawn on the world, as on the map.</summary>
        public static readonly Color MapInk = Hex(0x1a211f);
        public static readonly Color MapHalo = Hex(0xf9faf5);

        private static TMP_FontAsset body, bold;
        private static Material markerSprite, markerSpriteFade, labelMaterial;
        private static Texture2D discTexture;
        private static Sprite discSprite;
        private static readonly Dictionary<int, Sprite> RoundedSprites = new Dictionary<int, Sprite>();
        private static readonly Dictionary<string, Texture2D> IconTextures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Sprite> IconSprites = new Dictionary<string, Sprite>();

        public static Color Hex(int rgb, float alpha = 1f) =>
            new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, alpha);

        public static Color WithAlpha(this Color c, float a) => new Color(c.r, c.g, c.b, a);

        // ------------------------------------------------------------------
        //  Fonts
        // ------------------------------------------------------------------

        // Loaded once even when the fonts are missing: a null face used to re-run Resources.Load
        // and log two warnings for every text element built.
        private static bool bodyTried, boldTried;

        /// <summary>Manrope Medium, Greek glyphs via fallback. Null if the fonts are missing.</summary>
        public static TMP_FontAsset Body
        {
            get
            {
                if (body == null && !bodyTried) { bodyTried = true; body = LoadFont("Manrope-latin-500", "Manrope-greek-500"); }
                return body;
            }
        }

        /// <summary>Manrope Bold, Greek glyphs via fallback.</summary>
        public static TMP_FontAsset Bold
        {
            get
            {
                if (bold == null && !boldTried) { boldTried = true; bold = LoadFont("Manrope-latin-700", "Manrope-greek-700"); }
                return bold;
            }
        }

        // fontsource ships each script as its own subset file, so Greek rides as a fallback.
        private static TMP_FontAsset LoadFont(string latin, string greek)
        {
            TMP_FontAsset primary = CreateDynamic(latin);
            TMP_FontAsset fallback = CreateDynamic(greek);
            if (primary == null) return fallback;
            if (fallback != null)
                primary.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
            return primary;
        }

        private static TMP_FontAsset CreateDynamic(string resource)
        {
            var font = Resources.Load<Font>("Fonts/" + resource);
            if (font == null)
            {
                Debug.LogWarning($"[Brand] Font Resources/Fonts/{resource} missing — using the TMP default");
                return null;
            }
            var asset = TMP_FontAsset.CreateFontAsset(font, 64, 6,
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024);
            if (asset != null) asset.name = resource;
            return asset;
        }

        /// <summary>Apply the brand face; keeps the TMP default when the font failed to load.</summary>
        public static void Style(TMP_Text text, bool useBold = false)
        {
            TMP_FontAsset face = useBold || HighContrast ? Bold : Body;
            if (face != null) text.font = face;
        }

        // ------------------------------------------------------------------
        //  Marker materials (one of each, shared by every marker so they batch)
        // ------------------------------------------------------------------

        /// <summary>Depth-writing sprite material for marker halos, discs, glyphs and label plates.</summary>
        public static Material MarkerSpriteMaterial =>
            markerSprite != null ? markerSprite : (markerSprite = MakeMarkerSprite(true));

        private static Material markerSpriteMid, markerSpriteTop;

        /// <summary>
        /// The depth-writing sprite material for one piece of an icon, back to front: 0 the
        /// halo, 1 the disc, 2 the glyph. Each layer is pulled a step nearer in the depth buffer,
        /// so the pieces never fight however far the icon is (they flickered at range).
        /// </summary>
        public static Material MarkerSpriteLayer(int layer)
        {
            if (layer <= 0) return MarkerSpriteMaterial;
            if (layer == 1) return markerSpriteMid != null ? markerSpriteMid : (markerSpriteMid = Offset(MakeMarkerSprite(true), -1f, "MarkerSpriteMid"));
            return markerSpriteTop != null ? markerSpriteTop : (markerSpriteTop = Offset(MakeMarkerSprite(true), -2f, "MarkerSpriteTop"));
        }

        private static Material Offset(Material m, float steps, string name)
        {
            m.name = name;
            if (m.HasProperty("_OffsetFactor")) m.SetFloat("_OffsetFactor", steps);
            if (m.HasProperty("_OffsetUnits")) m.SetFloat("_OffsetUnits", steps * 2f);
            return m;
        }

        /// <summary>Same, without depth write: fading rings must not punch holes in what is behind.</summary>
        public static Material MarkerSpriteFadeMaterial =>
            markerSpriteFade != null ? markerSpriteFade : (markerSpriteFade = MakeMarkerSprite(false));

        private static Material MakeMarkerSprite(bool writesDepth)
        {
            Shader shader = Resources.Load<Shader>("Shaders/MarkerSprite");
            if (shader == null)
            {
                Debug.LogWarning("[Brand] Resources/Shaders/MarkerSprite missing — markers fall back to Sprites/Default");
                shader = Shader.Find("Sprites/Default");
            }
            var m = new Material(shader) { name = writesDepth ? "MarkerSprite" : "MarkerSpriteFade" };
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", writesDepth ? 1f : 0f);
            return m;
        }

        /// <summary>
        /// The one font material for marker labels: white face, dark outline. Setting
        /// outlineWidth per label made TMP clone a material per sensor (200 draw calls).
        /// </summary>
        public static Material LabelMaterial
        {
            get
            {
                if (labelMaterial != null) return labelMaterial;
                TMP_FontAsset face = Bold ?? TMP_Settings.defaultFontAsset;
                if (face == null || face.material == null) return null;
                labelMaterial = new Material(face.material) { name = "MarkerLabel" };
                labelMaterial.EnableKeyword(ShaderUtilities.Keyword_Outline);
                labelMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.12f);
                labelMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Canvas);
                return labelMaterial;
            }
        }

        // ------------------------------------------------------------------
        //  Shapes
        // ------------------------------------------------------------------

        /// <summary>Anti-aliased white disc, used tinted for marker halos and icon chips.</summary>
        public static Texture2D DiscTexture
        {
            get
            {
                if (discTexture != null) return discTexture;
                discTexture = RoundedTexture(128, 64f, "BrandDisc");
                return discTexture;
            }
        }

        public static Sprite DiscSprite =>
            discSprite != null ? discSprite
                : (discSprite = Sprite.Create(DiscTexture, new Rect(0, 0, 128, 128), new Vector2(0.5f, 0.5f), 100f));

        private static Sprite plate;

        /// <summary>
        /// Rounded plate for world-space labels (SpriteRenderer, sliced). Low pixels-per-unit so
        /// the corners stay round at label scale.
        /// </summary>
        public static Sprite Plate
        {
            get
            {
                if (plate != null) return plate;
                Texture2D tex = Rounded(24).texture;
                plate = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 12f, 0,
                    SpriteMeshType.FullRect, new Vector4(25, 25, 25, 25));
                return plate;
            }
        }

        /// <summary>9-sliced rounded rectangle; <paramref name="radius"/> in sprite pixels.</summary>
        public static Sprite Rounded(int radius)
        {
            if (RoundedSprites.TryGetValue(radius, out Sprite cached)) return cached;
            int size = radius * 2 + 4;
            Texture2D tex = RoundedTexture(size, radius, $"BrandRounded{radius}");
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(radius + 1, radius + 1, radius + 1, radius + 1));
            RoundedSprites[radius] = sprite;
            return sprite;
        }

        private static Texture2D RoundedTexture(int size, float radius, string name)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Signed distance to a rounded box filling the texture, 1 px AA rim.
                    float px = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float py = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float outside = new Vector2(Mathf.Max(px, 0f), Mathf.Max(py, 0f)).magnitude
                                    + Mathf.Min(Mathf.Max(px, py), 0f) - radius;
                    float a = Mathf.Clamp01(0.5f - outside);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(true);
            return tex;
        }

        // ------------------------------------------------------------------
        //  Sensor icons (brand-kit SVGs rasterized into Resources/SensorIcons)
        // ------------------------------------------------------------------

        public static Texture2D IconTexture(string icon)
        {
            if (string.IsNullOrEmpty(icon)) icon = "sensor-point";
            if (IconTextures.TryGetValue(icon, out Texture2D cached)) return cached;

            Texture2D texture = Resources.Load<Texture2D>("SensorIcons/" + icon);
            if (texture == null)
            {
                Debug.LogWarning($"[Brand] Sensor icon '{icon}' not in Resources/SensorIcons — using sensor-point");
                texture = Resources.Load<Texture2D>("SensorIcons/sensor-point");
            }
            IconTextures[icon] = texture;
            return texture;
        }

        public static Sprite IconSprite(string icon)
        {
            string key = string.IsNullOrEmpty(icon) ? "sensor-point" : icon;
            if (IconSprites.TryGetValue(key, out Sprite cached)) return cached;
            Texture2D tex = IconTexture(key);
            Sprite sprite = tex != null
                ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f)
                : null;
            IconSprites[key] = sprite;
            return sprite;
        }

        // ------------------------------------------------------------------
        //  Action icons (brand-kit UI glyphs in Resources/UiIcons)
        // ------------------------------------------------------------------

        public static Texture2D UiIconTexture(string icon)
        {
            string key = "ui:" + icon;
            if (IconTextures.TryGetValue(key, out Texture2D cached)) return cached;
            Texture2D texture = Resources.Load<Texture2D>("UiIcons/" + icon);
            if (texture == null) Debug.LogWarning($"[Brand] UI icon '{icon}' not in Resources/UiIcons");
            IconTextures[key] = texture;
            return texture;
        }

        public static Sprite UiIconSprite(string icon)
        {
            string key = "ui:" + icon;
            if (IconSprites.TryGetValue(key, out Sprite cached)) return cached;
            Texture2D tex = UiIconTexture(icon);
            Sprite sprite = tex != null
                ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f)
                : null;
            IconSprites[key] = sprite;
            return sprite;
        }
    }
}
