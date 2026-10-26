// Sprite shader for the sensor markers (halo, disc, glyph, label plate).
//
// Sprites/Default never writes depth, so each marker had to be its own SortingGroup to keep
// a near sensor's disc over a far one's — and every quad became its own draw call (~5 per
// sensor, ~1000 at 200 sensors). This one is Sprites/Default (same vertex/fragment path,
// same tint and instancing handling via UnitySprites.cginc) plus a depth write for the
// (almost) opaque pixels and a clip of the transparent ones, so near-over-far is settled by
// the depth test and all halos, discs and glyphs can batch across markers.
// Lives in Resources so it always ships in the build.
Shader "QuestMarker/MarkerSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.02
        [Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 1
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
            "RenderType" = "TransparentCutout"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite [_ZWrite]
        ZTest LEqual
        Blend One OneMinusSrcAlpha

        Pass
        {
        CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            fixed _Cutoff;

            fixed4 frag(v2f IN) : SV_Target
            {
                fixed4 c = SampleSpriteTexture(IN.texcoord) * IN.color;
                clip(c.a - _Cutoff);
                c.rgb *= c.a; // premultiplied, like Sprites/Default
                return c;
            }
        ENDCG
        }
    }

    Fallback "Sprites/Default"
}
