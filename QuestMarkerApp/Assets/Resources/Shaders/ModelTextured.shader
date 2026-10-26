// Fill for design models (ModelOverlay): textured photogrammetry meshes and flat-colour groups.
//
// Unlit _MainTex x _Color, alpha = _Color.a: a photogrammetry texture already carries its
// own light and shade, so scene lighting would only darken it, and the passthrough room has
// no lights worth matching anyway. Flat groups use the built-in white texture, so one shader
// serves the whole model. Two-sided (OBJ winding is not trusted; a terrain mesh is seen from
// below too). The blend state comes from material properties that ModelOverlay sets from
// the opacity slider: opaque (Geometry queue, ZWrite On, Blend One Zero) at full opacity so a
// textured mesh reads as solid, alpha-blended (Transparent queue, ZWrite Off) below it.
// Stereo-instanced (Quest single-pass instanced): a custom vertex function has to set up the
// instance and stereo eye itself, or the mesh draws in the left eye only.
// Lives in Resources so it always ships in the build.
Shader "QuestMarker/ModelTextured"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        [Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite [_ZWrite]
        ZTest LEqual
        Blend [_SrcBlend] [_DstBlend]

        Pass
        {
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                // Opacity is the material's, not the image's: a PNG atlas with transparent
                // padding must not punch holes in the mesh.
                c.a = _Color.a;
                return c;
            }
        ENDCG
        }
    }

    Fallback "Unlit/Texture"
}
