// Fill for design models (ModelOverlay): textured photogrammetry meshes and flat-colour groups.
//
// Unlit _MainTex x _Color, alpha = _Color.a: a photogrammetry texture already carries its
// own light and shade, so scene lighting would only darken it, and the passthrough room has
// no lights worth matching anyway. Flat groups use the built-in white texture, so one shader
// serves the whole model. Cull stays off: the mesh is walked through and looked at from
// both sides, and a one-sided fill hides the back of every wall. The blend
// state comes from material properties that ModelOverlay sets from the opacity slider:
// opaque (Geometry queue, ZWrite On, Blend One Zero) at full opacity so a textured mesh
// reads as solid, alpha-blended (Transparent queue, ZWrite Off) below it.
//
// _MODELHEAT is a global keyword ModelHeatmap turns on. The extra sample and interpolator
// are compiled out of the variant the app draws the rest of the time.
// _RIM is per material, for a CAD group whose wireframe was too dense to draw as lines:
// a derivative normal brightens the silhouette. No NORMAL attribute, so a chunked mesh
// does not pay an extra vertex fetch.
// Stereo-instanced (Quest single-pass instanced): a custom vertex function has to set up the
// instance and stereo eye itself, or the mesh draws in the left eye only.
// Lives in Resources so it always ships in the build.
Shader "QuestMarker/ModelTextured"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        [Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 1
        _Rim ("Rim", Range(0, 1)) = 0.65
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Cull [_Cull]
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
            #pragma multi_compile _ _MODELHEAT
            #pragma multi_compile_local _ _RIM
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
                #if defined(_MODELHEAT)
                float2 heatUv : TEXCOORD1;
                #endif
                #if defined(_RIM)
                float3 worldPos : TEXCOORD2;
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            #if defined(_MODELHEAT)
            sampler2D _ModelHeatTex;
            float4 _ModelHeatRect; // min x, min z, 1/size x, 1/size z (model-local metres)
            float _ModelHeatOpacity;
            #endif
            #if defined(_RIM)
            fixed _Rim;
            #endif

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                #if defined(_MODELHEAT)
                // The model's pieces sit under its root with no transform of their own: object
                // space is the model's local frame (x east, z north), as ModelHeatmap's grid.
                o.heatUv = (v.vertex.xz - _ModelHeatRect.xy) * _ModelHeatRect.zw;
                #endif
                #if defined(_RIM)
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                #endif
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                // Opacity is the material's, not the image's: a PNG atlas with transparent
                // padding must not punch holes in the mesh.
                c.a = _Color.a;
                #if defined(_MODELHEAT)
                fixed4 h = tex2D(_ModelHeatTex, i.heatUv);
                c.rgb = lerp(c.rgb, h.rgb, h.a * _ModelHeatOpacity);
                #endif
                #if defined(_RIM)
                // Face normal from the screen derivatives: no stored normals, works on both
                // sides of a two-sided face. Silhouette pixels get brighter and more opaque
                // so a building without a line mesh still reads its corners.
                float3 n = cross(ddx(i.worldPos), ddy(i.worldPos));
                float facing = abs(dot(normalize(n), normalize(_WorldSpaceCameraPos - i.worldPos)));
                float edgeOn = saturate(1.0 - facing);
                c.rgb *= lerp(1.0, 0.55 + edgeOn, _Rim);
                c.a = saturate(c.a + edgeOn * _Rim * 0.45);
                #endif
                return c;
            }
        ENDCG
        }
    }

    Fallback "Unlit/Texture"
}
