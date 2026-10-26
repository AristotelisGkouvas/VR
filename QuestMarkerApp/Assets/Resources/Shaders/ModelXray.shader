// X-ray fill for design models (ModelOverlay).
//
// Unlit, two-sided, alpha-blended, no depth write: the whole building shows through itself
// and through the passthrough walls. A flat tint at 35% alpha reads as a blob, so faces are
// shaded by how edge-on they are to the viewer (a Fresnel-style term): a face seen square-on
// is faint, a wall seen along its length glows, which is what makes the silhouette and the
// corners readable without lights. Stereo-instanced (Quest single-pass) via the UnityCG
// macros. Lives in Resources so it always ships in the build.
Shader "QuestMarker/ModelXray"
{
    Properties
    {
        _Color ("Color", Color) = (0.85, 0.88, 0.9, 0.35)
        _Rim ("Rim strength", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

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
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 toCamera : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;
            fixed _Rim;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.toCamera = _WorldSpaceCameraPos - mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 v = normalize(i.toCamera);
                // abs: the mesh is two-sided and OBJ winding is not trusted.
                float facing = abs(dot(n, v));
                float edgeOn = 1.0 - facing;
                fixed4 c = _Color;
                c.rgb *= lerp(1.0, 0.6 + 0.6 * edgeOn, _Rim);
                c.a *= lerp(1.0, 0.55 + 0.9 * edgeOn, _Rim);
                return c;
            }
        ENDCG
        }
    }

    Fallback "Legacy Shaders/Transparent/Diffuse"
}
