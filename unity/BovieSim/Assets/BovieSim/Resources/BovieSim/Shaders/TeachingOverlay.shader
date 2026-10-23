// Teaching view: the tissue surface drawn a second time over the realistic one, coloured by temperature or
// thermal damage (uv1 = temperature C, Omega written by Surface.compute) with isotherm and Omega = 1 lines.
// Unaffected tissue stays nearly transparent so the real surface shows through. HDRP ForwardOnly, transparent.
Shader "BovieSim/TeachingOverlay"
{
    Properties
    {
        _Mode ("Mode (1 temperature, 2 damage)", Float) = 1
        _Opacity ("Opacity", Range(0, 1)) = 0.85
    }
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            float _Mode;
            float _Opacity;

            struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; float2 to : TEXCOORD1; };
            struct VOut { float4 pos : SV_POSITION; float3 nws : TEXCOORD0; float2 to : TEXCOORD1; };

            VOut Vert(Attributes a)
            {
                VOut o;
                float3 ws = TransformObjectToWorld(a.positionOS);
                o.pos = TransformWorldToHClip(ws);
                o.nws = TransformObjectToWorldNormal(a.normalOS);
                o.to = a.to;
                return o;
            }

            // same ramps as the section view (Sim.compute) and the legend (SimUI)
            float3 TempRamp(float t)
            {
                float x = saturate((t - 37.0) / 263.0);
                float3 c1 = lerp(float3(0.1, 0.1, 0.5), float3(0.0, 0.8, 0.9), smoothstep(0.0, 0.25, x));
                float3 c2 = lerp(c1, float3(0.2, 0.9, 0.2), smoothstep(0.2, 0.45, x));
                float3 c3 = lerp(c2, float3(1.0, 0.9, 0.1), smoothstep(0.4, 0.65, x));
                float3 c4 = lerp(c3, float3(1.0, 0.2, 0.05), smoothstep(0.6, 0.85, x));
                return lerp(c4, float3(1, 1, 1), smoothstep(0.85, 1.0, x));
            }
            float3 DamageRamp(float om)
            {
                float x = saturate((log10(max(om, 1e-3)) + 2.0) / 3.0);
                float3 c = lerp(float3(0.08, 0.12, 0.35), float3(0.10, 0.45, 0.60), smoothstep(0.0, 0.45, x));
                c = lerp(c, float3(0.95, 0.70, 0.15), smoothstep(0.45, 0.57, x));
                c = lerp(c, float3(0.90, 0.15, 0.10), smoothstep(0.57, 0.667, x));
                return lerp(c, float3(1.0, 0.95, 0.90), smoothstep(0.667, 1.0, x));
            }
            /** 1 on a contour of v at level (about one pixel wide), 0 elsewhere */
            float Contour(float v, float level)
            {
                float d = abs(v - level) / max(fwidth(v), 1e-4);
                return 1.0 - saturate(d - 0.5);
            }

            float4 Frag(VOut i) : SV_Target
            {
                float T = i.to.x, om = i.to.y;
                float3 n = normalize(i.nws);
                float shade = 0.55 + 0.45 * saturate(dot(n, normalize(float3(0.3, 1.0, 0.2))));
                float3 c; float a;
                if (_Mode < 1.5)
                {
                    c = TempRamp(T) * shade;
                    a = lerp(0.12, _Opacity, saturate((T - 38.0) / 8.0));
                    c = lerp(c, float3(0.30, 0.90, 1.00), Contour(T, 45.0));
                    c = lerp(c, float3(1.00, 0.85, 0.20), Contour(T, 60.0));
                    c = lerp(c, float3(1.00, 1.00, 1.00), Contour(T, 100.0));
                    a = max(a, max(Contour(T, 45.0), max(Contour(T, 60.0), Contour(T, 100.0))));
                }
                else
                {
                    c = DamageRamp(om) * shade;
                    a = lerp(0.10, _Opacity, saturate((log10(max(om, 1e-3)) + 2.0) / 1.5));
                    float l = Contour(om, 1.0);
                    c = lerp(c, float3(1, 1, 1), l); a = max(a, l);
                }
                return float4(c, a);
            }
            ENDHLSL
        }
    }
}
