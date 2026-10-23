// Camera-facing particles for steam, smoke, arcs and blood droplets (HDRP, hand-written ForwardOnly pass).
// Instances come from _Particles (see Particles.cs): a = pos.xyz (field mm), size; b = rgb, alpha; c = dir.xyz, kind.
Shader "BovieSim/Particles"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        _Intensity ("Intensity", Float) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct ParticleData { float4 a; float4 b; float4 c; };
            StructuredBuffer<ParticleData> _Particles;
            float4x4 _FieldToWorld;
            int _Offset;
            float _Intensity;

            struct VOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 col : TEXCOORD1; float kind : TEXCOORD2; };

            static const float2 kCorners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            VOut Vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                ParticleData p = _Particles[_Offset + iid];
                float2 c = kCorners[vid % 6];
                float3 centreWS = mul(_FieldToWorld, float4(p.a.xyz, 1)).xyz;
                float scale = length(float3(_FieldToWorld[0][0], _FieldToWorld[1][0], _FieldToWorld[2][0])); // mm -> world units
                float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz;
                float3 posWS;
                if (p.c.w > 1.5 && p.c.w < 2.5)
                {
                    // arc filament: thin quad along dir, 0.18 mm half-width
                    float3 along = normalize(mul((float3x3)_FieldToWorld, p.c.xyz));
                    float3 view = normalize(_WorldSpaceCameraPos - centreWS);
                    float3 side = normalize(cross(along, view));
                    posWS = centreWS + along * (c.y * 0.5 + 0.5) * p.a.w * scale + side * c.x * 0.18 * scale;
                }
                else posWS = centreWS + (right * c.x + up * c.y) * p.a.w * scale;
                VOut o;
                o.pos = TransformWorldToHClip(GetCameraRelativePositionWS(posWS));
                o.uv = c; o.col = p.b; o.kind = p.c.w;
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            float4 Frag(VOut i) : SV_Target
            {
                float exposure = GetCurrentExposureMultiplier();
                float r = length(i.uv);
                if (i.kind > 2.5)
                {
                    // blood droplet: glossy bead
                    float disc = 1 - smoothstep(0.75, 1.0, r);
                    clip(disc - 0.01);
                    float nz = sqrt(saturate(1 - r * r));
                    float3 lit = i.col.rgb * (0.35 + 0.65 * saturate(i.uv.y * 0.4 + nz * 0.6)) + pow(nz, 30) * 0.5;
                    return float4(lit * disc * _Intensity * exposure, disc);
                }
                if (i.kind > 1.5)
                {
                    float core = 1 - smoothstep(0.0, 1.0, abs(i.uv.x));
                    float flick = 0.7 + 0.3 * Hash(float2(i.uv.y * 40, _Time.y * 60));
                    return float4(i.col.rgb * core * core * i.col.a * flick * 6 * _Intensity * exposure, 0);
                }
                float soft = 1 - smoothstep(0.35, 1.0, r);
                float a = soft * i.col.a;
                return float4(i.col.rgb * a * _Intensity * exposure, a);
            }
            ENDHLSL
        }
    }
}
