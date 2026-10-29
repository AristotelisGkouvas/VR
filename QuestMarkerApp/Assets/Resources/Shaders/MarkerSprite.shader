// Sprite shader for the sensor markers (halo, disc, glyph, label plate).
//
// Sprites/Default never writes depth, so each marker had to be its own SortingGroup to keep
// a near sensor's disc over a far one's — and every quad became its own draw call (~5 per
// sensor, ~1000 at 200 sensors). This one is Sprites/Default (same vertex/fragment path,
// same tint and instancing handling via UnitySprites.cginc) plus a depth write for the
// (almost) opaque pixels and a clip of the transparent ones, so near-over-far is settled by
// the depth test and all halos, discs and glyphs can batch across markers.
// Lives in Resources so it always ships in the build.
//
// With the Quest 3 depth sensor on (DepthOcclusion, Display menu), a marker behind a real
// pier or wall turns faint (_XrayAlpha) instead of floating solid in front of the concrete.
// Meta's EnvironmentDepthManager sets HARD_/SOFT_OCCLUSION globally; without it the
// occlusion macros compile to 1 and nothing changes.
Shader "QuestMarker/MarkerSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.02
        [Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 1
        // Depth-writing pieces use alpha-to-coverage (MSAA smooths their edges; a clip() edge
        // shimmered, and a near icon's faint rim hid a far one); the fade material blends.
        [Toggle] _AlphaToMask ("Alpha to coverage", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 10
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
        _XrayAlpha ("Alpha behind real surfaces", Range(0, 1)) = 0.3
        _OffsetFactor ("Depth offset factor", Float) = 0
        _OffsetUnits ("Depth offset units", Float) = 0
        _EnvironmentDepthBias ("Environment depth bias", Float) = 0.06
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
        // Halo, disc and glyph of one icon sit millimetres apart: far away the depth buffer
        // cannot tell them apart and they flickered through each other. Each piece draws with
        // its own offset in depth-buffer steps (Brand.MarkerSpriteLayer), which holds at any range.
        Offset [_OffsetFactor], [_OffsetUnits]
        Blend [_SrcBlend] [_DstBlend]
        AlphaToMask [_AlphaToMask]

        Pass
        {
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "UnitySprites.cginc"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"

            fixed _Cutoff;
            float _AlphaToMask;
            float _XrayAlpha;
            float _EnvironmentDepthBias;

            struct v2fOcc
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                META_DEPTH_VERTEX_OUTPUT(1)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2fOcc vert(appdata_t IN)
            {
                v2f s = SpriteVert(IN);
                v2fOcc OUT;
                UNITY_INITIALIZE_OUTPUT(v2fOcc, OUT);
                UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(s, OUT);
                OUT.vertex = s.vertex;
                OUT.color = s.color;
                OUT.texcoord = s.texcoord;
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(OUT, float4(IN.vertex.xyz, 1.0));
                return OUT;
            }

            fixed4 frag(v2fOcc IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                fixed4 c = SampleSpriteTexture(IN.texcoord) * IN.color;
                float visible = META_DEPTH_GET_OCCLUSION_VALUE(IN, _EnvironmentDepthBias);
                if (_AlphaToMask > 0.5)
                {
                    // A crisp edge one screen pixel wide: MSAA then resolves it into 4 steps.
                    c.a = saturate((c.a - 0.5) / max(fwidth(c.a), 1e-4) + 0.5);
                    c.a *= lerp(_XrayAlpha, 1.0, visible);
                    return c; // coverage does the blending: not premultiplied
                }
                clip(c.a - _Cutoff);
                c.a *= lerp(_XrayAlpha, 1.0, visible);
                c.rgb *= c.a; // premultiplied, like Sprites/Default
                return c;
            }
        ENDCG
        }
    }

    Fallback "Sprites/Default"
}
