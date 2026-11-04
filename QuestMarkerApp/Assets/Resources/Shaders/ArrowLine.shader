// Displacement arrows of the time machine (shafts and heads).
//
// A sensor sits on the model's surface, so an arrow that points sideways or down runs into
// the mesh: with Sprites/Default the depth test hid most of it and only the arrows pointing
// up stayed visible ("why do only T2, T5 and T6 have arrows?"). Two passes: the part inside
// or behind the model draws faint (_HiddenAlpha), the part in front at full strength, so
// every arrow can be read and its depth is still plain.
// Vertex colour (LineRenderer start/end colour) times _Color, like Sprites/Default.
// Lives in Resources so it always ships in the build.
Shader "QuestMarker/ArrowLine"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _HiddenAlpha ("Alpha inside or behind the model", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+12" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        CGINCLUDE
        #include "UnityCG.cginc"
        fixed4 _Color;
        float _HiddenAlpha;

        struct appdata
        {
            float4 vertex : POSITION;
            fixed4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            fixed4 color : COLOR;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        v2f vert(appdata v)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            o.pos = UnityObjectToClipPos(v.vertex);
            o.color = v.color * _Color;
            return o;
        }
        ENDCG

        // Inside or behind the model: faint.
        Pass
        {
            ZTest Greater
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragHidden
            #pragma multi_compile_instancing
            fixed4 fragHidden(v2f i) : SV_Target
            {
                fixed4 c = i.color;
                c.a *= _HiddenAlpha;
                return c;
            }
            ENDCG
        }

        // In front: full strength.
        Pass
        {
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragShown
            #pragma multi_compile_instancing
            fixed4 fragShown(v2f i) : SV_Target { return i.color; }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}
