// A sensor's heat zone (Markers/SensorHeatZones), for projects without a 3D model: a sphere
// around the sensor in the colour of its movement. Nothing is interpolated between sensors.
//
// _Paint 0: a soft glowing ball, its strength from how much of the sphere the ray crosses.
// _Paint 1: the colour lands on the real surface inside the sphere. The ray is walked through
// the sphere against the Quest depth map, and where it first goes behind a real surface that
// point is coloured, fading towards the sphere's edge. A sphere with nothing in it, or hidden
// behind a nearer real surface, stays a faint ball, so a zone is never simply gone.
//
// The back faces draw (Cull Front) so a zone still shows from inside it. The depth map is
// sampled with an explicit LOD: implicit gradients are not allowed in the loop.
// Stereo-instanced (Quest single-pass instanced). Lives in Resources so it ships in the build.
Shader "QuestMarker/SensorZone"
{
    Properties
    {
        _Color ("Colour", Color) = (1,1,1,1)
        _Paint ("Paint on the real surface", Float) = 0
    }

    SubShader
    {
        // Before the sensor icons (Transparent+10), so a zone never tints them.
        Tags { "Queue" = "Transparent+5" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Front
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            #define ZONE_STEPS 8
            #define PAINT_ALPHA 0.6
            #define BALL_ALPHA 0.45
            #define FAINT_ALPHA 0.10
            #define DEPTH_BIAS 0.02

            fixed4 _Color;
            float _Paint;

            UNITY_DECLARE_TEX2DARRAY(_EnvironmentDepthTexture);
            uniform float4x4 _EnvironmentDepthReprojectionMatrices[2];
            uniform float4 _EnvironmentDepthZBufferParams;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float3 center : TEXCOORD1;
                float radius : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.center = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                // The built-in sphere has a radius of 0.5 at scale 1.
                o.radius = length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20)) * 0.5;
                return o;
            }

            // Positive when the world point lies behind the real surface on its depth-map ray,
            // in metres. False when it falls outside the depth map.
            bool BehindSurface(float3 p, out float behind)
            {
                behind = 0;
                float4 d = mul(_EnvironmentDepthReprojectionMatrices[unity_StereoEyeIndex], float4(p, 1.0));
                if (d.w <= 0.0) return false;
                float2 uv = (d.xy / d.w + 1.0) * 0.5;
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) return false;
                float scene = (1.0 / ((d.z / d.w) + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
                float raw = UNITY_SAMPLE_TEX2DARRAY_LOD(_EnvironmentDepthTexture, float3(uv, (float)unity_StereoEyeIndex), 0).r;
                float ndc = raw * 2.0 - 1.0;
                if (ndc >= 1.0) return false; // no depth here
                float real = (1.0 / (ndc + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
                behind = scene - real;
                return true;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 eye = _WorldSpaceCameraPos;
                float3 dir = normalize(i.world - eye);
                float3 oc = eye - i.center;
                float b = dot(dir, oc);
                float disc = b * b - (dot(oc, oc) - i.radius * i.radius);
                clip(disc);
                float root = sqrt(max(disc, 0.0));
                float t1 = -b + root;
                float tRaw = -b - root;
                float t0 = max(tRaw, 0.0);
                clip(t1 - t0 - 1e-4);
                // Share of the diameter this ray crosses: 1 through the middle, 0 at the rim.
                float chord = saturate((t1 - t0) / (2.0 * i.radius));
                float ball = chord * chord;

                if (_Paint < 0.5) return fixed4(_Color.rgb, BALL_ALPHA * ball);

                float tHit = -1.0, tPrev = t0, behind;
                bool first = true;
                bool hidden = false;
                for (int s = 0; s < ZONE_STEPS; s++)
                {
                    float t = lerp(t0, t1, (s + 0.5) / ZONE_STEPS);
                    if (BehindSurface(eye + dir * t, behind) && behind >= -DEPTH_BIAS)
                    {
                        // Already behind something where the ray enters: a nearer real surface
                        // hides this zone (not when the eye is inside the sphere).
                        if (first && tRaw > 0.0) hidden = true;
                        else
                        {
                            // Two halvings between the last point in front and this one.
                            float lo = tPrev, hi = t;
                            for (int r = 0; r < 2; r++)
                            {
                                float mid = 0.5 * (lo + hi), b2;
                                if (BehindSurface(eye + dir * mid, b2) && b2 >= -DEPTH_BIAS) hi = mid;
                                else lo = mid;
                            }
                            tHit = hi;
                        }
                        break;
                    }
                    first = false;
                    tPrev = t;
                }

                if (tHit >= 0.0)
                {
                    float edge = length(eye + dir * tHit - i.center) / i.radius;
                    return fixed4(_Color.rgb, PAINT_ALPHA * (1.0 - smoothstep(0.55, 1.0, edge)));
                }
                return fixed4(_Color.rgb, (hidden ? 0.6 : 1.0) * FAINT_ALPHA * ball);
            }
        ENDCG
        }
    }

    Fallback Off
}
